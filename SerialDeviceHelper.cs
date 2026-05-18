using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;

namespace DateReminder
{
    /// <summary>
    /// 串口设备信息，用于按特征识别而非 COM 口号
    /// </summary>
    public class SerialDeviceInfo
    {
        public string ComPort { get; set; } = "";       // COM3
        public string Description { get; set; } = "";   // USB Serial Device / 虚拟串口描述
        public string HardwareId { get; set; } = "";     // USB\VID_xxxx&PID_xxxx...
        public string InstanceId { get; set; } = "";     // 设备实例ID
        public string Manufacturer { get; set; } = "";   // 厂商
        public string PNPDeviceID { get; set; } = "";     // PNP 设备 ID

        public string DisplayName => string.IsNullOrEmpty(Description)
            ? ComPort
            : $"{ComPort} - {Description}{(string.IsNullOrEmpty(Manufacturer) ? "" : $" ({Manufacturer})")}";

        /// <summary>
        /// 从 InstanceId 或 HardwareId 中提取 VID+PID 作为绑定标识
        /// 例: USB\VID_0C2E&PID_0B61\123456 → VID_0C2E&PID_0B61
        /// </summary>
        public string VidPid
        {
            get
            {
                // 优先从 InstanceId 提取
                var m = Regex.Match(InstanceId ?? "", @"VID_\w{4}&PID_\w{4}", RegexOptions.IgnoreCase);
                if (m.Success) return m.Value.ToUpperInvariant();
                // 其次从 HardwareId 提取
                m = Regex.Match(HardwareId ?? "", @"VID_\w{4}&PID_\w{4}", RegexOptions.IgnoreCase);
                if (m.Success) return m.Value.ToUpperInvariant();
                // 再从 PNPDeviceID 提取
                m = Regex.Match(PNPDeviceID ?? "", @"VID_\w{4}&PID_\w{4}", RegexOptions.IgnoreCase);
                if (m.Success) return m.Value.ToUpperInvariant();
                return "";
            }
        }

        /// <summary>
        /// 用于配置保存的绑定标识（VID+PID）
        /// </summary>
        public string DeviceKey => VidPid;
    }

    /// <summary>
    /// 通过 WMI 枚举串口设备，支持按特征查找 COM 口
    /// </summary>
    public static class SerialDeviceHelper
    {
        /// <summary>
        /// 枚举所有串口设备及其特征
        /// </summary>
        public static List<SerialDeviceInfo> EnumerateDevices()
        {
            var devices = new List<SerialDeviceInfo>();

            try
            {
                // 查 Win32_SerialPort 获取 COM 口 + 描述
                var portMap = new Dictionary<string, SerialDeviceInfo>();

                using var searcher = new ManagementObjectSearcher(
                    "SELECT DeviceID, Description, HardwareID, Manufacturer, PNPDeviceID FROM Win32_SerialPort");

                foreach (ManagementObject obj in searcher.Get())
                {
                    var info = new SerialDeviceInfo
                    {
                        ComPort = obj["DeviceID"]?.ToString() ?? "",
                        Description = obj["Description"]?.ToString() ?? "",
                        Manufacturer = obj["Manufacturer"]?.ToString() ?? "",
                    };

                    // HardwareID 可能是 string[]
                    var hwIds = obj["HardwareID"] as string[];
                    info.HardwareId = hwIds != null && hwIds.Length > 0 ? hwIds[0] : "";

                    // PNPDeviceID
                    info.PNPDeviceID = obj["PNPDeviceID"]?.ToString() ?? "";
                    // InstanceId 也用 PNPDeviceID 填充（Win32_SerialPort 没有 InstanceId 字段）
                    if (string.IsNullOrEmpty(info.InstanceId) && !string.IsNullOrEmpty(info.PNPDeviceID))
                        info.InstanceId = info.PNPDeviceID;

                    if (!string.IsNullOrEmpty(info.ComPort))
                        portMap[info.ComPort] = info;
                }

                // 补充 Win32_PnPEntity 中可能遗漏的串口
                using var pnpSearcher = new ManagementObjectSearcher(
                    "SELECT Name, DeviceID, Description, HardwareID, Manufacturer, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");

                foreach (ManagementObject obj in pnpSearcher.Get())
                {
                    var name = obj["Name"]?.ToString() ?? "";
                    // 从 Name 中提取 COM 口号，格式如 "USB Serial Device (COM3)"
                    var match = System.Text.RegularExpressions.Regex.Match(name, @"\(COM(\d+)\)");
                    if (!match.Success) continue;

                    var comPort = $"COM{match.Groups[1].Value}";
                    var pnpId2 = obj["PNPDeviceID"]?.ToString() ?? "";

                    if (portMap.TryGetValue(comPort, out var existing))
                    {
                        // 补充信息
                        if (string.IsNullOrEmpty(existing.InstanceId) && !string.IsNullOrEmpty(pnpId2))
                            existing.InstanceId = pnpId2;
                        if (string.IsNullOrEmpty(existing.PNPDeviceID) && !string.IsNullOrEmpty(pnpId2))
                            existing.PNPDeviceID = pnpId2;
                    }
                    else
                    {
                        var hwIds = obj["HardwareID"] as string[];
                        var info = new SerialDeviceInfo
                        {
                            ComPort = comPort,
                            Description = obj["Description"]?.ToString() ?? name,
                            Manufacturer = obj["Manufacturer"]?.ToString() ?? "",
                            HardwareId = hwIds != null && hwIds.Length > 0 ? hwIds[0] : "",
                            InstanceId = pnpId2,
                            PNPDeviceID = pnpId2
                        };
                        portMap[comPort] = info;
                    }
                }

                devices = portMap.Values
                    .OrderBy(d => d.ComPort)
                    .ToList();
            }
            catch { }

            return devices;
        }

