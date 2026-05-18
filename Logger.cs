using System;
using System.IO;
using System.Text;

namespace DateReminder
{
    /// <summary>
    /// 简单的日志工具类
    /// </summary>
    public static class Logger
    {
        private static readonly object _lock = new object();
        private static string _logPath = "";
        private static bool _enabled = true;

        /// <summary>
        /// 日志文件路径
        /// </summary>
        public static string LogPath => _logPath;

        /// <summary>
        /// 初始化日志（在程序启动时调用）
        /// </summary>
        public static void Init(string? logDir = null)
        {
            try
            {
                logDir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DateReminder");
                Directory.CreateDirectory(logDir);
                _logPath = Path.Combine(logDir, $"log_{DateTime.Now:yyyyMMdd}.txt");

                // 写入启动日志
                Info("========== 程序启动 ==========");
            }
            catch
            {
                _enabled = false;
            }
        }

        /// <summary>
        /// 记录信息日志
        /// </summary>
        public static void Info(string message)
        {
            WriteLog("INFO", message);
        }

        /// <summary>
        /// 记录警告日志
        /// </summary>
        public static void Warn(string message)
        {
            WriteLog("WARN", message);
        }

        /// <summary>
        /// 记录错误日志
        /// </summary>
        public static void Error(string message, Exception? ex = null)
        {
            var sb = new StringBuilder(message);
            if (ex != null)
            {
                sb.AppendLine();
                sb.AppendLine($"异常类型: {ex.GetType().FullName}");
                sb.AppendLine($"异常消息: {ex.Message}");
                sb.AppendLine($"堆栈跟踪: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    sb.AppendLine($"内部异常: {ex.InnerException.Message}");
                }
            }
            WriteLog("ERROR", sb.ToString());
        }

        /// <summary>
        /// 记录调试日志
        /// </summary>
        public static void Debug(string message)
        {
#if DEBUG
            WriteLog("DEBUG", message);
#endif
        }

        /// <summary>
        /// 记录扫码数据（特殊格式）
        /// </summary>
        public static void ScanData(string data)
        {
            WriteLog("SCAN", $"收到扫码数据: [{data}] (长度={data.Length}, 字节={BitConverter.ToString(System.Text.Encoding.UTF8.GetBytes(data))})");
        }

        private static void WriteLog(string level, string message)
        {
            if (!_enabled) return;

            try
            {
                lock (_lock)
                {
                    var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    var line = $"[{timestamp}] [{level}] {message}{Environment.NewLine}";
                    File.AppendAllText(_logPath, line);
                }
            }
            catch
            {
                // 日志写入失败，忽略
            }
        }
    }
}
