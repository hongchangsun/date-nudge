using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Windows.Forms;

namespace UpdateHelper
{
    /// <summary>
    /// 更新助手 - 独立进程，负责解压替换文件
    /// 用法: UpdateHelper.exe <目标目录> <zip路径> [主程序名]
    /// </summary>
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                MessageBox.Show(
                    "用法: UpdateHelper.exe <目标目录> <zip路径> [主程序名]\n\n" +
                    "此工具由日期提醒主程序自动调用。",
                    "更新助手",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            string targetDir = args[0];
            string zipPath = args[1];
            string mainExe = args.Length > 2 ? args[2] : "日期提醒.exe";

            // 等待主程序退出
            WaitMainProcessExit(mainExe);

            // 解压替换
            bool success = ExtractAndReplace(zipPath, targetDir);

            if (success)
            {
                // 删除临时 zip
                try { File.Delete(zipPath); } catch { }

                // 启动新版本
                string newExePath = Path.Combine(targetDir, mainExe);
                if (File.Exists(newExePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = newExePath,
                        WorkingDirectory = targetDir,
                        UseShellExecute = true
                    });
                }

                MessageBox.Show(
                    "更新完成！已启动新版本。",
                    "更新成功",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(
                    "更新失败，请手动下载新版本。",
                    "更新失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        static void WaitMainProcessExit(string mainExe)
        {
            // 最多等待 10 秒
            for (int i = 0; i < 100; i++)
            {
                var procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(mainExe));
                if (procs.Length == 0) return;

                foreach (var p in procs) p.Dispose();
                Thread.Sleep(100);
            }
        }

        static bool ExtractAndReplace(string zipPath, string targetDir)
        {
            try
            {
                string tempDir = Path.Combine(Path.GetTempPath(), $"date_reminder_update_{DateTime.Now.Ticks}");
                Directory.CreateDirectory(tempDir);

                // 解压到临时目录
                ZipFile.ExtractToDirectory(zipPath, tempDir);

                // 复制所有文件到目标目录
                foreach (var file in Directory.GetFiles(tempDir, "*.*", SearchOption.TopDirectoryOnly))
                {
                    string destFile = Path.Combine(targetDir, Path.GetFileName(file));
                    File.Copy(file, destFile, true);
                }

                // 清理临时目录
                Directory.Delete(tempDir, true);

                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"更新过程出错：\n\n{ex.Message}",
                    "更新错误",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return false;
            }
        }
    }
}
