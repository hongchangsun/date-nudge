using System;
using System.Threading;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DateReminder
{
    internal static class Program
    {
        // 单实例互斥体
        private static Mutex? _mutex;

        [STAThread]
        static void Main()
        {
            // 初始化日志
            Logger.Init();
            
            // 单实例检测：如果已运行则激活已有窗口并退出
            bool createdNew;
            _mutex = new Mutex(true, "DateReminder_SingleInstance", out createdNew);
            if (!createdNew)
            {
                // 已有实例运行，激活它
                ActivateExistingWindow();
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());

            GC.KeepAlive(_mutex);
        }

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        const int SW_RESTORE = 9;

        private static void ActivateExistingWindow()
        {
            // 找到已运行的实例并激活
            var current = System.Diagnostics.Process.GetCurrentProcess();
            var processes = System.Diagnostics.Process.GetProcessesByName("日期提醒");
            if (processes.Length == 0)
                processes = System.Diagnostics.Process.GetProcessesByName("DateReminder");

            foreach (var proc in processes)
            {
                if (proc.Id != current.Id && proc.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(proc.MainWindowHandle, SW_RESTORE);
                    SetForegroundWindow(proc.MainWindowHandle);
                    break;
                }
            }
        }
    }
}
