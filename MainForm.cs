using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DateReminder
{
    public partial class MainForm : Form
    {
        private AppConfig _cfg;
        private NotifyIcon? _notifyIcon;
        private ContextMenuStrip? _trayMenu;
        private FloatingCounter? _floatingCounter;
        private SettingsForm? _settingsForm;
        private Label? _lblStatus;
        private Label? _lblScannerStatus;
        private Label? _lblSoftwareName;
        private SerialPort? _serialPort;
        private string _scanBuffer = "";
        private int _scanCount = 0;
        private volatile bool _expiryBlocked = false;  // 过期弹窗阻塞中,停止模拟
        private bool _isStarted = false;     // 是否已点"启动"

        // Win11 圆角
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        const int DWMWCP_ROUND = 2;

        // 拖动
        [DllImport("user32.dll")]
        static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
        const int WM_NCLBUTTONDOWN = 0xA1;
        const int HTCAPTION = 2;

        // ======== Raw Input(USB HID 扫码枪) ========
        const int RID_INPUT = 0x10000003;
        const int RIDEV_INPUTSINK = 0x00000100;
        const int RIM_TYPEKEYBOARD = 1;
        const int WM_INPUT = 0x00FF;

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTDEVICELIST
        {
            public IntPtr hDevice;
            public uint dwType;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTHEADER
        {
            public uint dwType;
            public uint dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RAWKEYBOARD
        {
            public ushort MakeCode;
            public ushort Flags;
            public ushort Reserved;
            public ushort VKey;
            public uint Message;
            public uint ExtraInformation;
        }

        [StructLayout(LayoutKind.Explicit)]
        struct RAWDATA
        {
            [FieldOffset(0)] public RAWKEYBOARD Keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUT
        {
            public RAWINPUTHEADER header;
            public RAWDATA data;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        // 按键码转字符(仅处理普通可见字符,Enter/Tab 等走 VKey 特殊处理)
        static readonly Dictionary<int, char> VkToChar = new();
        static MainForm()
        {
            // 数字键 0-9
            for (int i = 0; i <= 9; i++) { VkToChar[0x30 + i] = (char)('0' + i); }
            // 小键盘 0-9
            for (int i = 0; i <= 9; i++) { VkToChar[0x60 + i] = (char)('0' + i); }
            // 字母 A-Z
            for (int i = 0; i < 26; i++) { VkToChar[0x41 + i] = (char)('A' + i); }
            // 部分符号
            VkToChar[0xBF] = '/';   VkToChar[0xBE] = '.'; VkToChar[0xBC] = ',';
            VkToChar[0xBD] = '-';   VkToChar[0x6B] = '+'; VkToChar[0x6D] = '-';
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern int GetRawInputDeviceList(IntPtr pDeviceList, ref uint pNumDevices, uint cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        static extern int GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

        [DllImport("user32.dll", SetLastError = true)]
        static extern int GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pDevices, uint numDevices, uint cbSize);

        // ======== 低级键盘钩子(截取扫码枪键盘数据) ========
        const int WH_KEYBOARD_LL = 13;
        const int WM_KEYDOWN = 0x0100;
        const int WM_KEYUP = 0x0101;
        const int WM_SYSKEYDOWN = 0x0104;
        const int WM_SYSKEYUP = 0x0105;

        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern IntPtr GetModuleHandle(string? lpModuleName);

        /// <summary>
        /// [Bug#2修复] 安装低级键盘钩子,在扫码枪输入时间窗口内抑制键盘消息,防止双重输入
        /// 策略:Raw Input 收到扫码枪字符后标记时间,钩子在窗口内吞掉 WM_KEYDOWN,
        /// 避免同一条扫码数据同时走 Raw Input 和系统键盘消息两条路径
        /// </summary>
        private void InstallKeyboardHook()
        {
            if (_keyboardHook != IntPtr.Zero) return;
            if (_usbHidDeviceHandles.Count == 0 && !_usbHidWaitingActivation) return;

            _keyboardProc = KeyboardHookProc;
            _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, GetModuleHandle(null), 0);
            if (_keyboardHook == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                Logger.Error($"低级键盘钩子安装失败!ErrorCode={err},HID模式将无法正常工作!");
                this.Invoke((MethodInvoker)(() =>
                {
                    _floatingCounter?.SetStatus(FloatingCounter.ScannerStatus.Disconnected);
                    MessageBox.Show(this,
                        $"键盘钩子注册失败(错误码:{err})\n\nHID扫码枪模式无法拦截双重输入,将导致扫码数据丢失!\n\n可能原因:\n1. 权限不足(请以管理员身份运行)\n2. 其他安全软件拦截了钩子\n3. 系统钩子队列已满",
                        "HID模式错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }));
            }
            else
            {
                Logger.Info($"低级键盘钩子已安装,Handle={_keyboardHook},防止HID扫码枪双重输入");
            }
        }

        /// <summary>
        /// 卸载低级键盘钩子
        /// </summary>
        private void UninstallKeyboardHook()
        {
            if (_keyboardHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
                Logger.Info("低级键盘钩子已卸载");
            }
        }

        /// <summary>
        /// [Bug#2修复] 低级键盘钩子回调:扫码枪输入时抑制键盘消息
        /// 当 Raw Input 刚收到扫码枪字符(在 _lastRawInputTick + RAW_INPUT_SUPPRESS_MS 窗口内),
        /// 吞掉 WM_KEYDOWN/WM_SYSKEYDOWN,因为数据已通过 Raw Input 路径处理。
        /// 窗口外的键盘消息正常放行,不影响用户正常打字。
        /// </summary>
        private const int LLKHF_INJECTED = 0x10;  // 程序模拟按键标志

        private IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            // === 第一道防线:模拟按键直接放行 ===
            // KBDLLHOOKSTRUCT.flags 偏移8字节处,LLKHF_INJECTED(0x10) 表示程序模拟的按键
            // 扫码枪是物理设备,不会带此标志;SendKeys/SendInput 发出的会带
            // 这是从根本上区分"扫码枪输入"和"模拟输出"
            if (nCode >= 0)
            {
                int flags = Marshal.ReadInt32(lParam, 8);  // KBDLLHOOKSTRUCT.flags
                if ((flags & LLKHF_INJECTED) != 0)
                {
                    // 程序模拟的按键,放行,不截获
                    return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
                }
            }

            // 第二道防线:_simulating 标志(防 SendKeys 用 journal hook 不带 INJECTED 标志的情况)
            if (_simulating) return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

            if (nCode >= 0 && _isStarted && _usbHidDeviceHandles.Count > 0)
            {
                int msgType = wParam.ToInt32();
                if (msgType == WM_KEYDOWN || msgType == WM_SYSKEYDOWN)
                {
                    int vkCode = Marshal.ReadInt32(lParam);
                    long now = Environment.TickCount;
                    long tickDiff = _lastRawInputTick > 0 ? (now - _lastRawInputTick) : long.MaxValue;
                    bool inWindow = tickDiff < RAW_INPUT_SUPPRESS_MS;

                    // === 核心逻辑:钩子侧捕获扫码数据 ===
                    //
                    // 问题根因:激活后第1个字符通过Raw Input正常到达,
                    // 更新_lastRawInputTick后钩子激活,后续字符的WM_KEYDOWN被截住,
                    // 同时Raw Input的WM_INPUT也停止到达,导致数据丢失。
                    //
                    // 方案:钩子截住时不丢弃,而是存入_hookCaptureBuffer,
                    // 收到Enter时,拼合Raw Input侧第1个字符 + 钩子侧后续字符,
                    // 合成完整条码送入ProcessScanData。

                    if (inWindow || _hookScanActive)
                    {
                        if (vkCode == 0x0D)  // VK_RETURN
                        {
                            // Enter = 扫码结束,拼合Raw Input侧 + 钩子侧数据
                            // 注意:第1个字符可能同时被Raw Input和钩子捕获,需去重
                            // 策略:如果Raw Input侧的字符是钩子侧的前缀,说明重叠,只取钩子侧
                            string rawPart = _usbHidCharBuffer;
                            string hookPart = _hookCaptureBuffer;
                            string fullBarcode;
                            if (rawPart.Length > 0 && hookPart.StartsWith(rawPart))
                            {
                                // Raw Input侧的字符是钩子侧的前缀 → 去重,只取钩子侧
                                fullBarcode = hookPart;
                                Logger.Info($"[HOOK] Enter! DEDUP raw=[{rawPart}] is prefix of hook=[{hookPart}] → full=[{fullBarcode}]");
                            }
                            else if (rawPart.Length > 0 && hookPart.Length > 0)
                            {
                                // 两边都有数据但不是前缀关系 → 拼合(Raw Input可能有第1个,钩子有后续)
                                fullBarcode = rawPart + hookPart;
                                Logger.Info($"[HOOK] Enter! CONCAT raw=[{rawPart}] + hook=[{hookPart}] → full=[{fullBarcode}]");
                            }
                            else
                            {
                                // 只有一侧有数据
                                fullBarcode = rawPart.Length > 0 ? rawPart : hookPart;
                                Logger.Info($"[HOOK] Enter! SINGLE raw=[{rawPart}] hook=[{hookPart}] → full=[{fullBarcode}]");
                            }

                            // 清空缓冲
                            _usbHidCharBuffer = "";
                            _hookCaptureBuffer = "";
                            _hookScanActive = false;
                            // 泄漏判定:只有钩子侧捕获到了数据(hookPart.Length > 0)才算有字符泄漏
                            // 因为第一个字符漏过了钩子,直接到了前台应用
                            // 如果hookPart为空(全部由Raw Input收到),则没有泄漏
                            _hookHadLeak = (hookPart.Length > 0);
                            _lastRawInputTick = 0;
                            _lastProcessedVKey = 0;

                            if (fullBarcode.Length > 0)
                            {
                                // 用BeginInvoke避免在钩子回调中直接处理UI
                                this.BeginInvoke((MethodInvoker)(() =>
                                {
                                    ProcessScanData(fullBarcode);
                                }));
                                _floatingCounter?.SetStatus(FloatingCounter.ScannerStatus.Scanning);
                            }
                            return (IntPtr)1;  // 吞掉Enter
                        }
                        else
                        {
                            // 普通字符 → 存入钩子缓冲
                            if (VkToChar.TryGetValue((ushort)vkCode, out char c))
                            {
                                _hookCaptureBuffer += c;
                                _hookScanActive = true;  // 标记钩子侧正在接收扫码数据
                                Logger.Info($"[HOOK] Captured vk={vkCode} char='{c}' hookBuffer=[{_hookCaptureBuffer}]");
                            }
                            else
                            {
                                Logger.Info($"[HOOK] Unknown vk={vkCode}, hookBuffer=[{_hookCaptureBuffer}]");
                            }
                            return (IntPtr)1;  // 吞掉
                        }
                    }
                    // 不在抑制窗口且不在钩子扫码接收中 → 正常键盘输入,放行
                }
                else if (msgType == WM_KEYUP || msgType == WM_SYSKEYUP)
                {
                    // KEYUP也要拦截(防止应用收到孤立的KEYUP)
                    long now = Environment.TickCount;
                    bool inWindow = _lastRawInputTick > 0 && (now - _lastRawInputTick) < RAW_INPUT_SUPPRESS_MS;
                    if (inWindow || _hookScanActive)
                    {
                        return (IntPtr)1;
                    }
                }
            }
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        // USB HID 扫码枪相关字段
        private HashSet<IntPtr> _usbHidDeviceHandles = new HashSet<IntPtr>(); // 已授权的扫码枪设备句柄集合
        private IntPtr _pendingDeviceHandle = IntPtr.Zero;    // 待确认的新设备句柄(自动发现)
        private string _pendingCharBuffer = "";               // 待确认设备的字符缓冲区
        private long _pendingStartTick = 0;                  // 待确认首字符时间戳
        private string _usbHidDevicePath = "";               // 持久化的设备路径
        private string _usbHidVidPid = "";                   // 持久化的 VID+PID(自动重连用)
        private bool _usbHidWaitingActivation = false;       // 是否在等待扫码激活
        private string _usbHidCharBuffer = "";                // Raw Input 字符拼接 buffer(已授权设备)
        private IntPtr _keyboardHook = IntPtr.Zero;           // 键盘钩子句柄
        private LowLevelKeyboardProc? _keyboardProc;          // 委托引用(防GC回收)

        // [Bug#2] Raw Input 抑制窗口:收到扫码枪字符的时间标记
        private long _lastRawInputTick = 0;
        private ushort _lastProcessedVKey = 0;  // 防HID扫码枪重复按键
        private long _lastVKeyTick = 0;  // 上次处理该vKey的时间
        const int RAW_INPUT_SUPPRESS_MS = 300;  // 抑制窗口 300ms(扫码枪一帧数据约50-100ms)

        // [DIAG] WM_INPUT 计数器,用于诊断激活后Raw Input是否丢失
        private int _wmInputCount = 0;
        private long _wmInputCountResetTick = 0;

        // 钩子截获缓冲区:钩子拦截的扫码枪字符暂存于此
        // 因为激活后第一个字符可能先通过Raw Input到达(_lastRawInputTick=0时不拦截),
        // 后续字符被钩子截住,需要拼合两者才是完整条码
        private string _hookCaptureBuffer = "";
        private bool _hookScanActive = false;  // 钩子侧扫码数据接收中
        private bool _hookHadLeak = false;     // 本次扫码是否有字符泄漏到目标应用(需Backspace补删)
        private volatile bool _simulating = false;  // 正在模拟输出,钩子应放行(防二次捕获)

        // 配色
        static readonly Color BG = Color.FromArgb(240, 243, 249);
        static readonly Color CARD = Color.FromArgb(255, 255, 255);
        static readonly Color ACCENT = Color.FromArgb(59, 130, 246);
        static readonly Color GREEN = Color.FromArgb(34, 197, 94);
        static readonly Color GREEN_HOVER = Color.FromArgb(22, 163, 74);
        static readonly Color TEXT = Color.FromArgb(15, 23, 42);
        static readonly Color TEXT2 = Color.FromArgb(100, 116, 139);
        static readonly Color BORDER = Color.FromArgb(226, 232, 240);

        public MainForm()
        {
            _cfg = AppConfig.Load();
            InitializeComponent();
            this.Text = $"日期提醒 v{UpdateChecker.GetCurrentVersion()}";
            InitTrayIcon();
            _floatingCounter = new FloatingCounter(this);
            _floatingCounter.PositionChanged += () => { SaveWindowPositions(); _cfg.Save(); };

            this.FormClosing += MainForm_FormClosing;
            this.LocationChanged += MainForm_LocationChanged;
            this.Activated += MainForm_Activated;
            this.Resize += MainForm_Resize;
            RestoreWindowPositions();
            UpdateStatusDisplay();

            // 启动 WMI USB 插拔监听(扫码枪热插拔自动重连)
            StartUsbMonitor();

            // 启动时自动连接扫码枪(仅串口模式;HID模式改为点"启动"时扫码激活)
            if (_cfg.ScannerType != ScannerType.UsbHid &&
                (!string.IsNullOrEmpty(_cfg.ScannerDeviceKey) || !string.IsNullOrEmpty(_cfg.ScannerHardwareId)))
            {
                System.Threading.Tasks.Task.Delay(500).ContinueWith(_ =>
                {
                    this.Invoke((MethodInvoker)(() =>
                    {
                        ConnectScanner();
                        if (!_cfg.ScannerConnected)
                        {
                            var key = _cfg.ScannerDeviceKey ?? _cfg.ScannerHardwareId ?? "";
                            bool isPlugged = SerialDeviceHelper.IsDevicePresent(key);

                            if (!isPlugged)
                            {
                                var result = MessageBox.Show(
                                    "扫码枪未插入,请插入扫码枪后重试。\n\n如需重新绑定其他设备,点击「是」打开设置。",
                                    "扫码枪未插入",
                                    MessageBoxButtons.YesNo,
                                    MessageBoxIcon.Warning);
                                if (result == DialogResult.Yes)
                                    OpenSettings();
                            }
                            else
                            {
                                var result = MessageBox.Show(
                                    "扫码枪已插入但连接失败,可能是端口被占用。\n\n是否重新绑定设备?",
                                    "连接失败",
                                    MessageBoxButtons.YesNo,
                                    MessageBoxIcon.Error);
                                if (result == DialogResult.Yes)
                                    OpenSettings();
                            }
                        }
                    }));
                });
            }

            try { int pref = DWMWCP_ROUND; DwmSetWindowAttribute(this.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)); }
            catch { }

            // USB HID 模式:注册 Raw Input(不自动连接,等用户点"启动"时扫码激活)
            if (_cfg.ScannerType == ScannerType.UsbHid)
            {
                RegisterUsbHidInput();
                Logger.Info("USB HID 模式:Raw Input 已注册,等待用户点'启动'后扫码激活");
            }

            // 启动时检查更新(静默,有新版本才提示)
            System.Threading.Tasks.Task.Delay(2000).ContinueWith(_ =>
            {
                this.Invoke((MethodInvoker)(async () =>
                {
                    await CheckForUpdateAsync(false);
                }));
            });
        }

        /// <summary>
        /// 检查更新
        /// </summary>
        private async System.Threading.Tasks.Task CheckForUpdateAsync(bool showNoUpdate)
        {
            var currentVer = UpdateChecker.GetCurrentVersion();
            var info = await UpdateChecker.CheckUpdateAsync(currentVer);
            if (info == null)
            {
                if (showNoUpdate)
                    MessageBox.Show("无法连接更新服务器,请稍后重试。", "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (UpdateChecker.NeedUpdate(currentVer, info.Version))
            {
                var result = MessageBox.Show(
                    $"发现新版本 {info.Version}\n\n" +
                    $"更新内容:\n{info.UpdateLog}\n\n" +
                    $"发布时间:{info.PublishTime}\n\n" +
                    "是否立即更新?",
                    "有新版本",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information);

                if (result == DialogResult.Yes)
                {
                    string appDir = AppDomain.CurrentDomain.BaseDirectory;
                    UpdateChecker.PerformUpdate(info, appDir);
                }
            }
            else if (showNoUpdate)
            {
                MessageBox.Show("当前已是最新版本。", "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        #region 扫码枪连接

        /// <summary>
        /// 连接扫码枪:按配置中的设备特征查找当前 COM 口并打开
        /// </summary>
        public void ConnectScanner()
        {
            Logger.Info($"=== ConnectScanner 开始 (模式: {_cfg.ScannerType}) ===");

            // USB HID 模式:走 Raw Input 注册,不走串口
            if (_cfg.ScannerType == ScannerType.UsbHid)
            {
                ConnectUsbHid();
                return;
            }

            Logger.Info($"配置: DeviceKey={_cfg.ScannerDeviceKey}, HardwareId={_cfg.ScannerHardwareId}, ComPort={_cfg.ComPort}, BaudRate={_cfg.BaudRate}");

            DisconnectScanner();

            string? comPort = null;

            // 优先按 InstanceId 查找
            if (!string.IsNullOrEmpty(_cfg.ScannerDeviceKey))
            {
                comPort = SerialDeviceHelper.FindComPortByDeviceKey(_cfg.ScannerDeviceKey);
                Logger.Info($"按 DeviceKey 查找: {comPort ?? "未找到"}");
            }

            // 兜底按 HardwareId 查找
            if (string.IsNullOrEmpty(comPort) && !string.IsNullOrEmpty(_cfg.ScannerHardwareId))
            {
                comPort = SerialDeviceHelper.FindComPortByDeviceKey(_cfg.ScannerHardwareId);
                Logger.Info($"按 HardwareId 查找: {comPort ?? "未找到"}");
            }

            // 最后兜底用保存的 COM 口(不依赖 WMI,直接检查系统端口)
            if (string.IsNullOrEmpty(comPort) && !string.IsNullOrEmpty(_cfg.ComPort))
            {
                var ports = SerialPort.GetPortNames();
                if (ports.Contains(_cfg.ComPort, StringComparer.OrdinalIgnoreCase))
                    comPort = _cfg.ComPort;
                Logger.Info($"按配置的 COM 口查找: {comPort ?? "未找到"}(系统端口: {string.Join(",", ports)})");
            }

            if (string.IsNullOrEmpty(comPort))
            {
                Logger.Warn("未找到可用的 COM 口");
                _cfg.ScannerConnected = false;
                _cfg.Save();
                UpdateStatusDisplay();
                throw new IOException("未找到扫码枪对应的 COM 口,请检查扫码枪是否已连接");
            }

            try
            {
                _serialPort = new SerialPort(comPort, _cfg.BaudRate)
                {
                    DataBits = 8,
                    Parity = Parity.None,
                    StopBits = StopBits.One,
                    ReadTimeout = 500,
                    WriteTimeout = 500
                };
                _serialPort.DataReceived += SerialPort_DataReceived;
                _serialPort.ErrorReceived += SerialPort_ErrorReceived;
                _serialPort.Open();

                _cfg.ComPort = comPort;
                _cfg.ScannerConnected = true;
                _cfg.Save();
                UpdateStatusDisplay();
            }
            catch (Exception ex)
            {
                Logger.Error("串口打开失败", ex);
                _cfg.ScannerConnected = false;
                _cfg.Save();
                _serialPort?.Dispose();
                _serialPort = null;
                UpdateStatusDisplay();
                throw new IOException($"扫码枪 COM 口打开失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// USB HID 模式连接:注册 Raw Input 并进入"等待扫码激活"状态
        /// 每次点启动都需扫一次码获取设备句柄,不依赖保存的路径恢复
        /// </summary>
        /// <summary>
        /// USB HID 连接入口:按优先级自动尝试重连
        /// 优先级:VID+PID → 设备路径 → 等待扫码激活
        /// </summary>
        private void ConnectUsbHid()
        {
            Logger.Info("=== ConnectUsbHid 开始(优先级绑定模式)===");
            DisconnectScanner();
            RegisterUsbHidInput();

            _usbHidVidPid = _cfg.UsbHidVID;
            string savedPath = _cfg.UsbHidDevicePath;

            // === 优先级1:VID+PID 自动重连 ===
            if (!string.IsNullOrEmpty(_usbHidVidPid))
            {
                IntPtr h = FindRawInputDeviceByVidPid(_usbHidVidPid);
                if (h != IntPtr.Zero)
                {
                    Logger.Info($"[P1] VID+PID 自动匹配成功: {_usbHidVidPid} → hDevice={h}");
                    ActivateUsbHidScanner(h, "");
                    return;
                }
                else
                {
                    Logger.Info($"[P1] VID+PID 匹配失败: {_usbHidVidPid},尝试 P2...");
                }
            }

            // === 优先级2:设备路径重连 ===
            if (!string.IsNullOrEmpty(savedPath) && savedPath != "WAITING_FOR_FIRST_SCAN")
            {
                IntPtr h = FindRawInputDeviceByPath(savedPath);
                if (h != IntPtr.Zero)
                {
                    Logger.Info($"[P2] 路径自动匹配成功: {savedPath.Substring(0, Math.Min(60, savedPath.Length))}...");
                    // 补充 VID+PID(如果之前没有)
                    if (string.IsNullOrEmpty(_cfg.UsbHidVID))
                    {
                        string vp = ExtractVidPidFromPath(savedPath);
                        if (!string.IsNullOrEmpty(vp))
                        {
                            _cfg.UsbHidVID = vp;
                            _cfg.Save();
                            Logger.Info($"[P2] 补充保存 VID+PID: {vp}");
                        }
                    }
                    ActivateUsbHidScanner(h, "");
                    return;
                }
                else
                {
                    Logger.Info($"[P2] 路径匹配失败: {savedPath.Substring(0, Math.Min(60, savedPath.Length))}...");
                }
            }

            // === 优先级3+4:均失败,进入等待扫码激活 ===
            Logger.Info("[P3/P4] 均失败,进入等待扫码激活状态");
            _usbHidWaitingActivation = true;
            _usbHidDeviceHandles.Clear();
            _usbHidCharBuffer = "";
            _pendingCharBuffer = "";
            _pendingDeviceHandle = IntPtr.Zero;
            _cfg.ScannerConnected = false;
            _cfg.Save();
            UpdateStatusDisplay();
        }

        /// <summary>
        /// 按 VID+PID 查找 Raw Input 设备句柄
        /// </summary>
        private IntPtr FindRawInputDeviceByVidPid(string vidPid)
        {
            if (string.IsNullOrEmpty(vidPid)) return IntPtr.Zero;

            uint devCount = 0;
            GetRawInputDeviceList(IntPtr.Zero, ref devCount, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST)));
            if (devCount == 0) return IntPtr.Zero;

            IntPtr buf = Marshal.AllocHGlobal((int)(devCount * Marshal.SizeOf(typeof(RAWINPUTDEVICELIST))));
            try
            {
                GetRawInputDeviceList(buf, ref devCount, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST)));
                for (int i = 0; i < devCount; i++)
                {
                    var dev = Marshal.PtrToStructure<RAWINPUTDEVICELIST>(buf + i * Marshal.SizeOf(typeof(RAWINPUTDEVICELIST)));
                    if (dev.dwType != RIM_TYPEKEYBOARD) continue;

                    uint size = 0;
                    GetRawInputDeviceInfo(dev.hDevice, 0x20000005, IntPtr.Zero, ref size);
                    if (size == 0) continue;

                    IntPtr nameBuf = Marshal.AllocHGlobal((int)size);
                    try
                    {
                        GetRawInputDeviceInfo(dev.hDevice, 0x20000005, nameBuf, ref size);
                        string? path = Marshal.PtrToStringAuto(nameBuf);
                        if (string.IsNullOrEmpty(path)) continue;

                        string foundVidPid = ExtractVidPidFromPath(path);
                        if (!string.IsNullOrEmpty(foundVidPid) &&
                            string.Equals(foundVidPid, vidPid, StringComparison.OrdinalIgnoreCase))
                        {
                            Logger.Info($"[FindRawInputDeviceByVidPid] 匹配: {vidPid} → hDevice={dev.hDevice}, path={path}");
                            return dev.hDevice;
                        }
                    }
                    finally { Marshal.FreeHGlobal(nameBuf); }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 从设备路径中提取 VID+PID 字符串(用于自动重连)
        /// </summary>
        private string ExtractVidPidFromPath(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath)) return "";
            // 格式1: \?\hid#vid_0c2e&pid_0005#...  (Windows 路径风格)
            var m = System.Text.RegularExpressions.Regex.Match(devicePath, @"vid_([0-9a-fA-F]{4})&pid_([0-9a-fA-F]{4})",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) return $"VID_{m.Groups[1].Value.ToUpper()}&PID_{m.Groups[2].Value.ToUpper()}";
            // 格式2: VID_0C2E&PID_0005
            m = System.Text.RegularExpressions.Regex.Match(devicePath, @"(VID_[0-9A-F]{4}&PID_[0-9A-F]{4})",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.ToUpper();
            return "";
        }

        /// <summary>
        /// 遍历 Raw Input 设备列表,找出所有 HID 键盘设备的 VID+PID
        /// 用途:当 WM_INPUT 的 hDevice 无法被 GetRawInputDeviceInfo 识别时,
        ///       通过设备列表枚举获取 VID+PID(设备列表的句柄可以正常查询)
        /// </summary>
        private string FindVidPidFromDeviceList()
        {
            uint devCount = 0;
            GetRawInputDeviceList(IntPtr.Zero, ref devCount, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST)));
            if (devCount == 0) return "";

            IntPtr buf = Marshal.AllocHGlobal((int)(devCount * Marshal.SizeOf(typeof(RAWINPUTDEVICELIST))));
            try
            {
                GetRawInputDeviceList(buf, ref devCount, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST)));
                var candidates = new List<string>();  // 所有带 VID+PID 的 HID 键盘

                for (int i = 0; i < devCount; i++)
                {
                    var dev = Marshal.PtrToStructure<RAWINPUTDEVICELIST>(buf + i * Marshal.SizeOf(typeof(RAWINPUTDEVICELIST)));
                    if (dev.dwType != RIM_TYPEKEYBOARD) continue;

                    uint size = 0;
                    GetRawInputDeviceInfo(dev.hDevice, 0x20000005, IntPtr.Zero, ref size);
                    if (size == 0) continue;

                    IntPtr nameBuf = Marshal.AllocHGlobal((int)size);
                    try
                    {
                        GetRawInputDeviceInfo(dev.hDevice, 0x20000005, nameBuf, ref size);
                        string? path = Marshal.PtrToStringAuto(nameBuf);
                        if (string.IsNullOrEmpty(path)) continue;

                        string vp = ExtractVidPidFromPath(path);
                        if (!string.IsNullOrEmpty(vp))
                        {
                            Logger.Info($"[FindVidPid] 发现 HID 键盘: VID+PID={vp}  path={path.Substring(0, Math.Min(80, path.Length))}");
                            candidates.Add(vp);
                        }
                    }
                    finally { Marshal.FreeHGlobal(nameBuf); }
                }

                if (candidates.Count == 0)
                {
                    Logger.Info("[FindVidPid] 未找到任何带 VID+PID 的 HID 键盘设备");
                    return "";
                }
                else if (candidates.Count == 1)
                {
                    Logger.Info($"[FindVidPid] 唯一 HID 键盘,自动选择: {candidates[0]}");
                    return candidates[0];
                }
                else
                {
                    // 多个 HID 键盘有 VID+PID,排除常见内置键盘 VID
                    // 常见内置键盘:VID_045E(Microsoft), VID_046D(Logitech), VID_048D(ITE)
                    var excludeVids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "045E", "046D", "048D", "1D2C", "1A2C", "0B05", "1038"
                    };
                    var filtered = candidates.Where(c =>
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(c, @"VID_([0-9A-F]{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        return !m.Success || !excludeVids.Contains(m.Groups[1].Value.ToUpper());
                    }).ToList();

                    if (filtered.Count == 1)
                    {
                        Logger.Info($"[FindVidPid] 过滤内置键盘后,自动选择: {filtered[0]}");
                        return filtered[0];
                    }
                    else if (filtered.Count > 1)
                    {
                        Logger.Info($"[FindVidPid] 多个候选 VID+PID: {string.Join(", ", filtered)},选择第一个: {filtered[0]}");
                        return filtered[0];
                    }
                    else
                    {
                        Logger.Info($"[FindVidPid] 过滤后无候选,使用原始第一个: {candidates[0]}");
                        return candidates[0];
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// <summary>
        /// [Bug#1修复] 枚举 Raw Input 设备列表,按设备路径查找匹配的设备句柄
        /// </summary>
        /// <summary>
        /// 按路径查找 Raw Input 设备句柄
        /// 当路径为空或以 "Handle=" 开头时(路径获取失败的回退),尝试按Handle匹配
        /// </summary>
        private IntPtr FindRawInputDeviceByPath(string targetPath)
        {
            if (string.IsNullOrEmpty(targetPath)) return IntPtr.Zero;

            bool matchByHandle = targetPath.StartsWith("Handle=", StringComparison.OrdinalIgnoreCase);
            IntPtr targetHandle = IntPtr.Zero;
            if (matchByHandle)
            {
                long hVal;
                if (!long.TryParse(targetPath.Substring(7), out hVal)) return IntPtr.Zero;
                targetHandle = new IntPtr(hVal);
                Logger.Info(string.Format("[FindDevice] Handle 匹配模式: {0}", targetHandle));
            }

            uint devCount = 0;
            GetRawInputDeviceList(IntPtr.Zero, ref devCount, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST)));
            if (devCount == 0) return IntPtr.Zero;

            IntPtr buf = Marshal.AllocHGlobal((int)(devCount * Marshal.SizeOf(typeof(RAWINPUTDEVICELIST))));
            try
            {
                GetRawInputDeviceList(buf, ref devCount, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST)));
                for (int i = 0; i < devCount; i++)
                {
                    var dev = Marshal.PtrToStructure<RAWINPUTDEVICELIST>(buf + i * Marshal.SizeOf(typeof(RAWINPUTDEVICELIST)));
                    if (dev.dwType != RIM_TYPEKEYBOARD) continue;

                    if (matchByHandle)
                    {
                        // Handle 直接比较(仅在同一会话内有效)
                        if (dev.hDevice == targetHandle) return dev.hDevice;
                        continue;
                    }

                    // 正常路径匹配
                    uint size = 0;
            Logger.Info("[RAW] ProcessRawInput called");
                    GetRawInputDeviceInfo(dev.hDevice, 0x20000005 /* RIDI_DEVICENAME */, IntPtr.Zero, ref size);
                    if (size == 0) continue;

                    IntPtr nameBuf = Marshal.AllocHGlobal((int)size);
                    try
                    {
                        GetRawInputDeviceInfo(dev.hDevice, 0x20000005, nameBuf, ref size);
                        string? path = Marshal.PtrToStringAuto(nameBuf);
                        if (!string.IsNullOrEmpty(path) &&
                            string.Equals(path, targetPath, StringComparison.OrdinalIgnoreCase))
                        {
                            return dev.hDevice;
                        }
                    }
                    finally { Marshal.FreeHGlobal(nameBuf); }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 串口错误/断开处理
        /// </summary>
        private void SerialPort_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
            this.Invoke((MethodInvoker)(() =>
            {
                DisconnectScanner();
                UpdateStatusDisplay();
                StartReconnectTimer();
            }));
        }

        private System.Windows.Forms.Timer? _reconnectTimer;
        private ManagementEventWatcher? _usbWatcher;

        /// <summary>
        /// 启动自动重连定时器(每5秒尝试一次,连上后停止)
        /// </summary>
        private void StartReconnectTimer()
        {
            if (_reconnectTimer != null) return; // 已在重连中

            _reconnectTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            _reconnectTimer.Tick += (s, _e) =>
            {
                // [Bug#5修复] 重连逻辑同时覆盖串口和HID模式
                bool hasSerialCfg = !string.IsNullOrEmpty(_cfg.ScannerDeviceKey) || !string.IsNullOrEmpty(_cfg.ScannerHardwareId) || !string.IsNullOrEmpty(_cfg.ComPort);
                bool hasHidCfg = _cfg.ScannerType == ScannerType.UsbHid && !string.IsNullOrEmpty(_cfg.UsbHidDevicePath) && _cfg.UsbHidDevicePath != "WAITING_FOR_FIRST_SCAN";
                if (hasSerialCfg || hasHidCfg)
                {
                    try
                    {
                        ConnectScanner();
                    }
                    catch { /* 连接失败,等下次重试 */ }
                    // 判断是否重连成功
                    bool reconnected = _cfg.ScannerType == ScannerType.UsbHid
                        ? _usbHidDeviceHandles.Count > 0
                        : _serialPort != null && _serialPort.IsOpen;
                    if (_cfg.ScannerConnected && reconnected)
                    {
                        // 连上了,停止重连
                        _reconnectTimer.Stop();
                        _reconnectTimer.Dispose();
                        _reconnectTimer = null;
                        UpdateStatusDisplay();
                    }
                }
                else
                {
                    _reconnectTimer.Stop();
                    _reconnectTimer.Dispose();
                    _reconnectTimer = null;
                }
            };
            _reconnectTimer.Start();
        }

        /// <summary>
        /// 启动 WMI USB 插拔监听:设备插入时自动尝试重连
        /// </summary>
        private void StartUsbMonitor()
        {
            StopUsbMonitor();
            try
            {
                // 监听 USB 设备插入事件
                var insertQuery = new WqlEventQuery("SELECT * FROM __InstanceCreationEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_PnPEntity'");
                _usbWatcher = new ManagementEventWatcher(insertQuery);
                _usbWatcher.EventArrived += (s, e) =>
                {
                    // [Bug#6修复] USB插拔监听同时覆盖串口和HID模式
                    bool hasSerialCfg = !string.IsNullOrEmpty(_cfg.ScannerDeviceKey) || !string.IsNullOrEmpty(_cfg.ScannerHardwareId);
                    bool hasHidCfg = _cfg.ScannerType == ScannerType.UsbHid && !string.IsNullOrEmpty(_cfg.UsbHidDevicePath) && _cfg.UsbHidDevicePath != "WAITING_FOR_FIRST_SCAN";
                    if (!hasSerialCfg && !hasHidCfg)
                        return;
                    // 串口已连着就跳过
                    if (_cfg.ScannerType != ScannerType.UsbHid && _serialPort != null && _serialPort.IsOpen)
                        return;
                    // HID已连着就跳过
                    if (_cfg.ScannerType == ScannerType.UsbHid && _usbHidDeviceHandles.Count > 0)
                        return;

                    // 延迟一点让驱动加载完
                    System.Threading.Tasks.Task.Delay(1500).ContinueWith(_ =>
                    {
                        this.Invoke((MethodInvoker)(() =>
                        {
                            ConnectScanner();
                            if (_cfg.ScannerConnected)
                            {
                                // 连上了,停止轮询重连
                                if (_reconnectTimer != null)
                                {
                                    _reconnectTimer.Stop();
                                    _reconnectTimer.Dispose();
                                    _reconnectTimer = null;
                                }
                                UpdateStatusDisplay();
                            }
                        }));
                    });
                };
                _usbWatcher.Start();
            }
            catch { /* WMI 不可用则静默忽略 */ }
        }

        private void StopUsbMonitor()
        {
            if (_usbWatcher != null)
            {
                try { _usbWatcher.Stop(); } catch { }
                _usbWatcher.Dispose();
                _usbWatcher = null;
            }
        }

        /// <summary>
        /// 断开扫码枪
        /// </summary>
        public void DisconnectScanner()
        {
            if (_serialPort != null)
            {
                try
                {
                    if (_serialPort.IsOpen) _serialPort.Close();
                }
                catch { }
                _serialPort.DataReceived -= SerialPort_DataReceived;
                _serialPort.Dispose();
                _serialPort = null;
            }
            _cfg.ScannerConnected = false;
            _cfg.Save();
            _scanBuffer = "";
            Logger.Info("扫码枪已断开,配置已保存");
        }

        /// <summary>
        /// 串口数据接收:累加到 buffer,遇换行符则处理一条完整扫码
        /// </summary>
        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if (_serialPort == null || !_serialPort.IsOpen)
            {
                Logger.Warn("串口未打开或已关闭");
                return;
            }

            try
            {
                var data = _serialPort.ReadExisting();
                Logger.ScanData(data);
                _scanBuffer += data;

                // 扫码枪通常以 \r 或 \r\n 结尾
                while (_scanBuffer.Contains('\r') || _scanBuffer.Contains('\n'))
                {
                    int idx = _scanBuffer.IndexOfAny(new[] { '\r', '\n' });
                    if (idx < 0) break;

                    var line = _scanBuffer.Substring(0, idx).Trim();
                    _scanBuffer = _scanBuffer.Substring(idx + 1);
                    // 跳过紧跟的 \n(\r\n 情况)
                    if (_scanBuffer.StartsWith("\n"))
                        _scanBuffer = _scanBuffer.Substring(1);

                    if (!string.IsNullOrEmpty(line))
                    {
                        Logger.Info($"处理完整扫码数据: [{line}]");
                        ProcessScanData(line);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("串口数据接收异常", ex);
            }
        }

        /// <summary>
        /// 处理一条完整的扫码数据
        /// </summary>
        private void ProcessScanData(string rawData)
        {
            Logger.Info($"[SCAN] ProcessScanData rawData=[{rawData}] isStarted={_isStarted} password=[{_cfg.ScannerPassword ?? "(none)"}]");

            // 【计数】非加密格式不计数、不显示悬浮窗数字
            // 密码绑定:扫描 mima:XXXX 格式的二维码
            if (rawData.StartsWith("mima:", StringComparison.OrdinalIgnoreCase))
            {
                var password = rawData.Substring(5).Trim();
                this.Invoke((MethodInvoker)(() =>
                {
                    _cfg.ScannerPassword = password;
                    _cfg.Save();
                    _floatingCounter?.SetStatus(FloatingCounter.ScannerStatus.PasswordBound);
                    UpdateStatusDisplay();
                }));
                return;
            }

            // 判断是否为加密格式:数字-]数字/
            string output = rawData;
            var match = Regex.Match(rawData, @"^(\d+)-[\]】]?(\d+)[/、,]?$");
            Logger.Info($"[SCAN] regex Success={match.Success} g1=[{match.Groups[1].Value}] g2=[{match.Groups[2].Value}]");
            if (match.Success && !string.IsNullOrEmpty(_cfg.ScannerPassword) && _cfg.ScannerPassword.Length >= 4)
            {
                // 【计数】加密格式才计数并显示悬浮窗
                _scanCount++;
                this.Invoke((MethodInvoker)(() =>
                {
                    _floatingCounter?.UpdateCount(_scanCount);
                    _floatingCounter?.SetStatus(FloatingCounter.ScannerStatus.Scanning);
                }));

                // 加密格式 → 解密 -] 后面的数字(限期日期)
                string barcode = match.Groups[1].Value;
                string encryptedNum = match.Groups[2].Value;
                string decryptedNum = BigDecrypt(encryptedNum, _cfg.ScannerPassword);
                Logger.Info($"[SCAN] Decrypt: encrypted=[{encryptedNum}] decrypted=[{decryptedNum}]");

                // 解密后的数字是限期日期(YYYYMMDD格式)
                if (DateTime.TryParseExact(decryptedNum, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime expiryDate))
                {
                    double daysLeft = (expiryDate - DateTime.Today).TotalDays;
                    int daysLeftInt = (int)daysLeft;
                    _floatingCounter?.SetDaysLeft(daysLeftInt);
                    if (daysLeft >= 2)
                    {
                        // 剩余≥2天 → 输出商品编号到目标程序
                        output = barcode;
                    }
                    else
                    {
                        // 剩余<2天 → 停止模拟+弹窗警告
                        _expiryBlocked = true;
                        this.Invoke((MethodInvoker)(() =>
                        {
                            AlarmSoundProvider.PlayAsync(_cfg.AlarmSound);
                            string expiryStr = expiryDate.ToString("yyyy年MM月dd日");
                            string msg = daysLeft < 0
                                ? $"商品已过期!\n到期日期:{expiryStr}(已过期{Math.Abs((int)daysLeft)}天)\n\n将停止模拟输入。"
                                : $"商品即将过期!\n到期日期:{expiryStr}(剩余{(int)daysLeft}天)\n\n将停止模拟输入。";
                            MessageBox.Show(this, msg, "过期警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            _expiryBlocked = false;
                        }));
                        // 不 return,弹窗确认后继续模拟输入商品编号
                    }
                }
                // 解析日期失败 → 原样输出(fall through)
            }
            else
            {
                // 非加密格式 → 清空悬浮窗天数显示
                _floatingCounter?.SetDaysLeft(null);
            }
            // 其他格式 → 原样模拟按键

            // 过期弹窗阻塞中 → 跳过所有模拟
            if (_expiryBlocked) return;

            // 未点"启动" → 不处理
            Logger.Warn($"[SCAN] DISCARDED! Not started. rawData=[{rawData}]");
            if (!_isStarted) { return; }

            // 模拟键盘输入到目标程序
            // HID模式下:第1个字符通过WM_KEYDOWN泄漏到目标应用,需要先Backspace删掉
            bool needBackspace = _cfg.ScannerType == ScannerType.UsbHid && _hookHadLeak;
            Logger.Info($"模拟键盘输入: [{output}], 目标程序: {_cfg.SoftwareName ?? "无"}, HID补Backspace={needBackspace}");
            Logger.Info($"[SCAN] OUTPUT: output=[{output}] isStarted={_isStarted} target=[{_cfg.SoftwareName ?? "(none)"}] mode={_cfg.OutputMode}");
            Logger.Info($"[SCAN] OUTPUT: output=[{output}] isStarted={_isStarted} target=[{_cfg.SoftwareName ?? "(none)"}] mode={_cfg.OutputMode}");
            Logger.Info($"[SCAN] OUTPUT: output=[{output}] isStarted={_isStarted} target=[{_cfg.SoftwareName ?? "(none)"}] mode={_cfg.OutputMode} backspace={needBackspace}");

            // 【关键修复】模拟输出前卸载钩子,彻底避免钩子二次捕获模拟按键
            // 之前用_simulating标志不够可靠,钩子回调中BeginInvoke和SendKeys的
            // 消息泵重入可能导致标志位状态不一致
            bool hadHook = (_keyboardHook != IntPtr.Zero);
            if (hadHook) UninstallKeyboardHook();
            _simulating = true;
            try
            {
                this.Invoke((MethodInvoker)(() =>
                {
                    if (needBackspace)
                    {
                        // 先发送Backspace删掉泄漏的第1个字符
                        // 不能用剪贴板模式粘贴退格符,必须用键盘模拟
                        try
                        {
                            SendKeys.SendWait("{BS}");
                            Logger.Info("[BACKSPACE] 已发送Backspace删除泄漏字符");
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn($"[BACKSPACE] SendKeys失败: {ex.Message}");
                        }
                    }
                    KeyboardSimulator.TypeToProcess(output + "\r", _cfg.SoftwareName ?? "", _cfg.OutputMode);
                }));
            }
            finally
            {
                _simulating = false;
                _hookHadLeak = false;
                // 模拟结束后重新安装钩子
                if (hadHook) InstallKeyboardHook();
            }

            // [Bug#3修复] 短暂延迟后恢复状态 - 同时检查串口和HID模式
            System.Threading.Tasks.Task.Delay(800).ContinueWith(_ =>
            {
                this.Invoke((MethodInvoker)(() =>
                {
                    bool isConnected = _cfg.ScannerType == ScannerType.UsbHid
                        ? _usbHidDeviceHandles.Count > 0
                        : _serialPort != null && _serialPort.IsOpen;
                    _floatingCounter?.SetStatus(isConnected
                        ? FloatingCounter.ScannerStatus.Connected
                        : FloatingCounter.ScannerStatus.Disconnected);
                }));
            });
        }

        /// <summary>
        /// 大数解密:逆序做逆运算(减代替加,除代替乘)
        /// 加密流程:+密1 → ×密2 → +2 → ×密3 → +密4
        /// 解密流程:-密4 → ÷密3 → -2 → ÷密2 → -密1
        /// </summary>
        private string BigDecrypt(string encrypted, string password)
        {
            BigInteger result = BigInteger.Parse(encrypted);
            int m1 = password[0] - '0';
            int m2 = password[1] - '0';
            int m3 = password[2] - '0';
            int m4 = password[3] - '0';

            // 逆步骤5: 减密4
            result -= m4;
            // 逆步骤4: 除密3
            result /= m3;
            // 逆步骤3: 减固定值2
            result -= 2;
            // 逆步骤2: 除密2
            result /= m2;
            // 逆步骤1: 减密1
            result -= m1;

            return result.ToString();
        }

        #region USB HID 扫码枪(Raw Input)

        /// <summary>
        /// 注册 Raw Input 接收键盘事件(USB HID 扫码枪专用)
        /// </summary>
        private void RegisterUsbHidInput()
        {
            RAWINPUTDEVICE[] devices = new RAWINPUTDEVICE[1];
            devices[0] = new RAWINPUTDEVICE
            {
                usUsagePage = 0x01,        // 通用桌面设备
                usUsage = 0x06,            // 键盘
                dwFlags = RIDEV_INPUTSINK, // 即使不是前台窗口也接收输入
                hwndTarget = this.Handle
            };
            if (!RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))))
            {
                Logger.Warn("Raw Input 注册失败,USB HID 模式可能无法正常工作");
            }
            else
            {
                Logger.Info("Raw Input 已注册,USB HID 扫码枪监听中");
            }

            // 若已有保存的设备路径,尝试恢复(句柄会变,仅作记录)
            _usbHidDevicePath = _cfg.UsbHidDevicePath;
        }

        /// <summary>
        /// 拦截 WM_INPUT 消息,处理 Raw Input 数据
        /// </summary>
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_INPUT && _cfg.ScannerType == ScannerType.UsbHid && !_simulating)
            {
                // [DIAG] WM_INPUT 计数器
                long now = Environment.TickCount;
                if (now - _wmInputCountResetTick > 2000)  // 2秒窗口重置
                {
                    if (_wmInputCount > 0)
                        Logger.Info($"[DIAG] WM_INPUT burst: {_wmInputCount} messages in 2s window");
                    _wmInputCount = 0;
                    _wmInputCountResetTick = now;
                }
                _wmInputCount++;
                Logger.Info($"[WNDPROC] WM_INPUT #{_wmInputCount}, started={_isStarted}, handles={_usbHidDeviceHandles.Count}, waiting={_usbHidWaitingActivation}");
                ProcessRawInput(m.LParam);
            }
            base.WndProc(ref m);
        }

        /// <summary>
        /// 处理一条 Raw Input 数据
        /// </summary>
        private void ProcessRawInput(IntPtr hRawInput)
        {
            uint size = 0;
            Logger.Info("[RAW] ProcessRawInput called");
            GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER)));
            if (size == 0) return;

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                GetRawInputData(hRawInput, RID_INPUT, buffer, ref size, (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER)));
                RAWINPUT raw = Marshal.PtrToStructure<RAWINPUT>(buffer);

                // 只处理键盘数据
                if (raw.header.dwType != RIM_TYPEKEYBOARD) return;

                IntPtr deviceHandle = raw.header.hDevice;
                ushort vKey = raw.data.Keyboard.VKey;
                ushort flags = raw.data.Keyboard.Flags;
                bool isKnownDevice = _usbHidDeviceHandles.Contains(deviceHandle);
                Logger.Info($"[RAW] device={deviceHandle} vKey={vKey} flags=0x{flags:X2} known={isKnownDevice} waiting={_usbHidWaitingActivation}");

                // [Bug修复] 如果不是已授权设备且不是在等待激活,则不处理
                if (!isKnownDevice && !_usbHidWaitingActivation) return;

                // 过滤 KeyUp 事件:标准 KeyUp 的 flags 含 RI_KEY_BREAK(0x80)
                // 但部分HID扫码枪不发0x80,改用时间间隔过滤:
                // 扫码枪同一字符连续两次事件间隔<10ms,人类不可能这么快 → 一定是重复
                long nowTick = Environment.TickCount;
                bool isKeyBreak = (flags & 0x80) != 0;  // 标准 KeyUp
                if (isKeyBreak) return;  // 标准 KeyUp,直接跳过

                if (vKey == 0x0D)  // VK_RETURN
                {
                    // 检查已授权设备的主缓冲区
                    // 注意:激活后Raw Input可能只收到第1个字符,后续字符被钩子捕获
                    // 如果钩子侧正在接收数据(_hookScanActive),Raw Input侧不应独自处理
                    if (_hookScanActive)
                    {
                        Logger.Info($"[RAW] Enter skipped - hook is capturing, rawBuffer=[{_usbHidCharBuffer}] hookBuffer=[{_hookCaptureBuffer}]");
                        // Raw Input的Enter不处理,等钩子侧的Enter来拼合
                        // 但更新_lastRawInputTick让钩子抑制窗口继续
                        _lastRawInputTick = Environment.TickCount;
                        return;
                    }

                    if (_usbHidCharBuffer.Length > 0)
                    {
                        string barcode = _usbHidCharBuffer;
            Logger.Info($"[RAW] Enter fired barcode=[{barcode}] waiting={_usbHidWaitingActivation} authorized={_usbHidDeviceHandles.Count}");
                        _usbHidCharBuffer = "";
                        _lastProcessedVKey = 0;  // Enter后重置,允许下次扫码首键

                        // 等待激活阶段:扫码 → 记录设备句柄,确认激活
                        if (_usbHidWaitingActivation)
                        {
                            ActivateUsbHidScanner(deviceHandle, barcode);
                            return;
                        }

                        // 已激活:交给处理流程
                        _lastRawInputTick = Environment.TickCount;
                        this.Invoke((MethodInvoker)(() => ProcessScanData(barcode)));
                        _floatingCounter?.SetStatus(FloatingCounter.ScannerStatus.Scanning);
                    }
                    // [Bug#2修复] 已禁用自动发现,不再处理待确认设备的 Enter
                    // 原因:自动发现会把快速打字的普通键盘误认为扫码枪并自动授权
                    // else if (_pendingCharBuffer.Length > 0) { ... }
                    return;
                }

                // 字符按键处理
                // 此HID扫码枪每个按键发两个事件:flags=0x00 + flags=0x01(RI_KEY_E0)
                // flags=0x01 是扩展键重复事件,数字键不需要E0标志 → 直接跳过
                // flags含0x80(RI_KEY_BREAK)的标准KeyUp已在上方过滤
                if ((flags & 0x01) != 0)
                {
                    Logger.Info($"[RAW] E0-DEDUP skip vKey={vKey} flags=0x{flags:X2}");
                    return;
                }
                _lastProcessedVKey = vKey;
                Logger.Info($"[RAW] CHAR vKey={vKey} flags=0x{flags:X2} buffer=[{_usbHidCharBuffer}]");

                if (vKey != 0x00)
                {
                    // ===== 等待激活阶段:任何键盘设备的字符都积累 =====
                    if (_usbHidWaitingActivation)
                    {
                        if (VkToChar.TryGetValue(vKey, out char c))
                        {
                            _usbHidCharBuffer += c;
                            _lastRawInputTick = Environment.TickCount;
                        }
                        return;
                    }

                    // ===== 已激活阶段:已授权设备 → 主缓冲区 =====
                    if (_usbHidDeviceHandles.Contains(deviceHandle))
                    {
                        if (VkToChar.TryGetValue(vKey, out char c))
                        {
                            // 如果钩子侧正在捕获数据,Raw Input侧的字符仍记录到_usbHidCharBuffer
                            // 但只作为拼合参考,最终由钩子侧的Enter统一触发ProcessScanData
                            _usbHidCharBuffer += c;
                            _lastRawInputTick = Environment.TickCount;
                            Logger.Info($"[RAW] CHAR added to rawBuffer, raw=[{_usbHidCharBuffer}] hook=[{_hookCaptureBuffer}] scanActive={_hookScanActive}");
                        }
                    }
                    // ===== 已激活阶段:未知设备 → 忽略(已禁用自动发现) =====
                    // [Bug#2修复] 未知设备的输入不再积累到待确认缓冲区,也不更新钩子时间标记
                    // 原因:自动发现机制会把快速打字的普通键盘误认为扫码枪,
                    // 且 _lastRawInputTick 更新会激活键盘钩子,导致普通键盘输入被截获
                    else
                    {
                        Logger.Info($"[RAW] Ignored unknown device={deviceHandle}, vKey={vKey}");
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>
        /// 扫码枪激活:记录设备句柄,安装钩子,进入工作状态
        /// </summary>
        private void ActivateUsbHidScanner(IntPtr deviceHandle, string testBarcode)
        {
            _usbHidDeviceHandles.Add(deviceHandle);
            _usbHidCharBuffer = "";
            _hookCaptureBuffer = "";  // 清空钩子侧缓冲
            _hookScanActive = false;
            _hookHadLeak = false;       // 激活时清零泄漏标记
            _usbHidWaitingActivation = false;
            _lastRawInputTick = 0;  // 重置时间标记
            _cfg.ScannerConnected = true;

            // === 获取设备路径和 VID+PID ===
            // 方法1:用 WM_INPUT 的 hDevice 直接查询(部分系统可用)
            uint size = 0;
            Logger.Info($"[ACTIVATE] 尝试 GetRawInputDeviceInfo(WM_INPUT handle={deviceHandle})...");
            GetRawInputDeviceInfo(deviceHandle, 0x20000005, IntPtr.Zero, ref size); // RIDI_DEVICENAME
            if (size > 0)
            {
                IntPtr nameBuf = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (GetRawInputDeviceInfo(deviceHandle, 0x20000005, nameBuf, ref size) > 0)
                    {
                        _usbHidDevicePath = Marshal.PtrToStringAuto(nameBuf) ?? "";
                        _cfg.UsbHidDevicePath = _usbHidDevicePath;
                        _cfg.UsbHidDeviceName = GetDeviceDisplayName(deviceHandle);

                        string vp = ExtractVidPidFromPath(_usbHidDevicePath);
                        if (!string.IsNullOrEmpty(vp))
                        {
                            _cfg.UsbHidVID = vp;
                            _cfg.UsbHidPID = vp.Split('&').Length > 1 ? vp.Split('&')[1] : "";
                            Logger.Info($"[ACTIVATE] 方法1成功,保存 VID+PID: {vp}");
                        }
                    }
                }
                finally { Marshal.FreeHGlobal(nameBuf); }
            }
            else
            {
                // 方法2:WM_INPUT 句柄查询失败 → 遍历设备列表找 VID+PID
                // 原因:WM_INPUT 的 hDevice 是64位内核指针,GetRawInputDeviceInfo 不认
                //       GetRawInputDeviceList 返回的句柄才可以查
                Logger.Info($"[ACTIVATE] 方法1失败(size=0),遍历设备列表查找 VID+PID...");
                string foundVidPid = FindVidPidFromDeviceList();
                if (!string.IsNullOrEmpty(foundVidPid))
                {
                    _cfg.UsbHidVID = foundVidPid;
                    _cfg.UsbHidPID = foundVidPid.Split('&').Length > 1 ? foundVidPid.Split('&')[1] : "";
                    _cfg.UsbHidDevicePath = "VIDPID:" + foundVidPid;  // 标记已绑定
                    _cfg.UsbHidDeviceName = "USB扫码枪";
                    Logger.Info($"[ACTIVATE] 方法2成功,保存 VID+PID: {foundVidPid}");
                }
                else
                {
                    Logger.Info("[ACTIVATE] 方法2也失败,无法获取 VID+PID,下次启动仍需扫码绑定");
                }
            }

            _cfg.Save();

            // 安装低级键盘钩子,防止扫码数据双重输入
            InstallKeyboardHook();

            Logger.Info($"USB HID 扫码枪已激活,设备句柄={deviceHandle}, 路径={_usbHidDevicePath}");
            UpdateStatusDisplay();

            // 不再自动启动目标程序,模拟输入直接打到光标位置

            // [修复] 激活成功后不再弹MessageBox+ShowFromTray+HideToTray
            // 旧代码的ShowFromTray+MessageBox+HideToTray流程会导致:
            // 1. 与BtnStart_Click的MessageBox嵌套(双重modal loop)
            // 2. ShowFromTray/HideToTray切换可能干扰焦点和Raw Input接收
            // 3. MessageBox阻塞期间_isStarted状态可能不一致
            // 改用气泡通知,完全非阻塞
            this.Invoke((MethodInvoker)(() =>
            {
                _isStarted = true;  // 确保激活后_isStarted为true
                _notifyIcon.ShowBalloonTip(3000, "激活成功",
                    $"✅ 扫码枪已激活!测试码:{testBarcode}\n现在可以扫商品码了",
                    ToolTipIcon.Info);
                UpdateStatusDisplay();
            }));

            _floatingCounter?.SetStatus(FloatingCounter.ScannerStatus.Connected);
        }

        /// <summary>
        /// 获取设备显示名(通过枚举 Raw Input 设备列表匹配句柄)
        /// </summary>
        private string GetDeviceDisplayName(IntPtr targetHandle)
        {
            uint count = 0;
            GetRawInputDeviceList(IntPtr.Zero, ref count, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST)));
            if (count == 0) return "USB扫码枪";

            IntPtr listBuf = Marshal.AllocHGlobal((int)(count * Marshal.SizeOf(typeof(RAWINPUTDEVICELIST))));
            try
            {
                GetRawInputDeviceList(listBuf, ref count, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST)));
                for (int i = 0; i < count; i++)
                {
                    var dev = Marshal.PtrToStructure<RAWINPUTDEVICELIST>(listBuf + i * Marshal.SizeOf(typeof(RAWINPUTDEVICELIST)));
                    if (dev.hDevice == targetHandle)
                    {
                        uint size = 0;
            Logger.Info("[RAW] ProcessRawInput called");
                        GetRawInputDeviceInfo(dev.hDevice, 0x20000005, IntPtr.Zero, ref size);
                        if (size > 0)
                        {
                            IntPtr nameBuf = Marshal.AllocHGlobal((int)size);
                            try
                            {
                                GetRawInputDeviceInfo(dev.hDevice, 0x20000005, nameBuf, ref size);
                                var path = Marshal.PtrToStringAuto(nameBuf);
                                if (!string.IsNullOrEmpty(path)) return Path.GetFileName(path);
                            }
                            finally { Marshal.FreeHGlobal(nameBuf); }
                        }
                        return "USB扫码枪";
                    }
                }
            }
            finally { Marshal.FreeHGlobal(listBuf); }
            return "USB扫码枪";
        }

        #endregion

        #endregion

        #region 状态更新

        internal void UpdateStatusDisplay()
        {
            if (_lblStatus == null) return;

            bool hasSoftware = !string.IsNullOrEmpty(_cfg.SoftwarePath) && File.Exists(_cfg.SoftwarePath);
            _lblSoftwareName!.Text = hasSoftware ? _cfg.SoftwareName : "未绑定程序";
            _lblSoftwareName.ForeColor = hasSoftware ? TEXT : TEXT2;

            bool hasScanner = _cfg.ScannerType == ScannerType.UsbHid
                ? _cfg.ScannerConnected && _usbHidDeviceHandles.Count > 0
                : _cfg.ScannerConnected && _serialPort != null && _serialPort.IsOpen;

            // 右上角扫码枪状态
            if (_lblScannerStatus != null)
            {
                if (hasScanner)
                {
                    _lblScannerStatus.Text = "🔫 已连接";
                    _lblScannerStatus.ForeColor = GREEN;
                }
                else if (_cfg.ScannerType == ScannerType.UsbHid)
                {
                    // USB HID 模式:检查是否已注册
                    if (!string.IsNullOrEmpty(_cfg.UsbHidDevicePath) || _usbHidDeviceHandles.Count > 0)
                    {
                        _lblScannerStatus.Text = "🔫 已连接";
                        _lblScannerStatus.ForeColor = GREEN;
                    }
                    else if (_usbHidWaitingActivation)
                    {
                        _lblScannerStatus.Text = "🔫 等待激活";
                        _lblScannerStatus.ForeColor = Color.FromArgb(251, 191, 36); // 黄色
                    }
                    else
                    {
                        _lblScannerStatus.Text = "🔫 未绑定";
                        _lblScannerStatus.ForeColor = TEXT2;
                    }
                }
                else if (!string.IsNullOrEmpty(_cfg.ScannerDeviceKey) || !string.IsNullOrEmpty(_cfg.ScannerHardwareId))
                {
                    _lblScannerStatus.Text = "🔫 未连接";
                    _lblScannerStatus.ForeColor = Color.FromArgb(239, 68, 68); // 红色
                }
                else
                {
                    _lblScannerStatus.Text = "🔫 未绑定";
                    _lblScannerStatus.ForeColor = TEXT2;
                }
            }

            if (hasSoftware && hasScanner)
            {
                _lblStatus.Text = "● 全部就绪";
                _lblStatus.ForeColor = GREEN;
            }
            else if (hasSoftware)
            {
                _lblStatus.Text = "● 程序已绑定";
                _lblStatus.ForeColor = ACCENT;
            }
            else if (hasScanner)
            {
                _lblStatus.Text = "● 扫码枪已连接";
                _lblStatus.ForeColor = ACCENT;
            }
            else
            {
                _lblStatus.Text = "○ 等待配置";
                _lblStatus.ForeColor = TEXT2;
            }

            // 更新悬浮窗状态
            if (_cfg.ScannerType == ScannerType.UsbHid)
            {
                if (_cfg.ScannerConnected && _usbHidDeviceHandles.Count > 0)
                    _floatingCounter?.SetStatus(FloatingCounter.ScannerStatus.Connected);
                else if (_usbHidWaitingActivation)
                    _floatingCounter?.SetStatus(FloatingCounter.ScannerStatus.WaitingActivation);
                else
                    _floatingCounter?.SetStatus(FloatingCounter.ScannerStatus.Disconnected);
            }
            else
            {
                if (_cfg.ScannerConnected && _serialPort != null && _serialPort.IsOpen)
                    _floatingCounter?.SetStatus(FloatingCounter.ScannerStatus.Connected);
                else
                    _floatingCounter?.SetStatus(FloatingCounter.ScannerStatus.Disconnected);
            }
        }

        #endregion

        #region 托盘图标

        private void InitTrayIcon()
        {
            _trayMenu = new ContextMenuStrip();
            _trayMenu.BackColor = CARD;
            _trayMenu.ForeColor = TEXT;
            _trayMenu.Items.Add("📋 显示主窗口", null, (s, e) => ShowFromTray());
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add("❌ 退出", null, (s, e) => ExitApp());

            _notifyIcon = new NotifyIcon();
            _notifyIcon.Icon = this.Icon;
            _notifyIcon.Text = "日期提醒";
            _notifyIcon.Visible = true;
            _notifyIcon.ContextMenuStrip = _trayMenu;
            _notifyIcon.DoubleClick += (s, e) => ShowFromTray();
        }

        internal void ShowFromTray()
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.Activate();
            _floatingCounter?.Hide();
        }

        private void HideToTray()
        {
            this.Hide();
            _floatingCounter?.Show();
        }

        private void ExitApp()
        {
            DisconnectScanner();
            StopUsbMonitor();
            _reconnectTimer?.Stop();
            _reconnectTimer?.Dispose();
            SaveWindowPositions();
            _floatingCounter?.Dispose();
            _notifyIcon!.Visible = false;
            _cfg.Save();
            Application.Exit();
        }

        #endregion

        #region 窗口位置记忆

        private void RestoreWindowPositions()
        {
            if (_cfg.MainWindowX >= 0 && _cfg.MainWindowY >= 0)
            {
                var pos = new Point(_cfg.MainWindowX, _cfg.MainWindowY);
                if (IsPositionOnScreen(pos, this.Size))
                {
                    this.StartPosition = FormStartPosition.Manual;
                    this.Location = pos;
                }
            }
            if (_cfg.FloatingX >= 0 && _cfg.FloatingY >= 0)
            {
                var pos = new Point(_cfg.FloatingX, _cfg.FloatingY);
                if (IsPositionOnScreen(pos, _floatingCounter!.Size))
                {
                    _floatingCounter.StartPosition = FormStartPosition.Manual;
                    _floatingCounter.Location = pos;
                }
            }
        }

        private void SaveWindowPositions()
        {
            if (this.WindowState != FormWindowState.Minimized)
            {
                _cfg.MainWindowX = this.Location.X;
                _cfg.MainWindowY = this.Location.Y;
            }
            if (_floatingCounter != null)
            {
                _cfg.FloatingX = _floatingCounter.Location.X;
                _cfg.FloatingY = _floatingCounter.Location.Y;
            }
        }

        private bool IsPositionOnScreen(Point pos, Size size)
        {
            foreach (Screen screen in Screen.AllScreens)
            {
                var area = screen.WorkingArea;
                var overlapX = Math.Max(0, Math.Min(pos.X + size.Width, area.Right) - Math.Max(pos.X, area.Left));
                var overlapY = Math.Max(0, Math.Min(pos.Y + size.Height, area.Bottom) - Math.Max(pos.Y, area.Top));
                if (overlapX * overlapY > (size.Width * size.Height) / 2) return true;
            }
            return false;
        }

        // 窗口从托盘恢复时,不再自动重置 _isStarted
        // 原逻辑:Activated 事件里 _isStarted = false,但这会导致 HID 激活后被意外重置
        // 新逻辑:_isStarted 只在 BtnStop_Click 里设为 false
        private void MainForm_Activated(object? sender, EventArgs e)
        {
            // 不再重置 _isStarted
        }

        private void MainForm_Resize(object? sender, EventArgs e)
        {
            if (this.WindowState == FormWindowState.Minimized)
            {
                this.Hide();
            }
        }

        private void MainForm_LocationChanged(object? sender, EventArgs e)
        {
            _locationSaveTimer?.Dispose();
            _locationSaveTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _locationSaveTimer.Tick += (s, _) =>
            {
                ((System.Windows.Forms.Timer)s!).Stop();
                ((System.Windows.Forms.Timer)s!).Dispose();
                SaveWindowPositions();
                _cfg.Save();
            };
            _locationSaveTimer.Start();
        }

        private System.Windows.Forms.Timer? _locationSaveTimer;

        #endregion

        #region UI

        private void InitializeComponent()
        {
            this.SuspendLayout();

            this.Text = "日期提醒";
            this.ClientSize = new Size(340, 280);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.None;
            this.BackColor = CARD;
            this.ForeColor = TEXT;
            this.Font = new Font("Microsoft YaHei", 9f);
            this.DoubleBuffered = true;
            this.TopMost = true;
            this.ShowInTaskbar = false;

            try
            {
                var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (File.Exists(iconPath))
                    this.Icon = new Icon(iconPath);
            }
            catch { }

            // ====== 顶栏 - 可拖动 ======
            var topBar = new Panel
            {
                Location = new Point(0, 0),
                Size = new Size(340, 56),
                BackColor = CARD,
                Cursor = Cursors.SizeAll
            };
            topBar.Paint += (s, e) =>
            {
                // 蓝色底边
                using var line = new SolidBrush(ACCENT);
                e.Graphics.FillRectangle(line, 0, 53, 340, 3);
            };
            topBar.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(this.Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0); } };

            var lblTitle = new Label
            {
                Text = "📅 日期提醒",
                ForeColor = TEXT,
                Font = new Font("Microsoft YaHei", 14f, FontStyle.Bold),
                AutoSize = true,
                BackColor = Color.Transparent,
                Location = new Point(20, 16)
            };
            topBar.Controls.Add(lblTitle);

            // 右上角扫码枪状态
            _lblScannerStatus = new Label
            {
                Text = "🔫 未绑定",
                ForeColor = TEXT2,
                Font = new Font("Microsoft YaHei", 9f),
                AutoSize = true,
                BackColor = Color.Transparent,
                Location = new Point(240, 4)
            };
            topBar.Controls.Add(_lblScannerStatus);

            _lblStatus = new Label
            {
                Text = "○ 等待配置",
                ForeColor = TEXT2,
                Font = new Font("Microsoft YaHei", 9f),
                AutoSize = true,
                BackColor = Color.Transparent,
                Location = new Point(200, 22)
            };
            topBar.Controls.Add(_lblStatus);

            // 右上角最小化按钮
            var btnMin = new Button
            {
                Text = "-",
                Location = new Point(304, 0),
                Size = new Size(36, 36),
                BackColor = Color.Transparent,
                ForeColor = TEXT2,
                Font = new Font("Microsoft YaHei", 16f, FontStyle.Bold),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                TabStop = false
            };
            btnMin.FlatAppearance.BorderSize = 0;
            btnMin.FlatAppearance.MouseOverBackColor = Color.FromArgb(240, 243, 249);
            btnMin.FlatAppearance.MouseDownBackColor = Color.FromArgb(226, 232, 240);
            btnMin.Click += (s, e) => { this.Hide(); this.WindowState = FormWindowState.Minimized; };
            topBar.Controls.Add(btnMin);

            this.Controls.Add(topBar);

            // ====== 程序名 ======
            _lblSoftwareName = new Label
            {
                Text = "未绑定程序",
                ForeColor = TEXT2,
                Font = new Font("Microsoft YaHei", 10f),
                AutoSize = true,
                BackColor = Color.Transparent,
                Location = new Point(24, 72)
            };
            this.Controls.Add(_lblSoftwareName);

            // ====== 启动按钮 - 大号绿色 ======
            var btnStart = new Button
            {
                Text = "▶  启  动",
                Location = new Point(20, 104),
                Size = new Size(300, 52),
                BackColor = GREEN,
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei", 14f, FontStyle.Bold),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            btnStart.FlatAppearance.BorderSize = 0;
            btnStart.FlatAppearance.MouseOverBackColor = GREEN_HOVER;
            btnStart.FlatAppearance.MouseDownBackColor = Color.FromArgb(21, 128, 61);
            btnStart.Click += BtnStart_Click;
            this.Controls.Add(btnStart);

            // ====== 设置按钮 ======
            var btnSettings = new Button
            {
                Text = "⚙  设置",
                Location = new Point(20, 170),
                Size = new Size(300, 42),
                BackColor = BG,
                ForeColor = TEXT2,
                Font = new Font("Microsoft YaHei", 11f),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            btnSettings.FlatAppearance.BorderColor = BORDER;
            btnSettings.FlatAppearance.BorderSize = 1;
            btnSettings.FlatAppearance.MouseOverBackColor = Color.FromArgb(226, 232, 240);
            btnSettings.FlatAppearance.MouseDownBackColor = Color.FromArgb(203, 213, 225);
            btnSettings.Click += (s, e) => OpenSettings();
            this.Controls.Add(btnSettings);

            // ====== 诊断按钮 ======

            // ====== 底部信息 ======
            var lblFooter = new Label
            {
                Text = "拖动顶栏可移动窗口",
                ForeColor = Color.FromArgb(203, 213, 225),
                Font = new Font("Microsoft YaHei", 8f),
                AutoSize = true,
                BackColor = Color.Transparent,
                Location = new Point(110, 270)
            };
            this.Controls.Add(lblFooter);

            // ====== 窗口边框 ======
            this.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var pen = new Pen(BORDER, 1);
                e.Graphics.DrawRectangle(pen, 0, 0, this.ClientSize.Width - 1, this.ClientSize.Height - 1);
            };

            this.ResumeLayout(false);
        }

        private void BtnStart_Click(object? sender, EventArgs e)
        {
            // 不再强制要求绑定目标程序,模拟输入直接打到光标所在位置

            // 检查扫码枪是否已配置(HID模式不需要预先绑定,启动时扫码激活即可)
            if (_cfg.ScannerType == ScannerType.UsbHid)
            {
                // HID 模式:检查解密密码是否已设置(唯一需要预先配置的项)
                if (string.IsNullOrEmpty(_cfg.ScannerPassword))
                {
                    MessageBox.Show("请先在设置中设置解密密码", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }
            else
            {
                // 串口模式:检查 DeviceKey
                if (string.IsNullOrEmpty(_cfg.ScannerDeviceKey))
                {
                    MessageBox.Show("请先绑定扫码枪", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }

            // 检查解密密码是否已设置
            if (string.IsNullOrEmpty(_cfg.ScannerPassword))
            {
                MessageBox.Show("请先在设置中设置解密密码", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 尝试连接扫码枪
            try
            {
                ConnectScanner();
            }
            catch (Exception ex)
            {
                // 串口模式:连接失败直接报错
                if (_cfg.ScannerType != ScannerType.UsbHid)
                {
                    MessageBox.Show("扫码枪未连接,请检查USB连接\n" + ex.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                // HID 模式:ConnectUsbHid 不会抛异常(进入等待激活状态),这里不应该走到
                Logger.Error($"ConnectUsbHid 意外异常: {ex.Message}");
                return;
            }

            // HID 模式:ConnectUsbHid 后进入等待激活状态,直接到托盘等扫码
            // [修复] 不弹MessageBox--弹窗会阻塞并导致与ActivateUsbHidScanner的MessageBox嵌套
            // 导致_isStarted和窗口状态混乱,激活后扫码数据无法进入处理流程
            if (_cfg.ScannerType == ScannerType.UsbHid && _usbHidWaitingActivation)
            {
                _isStarted = true;  // 标记已启动(激活后会继续工作)

                // 用气泡提示代替MessageBox,避免阻塞+嵌套
                _notifyIcon.ShowBalloonTip(5000, "等待扫码激活",
                    "🔫 请用扫码枪扫描任意条码以激活设备",
                    ToolTipIcon.Info);

                HideToTray();
                Logger.Info("已进入托盘,等待用户扫码激活...");
                return;  // 不往下走,等 ProcessRawInput 收到扫码后激活
            }

            _isStarted = true;  // 标记已启动(非HID模式或HID已激活)

            // 可选:启动绑定的程序(只是启动,不管输入目标)
            if (_cfg.AutoLaunch && !string.IsNullOrEmpty(_cfg.SoftwarePath) && File.Exists(_cfg.SoftwarePath))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c start \"\" \"" + _cfg.SoftwarePath + "\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    Logger.Info($"已启动绑定程序:{_cfg.SoftwareName}");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"启动绑定程序失败:{ex.Message}");
                }
            }

            // 切换到托盘
            HideToTray();
        }

        private void OpenSettings()
        {
            if (_settingsForm != null && !_settingsForm.IsDisposed)
            {
                _settingsForm.Activate();
                return;
            }

            _settingsForm = new SettingsForm(_cfg);
            _settingsForm.RequestReleasePort += () =>
            {
                Logger.Info("SettingsForm 请求释放串口");
                DisconnectScanner();
            };
            _settingsForm.FormClosed += (s, e) =>
            {
                _settingsForm = null;
                _cfg.Save();
                UpdateStatusDisplay();
                // 设置关闭后,如果扫码枪配置变了,重新连接
                if (_cfg.ScannerConnected)
                    ConnectScanner();
            };
            _settingsForm.Show();
        }

        #endregion

        private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            if (_notifyIcon != null && _notifyIcon.Visible)
            {
                e.Cancel = true;
                HideToTray();
            }
            else
            {
                DisconnectScanner();
                _cfg.Save();
            }
        }
    }
}

