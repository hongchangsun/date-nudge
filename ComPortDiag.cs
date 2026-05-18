using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Management;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace DateReminder
{
    public class ComPortDiag : Form
    {
        private TextBox txtLog;
        private Timer _refreshTimer;

        public ComPortDiag()
        {
            Text = "串口诊断工具";
            Size = new System.Drawing.Size(800, 600);
            StartPosition = FormStartPosition.CenterScreen;

            txtLog = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new System.Drawing.Font("Consolas", 10),
                ReadOnly = true
            };

            var panel = new Panel { Dock = DockStyle.Bottom, Height = 40 };
            var btnRefresh = new Button { Text = "🔄 刷新", Left = 10, Top = 8, Width = 100 };
            btnRefresh.Click += (s, e) => RefreshInfo();

            var btnCopy = new Button { Text = "📋 复制日志", Left = 120, Top = 8, Width = 100 };
            btnCopy.Click += (s, e) => { Clipboard.SetText(txtLog.Text); MessageBox.Show("已复制到剪贴板"); };

            panel.Controls.AddRange(new Control[] { btnRefresh, btnCopy });
            Controls.AddRange(new Control[] { txtLog, panel });

            _refreshTimer = new Timer { Interval = 2000 };
            _refreshTimer.Tick += (s, e) => RefreshInfo();
            _refreshTimer.Start();

            RefreshInfo();
        }

        void RefreshInfo()
        {
            txtLog.Clear();
            Log("═══════════════════════════════════════════");
            Log("  串口诊断工具 - " + DateTime.Now.ToString("HH:mm:ss"));
            Log("═══════════════════════════════════════════\n");

            // 1. 系统串口列表
            Log("【1. 系统串口列表】");
            var ports = SerialPort.GetPortNames();
            if (ports.Length == 0)
                Log("  ⚠ 未找到任何串口");
            else
                foreach (var p in ports)
                    Log($"  ✓ {p}");
            Log("");

            // 2. WMI Win32_SerialPort
            Log("【2. WMI Win32_SerialPort 查询结果】");
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT DeviceID, Description, HardwareID, Manufacturer, PNPDeviceID FROM Win32_SerialPort");
                int count = 0;
                foreach (ManagementObject obj in searcher.Get())
                {
                    count++;
                    string deviceId = obj["DeviceID"]?.ToString() ?? "";
                    string desc = obj["Description"]?.ToString() ?? "";
                    string mfg = obj["Manufacturer"]?.ToString() ?? "";
                    string pnp = obj["PNPDeviceID"]?.ToString() ?? "";

                    var hwIds = obj["HardwareID"] as string[];
                    string hwid = hwIds != null && hwIds.Length > 0 ? string.Join(", ", hwIds) : "(无)";

                    Log($"  [{count}] {deviceId}");
                    Log($"      描述: {desc}");
                    Log($"      厂商: {mfg}");
                    Log($"      PNP:  {pnp}");
                    Log($"      硬件ID: {hwid}");

                    // 提取 VID/PID
                    var vidpid = ExtractVidPid(pnp);
                    if (!string.IsNullOrEmpty(vidpid))
                        Log($"      VID/PID: {vidpid}");
                    Log("");
                }
                if (count == 0) Log("  ⚠ Win32_SerialPort 未返回任何设备\n");
            }
            catch (Exception ex)
            {
                Log($"  ✗ 查询失败: {ex.Message}\n");
            }

            // 3. WMI Win32_PnPEntity (COM 设备)
            Log("【3. WMI Win32_PnPEntity (COM设备)】");
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, DeviceID, Description, HardwareID, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
                int count = 0;
                foreach (ManagementObject obj in searcher.Get())
                {
                    count++;
                    string name = obj["Name"]?.ToString() ?? "";
                    string deviceId = obj["DeviceID"]?.ToString() ?? "";
                    string desc = obj["Description"]?.ToString() ?? "";
                    string pnp = obj["PNPDeviceID"]?.ToString() ?? "";

                    var hwIds = obj["HardwareID"] as string[];
                    string hwid = hwIds != null && hwIds.Length > 0 ? string.Join(", ", hwIds) : "(无)";

                    Log($"  [{count}] {name}");
                    Log($"      DeviceID: {deviceId}");
                    Log($"      PNP: {pnp}");
                    Log($"      VID/PID: {ExtractVidPid(pnp)}");
                    Log("");
                }
                if (count == 0) Log("  ⚠ 未找到 COM 设备\n");
            }
            catch (Exception ex)
            {
                Log($"  ✗ 查询失败: {ex.Message}\n");
            }

            // 4. USB 设备（可能包含未识别为串口的）
            Log("【4. USB 设备列表（可能包含未正确安装驱动的扫码枪）】");
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, DeviceID, PNPDeviceID, Status FROM Win32_PnPEntity WHERE PNPDeviceID LIKE '%USB%'");
                int count = 0;
                foreach (ManagementObject obj in searcher.Get())
                {
                    string name = obj["Name"]?.ToString() ?? "";
                    string pnp = obj["PNPDeviceID"]?.ToString() ?? "";
                    string status = obj["Status"]?.ToString() ?? "";

                    // 只显示可能是扫码枪的设备
                    if (name.ToLower().Contains("serial") ||
                        name.ToLower().Contains("com") ||
                        name.ToLower().Contains("usb") ||
                        pnp.ToLower().Contains("vid_"))
                    {
                        count++;
                        Log($"  [{count}] {name}");
                        Log($"      PNP: {pnp}");
                        Log($"      状态: {status}");
                        Log($"      VID/PID: {ExtractVidPid(pnp)}");
                        Log("");
                    }
                }
                if (count == 0) Log("  (无相关设备)\n");
            }
            catch (Exception ex)
            {
                Log($"  ✗ 查询失败: {ex.Message}\n");
            }

            Log("═══════════════════════════════════════════");
            Log("诊断完成。如果插上扫码枪后这里没有显示，可能是：");
            Log("1. 扫码枪未切换到虚拟串口模式（需扫配置条码）");
            Log("2. 驱动未安装（Windows 无法识别为串口）");
            Log("3. USB 线或接口有问题");
            Log("═══════════════════════════════════════════");
        }

        string ExtractVidPid(string pnp)
        {
            if (string.IsNullOrEmpty(pnp)) return "";
            var m = Regex.Match(pnp, @"VID_[0-9A-F]{4}&PID_[0-9A-F]{4}", RegexOptions.IgnoreCase);
            return m.Success ? m.Value.ToUpperInvariant() : "";
        }

        void Log(string msg)
        {
            txtLog.AppendText(msg + Environment.NewLine);
        }
    }
}
