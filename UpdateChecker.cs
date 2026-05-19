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
    /// 在线更新检查器 - 极简版
    /// 版本号硬编码在 VERSION 常量中，不依赖 Assembly
    /// </summary>
    public static class UpdateChecker
    {
        // ★ 唯一版本号定义 - 每次发版改这里
        public const string VERSION = "1.6.0";

        private const string VersionUrl = "https://myapp-1349312442.cos.ap-beijing.myqcloud.com/win/version.json";

        /// <summary>
        /// 获取当前版本号
        /// </summary>
        public static string GetCurrentVersion()
        {
            return VERSION;
        }

        /// <summary>
        /// 检查更新
        /// </summary>
        public static async Task<UpdateInfo?> CheckUpdateAsync()
        {
            try
            {
                // .NET Framework 4.8: 确保启用 TLS 1.2
                try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch {}
                ServicePointManager.Expect100Continue = false;

                using (var client = new WebClient())
                {
                    client.Encoding = System.Text.Encoding.UTF8;
                    client.Headers.Add("User-Agent", "DateReminder/" + VERSION);
                    string json = "";
                    try
                    {
                        json = await client.DownloadStringTaskAsync(VersionUrl + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    }
                    catch (WebException wex)
                    {
                        // 详细日志：区分网络错误、SSL错误、HTTP错误
                        var resp = wex.Response as HttpWebResponse;
                        if (resp != null)
                            Debug.WriteLine("[UpdateChecker] HTTP " + (int)resp.StatusCode + ": " + resp.StatusDescription);
                        else if (wex.InnerException != null)
                            Debug.WriteLine("[UpdateChecker] 网络异常: " + wex.InnerException.Message);
                        Debug.WriteLine("[UpdateChecker] URL: " + VersionUrl);
                        throw; // rethrow to outer catch
                    }
                    Debug.WriteLine("[UpdateChecker] 响应: " + json);
                    return JsonSerializer.Deserialize<UpdateInfo>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
            }
            catch (Exception ex)
            {
                // 弹出详细错误信息用于调试
                var detail = "错误: " + ex.Message;
                if (ex.InnerException != null)
                    detail += "\n\n内部异常: " + ex.InnerException.Message;
                if (ex is System.Net.WebException wex && wex.Response != null)
                    detail += "\n\nHTTP: " + ((System.Net.HttpWebResponse)wex.Response).StatusCode;
                MessageBox.Show(detail, "更新检查失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return null;
            }
        }

        /// <summary>
        /// 对比版本号
        /// </summary>
        public static bool NeedUpdate(string currentVersion, string latestVersion)
        {
            try
            {
                var cur = ParseVersion(currentVersion);
                var lat = ParseVersion(latestVersion);
                return lat > cur;
            }
            catch
            {
                return false;
            }
        }

        private static Version ParseVersion(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return new Version(0, 0, 0);
            v = v.Trim().TrimStart('v');
            return new Version(v);
        }

        /// <summary>
        /// 执行更新
        /// </summary>
        public static void PerformUpdate(UpdateInfo info, string appDir)
        {
            string zipPath = Path.Combine(Path.GetTempPath(), $"date_reminder_update_{DateTime.Now:yyyyMMddHHmmss}.zip");

            using (var form = new DownloadForm(info.PackageUrl ?? "", zipPath))
            {
                if (form.ShowDialog() != DialogResult.OK)
                {
                    MessageBox.Show("下载取消或失败。", "更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            if (!File.Exists(zipPath) || new FileInfo(zipPath).Length == 0)
            {
                MessageBox.Show("下载文件不完整，请重试。", "更新失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string helperPath = Path.Combine(appDir, "UpdateHelper.exe");
            if (!File.Exists(helperPath))
            {
                MessageBox.Show("找不到 UpdateHelper.exe，无法自动更新。\n\n请手动下载新版本。", "更新失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
    /// 版本信息
    /// </summary>
    public class UpdateInfo
    {
        public string Version { get; set; } = "";
        public int VersionCode { get; set; }
        public string PackageUrl { get; set; } = "";
        public long PackageSize { get; set; }
        public string UpdateLog { get; set; } = "";
        public string PublishTime { get; set; } = "";
        public string ReleaseNotes { get; set; } = "";
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
            ShowInTaskbar = false;

            _label = new Label
            {
                Text = "正在连接服务器...",
                Location = new System.Drawing.Point(20, 15),
                Size = new System.Drawing.Size(350, 22)
            };
            Controls.Add(_label);

            _progress = new ProgressBar
            {
                Location = new System.Drawing.Point(20, 45),
                Size = new System.Drawing.Size(350, 25)
            };
            Controls.Add(_progress);

            var btnCancel = new Button
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                Location = new System.Drawing.Point(150, 85),
                Size = new System.Drawing.Size(100, 30)
            };
            btnCancel.Click += (s, e) => _client?.CancelAsync();
            Controls.Add(btnCancel);

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            ServicePointManager.Expect100Continue = false;

            _client = new WebClient();
            _client.DownloadProgressChanged += (s, e) =>
            {
                _progress.Value = e.ProgressPercentage;
                double mbR = e.BytesReceived / 1024.0 / 1024.0;
                double mbT = e.TotalBytesToReceive / 1024.0 / 1024.0;
                _label.Text = $"正在下载... {mbR:F1} MB / {mbT:F1} MB";
            };
            _client.DownloadFileCompleted += (s, e) =>
            {
                if (e.Cancelled || e.Error != null)
                {
                    if (e.Error != null)
                        MessageBox.Show("下载失败：" + e.Error.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    DialogResult = DialogResult.Cancel;
                }
                else
                {
                    _label.Text = "下载完成！";
                    _progress.Value = 100;
                    Task.Delay(500).ContinueWith(_ => { try { Close(); } catch { } });
                }
            };
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            try
            {
                _client.DownloadFileAsync(new Uri(_url), _destPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法开始下载：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                DialogResult = DialogResult.Cancel;
                Close();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_client.IsBusy) _client.CancelAsync();
            _client.Dispose();
            base.OnFormClosing(e);
        }
    }
}
