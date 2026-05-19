using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DateReminder
{
    public class SettingsForm : Form
    {
        private AppConfig _cfg;

        // ===== Raw Input P/Invoke (HID设备枚举) =====
        const int RIM_TYPEKEYBOARD = 1;

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTDEVICELIST
        {
            public IntPtr hDevice;
            public uint dwType;
        }

        [DllImport("user32.dll")]
        static extern uint GetRawInputDeviceList(IntPtr pDeviceList, ref uint pNumDevices, uint cbSize);

        [DllImport("user32.dll")]
        static extern int GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

        // ===== UI P/Invoke =====

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        const int DWMWCP_ROUND = 2;

        [DllImport("user32.dll")]
        static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
        const int WM_NCLBUTTONDOWN = 0xA1;
        const int HTCAPTION = 2;

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        const int SW_RESTORE = 9;

        // 配色
        static readonly Color BG = Color.FromArgb(240, 243, 249);
        static readonly Color CARD = Color.FromArgb(255, 255, 255);
        static readonly Color INPUT_BG = Color.FromArgb(248, 250, 252);
        static readonly Color ACCENT = Color.FromArgb(59, 130, 246);
        static readonly Color ACCENT_HOVER = Color.FromArgb(37, 99, 235);
        static readonly Color GREEN = Color.FromArgb(34, 197, 94);
        static readonly Color RED = Color.FromArgb(239, 68, 68);
        static readonly Color TEXT = Color.FromArgb(15, 23, 42);
        static readonly Color TEXT2 = Color.FromArgb(100, 116, 139);
        static readonly Color BORDER = Color.FromArgb(226, 232, 240);
        static readonly Color SECTION_BG = Color.FromArgb(248, 250, 252);

        private TextBox txtSoftwarePath = null!;
        private Label lblSoftwareStatus = null!;
        private CheckBox chkAutoLaunch = null!;
        private ComboBox cboComPort = null!;
        private ComboBox cboBaudRate = null!;
        private Button btnBindScanner = null!;
        private Label lblScannerStatus = null!;
        private Label btnUnbindScanner = null!;
        private Button btnBindPassword = null!;
        private Label lblPasswordStatus = null!;
        private ComboBox cboAlarmSound = null!;
        private ComboBox cboOutputMode = null!;
        private Label lblAlarmStatus = null!;
        private bool _waitingForPassword = false;
        private Panel _serialSettingsPanel = null!;
        private Panel _hidSettingsPanel = null!;
        private Label _lblScanModeDesc = null!;
        private Label? _lblHidBindStatus = null;
        private Button? _hidSaveBtn = null;
        private RadioButton? _rdoSerial;
        private RadioButton? _rdoUsbHid;
        private RadioButton? _rdoRealCom;
        private SerialPort? _serialPort;
        private string[] CommonBaudRates = { "9600", "19200", "38400", "57600", "115200" };

        /// <summary>
        /// 请求释放串口的事件(由 MainForm 处理)
        /// </summary>
        public event Action? RequestReleasePort;

        public SettingsForm(AppConfig cfg)
        {
            _cfg = cfg;
            InitializeComponent();
            LoadConfigToUI();

            try { int pref = DWMWCP_ROUND; DwmSetWindowAttribute(this.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)); }
            catch { }
        }

        private void InitializeComponent()
        {
            this.SuspendLayout();

            this.Text = "日期提醒 - 设置";
            this.Size = new Size(560, 480);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = BG;
            this.ForeColor = TEXT;
            this.Font = new Font("Microsoft YaHei", 9f);
            this.DoubleBuffered = true;
            this.TopMost = true;
            this.ShowInTaskbar = false;
            this.FormBorderStyle = FormBorderStyle.None;

            int W = 560;
            int H = 480;

            // ====== 标题栏 ======
            var titleBar = new Panel
            {
                Location = new Point(0, 0),
                Size = new Size(W, 52),
                BackColor = CARD
            };
            titleBar.Paint += (s, e) =>
            {
                using var brush = new SolidBrush(TEXT);
                e.Graphics.DrawString("⚙  设置", new Font("Microsoft YaHei", 14f, FontStyle.Bold), brush, 20, 14);
                using var line = new SolidBrush(ACCENT);
                e.Graphics.FillRectangle(line, 0, titleBar.Height - 3, titleBar.Width, 3);
            };
            titleBar.MouseDown += TitleBar_MouseDown;
            this.Controls.Add(titleBar);

            // 关闭按钮
            var btnClose = new Label
            {
                Text = "✕",
                Location = new Point(W - 52, 0),
                Size = new Size(52, 52),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 14f),
                ForeColor = TEXT2,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand
            };
            btnClose.MouseEnter += (s, e) => { btnClose.ForeColor = RED; btnClose.BackColor = Color.FromArgb(254, 242, 242); };
            btnClose.MouseLeave += (s, e) => { btnClose.ForeColor = TEXT2; btnClose.BackColor = Color.Transparent; };
            btnClose.Click += (s, e) => this.Close();
            titleBar.Controls.Add(btnClose);

            // ====== TabControl ======
            var tabControl = new TabControl
            {
                Location = new Point(20, 62),
                Size = new Size(W - 40, H - 82),
                BackColor = BG,
                Font = new Font("Microsoft YaHei", 10f)
            };
            this.Controls.Add(tabControl);

            // ====== TabPage 1: 扫码枪 ======
            var tabScanner = new TabPage("🔫 扫码枪") { BackColor = BG };
            tabControl.TabPages.Add(tabScanner);

            // 扫码枪类型选择
            var grpType = CreateGroupBox("接入方式", 20, 15, 500, 70);
            tabScanner.Controls.Add(grpType);

            _rdoSerial = new RadioButton
            {
                Text = "串口模式 (USB虚拟串口)",
                Location = new Point(15, 28),
                Size = new Size(160, 24),
                ForeColor = TEXT,
                Font = new Font("Microsoft YaHei", 9f),
                Tag = ScannerType.VirtualCom,
                AutoCheck = true
            };
            _rdoSerial.Click += (s, e) => SelectScannerType(ScannerType.VirtualCom);
            grpType.Controls.Add(_rdoSerial);

            _rdoUsbHid = new RadioButton
            {
                Text = "USB HID (键盘模式)",
                Location = new Point(185, 28),
                Size = new Size(150, 24),
                ForeColor = TEXT,
                Font = new Font("Microsoft YaHei", 9f),
                Tag = ScannerType.UsbHid,
                AutoCheck = true
            };
            _rdoUsbHid.Click += (s, e) => SelectScannerType(ScannerType.UsbHid);
            grpType.Controls.Add(_rdoUsbHid);

            _rdoRealCom = new RadioButton
            {
                Text = "真串口 (RS232)",
                Location = new Point(345, 28),
                Size = new Size(130, 24),
                ForeColor = TEXT,
                Font = new Font("Microsoft YaHei", 9f),
                Tag = ScannerType.RealCom,
                AutoCheck = true
            };
            _rdoRealCom.Click += (s, e) => SelectScannerType(ScannerType.RealCom);
            grpType.Controls.Add(_rdoRealCom);

            // 串口设置区域
            _serialSettingsPanel = new Panel { Location = new Point(20, 95), Size = new Size(500, 220), BackColor = BG };
            tabScanner.Controls.Add(_serialSettingsPanel);

            var grpSerial = CreateGroupBox("串口参数", 0, 0, 500, 170);
            _serialSettingsPanel.Controls.Add(grpSerial);

            // COM口
            var lblComPort = new Label
            {
                Text = "串口号:",
                Location = new Point(15, 30),
                Size = new Size(55, 26),
                ForeColor = TEXT,
                Font = new Font("Microsoft YaHei", 9f),
                TextAlign = ContentAlignment.MiddleLeft
            };
            grpSerial.Controls.Add(lblComPort);

            cboComPort = new ComboBox
            {
                Location = new Point(75, 30),
                Size = new Size(120, 26),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = INPUT_BG,
                ForeColor = TEXT,
                Font = new Font("Microsoft YaHei", 9f),
                FlatStyle = FlatStyle.Flat
            };
            grpSerial.Controls.Add(cboComPort);

            // 波特率
            var lblBaudRate = new Label
            {
                Text = "波特率:",
                Location = new Point(215, 30),
                Size = new Size(55, 26),
                ForeColor = TEXT,
                Font = new Font("Microsoft YaHei", 9f),
                TextAlign = ContentAlignment.MiddleLeft
            };
            grpSerial.Controls.Add(lblBaudRate);

            cboBaudRate = new ComboBox
            {
                Location = new Point(275, 30),
                Size = new Size(100, 26),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = INPUT_BG,
                ForeColor = TEXT,
                Font = new Font("Microsoft YaHei", 9f),
                FlatStyle = FlatStyle.Flat
            };
            cboBaudRate.Items.AddRange(CommonBaudRates);
            grpSerial.Controls.Add(cboBaudRate);

            // 连接按钮
            btnBindScanner = CreateAccentBtn("连接并绑定", 120);
            btnBindScanner.Location = new Point(15, 70);
            btnBindScanner.Click += BtnBindScanner_NewClick;
            grpSerial.Controls.Add(btnBindScanner);

            // 状态（固定宽度，不遮挡按钮）
            lblScannerStatus = new Label
            {
                Text = "未绑定",
                ForeColor = TEXT2,
                Font = new Font("Microsoft YaHei", 9f),
                AutoSize = false,
                Size = new Size(220, 28),
                Location = new Point(145, 73)
            };
            grpSerial.Controls.Add(lblScannerStatus);

            // 解除绑定（放在连接按钮下方右侧）
            btnUnbindScanner = CreateTextBtn("解除绑定", RED);
            btnUnbindScanner.Location = new Point(380, 115);
            btnUnbindScanner.Click += UnbindScanner;
            grpSerial.Controls.Add(btnUnbindScanner);

            _lblScanModeDesc = new Label
            {
                Text = "提示: 选择串口模式后，请插入扫码枪并选择对应的COM口号",
                Location = new Point(0, 178),
                Size = new Size(480, 20),
                ForeColor = TEXT2,
                Font = new Font("Microsoft YaHei", 7.5f)
            };
            _serialSettingsPanel.Controls.Add(_lblScanModeDesc);

            // ====== USB HID 设置区域（仅 USB HID 模式显示）=====
            _hidSettingsPanel = new Panel { Location = new Point(20, 95), Size = new Size(500, 200), BackColor = BG, Visible = false };
            tabScanner.Controls.Add(_hidSettingsPanel);

            var grpHid = CreateGroupBox("USB HID 设备", 0, 0, 500, 180);
            _hidSettingsPanel.Controls.Add(grpHid);

            var lblHidInfo = new Label
            {
                Text = "USB HID 键盘模式（扫码枪模拟键盘输入）\n\n" +
                        "工作流程：\n" +
                        "1. 插入扫码枪（系统识别为键盘设备）\n" +
                        "2. 点「启动」→ 弹窗提示「请扫码激活」\n" +
                        "3. 用扫码枪扫一次码 → 获取设备句柄 → 激活成功\n" +
                        "4. 后续扫码将被拦截，不会输入到其他窗口",
                Location = new Point(15, 25),
                Size = new Size(480, 95),
                ForeColor = TEXT,
                Font = new Font("Microsoft YaHei", 9f)
            };
            grpHid.Controls.Add(lblHidInfo);

            // HID 设备状态标签（显示上次激活的设备信息，仅供参考）
            _lblHidBindStatus = new Label
            {
                Text = string.IsNullOrEmpty(_cfg.UsbHidDevicePath) || _cfg.UsbHidDevicePath == "WAITING_FOR_FIRST_SCAN"
                    ? "⏳ 尚未激活（启动时扫码即可）"
                    : $"📋 上次激活: {_cfg.UsbHidDeviceName ?? _cfg.UsbHidDevicePath}",
                ForeColor = string.IsNullOrEmpty(_cfg.UsbHidDevicePath) || _cfg.UsbHidDevicePath == "WAITING_FOR_FIRST_SCAN" ? Color.FromArgb(245, 158, 11) : GREEN,
                Font = new Font("Microsoft YaHei", 9f),
                AutoSize = true,
                Location = new Point(15, 125)
            };
            grpHid.Controls.Add(_lblHidBindStatus);

            // 绑定扫码枪按钮
            btnBindScanner = CreateAccentBtn("🔗 绑定扫码枪", 120);
            btnBindScanner.Location = new Point(15, 148);
            btnBindScanner.Click += BtnBindHidScanner_Click;
            grpHid.Controls.Add(btnBindScanner);

            // 清除上次激活记录按钮
            var btnClearHidRecord = new Button
            {
                Text = "清除记录",
                Size = new Size(75, 24),
                Location = new Point(145, 148),
                FlatStyle = FlatStyle.Flat,
                BackColor = BG,
                ForeColor = TEXT2,
                Font = new Font("Microsoft YaHei", 8f),
                Cursor = Cursors.Hand
            };
            btnClearHidRecord.FlatAppearance.BorderColor = BORDER;
            btnClearHidRecord.Click += (s, e) =>
            {
                _cfg.UsbHidDevicePath = null;
                _cfg.UsbHidDeviceName = null;
                _cfg.Save();
                if (_lblHidBindStatus != null)
                {
                    _lblHidBindStatus.Text = "⏳ 尚未激活（启动时扫码即可）";
                    _lblHidBindStatus.ForeColor = Color.FromArgb(245, 158, 11);
                }
            };
            grpHid.Controls.Add(btnClearHidRecord);

            // ====== TabPage 2: 程序绑定 ======
            var tabProgram = new TabPage("🔗 程序绑定") { BackColor = BG };
            tabControl.TabPages.Add(tabProgram);

            // 程序路径
            var grpProg = CreateGroupBox("收银软件路径", 20, 15, 500, 140);
            tabProgram.Controls.Add(grpProg);

            txtSoftwarePath = new TextBox
            {
                Location = new Point(15, 35),
                Size = new Size(400, 28),
                BackColor = INPUT_BG,
                ForeColor = TEXT,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Microsoft YaHei", 9f)
            };
            grpProg.Controls.Add(txtSoftwarePath);

            var btnBrowse = CreateSmallBtn("浏览...", 60);
            btnBrowse.Location = new Point(425, 35);
            btnBrowse.Click += BrowseSoftware;
            grpProg.Controls.Add(btnBrowse);

            lblSoftwareStatus = new Label
            {
                Text = "未绑定",
                ForeColor = TEXT2,
                Font = new Font("Microsoft YaHei", 9f),
                AutoSize = true,
                Location = new Point(15, 72)
            };
            grpProg.Controls.Add(lblSoftwareStatus);

            var btnClear = CreateTextBtn("清除路径", RED);
            btnClear.Location = new Point(380, 70);
            btnClear.Click += ClearSoftware;
            grpProg.Controls.Add(btnClear);

            chkAutoLaunch = new CheckBox
            {
                Text = "随本程序自动启动",
                AutoSize = true,
                ForeColor = TEXT,
                BackColor = Color.Transparent,
                Font = new Font("Microsoft YaHei", 9f),
                Location = new Point(15, 105)
            };
            grpProg.Controls.Add(chkAutoLaunch);

            // 解密密码
            var grpPwd = CreateGroupBox("解密密码", 20, 165, 500, 100);
            tabProgram.Controls.Add(grpPwd);

            btnBindPassword = CreateAccentBtn("🔑 输入密码", 140);
            btnBindPassword.Location = new Point(15, 35);
            btnBindPassword.Click += StartBindPassword;
            grpPwd.Controls.Add(btnBindPassword);

            lblPasswordStatus = new Label
            {
                Text = string.IsNullOrEmpty(_cfg.ScannerPassword) ? "未绑定" : "✅ 已绑定",
                ForeColor = string.IsNullOrEmpty(_cfg.ScannerPassword) ? TEXT2 : GREEN,
                Font = new Font("Microsoft YaHei", 9f),
                AutoSize = true,
                Location = new Point(165, 42)
            };
            grpPwd.Controls.Add(lblPasswordStatus);

            var lblPwdHint = new Label
            {
                Text = "部分扫码枪需要输入解密密码才能正常工作",
                Location = new Point(15, 70),
                Size = new Size(400, 20),
                ForeColor = TEXT2,
                Font = new Font("Microsoft YaHei", 8f)
            };
            grpPwd.Controls.Add(lblPwdHint);

            // ====== TabPage 3: 其他设置 ======
            var tabOther = new TabPage("⚙ 其他设置") { BackColor = BG };
            tabControl.TabPages.Add(tabOther);

            // 警报声
            var grpAlarm = CreateGroupBox("警报声设置", 20, 15, 500, 110);
            tabOther.Controls.Add(grpAlarm);

            cboAlarmSound = new ComboBox
            {
                Location = new Point(15, 30),
                Size = new Size(200, 28),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = INPUT_BG,
                ForeColor = TEXT,
                Font = new Font("Microsoft YaHei", 9f),
                FlatStyle = FlatStyle.Flat
            };
            cboAlarmSound.Items.AddRange(AlarmSoundProvider.SoundNames);
            cboAlarmSound.SelectedIndexChanged += CboAlarmSound_SelectedIndexChanged;
            grpAlarm.Controls.Add(cboAlarmSound);

            var btnPreview = CreateSmallBtn("▶ 试听", 70);
            btnPreview.Location = new Point(225, 30);
            btnPreview.Click += BtnPreview_Click;
            grpAlarm.Controls.Add(btnPreview);

            var btnConfirmAlarm = CreateAccentBtn("✓ 确认", 80);
            btnConfirmAlarm.Location = new Point(310, 28);
            btnConfirmAlarm.Click += BtnConfirmAlarm_Click;
            grpAlarm.Controls.Add(btnConfirmAlarm);

            lblAlarmStatus = new Label
            {
                Text = "",
                ForeColor = GREEN,
                Font = new Font("Microsoft YaHei", 8f),
                AutoSize = true,
                Location = new Point(15, 70)
            };
            grpAlarm.Controls.Add(lblAlarmStatus);

            // 输出模式
            var grpOutput = CreateGroupBox("输出模式", 20, 135, 500, 130);
            tabOther.Controls.Add(grpOutput);

            var outputModeLabels = new string[] { "0-剪贴板粘贴", "1-SendKeys(.NET)" };
            cboOutputMode = new ComboBox
            {
                Location = new Point(15, 30),
                Size = new Size(280, 28),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = INPUT_BG,
                ForeColor = TEXT,
                Font = new Font("Microsoft YaHei", 9f),
                FlatStyle = FlatStyle.Flat
            };
            cboOutputMode.Items.AddRange(outputModeLabels);
            cboOutputMode.SelectedIndexChanged += CboOutputMode_SelectedIndexChanged;
            grpOutput.Controls.Add(cboOutputMode);

            var btnConfirmOut = CreateAccentBtn("✓ 确认", 80);
            btnConfirmOut.Location = new Point(310, 28);
            btnConfirmOut.Click += BtnConfirmOutputMode_Click;
            grpOutput.Controls.Add(btnConfirmOut);

            var lblOutHint = new Label
            {
                Text = "提示: 如果扫码后目标窗口收不到输入，请尝试切换输出模式\n剪贴板粘贴: 兼容性好，但会覆盖剪贴板内容\nSendKeys: 不覆盖剪贴板，但部分软件可能不兼容",
                Location = new Point(15, 65),
                Size = new Size(470, 55),
                ForeColor = TEXT2,
                Font = new Font("Microsoft YaHei", 8f)
            };
            grpOutput.Controls.Add(lblOutHint);

            // 关于
            var grpAbout = CreateGroupBox("关于", 20, 275, 500, 100);
            tabOther.Controls.Add(grpAbout);

            var lblVersion = new Label
            {
                Text = $"日期提醒 v{UpdateChecker.GetCurrentVersion()}",
                Location = new Point(15, 25),
                AutoSize = true,
                ForeColor = TEXT,
                Font = new Font("Microsoft YaHei", 10f, FontStyle.Bold)
            };
            grpAbout.Controls.Add(lblVersion);

            var btnCheckUpdate = CreateAccentBtn("🔄 检查更新", 120);
            btnCheckUpdate.Location = new Point(15, 55);
            btnCheckUpdate.Click += async (s, e) =>
            {
                btnCheckUpdate.Enabled = false;
                btnCheckUpdate.Text = "检查中...";
                var currentVer = UpdateChecker.GetCurrentVersion();
                var info = await UpdateChecker.CheckUpdateAsync(currentVer);
                btnCheckUpdate.Enabled = true;
                btnCheckUpdate.Text = "🔄 检查更新";

                if (info == null)
                {
                    MessageBox.Show("无法连接更新服务器，请稍后重试。", "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (UpdateChecker.NeedUpdate(currentVer, info.Version))
                {
                    var result = MessageBox.Show(
                        $"发现新版本 {info.Version}\n\n" +
                        $"更新内容：\n{info.UpdateLog}\n\n" +
                        $"发布时间：{info.PublishTime}\n\n" +
                        "是否立即更新？",
                        "有新版本",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Information);

                    if (result == DialogResult.Yes)
                    {
                        string appDir = AppDomain.CurrentDomain.BaseDirectory;
                        UpdateChecker.PerformUpdate(info, appDir);
                    }
                }
                else
                {
                    MessageBox.Show("当前已是最新版本。", "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            };
            grpAbout.Controls.Add(btnCheckUpdate);

            this.ResumeLayout(false);
        }

        private GroupBox CreateGroupBox(string text, int x, int y, int w, int h)
        {
            return new GroupBox
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, h),
                BackColor = BG,
                ForeColor = TEXT,
                Font = new Font("Microsoft YaHei", 10f, FontStyle.Bold)
            };
        }

        /// <summary>
        /// 在窗体上绘制一个卡片背景(标题+副标题+圆角边框)
        /// </summary>
        private Panel DrawCard(int x, int y, int w, int h, string title, string subtitle)
        {
            var card = new Panel
            {
                Location = new Point(x, y),
                Size = new Size(w, h),
                BackColor = CARD
            };
            card.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                // 圆角边框
                var rect = new Rectangle(0, 0, card.Width - 1, card.Height - 1);
                using var path = RoundedRect(rect, 12);
                using var pen = new Pen(BORDER, 1);
                e.Graphics.DrawPath(pen, path);
                // 标题
                using var titleBrush = new SolidBrush(TEXT);
                e.Graphics.DrawString(title, new Font("Microsoft YaHei", 11f, FontStyle.Bold), titleBrush, 18, 14);
                // 副标题
                if (subtitle != null)
                {
                    using var subBrush = new SolidBrush(TEXT2);
                    e.Graphics.DrawString(subtitle, new Font("Microsoft YaHei", 8f), subBrush, 18, 34);
                }
                // 分隔线
                using var linePen = new Pen(BORDER, 1);
                e.Graphics.DrawLine(linePen, 18, 54, w - 18, 54);
            };
            this.Controls.Add(card);
            return card;
        }

        #region UI 组件工厂

        private Button CreateSmallBtn(string text, int width)
        {
            var btn = new Button
            {
                Text = text,
                Width = width,
                Height = 30,
                FlatStyle = FlatStyle.Flat,
                BackColor = SECTION_BG,
                ForeColor = TEXT,
                Font = new Font("Microsoft YaHei", 9f),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderColor = BORDER;
            btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(226, 232, 240);
            return btn;
        }

        private Label CreateTextBtn(string text, Color color)
        {
            var lbl = new Label
            {
                Text = text,
                ForeColor = color,
                Font = new Font("Microsoft YaHei", 8.5f, FontStyle.Underline),
                AutoSize = true,
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand
            };
            return lbl;
        }

        private Button CreateAccentBtn(string text, int width)
        {
            var btn = new Button
            {
                Text = text,
                Width = width,
                Height = 34,
                FlatStyle = FlatStyle.Flat,
                BackColor = ACCENT,
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.FlatAppearance.MouseOverBackColor = ACCENT_HOVER;
            return btn;
        }

        private static GraphicsPath RoundedRect(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            int r = radius;
            path.AddArc(rect.X, rect.Y, r, r, 180, 90);
            path.AddArc(rect.Right - r, rect.Y, r, r, 270, 90);
            path.AddArc(rect.Right - r, rect.Bottom - r, r, r, 0, 90);
            path.AddArc(rect.X, rect.Bottom - r, r, r, 90, 90);
            path.CloseFigure();
            return path;
        }

        #endregion

        #region 输出模式

        private void CboOutputMode_SelectedIndexChanged(object? sender, EventArgs e)
        {
            // 切换选择时清空确认提示
        }

        private void BtnConfirmOutputMode_Click(object? sender, EventArgs e)
        {
            if (cboOutputMode.SelectedIndex < 0) return;
            _cfg.OutputMode = cboOutputMode.SelectedIndex;
            _cfg.Save();
            System.Windows.Forms.MessageBox.Show(
                $"输出模式已保存为: {cboOutputMode.SelectedItem}\n下次扫码时将使用新方式输入。",
                "保存成功",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Information);
        }

        #endregion

        #region 配置

        private void LoadConfigToUI()
        {
            // 初始化 COM 口下拉框
            PopulateComPorts();

            // 初始化波特率下拉框
            if (cboBaudRate != null && cboBaudRate.Items.Count > 0)
            {
                int baudIdx = Array.IndexOf(CommonBaudRates, _cfg.BaudRate.ToString());
                if (baudIdx >= 0)
                    cboBaudRate.SelectedIndex = baudIdx;
                else
                    cboBaudRate.SelectedIndex = 0; // 默认 9600
            }

            if (!string.IsNullOrEmpty(_cfg.SoftwarePath))
            {
                txtSoftwarePath.Text = _cfg.SoftwarePath;
                lblSoftwareStatus.Text = string.IsNullOrEmpty(_cfg.SoftwareName) ? "已绑定" : $"✅ {_cfg.SoftwareName}";
                lblSoftwareStatus.ForeColor = GREEN;
            }
            chkAutoLaunch.Checked = _cfg.AutoLaunch;

            // 扫码枪接入方式选择
            if (_rdoSerial != null && _rdoUsbHid != null && _rdoRealCom != null)
            {
                _rdoSerial.Checked = (_cfg.ScannerType == ScannerType.VirtualCom);
                _rdoUsbHid.Checked = (_cfg.ScannerType == ScannerType.UsbHid);
                _rdoRealCom.Checked = (_cfg.ScannerType == ScannerType.RealCom);
                // 触发一次面板切换
                SelectScannerType(_cfg.ScannerType);
            }

            // USB HID 绑定状态
            if (_lblHidBindStatus != null)
            {
                if (!string.IsNullOrEmpty(_cfg.UsbHidDevicePath) && _cfg.UsbHidDevicePath != "WAITING_FOR_FIRST_SCAN")
                {
                    string displayVid = !string.IsNullOrEmpty(_cfg.UsbHidVID) ? _cfg.UsbHidVID : (_cfg.UsbHidDeviceName ?? "HID设备");
                    _lblHidBindStatus.Text = $"✅ 已绑定 ({displayVid})";
                    _lblHidBindStatus.ForeColor = GREEN;
                }
                else if (!string.IsNullOrEmpty(_cfg.UsbHidVID))
                {
                    // 有 VID+PID 但无设备路径（方法2绑定的）
                    _lblHidBindStatus.Text = $"✅ 已绑定 ({_cfg.UsbHidVID})";
                    _lblHidBindStatus.ForeColor = GREEN;
                }
                else if (_cfg.UsbHidDevicePath == "WAITING_FOR_FIRST_SCAN")
                {
                    _lblHidBindStatus.Text = "⏳ 等待首次扫码...";
                    _lblHidBindStatus.ForeColor = Color.FromArgb(245, 158, 11);
                }
                else
                {
                    _lblHidBindStatus.Text = "未绑定";
                    _lblHidBindStatus.ForeColor = TEXT2;
                }
            }

            // 扫码枪绑定状态
            if (lblScannerStatus != null)
            {
                if (!string.IsNullOrEmpty(_cfg.ScannerDeviceKey))
                {
                    lblScannerStatus.Text = $"✅ 已绑定 ({_cfg.ScannerDeviceName})";
                    lblScannerStatus.ForeColor = GREEN;
                }
                else
                {
                    lblScannerStatus.Text = "未绑定";
                    lblScannerStatus.ForeColor = TEXT2;
                }
            }

            // 密码绑定状态
            if (lblPasswordStatus != null)
            {
                lblPasswordStatus.Text = string.IsNullOrEmpty(_cfg.ScannerPassword) ? "未绑定" : "✅ 已绑定";
                lblPasswordStatus.ForeColor = string.IsNullOrEmpty(_cfg.ScannerPassword) ? TEXT2 : GREEN;
            }

            // 警报声设置
            if (cboAlarmSound != null)
            {
                var alarmIdx = Array.IndexOf(AlarmSoundProvider.SoundNames, _cfg.AlarmSound);
                if (alarmIdx < 0) alarmIdx = AlarmSoundProvider.DefaultSoundIndex;
                cboAlarmSound.SelectedIndex = alarmIdx;
            }

            // 输出模式设置
            if (cboOutputMode != null && _cfg.OutputMode >= 0 && _cfg.OutputMode < cboOutputMode.Items.Count)
            {
                cboOutputMode.SelectedIndex = _cfg.OutputMode;
            }
        }

        private void SaveConfig()
        {
            _cfg.SoftwarePath = txtSoftwarePath.Text.Trim();
            _cfg.SoftwareName = Path.GetFileNameWithoutExtension(_cfg.SoftwarePath);
            _cfg.AutoLaunch = chkAutoLaunch.Checked;
            // ScannerPassword 由扫码绑定流程写入,此处不覆盖
            // 波特率由自动探测写入,此处不覆盖
            _cfg.Save();
        }

        #endregion

        #region 警报声设置

        private void CboAlarmSound_SelectedIndexChanged(object? sender, EventArgs e)
        {
            // 切换选择时清空确认提示
            lblAlarmStatus.Text = "";
        }

        private void BtnPreview_Click(object? sender, EventArgs e)
        {
            if (cboAlarmSound.SelectedIndex < 0) return;
            var soundName = cboAlarmSound.SelectedItem?.ToString() ?? AlarmSoundProvider.SoundNames[0];
            AlarmSoundProvider.PlayAsync(soundName);
            lblAlarmStatus.Text = $"▶ 正在播放: {soundName}";
            lblAlarmStatus.ForeColor = ACCENT;
        }

        private void BtnConfirmAlarm_Click(object? sender, EventArgs e)
        {
            if (cboAlarmSound.SelectedIndex < 0)
            {
                MessageBox.Show("请先选择一种警报声", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var soundName = cboAlarmSound.SelectedItem?.ToString() ?? AlarmSoundProvider.SoundNames[0];
            _cfg.AlarmSound = soundName;
            _cfg.Save();
            lblAlarmStatus.Text = $"✅ 已选择: {soundName}";
            lblAlarmStatus.ForeColor = GREEN;
        }

        #endregion

        #region 绑定程序

        private void BrowseSoftware(object? sender, EventArgs e)
        {
            using var ofd = new OpenFileDialog
            {
                Filter = "可执行文件|*.exe|所有文件|*.*",
                Title = "选择程序"
            };
            if (ofd.ShowDialog() != DialogResult.OK) return;

            var name = Path.GetFileNameWithoutExtension(ofd.FileName);
            txtSoftwarePath.Text = ofd.FileName;
            lblSoftwareStatus.Text = $"✅ {name}";
            lblSoftwareStatus.ForeColor = GREEN;
            _cfg.SoftwarePath = ofd.FileName;
            _cfg.SoftwareName = name;
            _cfg.Save();
        }

        private void ClearSoftware(object? sender, EventArgs e)
        {
            txtSoftwarePath.Text = "";
            lblSoftwareStatus.Text = "未绑定";
            lblSoftwareStatus.ForeColor = TEXT2;
            _cfg.SoftwarePath = "";
            _cfg.SoftwareName = "";
            _cfg.Save();
        }

        #endregion

        #region 绑定扫码枪(向导式)

        /// <summary>
        /// 第1步:点击"绑定扫码枪",提示拔掉
        /// </summary>


        /// <summary>
        /// 第2步:提示插入扫码枪,轮询检测新设备
        /// </summary>


        /// <summary>
        /// 轮询检测新插入的设备
        /// </summary>

        private int _detectedBaudRate = 9600;

        #region 新绑定流程

        /// <summary>
        /// 刷新串口列表
        /// </summary>
        private void PopulateComPorts()
        {
            if (cboComPort == null) return;

            cboComPort.Items.Clear();

            // 方法1:通过 WMI 获取详细信息
            var devices = SerialDeviceHelper.EnumerateDevices();

            if (devices.Count > 0)
            {
                // 有 WMI 数据,显示详细信息
                foreach (var device in devices)
                {
                    cboComPort.Items.Add(device.ComPort);
                }
            }
            else
            {
                // 方法2:备用方案,直接获取 COM 口列表
                var ports = SerialPort.GetPortNames();
                foreach (var port in ports.OrderBy(p => p))
                {
                    cboComPort.Items.Add(port);
                }
            }

            // 如果有配置过的 COM 口,默认选中
            if (!string.IsNullOrEmpty(_cfg.ComPort) && cboComPort.Items.Contains(_cfg.ComPort))
            {
                cboComPort.SelectedItem = _cfg.ComPort;
            }
            else if (cboComPort.Items.Count > 0)
            {
                cboComPort.SelectedIndex = 0;
            }
        }

        /// <summary>
        /// 新绑定按钮点击事件:选择 COM 口 + 波特率 → 连接 → 提示扫码测试 → 收到数据后查询 VID/PID 绑定
        /// </summary>
        private void BtnBindScanner_NewClick(object? sender, EventArgs e)
        {
            Logger.Info("=== 开始绑定扫码枪 ===");

            // 检查是否选择了 COM 口
            if (cboComPort.SelectedItem == null)
            {
                Logger.Warn("未选择 COM 口");
                MessageBox.Show("请选择串口", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 检查是否选择了波特率
            if (cboBaudRate.SelectedItem == null)
            {
                Logger.Warn("未选择波特率");
                MessageBox.Show("请选择波特率", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string comPort = cboComPort.SelectedItem.ToString() ?? "";
            int baudRate = int.Parse(cboBaudRate.SelectedItem.ToString() ?? "9600");
            Logger.Info($"选择 COM口={comPort}, 波特率={baudRate}");

            // 通知 MainForm 释放串口
            RequestReleasePort?.Invoke();

            // 如果已有串口连接,先关闭
            if (_serialPort != null && _serialPort.IsOpen)
            {
                Logger.Info("关闭现有串口连接");
                try { _serialPort.Close(); _serialPort.Dispose(); } catch { }
                _serialPort = null;
            }

            // 提示用户准备扫码
            var result = MessageBox.Show(
                $"即将连接 {comPort} @ {baudRate}\n\n" +
                "连接成功后,请用扫码枪扫描任意条码进行测试。\n\n" +
                "是否继续?",
                "绑定扫码枪",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Information);

            if (result != DialogResult.OK) return;

            // 尝试打开串口
            try
            {
                _serialPort = new SerialPort(comPort, baudRate)
                {
                    DataBits = 8,
                    Parity = Parity.None,
                    StopBits = StopBits.One,
                    ReadTimeout = 10000,
                    WriteTimeout = 500
                };
                _serialPort.DataReceived += BindDevice_DataReceived;
                _serialPort.Open();

                // 更新按钮状态
                btnBindScanner.Text = "⏳ 等待扫码...";
                btnBindScanner.Enabled = false;
                cboComPort.Enabled = false;
                cboBaudRate.Enabled = false;

                // 临时保存波特率
                _detectedBaudRate = baudRate;
            }
            catch (Exception ex)
            {
                Logger.Error("打开串口失败", ex);
                MessageBox.Show($"打开串口失败: {ex.Message}\n\n请检查串口是否被其他程序占用。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 绑定过程中收到扫码数据 → 查询 VID/PID → 绑定
        /// </summary>
        private void BindDevice_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            Logger.Info("BindDevice_DataReceived 触发");
            try
            {
                var sp = (SerialPort)sender;
                var data = sp.ReadExisting();
                Logger.ScanData(data);

                // 检测密码绑定
                var idx = data.IndexOf("mima");
                if (idx >= 0 && _waitingForPassword)
                {
                    var after = data.Substring(idx + 4);
                    var digits = new string(after.Where(char.IsDigit).Take(4).ToArray());
                    if (digits.Length == 4)
                    {
                        this.BeginInvoke(new Action(() =>
                        {
                            _cfg.ScannerPassword = digits;
                            _cfg.Save();
                            _waitingForPassword = false;

                            if (lblPasswordStatus != null)
                            {
                                lblPasswordStatus.Text = "✅ 已绑定";
                                lblPasswordStatus.ForeColor = GREEN;
                            }
                            btnBindPassword.Text = "🔑  修改密码";
                            btnBindPassword.Enabled = true;

                            MessageBox.Show("密码绑定成功!", "解密密码", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }));
                        return;
                    }
                }

                // 去掉回车换行显示
                var display = data.Trim('\r', '\n');

                // 获取当前 COM 口
                string comPort = sp.PortName;

                // 查询该 COM 口对应的设备信息(VID/PID)
                var devices = SerialDeviceHelper.EnumerateDevices();
                var device = devices.FirstOrDefault(d => d.ComPort == comPort);

                this.BeginInvoke(new Action(() =>
                {
                    var result = MessageBox.Show(
                        $"✅ 扫码枪通信成功!\n\n" +
                        $"端口: {comPort}\n" +
                        $"设备: {(device != null ? device.DisplayName : "未知设备")}\n" +
                        $"型号: {(device != null ? device.VidPid : "未知")}\n" +
                        $"示例: {display}\n\n" +
                        $"确认绑定此扫码枪?",
                        "绑定确认",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);

                    if (result == DialogResult.Yes)
                    {
                        // 保存绑定信息
                        _cfg.ComPort = comPort;
                        if (device != null)
                        {
                            _cfg.ScannerDeviceKey = device.VidPid;
                            _cfg.ScannerHardwareId = device.VidPid;
                            _cfg.ScannerDeviceName = device.DisplayName;
                        }
                        else
                        {
                            _cfg.ScannerDeviceKey = comPort; // 回退:用 COM 口作为标识
                            _cfg.ScannerHardwareId = comPort;
                            _cfg.ScannerDeviceName = comPort;
                        }
                        _cfg.BaudRate = _detectedBaudRate;
                        _cfg.ScannerConnected = true;
                        _cfg.Save();

                        // 更新UI
                        lblScannerStatus.Text = $"✅ 已绑定 ({_cfg.ScannerDeviceName})";
                        lblScannerStatus.ForeColor = GREEN;

                        // 恢复按钮状态
                        btnBindScanner.Text = "连接并绑定";
                        btnBindScanner.Enabled = true;
                        cboComPort.Enabled = true;
                        cboBaudRate.Enabled = true;

                        // 绑定完成,关闭 SettingsForm 的串口
                        // MainForm 会自动重连(因为配置已更新)
                        if (_serialPort != null)
                        {
                            try { _serialPort.Close(); _serialPort.Dispose(); } catch { }
                            _serialPort = null;
                        }

                        // 提示用户重启程序使配置生效
                        MessageBox.Show(
                            "绑定成功!\n\n程序将自动重新连接扫码枪。",
                            "绑定成功",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
                    }
                    else
                    {
                        // 用户取消,关闭串口
                        if (_serialPort != null && _serialPort.IsOpen)
                        {
                            try { _serialPort.Close(); _serialPort.Dispose(); } catch { }
                            _serialPort = null;
                        }

                        // 恢复按钮状态
                        btnBindScanner.Text = "连接并绑定";
                        btnBindScanner.Enabled = true;
                        cboComPort.Enabled = true;
                        cboBaudRate.Enabled = true;
                    }
                }));
            }
            catch { }
        }

        #endregion

        /// <summary>
        /// 解除绑定
        /// </summary>
        private void UnbindScanner(object? sender, EventArgs e)
        {
            // 未绑定时提示
            if (string.IsNullOrEmpty(_cfg.ScannerDeviceKey) && string.IsNullOrEmpty(_cfg.ScannerHardwareId))
            {
                MessageBox.Show("当前没有绑定扫码枪,无需解除。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var result = MessageBox.Show(
                $"确定要解除绑定扫码枪吗?\n\n当前绑定:{_cfg.ScannerDeviceName}\n\n解除后需重新绑定才能使用。",
                "解除绑定确认",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result != DialogResult.Yes) return;

            if (_serialPort != null && _serialPort.IsOpen)
            {
                try { _serialPort.Close(); _serialPort.Dispose(); } catch { }
                _serialPort = null;
            }

            _cfg.ComPort = "";
            _cfg.ScannerDeviceKey = "";
            _cfg.ScannerHardwareId = "";
            _cfg.ScannerDeviceName = "";
            _cfg.ScannerConnected = false;
            _cfg.Save();

            lblScannerStatus.Text = "未绑定";
            lblScannerStatus.ForeColor = TEXT2;
            btnBindScanner.Text = "🔑  绑定扫码枪";
        }

        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                var sp = (SerialPort)sender;
                var data = sp.ReadExisting();

                // 检测密码绑定:数据中含 "mima" 则提取后面4位数字
                if (_waitingForPassword)
                {
                    var idx = data.IndexOf("mima");
                    if (idx >= 0)
                    {
                        // 提取 mima 后面连续4位数字
                        var after = data.Substring(idx + 4);
                        var digits = new string(after.Where(char.IsDigit).Take(4).ToArray());
                        if (digits.Length == 4)
                        {
                            this.BeginInvoke(new Action(() =>
                            {
                                _cfg.ScannerPassword = digits;
                                _cfg.Save();
                                _waitingForPassword = false;

                                if (lblPasswordStatus != null)
                                {
                                    lblPasswordStatus.Text = "✅ 已绑定";
                                    lblPasswordStatus.ForeColor = GREEN;
                                }
                                btnBindPassword.Text = "🔑  修改密码";
                                btnBindPassword.Enabled = true;

                                MessageBox.Show(
                                    "密码绑定成功!",
                                    "解密密码",
                                    MessageBoxButtons.OK,
                                    MessageBoxIcon.Information);
                            }));
                            return;
                        }
                    }
                }

                this.BeginInvoke(new Action(() => OnScanDataReceived?.Invoke(data)));
            }
            catch { }
        }

        public event Action<string>? OnScanDataReceived;

        /// <summary>
        /// 程序启动时自动根据设备特征重连扫码枪
        /// </summary>
        public bool AutoReconnectScanner()
        {
            // 优先按 DeviceKey/HardwareId 找（WMI 查询）
            string? comPort = null;
            if (!string.IsNullOrEmpty(_cfg.ScannerDeviceKey))
                comPort = SerialDeviceHelper.FindComPortByDeviceKey(_cfg.ScannerDeviceKey);
            if (comPort == null && !string.IsNullOrEmpty(_cfg.ScannerHardwareId))
                comPort = SerialDeviceHelper.FindComPortByDeviceKey(_cfg.ScannerHardwareId);

            // 兜底：WMI 查不到时，直接用配置里保存的 ComPort 尝试连接
            // （某些电脑上 WMI 可能返回空，但串口实际存在）
            if (comPort == null && !string.IsNullOrEmpty(_cfg.ComPort))
            {
                var ports = System.IO.Ports.SerialPort.GetPortNames();
                if (ports.Contains(_cfg.ComPort, StringComparer.OrdinalIgnoreCase))
                {
                    Logger.Info($"WMI 未找到设备，用保存的 ComPort: {_cfg.ComPort}（系统端口验证通过）");
                    comPort = _cfg.ComPort;
                }
                else
                {
                    Logger.Info($"保存的 ComPort: {_cfg.ComPort} 不在系统端口列表中（可用: {string.Join(",", ports)}）");
                }
            }

            if (comPort == null) return false;

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
                _serialPort.Open();

                _cfg.ComPort = comPort;
                _cfg.ScannerConnected = true;
                _cfg.Save();
                return true;
            }
            catch
            {
                _serialPort = null;
                _cfg.ScannerConnected = false;
                _cfg.Save();
                return false;
            }
        }

        #endregion

        #region 绑定密码

        private void StartBindPassword(object? sender, EventArgs e)
        {
            // 弹出输入框让用户手动输入密码
            using var inputForm = new Form
            {
                Text = "输入解密密码",
                Size = new Size(320, 180),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false
            };

            var lbl = new Label { Text = "请输入扫码枪解密密码：", Location = new Point(20, 20), AutoSize = true };
            var txt = new TextBox { Location = new Point(20, 50), Width = 250, Text = _cfg.ScannerPassword ?? "" };
            var btnOk = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(120, 90), Width = 70 };
            var btnCancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(200, 90), Width = 70 };

            inputForm.Controls.AddRange(new Control[] { lbl, txt, btnOk, btnCancel });
            inputForm.AcceptButton = btnOk;
            inputForm.CancelButton = btnCancel;

            if (inputForm.ShowDialog(this) != DialogResult.OK) return;

            var pwd = txt.Text.Trim();
            if (string.IsNullOrEmpty(pwd))
            {
                MessageBox.Show("密码不能为空！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _cfg.ScannerPassword = pwd;
            _cfg.Save();

            if (lblPasswordStatus != null)
            {
                lblPasswordStatus.Text = "✅ 已绑定";
                lblPasswordStatus.ForeColor = GREEN;
            }
            btnBindPassword.Text = "🔑  修改密码";

            MessageBox.Show("密码设置成功！", "解密密码", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>
        /// 枚举当前连接的 HID 键盘设备，弹窗让用户选择一个绑定
        /// </summary>
        /// <summary>
        /// 枚举当前所有 HID 键盘设备
        /// <summary>
        /// 诊断方法：枚举所有 Raw Input 设备（不限类型），写入日志用于排查
        /// </summary>
        private void LogAllRawInputDevices(string tag)
        {
            uint count = 0;
            GetRawInputDeviceList(IntPtr.Zero, ref count, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST)));
            int structSize = Marshal.SizeOf(typeof(RAWINPUTDEVICELIST));
            Logger.Info(string.Format("[{0}] Raw Input 设备总数: {1}, structSize={2}", tag, count, structSize));

            if (count == 0) return;

            IntPtr listBuf = Marshal.AllocHGlobal((int)(count * Marshal.SizeOf(typeof(RAWINPUTDEVICELIST))));
            try
            {
                GetRawInputDeviceList(listBuf, ref count, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST)));
                for (int i = 0; i < count; i++)
                {
                    var dev = Marshal.PtrToStructure<RAWINPUTDEVICELIST>(listBuf + i * Marshal.SizeOf(typeof(RAWINPUTDEVICELIST)));

                    string typeName = dev.dwType == 0 ? "MOUSE" : dev.dwType == 1 ? "KEYBOARD" : dev.dwType == 2 ? "HID" : $"UNKNOWN({dev.dwType})";

                    uint size = 0;
                    GetRawInputDeviceInfo(dev.hDevice, 0x20000005, IntPtr.Zero, ref size);
                    string pathStr = "(无法获取名称)";
                    if (size > 0)
                    {
                        IntPtr nameBuf = Marshal.AllocHGlobal((int)size);
                        try
                        {
                            GetRawInputDeviceInfo(dev.hDevice, 0x20000005, nameBuf, ref size);
                            pathStr = Marshal.PtrToStringAuto(nameBuf) ?? "(null)";
                        }
                        finally { Marshal.FreeHGlobal(nameBuf); }
                    }

                    Logger.Info($"[{tag}]   [{i}] 类型={typeName} Handle={dev.hDevice} 路径={pathStr}");
                }
            }
            finally { Marshal.FreeHGlobal(listBuf); }
        }

        /// </summary>
        /// <summary>
        /// 枚举所有 Raw Input 键盘设备，返回 (Handle, 路径, 友好名称)
        /// 当 GetRawInputDeviceInfo 无法获取路径时，用 RIDI_DEVICEINFO 获取 VID/PID 构造名称
        /// </summary>
        private List<(IntPtr Handle, string Path, string Name)> EnumerateHidKeyboards()
        {
            var result = new List<(IntPtr Handle, string Path, string Name)>();

            uint count = 0;
            uint ret = GetRawInputDeviceList(IntPtr.Zero, ref count, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST)));
            int structSize = Marshal.SizeOf(typeof(RAWINPUTDEVICELIST));
            Logger.Info(string.Format("[EnumerateHidKeyboards] 返回={0}, count={1}, structSize={2}", ret, count, structSize));
            if (count == 0) return result;

            IntPtr listBuf = Marshal.AllocHGlobal((int)(count * Marshal.SizeOf(typeof(RAWINPUTDEVICELIST))));
            try
            {
                GetRawInputDeviceList(listBuf, ref count, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST)));
                Logger.Info(string.Format("[EnumerateHidKeyboards] 第二次调用后 count={0}", count));
                for (int i = 0; i < count; i++)
                {
                    var dev = Marshal.PtrToStructure<RAWINPUTDEVICELIST>(listBuf + i * Marshal.SizeOf(typeof(RAWINPUTDEVICELIST)));
                    if (dev.dwType != RIM_TYPEKEYBOARD) continue;

                    // 尝试获取设备路径
                    string path = "";
                    uint size = 0;
                    GetRawInputDeviceInfo(dev.hDevice, 0x20000005 /* RIDI_DEVICENAME */, IntPtr.Zero, ref size);
                    if (size > 0)
                    {
                        IntPtr nameBuf = Marshal.AllocHGlobal((int)size);
                        try
                        {
                            GetRawInputDeviceInfo(dev.hDevice, 0x20000005, nameBuf, ref size);
                            path = Marshal.PtrToStringAuto(nameBuf) ?? "";
                        }
                        finally { Marshal.FreeHGlobal(nameBuf); }
                    }

                    // 构造友好名称
                    string friendlyName;
                    if (!string.IsNullOrEmpty(path))
                    {
                        friendlyName = ParseHidDeviceName(path);
                    }
                    else
                    {
                        // 路径获取失败时，构造 Handle 标识
                        friendlyName = GetDeviceInfoFallback(dev.hDevice);
                    }

                    // 不管路径是否为空，都加入列表（用 Handle 做唯一标识）
                    string displayPath = string.IsNullOrEmpty(path) ? $"Handle={dev.hDevice}" : path;
                    result.Add((dev.hDevice, displayPath, friendlyName));
                }
            }
            finally { Marshal.FreeHGlobal(listBuf); }

            return result;
        }

        /// <summary>
        /// 当路径无法获取时，构造一个基于 Handle 的标识字符串
        /// </summary>
        private string GetDeviceInfoFallback(IntPtr hDevice)
        {
            return string.Format("Keyboard (Handle={0})", hDevice);
        }

        private async void DetectAndBindHidDevice()
        {
            // ====== 诊断：先记录所有 Raw Input 设备 ======
            LogAllRawInputDevices("绑定-插入前");

            // ====== 第一步：拍快照（插入前的设备列表）======
            Logger.Info("=== HID 扫码枪绑定：拍插入前快照 ===");
            var beforeDevices = EnumerateHidKeyboards();
            var beforeHandles = new HashSet<IntPtr>(beforeDevices.Select(d => d.Handle));

            Logger.Info($"插入前设备数: {beforeDevices.Count}");
            foreach (var d in beforeDevices)
                Logger.Info($"  Handle={d.Handle} | {d.Name} | {d.Path}");

            // ====== 第二步：非模态弹窗提示用户插入扫码枪（不阻塞设置窗口）======
            var tcs = new TaskCompletionSource<bool>();

            var waitForm = new Form
            {
                Text = "绑定 HID 扫码枪",
                Size = new Size(400, 200),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.SizableToolWindow,
                MaximizeBox = false,
                MinimizeBox = false,
                TopMost = true
            };

            var lbl1 = new Label
            {
                Text = "请将扫码枪的 USB 线插入电脑",
                Location = new Point(30, 30),
                AutoSize = true,
                Font = new Font("微软雅黑", 10f, FontStyle.Bold)
            };
            waitForm.Controls.Add(lbl1);

            var lbl2 = new Label
            {
                Text = "插入后请点击下方按钮",
                Location = new Point(30, 65),
                AutoSize = true,
                ForeColor = Color.Gray
            };
            waitForm.Controls.Add(lbl2);

            var btnReady = new Button
            {
                Text = "我已插入",
                Location = new Point(120, 110),
                Size = new Size(160, 40),
                Font = new Font("微软雅黑", 10f)
            };
            waitForm.Controls.Add(btnReady);

            var btnCancel = new Button
            {
                Text = "取消",
                Location = new Point(30, 110),
                Size = new Size(75, 40)
            };
            waitForm.Controls.Add(btnCancel);

            btnReady.Click += (s, e) =>
            {
                tcs.TrySetResult(true);
                waitForm.Close();
            };
            btnCancel.Click += (s, e) =>
            {
                tcs.TrySetResult(false);
                waitForm.Close();
            };
            waitForm.FormClosing += (s, e) =>
            {
                if (!tcs.Task.IsCompleted) tcs.TrySetResult(false);
            };

            waitForm.Show(this);

            // 等待用户操作，不阻塞 UI 线程
            bool confirmed = await tcs.Task;
            if (!confirmed)
            {
                Logger.Info("用户取消了绑定操作");
                return;
            }

            // ====== 第三步：再拍一次快照（插入后）======
            // 异步等待，不阻塞 UI 线程
            await System.Threading.Tasks.Task.Delay(500);

            LogAllRawInputDevices("绑定-插入后");

            Logger.Info("=== HID 扫码枪绑定：拍插入后快照 ===");
            var afterDevices = EnumerateHidKeyboards();

            Logger.Info($"插入后设备数: {afterDevices.Count}");
            foreach (var d in afterDevices)
                Logger.Info($"  Handle={d.Handle} | {d.Name} | {d.Path}");

            // ====== 第四步：对比差集，用 Handle 找出新设备 ======
            var newDevices = afterDevices.Where(d => !beforeHandles.Contains(d.Handle)).ToList();

            Logger.Info($"新增设备数: {newDevices.Count}");
            foreach (var d in newDevices)
                Logger.Info($"  ★ Handle={d.Handle} | {d.Name} | {d.Path}");

            if (newDevices.Count == 0)
            {
                // 没有发现新设备
                var retryResult = MessageBox.Show(
                    "未检测到新插入的设备。\n\n" +
                    "可能的原因：\n" +
                    "1. 扫码枪已插入（不是新插入的）\n" +
                    "2. 扫码枪不是 HID 键盘模式\n" +
                    "3. 设备尚未就绪\n\n" +
                    "是否列出当前所有 HID 设备供手动选择？",
                    "未检测到新设备",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (retryResult == DialogResult.Yes && afterDevices.Count > 0)
                {
                    // 回退到手动选择模式
                    ShowManualDeviceSelection(afterDevices);
                }
                return;
            }

            if (newDevices.Count == 1)
            {
                // 只有一个新设备，直接绑定
                var found = newDevices[0];
                Logger.Info($"自动识别扫码枪: {found.Name} | {found.Path}");

                MessageBox.Show(
                    $"检测到新设备：\n\n" +
                    $"名称：{found.Name}\n" +
                    $"路径：{found.Path}\n\n" +
                    $"将绑定此设备为扫码枪。",
                    "绑定成功",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                BindHidDevice(found.Path, found.Name);
                return;
            }

            // 多个新设备（罕见），让用户选择
            ShowManualDeviceSelection(newDevices);
        }

        /// <summary>
        /// 手动选择设备列表弹窗
        /// </summary>
        private void ShowManualDeviceSelection(List<(IntPtr Handle, string Path, string Name)> devices)
        {
            using var selectForm = new Form
            {
                Text = "选择 HID 扫码枪",
                Size = new Size(520, 300),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false
            };

            var lbl = new Label
            {
                Text = "请选择你的扫码枪：",
                Location = new Point(15, 15),
                AutoSize = true
            };
            selectForm.Controls.Add(lbl);

            var listBox = new ListBox
            {
                Location = new Point(15, 45),
                Size = new Size(480, 170),
                Font = new Font("微软雅黑", 9f)
            };
            foreach (var d in devices)
            {
                listBox.Items.Add($"{d.Name}    [{d.Path}]");
            }
            listBox.SelectedIndex = 0;
            selectForm.Controls.Add(listBox);

            var btnOk = new Button
            {
                Text = "确认绑定",
                DialogResult = DialogResult.OK,
                Location = new Point(300, 225),
                Size = new Size(90, 30)
            };
            selectForm.Controls.Add(btnOk);

            var btnCancel = new Button
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                Location = new Point(405, 225),
                Size = new Size(75, 30)
            };
            selectForm.Controls.Add(btnCancel);

            selectForm.AcceptButton = btnOk;
            selectForm.CancelButton = btnCancel;

            if (selectForm.ShowDialog() == DialogResult.OK && listBox.SelectedIndex >= 0)
            {
                var chosen = devices[listBox.SelectedIndex];
                BindHidDevice(chosen.Path, chosen.Name);
            }
        }

        /// <summary>
        /// 绑定 HID 扫码枪按钮点击 — 进入扫码绑定模式
        /// </summary>
        private void BtnBindHidScanner_Click(object? sender, EventArgs e)
        {
            // 注册 Raw Input 接收键盘输入
            RegisterRawInputDevices();

            // 显示扫码提示弹窗
            var promptForm = new Form
            {
                Text = "绑定扫码枪",
                Size = new Size(400, 180),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = BG,
                TopMost = true
            };

            var lblPrompt = new Label
            {
                Text = "📱 请用扫码枪扫描任意条码...",
                Location = new Point(20, 30),
                Size = new Size(360, 30),
                ForeColor = TEXT,
                Font = new Font("Microsoft YaHei", 11f),
                TextAlign = ContentAlignment.MiddleCenter
            };
            promptForm.Controls.Add(lblPrompt);

            var btnCancel = new Button
            {
                Text = "取消",
                Size = new Size(80, 30),
                Location = new Point(160, 100),
                FlatStyle = FlatStyle.Flat,
                BackColor = BG,
                ForeColor = TEXT,
                Font = new Font("Microsoft YaHei", 9f)
            };
            btnCancel.FlatAppearance.BorderColor = BORDER;
            btnCancel.Click += (s, args) => promptForm.Close();
            promptForm.Controls.Add(btnCancel);

            // 标记等待扫码
            _waitingHidBind = true;
            _hidBindBuffer = "";
            _hidBindDeviceHandle = IntPtr.Zero;
            _hidBindPromptForm = promptForm;

            promptForm.FormClosed += (s, args) =>
            {
                _waitingHidBind = false;
                _hidBindBuffer = "";
                _hidBindPromptForm = null;
            };

            promptForm.ShowDialog(this);
        }

        // HID 绑定等待状态
        private bool _waitingHidBind = false;
        private string _hidBindBuffer = "";
        private IntPtr _hidBindDeviceHandle = IntPtr.Zero;
        private Form? _hidBindPromptForm = null;

        /// <summary>
        /// 处理 HID 绑定扫码输入（由 WndProc 调用）
        /// </summary>
        public void ProcessHidBindInput(char c, IntPtr deviceHandle)
        {
            if (!_waitingHidBind) return;

            _hidBindBuffer += c;
            _hidBindDeviceHandle = deviceHandle;
        }

        /// <summary>
        /// 处理 HID 绑定扫码完成（Enter 键）
        /// </summary>
        public void CompleteHidBind()
        {
            if (!_waitingHidBind || _hidBindPromptForm == null) return;

            _waitingHidBind = false;
            var promptForm = _hidBindPromptForm;
            _hidBindPromptForm = null;

            // 获取设备路径（主方案：从 WM_INPUT 的 hDevice 查询）
            uint size = 0;
            GetRawInputDeviceInfo(_hidBindDeviceHandle, 0x20000005, IntPtr.Zero, ref size);
            string devicePath = "";
            string vidPid = "";

            if (size > 0)
            {
                IntPtr nameBuf = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (GetRawInputDeviceInfo(_hidBindDeviceHandle, 0x20000005, nameBuf, ref size) > 0)
                    {
                        devicePath = Marshal.PtrToStringAuto(nameBuf) ?? "";
                        vidPid = ExtractVidPidFromPath(devicePath);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(nameBuf);
                }
            }

            // 备用方案：遍历设备列表找 HID 键盘的 VID+PID（排除内置键盘）
            if (string.IsNullOrEmpty(vidPid))
            {
                vidPid = FindVidPidFromDeviceList();
            }

            promptForm.Close();

            // 判断是否能长期绑定
            if (!string.IsNullOrEmpty(vidPid))
            {
                // 有 VID+PID，可以长期绑定
                _cfg.UsbHidDevicePath = devicePath;
                _cfg.UsbHidDeviceName = "USB扫码枪";
                _cfg.UsbHidVID = vidPid;
                _cfg.Save();

                if (_lblHidBindStatus != null)
                {
                    _lblHidBindStatus.Text = $"✅ 已绑定 ({vidPid})";
                    _lblHidBindStatus.ForeColor = GREEN;
                }

                MessageBox.Show(
                    $"扫码枪绑定成功！\n\n" +
                    $"设备标识: {vidPid}\n\n" +
                    $"✅ 已保存长期绑定特征，下次启动自动识别。",
                    "绑定成功",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            else if (!string.IsNullOrEmpty(devicePath))
            {
                // 没有VID+PID，但有设备路径（临时绑定）
                _cfg.UsbHidDevicePath = devicePath;
                _cfg.UsbHidDeviceName = "USB扫码枪";
                _cfg.UsbHidVID = "";
                _cfg.Save();

                if (_lblHidBindStatus != null)
                {
                    _lblHidBindStatus.Text = "⏳ 已绑定（每次启动需扫码激活）";
                    _lblHidBindStatus.ForeColor = Color.FromArgb(245, 158, 11);
                }

                MessageBox.Show(
                    "绑定成功！\n\n" +
                    "⚠️ 未能获取长期绑定特征，每次点击「启动」时需要扫码激活。",
                    "绑定成功",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            else
            {
                // 完全获取失败
                MessageBox.Show(
                    "无法获取设备信息。\n\n" +
                    "请在主界面点击「启动」后扫码激活。",
                    "绑定失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 从设备路径提取 VID+PID
        /// </summary>
        private string ExtractVidPidFromPath(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath)) return "";
            var m = System.Text.RegularExpressions.Regex.Match(devicePath, @"vid_([0-9a-fA-F]{4})&pid_([0-9a-fA-F]{4})",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) return $"VID_{m.Groups[1].Value.ToUpper()}&PID_{m.Groups[2].Value.ToUpper()}";
            return "";
        }

        /// <summary>
        /// 注册 Raw Input 接收键盘输入
        /// </summary>
        private void RegisterRawInputDevices()
        {
            var rid = new RAWINPUTDEVICE[1];
            rid[0].usUsagePage = 0x01;  // Generic Desktop
            rid[0].usUsage = 0x06;      // Keyboard
            rid[0].dwFlags = 0x00000100;  // RIDEV_INPUTSINK (接收所有键盘输入)
            rid[0].hwndTarget = this.Handle;
            RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
        }

        // Raw Input API 声明
        [DllImport("user32.dll")]
        private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

        // GetRawInputDeviceInfo 已在文件开头定义

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        /// <summary>
        /// 从 HID 设备路径中解析友好名称
        /// </summary>
        private string ParseHidDeviceName(string devicePath)
        {
            try
            {
                int vidIdx = devicePath.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
                int pidIdx = devicePath.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);

                string vid = "", pid = "";
                if (vidIdx >= 0 && vidIdx + 8 <= devicePath.Length)
                    vid = devicePath.Substring(vidIdx, 8);
                if (pidIdx >= 0 && pidIdx + 8 <= devicePath.Length)
                    pid = devicePath.Substring(pidIdx, 8);

                if (!string.IsNullOrEmpty(vid) && !string.IsNullOrEmpty(pid))
                    return $"HID设备 {vid}&{pid}";

                return "HID键盘设备";
            }
            catch
            {
                return "HID设备";
            }
        }

        /// <summary>
        /// 备用方案：遍历 Raw Input 设备列表找 HID 键盘的 VID+PID（排除内置键盘）
        /// </summary>
        private string FindVidPidFromDeviceList()
        {
            try
            {
                uint devCount = 0;
                GetRawInputDeviceList(IntPtr.Zero, ref devCount, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST)));
                if (devCount == 0) return "";

                IntPtr buf = Marshal.AllocHGlobal((int)(devCount * Marshal.SizeOf(typeof(RAWINPUTDEVICELIST))));
                try
                {
                    GetRawInputDeviceList(buf, ref devCount, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST)));
                    var candidates = new List<string>();

                    for (int i = 0; i < devCount; i++)
                    {
                        var dev = Marshal.PtrToStructure<RAWINPUTDEVICELIST>(buf + i * Marshal.SizeOf(typeof(RAWINPUTDEVICELIST)));
                        if (dev.dwType != RIM_TYPEKEYBOARD) continue;

                        uint sz = 0;
                        GetRawInputDeviceInfo(dev.hDevice, 0x20000005, IntPtr.Zero, ref sz);
                        if (sz == 0) continue;

                        IntPtr nameBuf = Marshal.AllocHGlobal((int)sz);
                        try
                        {
                            if (GetRawInputDeviceInfo(dev.hDevice, 0x20000005, nameBuf, ref sz) > 0)
                            {
                                string path = Marshal.PtrToStringAuto(nameBuf) ?? "";
                                string vp = ExtractVidPidFromPath(path);
                                if (!string.IsNullOrEmpty(vp))
                                {
                                    // 排除常见内置键盘
                                    if (path.IndexOf("VID_0486", StringComparison.OrdinalIgnoreCase) < 0 &&
                                        path.IndexOf("VID_04F2", StringComparison.OrdinalIgnoreCase) < 0 &&
                                        path.IndexOf("VID_5986", StringComparison.OrdinalIgnoreCase) < 0 &&
                                        path.IndexOf("VID_1C2D", StringComparison.OrdinalIgnoreCase) < 0 &&
                                        path.IndexOf("VID_06CB", StringComparison.OrdinalIgnoreCase) < 0)
                                    {
                                        candidates.Add(vp);
                                    }
                                }
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(nameBuf);
                        }
                    }

                    // 只有一个候选（扫码枪），直接用；多个取第一个
                    return candidates.Count > 0 ? candidates[0] : "";
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// 绑定选中的 HID 设备到配置
        /// </summary>
        private void BindHidDevice(string devicePath, string deviceName)
        {
            _cfg.UsbHidDevicePath = devicePath;
            _cfg.UsbHidDeviceName = deviceName;
            _cfg.Save();

            _lblHidBindStatus!.Text = $"✅ 已绑定 ({deviceName})";
            _lblHidBindStatus.ForeColor = GREEN;

            MessageBox.Show($"扫码枪已绑定！\n设备：{deviceName}", "绑定成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>
        /// 根据扫码枪类型切换UI显示
        /// </summary>
        private void SelectScannerType(ScannerType type)
        {
            _cfg.ScannerType = type;
            _cfg.Save();  // [Bug#4修复] 切换类型后立即保存，防止重启恢复旧类型
            
            // 切换面板显示
            bool isSerial = (type == ScannerType.VirtualCom || type == ScannerType.RealCom);
            _serialSettingsPanel.Visible = isSerial;
            _hidSettingsPanel.Visible = (type == ScannerType.UsbHid);
            
            _lblScanModeDesc.Text = type switch
            {
                ScannerType.VirtualCom => "USB虚拟串口 — 将自动探测COM口",
                ScannerType.RealCom =>   "真串口 — 请选择COM口和波特率",
                ScannerType.UsbHid =>    "USB HID键盘模式 — 需检测绑定设备"
            };
            
            lblScannerStatus.Text = type switch
            {
                ScannerType.VirtualCom => "📡 USB虚拟串口 — 将自动探测COM口",
                ScannerType.RealCom =>   "🔌 真串口 — 请选择COM口",
                ScannerType.UsbHid =>    "⌨️  USB HID键盘模式 — 需检测绑定",
                _ => ""
            };
        }

        #endregion

        private void TitleBar_MouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(this.Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveConfig();
            base.OnFormClosing(e);
        }

        private const int WM_INPUT = 0x00FF;
        private const int RID_INPUT = 0x10000003;
        // RIM_TYPEKEYBOARD 已在文件开头定义

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_INPUT && _waitingHidBind)
            {
                ProcessRawInput(m.LParam);
            }
            base.WndProc(ref m);
        }

        private void ProcessRawInput(IntPtr hRawInput)
        {
            uint size = 0;
            GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER)));
            if (size == 0) return;

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                GetRawInputData(hRawInput, RID_INPUT, buffer, ref size, (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER)));
                RAWINPUT raw = Marshal.PtrToStructure<RAWINPUT>(buffer);

                if (raw.header.dwType != RIM_TYPEKEYBOARD) return;

                ushort vKey = raw.data.VKey;
                ushort flags = raw.data.Flags;
                IntPtr deviceHandle = raw.header.hDevice;

                // 过滤 KeyUp 事件
                if ((flags & 0x80) != 0) return;

                // 过滤扩展键重复
                if ((flags & 0x01) != 0) return;

                if (vKey == 0x0D)  // VK_RETURN
                {
                    // 必须用 BeginInvoke 延迟执行，不能在 WndProc 内直接操作 UI（会死锁）
                    this.BeginInvoke((MethodInvoker)(() => CompleteHidBind()));
                    return;
                }

                // 转换为字符
                if (VkToChar.TryGetValue(vKey, out char c))
                {
                    ProcessHidBindInput(c, deviceHandle);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [DllImport("user32.dll")]
        private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTHEADER
        {
            public uint dwType;
            public uint dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUT
        {
            public RAWINPUTHEADER header;
            public RAWKEYBOARD data;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWKEYBOARD
        {
            public ushort MakeCode;
            public ushort Flags;
            public ushort Reserved;
            public ushort VKey;
            public uint Message;
            public uint ExtraInformation;
        }

        private static readonly Dictionary<ushort, char> VkToChar = new Dictionary<ushort, char>
        {
            {0x30, '0'}, {0x31, '1'}, {0x32, '2'}, {0x33, '3'}, {0x34, '4'},
            {0x35, '5'}, {0x36, '6'}, {0x37, '7'}, {0x38, '8'}, {0x39, '9'},
            {0x41, 'A'}, {0x42, 'B'}, {0x43, 'C'}, {0x44, 'D'}, {0x45, 'E'},
            {0x46, 'F'}, {0x47, 'G'}, {0x48, 'H'}, {0x49, 'I'}, {0x4A, 'J'},
            {0x4B, 'K'}, {0x4C, 'L'}, {0x4D, 'M'}, {0x4E, 'N'}, {0x4F, 'O'},
            {0x50, 'P'}, {0x51, 'Q'}, {0x52, 'R'}, {0x53, 'S'}, {0x54, 'T'},
            {0x55, 'U'}, {0x56, 'V'}, {0x57, 'W'}, {0x58, 'X'}, {0x59, 'Y'},
            {0x5A, 'Z'},
            {0xBD, '-'}, {0xBB, '='}, {0xDB, '['}, {0xDD, ']'}, {0xBA, ';'},
            {0xDE, '\''}, {0xC0, '`'}, {0xDC, '\\'}, {0xBE, '.'}, {0xBF, '/'},
            {0xDF, ']'}
        };
    }
}
