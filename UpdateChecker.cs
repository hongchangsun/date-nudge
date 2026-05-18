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
    /// </summary>
    public static class UpdateChecker
    {
        private const string VersionUrl = "https://myapp-1349312442.cos.ap-beijing.myqcloud.com/win/version.json";

        /// <summary>
        /// 检查更新（异步）
        /// </summary>
        public static async Task<UpdateInfo?> CheckUpdateAsync(string currentVersion)
        {
            try
            {
                System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
                using var client = new WebClient();
                client.Headers.Add("User-Agent", "DateReminder/" + currentVersion);
                var json = await client.DownloadStringTaskAsync(VersionUrl + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                return JsonSerializer.Deserialize<UpdateInfo>(json);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 对比版本号，返回是否需要更新
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

        private static Version ParseVersion(string v)
        {
            // 处理 "1.2.0" 格式
            var parts = v.Split('.');
            int major = parts.Length > 0 ? int.Parse(parts[0]) : 0;
            int minor = parts.Length > 1 ? int.Parse(parts[1]) : 0;
            int build = parts.Length > 2 ? int.Parse(parts[2]) : 0;
            return new Version(major, minor, build);
        }

        /// <summary>
        /// 执行更新流程
        /// </summary>
        public static void PerformUpdate(UpdateInfo info, string appDir)
        {
            // 下载 zip 到临时目录
            string zipPath = Path.Combine(Path.GetTempPath(), $"date_reminder_update_{DateTime.Now.Ticks}.zip");

            using (var form = new DownloadForm(info.PackageUrl ?? "", zipPath))
            {
                if (form.ShowDialog() != DialogResult.OK)
                {
                    MessageBox.Show("下载取消或失败。", "更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            // 启动 UpdateHelper
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

            // 主程序退出
            Application.Exit();
        }
    }

    /// <summary>
    /// 版本信息
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
    /// 下载进度窗口
    /// </summary>
    public class DownloadForm : Form
    {
        private readonly string _url;
        private readonly string _destPath;
        private readonly ProgressBar _progress;
        private readonly Label _label;
        private readonly WebClient _client;
        private bool _success = false;

        public DownloadForm(string url, string destPath)
        {
            _url = url;
            _destPath = destPath;

            Text = "正在下载更新";
            Size = new System.Drawing.Size(400, 150);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            _label = new Label
            {
                Text = "正在连接服务器...",
                Location = new System.Drawing.Point(20, 20),
                Size = new System.Drawing.Size(340, 20)
            };
            Controls.Add(_label);

            _progress = new ProgressBar
            {
                Location = new System.Drawing.Point(20, 50),
                Size = new System.Drawing.Size(340, 30),
                Style = ProgressBarStyle.Continuous
            };
            Controls.Add(_progress);

            var btnCancel = new Button
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                Location = new System.Drawing.Point(150, 90),
                Size = new System.Drawing.Size(100, 30)
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
                _label.Text = $"已下载 {e.BytesReceived / 1024:N0} KB / {e.TotalBytesToReceive / 1024:N0} KB";
            };
            _client.DownloadFileCompleted += (s, e) =>
            {
                if (e.Cancelled)
                {
                    DialogResult = DialogResult.Cancel;
                }
                else if (e.Error != null)
                {
                    MessageBox.Show($"下载失败：{e.Error.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    DialogResult = DialogResult.Cancel;
                }
                else
                {
                    _success = true;
                    DialogResult = DialogResult.OK;
                }
                Close();
            };
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _client.DownloadFileAsync(new Uri(_url), _destPath);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_client.IsBusy)
            {
                _client.CancelAsync();
            }
            _client.Dispose();
            base.OnFormClosing(e);
        }
    }
}
