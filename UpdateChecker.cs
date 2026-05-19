using System.Drawing;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DateReminder
{
    /// <summary>
    /// 在线更新检查器
    /// 统一版本号来源：全部从 Assembly FileVersion 读取，唯一真相源
    /// </summary>
    public static class UpdateChecker
    {
        private const string VersionUrl = "https://myapp-1349312442.cos.ap-beijing.myqcloud.com/win/version.json";
        private const int MaxRetries = 2;

        /// <summary>
        /// 获取当前程序版本号（从 Assembly FileVersion 读取，唯一真相源）
        /// 所有显示版本号的地方都必须调用此方法
        /// </summary>
        public static string GetCurrentVersion()
        {
            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var fvi = FileVersionInfo.GetVersionInfo(assembly.Location);
                return fvi.FileVersion ?? "1.0.0";
            }
            catch
            {
                return "1.0.0";
            }
        }

        /// <summary>
        /// 检查更新（带重试机制）
        /// </summary>
        public static async Task<UpdateInfo?> CheckUpdateAsync(string currentVersion)
        {
            for (int attempt = 0; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    if (attempt > 0)
                        await Task.Delay(1000 * attempt);

                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    using var client = new WebClient();
                    client.Headers.Add("User-Agent", "DateReminder/" + currentVersion);
                    // 加时间戳防止 CDN 缓存旧版本信息
                    var json = await client.DownloadStringTaskAsync(VersionUrl + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    return JsonSerializer.Deserialize<UpdateInfo>(json);
                }
                catch (Exception ex) when (attempt < MaxRetries)
                {
                    Debug.WriteLine($"[UpdateChecker] 检查更新第{attempt + 1}次失败: {ex.Message}");
                }
                catch
                {
                    break;
                }
            }
            return null;
        }

        /// <summary>
        /// 对比版本号，返回是否需要更新（语义化比较）
        /// </summary>
        public static bool NeedUpdate(string currentVersion, string latestVersion)
        {
            try
            {
                var current = ParseVersion(currentVersion);
                var latest = ParseVersion(latestVersion);
                return latest > current;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 解析版本号字符串为 Version 对象
        /// 支持 "1.2.3" 和 "1.2" 格式
        /// </summary>
        private static Version ParseVersion(string v)
        {
            if (string.IsNullOrWhiteSpace(v))
                return new Version(0, 0, 0);

            v = v.Trim().TrimStart('v');
            var parts = v.Split('.');
            int major = parts.Length > 0 ? int.Parse(parts[0]) : 0;
            int minor = parts.Length > 1 ? int.Parse(parts[1]) : 0;
            int build = parts.Length > 2 ? int.Parse(parts[2]) : 0;
            return new Version(major, minor, build);
        }

        /// <summary>
        /// 执行完整更新流程：下载 → 调用 UpdateHelper 替换 → 退出当前程序
        /// </summary>
        public static void PerformUpdate(UpdateInfo info, string appDir)
        {
            string zipPath = Path.Combine(Path.GetTempPath(), $"date_reminder_update_{DateTime.Now:yyyyMMddHHmmss}.zip");

            using (var form = new DownloadForm(info.PackageUrl ?? "", zipPath, info.PackageSize))
            {
                if (form.ShowDialog() != DialogResult.OK)
                {
                    MessageBox.Show("下载取消或失败。", "更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            // 验证下载文件完整性
            if (!File.Exists(zipPath) || new FileInfo(zipPath).Length == 0)
            {
                MessageBox.Show("下载文件不完整，请重试。", "更新失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // 启动 UpdateHelper 执行替换
            string helperPath = Path.Combine(appDir, "UpdateHelper.exe");
            if (!File.Exists(helperPath))
            {
                MessageBox.Show(
                    "找不到 UpdateHelper.exe，无法自动更新。\n\n请手动下载新版本。",
                    "更新失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = helperPath,
                Arguments = $"\"{appDir}\" \"{zipPath}\" \"日期提醒.exe\"",
                UseShellExecute = true
            });

            Application.Exit();
        }
    }

    /// <summary>
    /// 版本信息（与服务器 version.json 格式对应）
    /// </summary>
    public class UpdateInfo
    {
        public string Version { get; set; } = "";
        public int VersionCode { get; set; }
        public string UpdateLog { get; set; } = "";
        public string PackageUrl { get; set; } = "";
        public long PackageSize { get; set; }
        public string PublishTime { get; set; } = "";
    }

    /// <summary>
    /// 更新下载进度窗口
    /// </summary>
    public class DownloadForm : Form
    {
        private readonly string _url;
        private readonly string _destPath;
        private readonly long _expectedSize;
        private readonly ProgressBar _progress;
        private readonly Label _label;
        private readonly Label _speedLabel;
        private readonly WebClient _client;
        private bool _success = false;
        private DateTime _downloadStart;
        private long _lastBytes = 0;

        public DownloadForm(string url, string destPath, long expectedSize = 0)
        {
            _url = url;
            _destPath = destPath;
            _expectedSize = expectedSize;

            Text = "正在下载更新";
            Size = new System.Drawing.Size(420, 180);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;

            _label = new Label
            {
                Text = "正在连接服务器...",
                Location = new System.Drawing.Point(20, 18),
                Size = new System.Drawing.Size(380, 22),
                Font = new Font("Microsoft YaHei", 9f)
            };
            Controls.Add(_label);

            _progress = new ProgressBar
            {
                Location = new System.Drawing.Point(20, 48),
                Size = new System.Drawing.Size(380, 28),
                Style = ProgressBarStyle.Continuous
            };
            Controls.Add(_progress);

            _speedLabel = new Label
            {
                Text = "",
                Location = new System.Drawing.Point(20, 82),
                Size = new System.Drawing.Size(380, 20),
                ForeColor = System.Drawing.Color.Gray,
                Font = new Font("Microsoft YaHei", 8f)
            };
            Controls.Add(_speedLabel);

            var btnCancel = new Button
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                Location = new System.Drawing.Point(160, 115),
                Size = new System.Drawing.Size(100, 32),
                Font = new Font("Microsoft YaHei", 9f)
            };
            btnCancel.Click += (s, e) =>
            {
                _client?.CancelAsync();
            };
            Controls.Add(btnCancel);

            _client = new WebClient();
            _client.DownloadProgressChanged += (s, e) =>
            {
                _progress.Value = e.ProgressPercentage;
                double mbReceived = e.BytesReceived / 1024.0 / 1024.0;
                double mbTotal = e.TotalBytesToReceive / 1024.0 / 1024.0;
                _label.Text = $"正在下载... {mbReceived:F1} MB / {mbTotal:F1} MB";

                // 计算下载速度
                var elapsed = (DateTime.Now - _downloadStart).TotalSeconds;
                if (elapsed > 0)
                {
                    double speedMB = (e.BytesReceived - _lastBytes) / 1024.0 / 1024.0 / elapsed;
                    _speedLabel.Text = speedMB > 1
                        ? $"下载速度: {speedMB:F1} MB/s"
                        : $"下载速度: {(e.BytesReceived - _lastBytes) / 1024.0 / elapsed:F0} KB/s";
                }
                _lastBytes = e.BytesReceived;
                _downloadStart = DateTime.Now;
            };
            _client.DownloadFileCompleted += (s, e) =>
            {
                if (e.Cancelled)
                {
                    DialogResult = DialogResult.Cancel;
                }
                else if (e.Error != null)
                {
                    MessageBox.Show($"下载失败：{e.Error.Message}\n\n请检查网络连接后重试。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    DialogResult = DialogResult.Cancel;
                }
                else
                {
                    _success = true;
                    _label.Text = "下载完成！";
                    _speedLabel.Text = "";
                    _progress.Value = 100;
                    // 短暂延迟让用户看到"下载完成"
                    Task.Delay(500).ContinueWith(_ => Close());
                }
                Close();
            };
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _downloadStart = DateTime.Now;
            _client.DownloadFileAsync(new Uri(_url), _destPath);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_client.IsBusy)
                _client.CancelAsync();
            _client.Dispose();
            base.OnFormClosing(e);
        }
    }
}