        /// <summary>
        /// 检查指定 VID+PID 的设备是否已物理插入（USB 上可见），不关心 COM 口能否打开
        /// </summary>
        public static bool IsDevicePresent(string deviceKey)
        {
            if (string.IsNullOrEmpty(deviceKey)) return false;
            var devices = EnumerateDevices();
            return devices.Any(d =>
                (!string.IsNullOrEmpty(d.VidPid) && d.VidPid.Equals(deviceKey, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(d.InstanceId) && d.InstanceId.IndexOf(deviceKey, StringComparison.OrdinalIgnoreCase) >= 0) ||
                (!string.IsNullOrEmpty(d.HardwareId) && d.HardwareId.IndexOf(deviceKey, StringComparison.OrdinalIgnoreCase) >= 0));
        }

        /// <summary>
        /// 根据 VID+PID 查找当前对应的 COM 口
        /// deviceKey 格式: "VID_0C2E&PID_0B61" 或回退时的 COM 口名 "COM3"
        /// </summary>
        public static string? FindComPortByDeviceKey(string deviceKey)
        {
            if (string.IsNullOrEmpty(deviceKey)) return null;

            Logger.Info($"FindComPortByDeviceKey 开始查找: {deviceKey}");
            var devices = EnumerateDevices();
            Logger.Info($"找到 {devices.Count} 个串口设备: {string.Join(", ", devices.Select(d => d.ComPort))}");

            // 如果 deviceKey 本身就是 COM 口格式（回退情况），直接检查系统 COM 口列表
            // 不依赖 WMI，因为某些电脑上 WMI 查询不稳定
            if (deviceKey.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            {
                var ports = SerialPort.GetPortNames();
                if (ports.Contains(deviceKey, StringComparer.OrdinalIgnoreCase))
                {
                    Logger.Info($"COM 口 {deviceKey} 在系统中存在，直接使用");
                    return deviceKey;
                }
                Logger.Info($"COM 口 {deviceKey} 不在系统端口列表中（可用端口: {string.Join(",", ports)}）");
                return null;
            }

            // 按 VID+PID 精确匹配
            var match2 = devices.FirstOrDefault(d =>
                !string.IsNullOrEmpty(d.VidPid) &&
                d.VidPid.Equals(deviceKey, StringComparison.OrdinalIgnoreCase));
            if (match2 != null)
            {
                Logger.Info($"按 VID+PID 匹配成功: {match2.ComPort}");
                return match2.ComPort;
            }
            Logger.Info("按 VID+PID 匹配失败");

            // 兜底：deviceKey 包含在 InstanceId 或 HardwareId 中
            var fuzzy = devices.FirstOrDefault(d =>
                (!string.IsNullOrEmpty(d.InstanceId) && d.InstanceId.IndexOf(deviceKey, StringComparison.OrdinalIgnoreCase) >= 0) ||
                (!string.IsNullOrEmpty(d.HardwareId) && d.HardwareId.IndexOf(deviceKey, StringComparison.OrdinalIgnoreCase) >= 0));
            if (fuzzy != null)
            {
                Logger.Info($"按模糊匹配成功: {fuzzy.ComPort}");
                return fuzzy.ComPort;
            }
            Logger.Info("模糊匹配也失败");

            return null;
        }
    }
}
