using System;
using System.IO;
using System.Text.Json;

namespace DateReminder
{
    public enum ScannerType
    {
        /// <summary>USB HID 键盘模式（扫码枪模拟键盘输入）</summary>
        UsbHid = 0,
        /// <summary>USB 转虚拟串口模式（自动探测COM口）</summary>
        VirtualCom = 1,
        /// <summary>真串口模式（物理RS232，用户手动选COM口）</summary>
        RealCom = 2
    }

    public class AppConfig
    {
        // 版本信息
        public string AppVersion { get; set; } = "1.4.2";

        // 窗口位置
        public int MainWindowX { get; set; } = -1;
        public int MainWindowY { get; set; } = -1;
        public int FloatingX { get; set; } = -1;
        public int FloatingY { get; set; } = -1;

        // 绑定程序
        public string SoftwarePath { get; set; } = "";
        public string SoftwareName { get; set; } = "";
        public bool AutoLaunch { get; set; } = true;

        // 绑定扫码枪
        public ScannerType ScannerType { get; set; } = ScannerType.VirtualCom;  // 扫码枪接入方式
        public string ComPort { get; set; } = "";              // 当前 COM 口（运行时）
        public string ScannerDeviceKey { get; set; } = "";      // InstanceId（最稳定，持久化主键）
        public string ScannerHardwareId { get; set; } = "";     // HardwareId（次稳定，InstanceId 变了还能兜底）
        public string ScannerDeviceName { get; set; } = "";     // 设备显示名
        public int BaudRate { get; set; } = 9600;
        public bool ScannerConnected { get; set; } = false;

        // USB HID 模式专用
        public string UsbHidDevicePath { get; set; } = "";      // Raw Input 设备路径（持久化）
        public string UsbHidDeviceName { get; set; } = "";      // 设备显示名
        public string UsbHidVID { get; set; } = "";             // VID+PID（金标准，稳定持久化）
        public string UsbHidPID { get; set; } = "";

        // 绑定密码（扫码数据解密用）
        public string ScannerPassword { get; set; } = "";

        // 输出模式（键盘模拟方式）
        public int OutputMode { get; set; } = 0;  // 0=剪贴板粘贴, 1=SendKeys

        // 警报声设置
        public string AlarmSound { get; set; } = AlarmSoundProvider.SoundNames[AlarmSoundProvider.DefaultSoundIndex];

        private static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".date_reminder_config.json");

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = null
        };

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts) ?? new AppConfig();

                    // 如果运行版本比配置文件版本高，自动升级配置文件的版本号
                    // 自动升级：如果代码默认版本比配置文件高，覆盖配置文件的版本号
                    var codeVersion = "1.4.2";
                    if (!string.IsNullOrEmpty(codeVersion) && codeVersion != (cfg.AppVersion ?? ""))
                    {
                        cfg.AppVersion = codeVersion;
                    }
                    return cfg;
                }
            }
            catch { }
            return new AppConfig();
        }

        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(this, JsonOpts);
                File.WriteAllText(ConfigPath, json);
            }
            catch { }
        }
    }
}
