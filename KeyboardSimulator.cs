using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace DateReminder
{
    /// <summary>
    /// 键盘模拟器 - 支持5种输出模式
    /// 0=剪贴板粘贴(keybd_event), 1=SendKeys, 2=SendInput, 3=SendMessage WM_CHAR, 4=SendMessage WM_PASTE
    /// </summary>
    public static class KeyboardSimulator
    {
        [DllImport("user32.dll")]
        static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern IntPtr GetFocus();

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        // SendMessage 消息
        const int WM_CHAR = 0x0102;
        const int WM_PASTE = 0x0302;
        const int WM_SETTEXT = 0x000C;

        const byte VK_CONTROL = 0x11;
        const byte VK_V = 0x56;
        const byte VK_RETURN = 0x0D;
        const uint KEYEVENTF_KEYUP = 0x0002;

        // SendInput 结构体
        [StructLayout(LayoutKind.Explicit)]
        struct INPUT
        {
            [FieldOffset(0)] public uint type;
            [FieldOffset(8)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        const uint INPUT_KEYBOARD = 1;
        const uint KEYEVENTF_UNICODE = 0x0004;

        // ====== 输出模式名称 ======
        public static readonly string[] OutputModeNames = new string[]
        {
            "0-剪贴板粘贴",
            "1-SendKeys(.NET)"
        };

        /// <summary>
        /// 根据配置的输出模式，输入文本到目标程序
        /// </summary>
        public static void TypeToProcess(string text, string processName, int outputMode)
        {
            if (string.IsNullOrEmpty(text)) return;

            Logger.Info($"开始模拟键盘输入（模式 {outputMode}），字符数：{text.Length}");

            try
            {
                switch (outputMode)
                {
                    case 0: Method_ClipboardPaste(text); break;
                    case 1: Method_SendKeys(text); break;
                    default: Method_ClipboardPaste(text); break;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[输出模式{outputMode}] 执行失败", ex);
            }
        }

        /// <summary>
        /// 模式0: 剪贴板 + keybd_event 模拟 Ctrl+V
        /// </summary>
        static void Method_ClipboardPaste(string text)
        {
            // 分离回车符：粘贴纯文本，然后用按键模拟回车
            bool hasEnter = text.EndsWith("\r") || text.EndsWith("\n") || text.EndsWith("\r\n");
            string pasteText = text.TrimEnd('\r', '\n');

            string? oldClipboard = null;
            try { oldClipboard = Clipboard.GetText(); } catch { }

            try
            {
                Clipboard.SetText(pasteText);

                keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
                keybd_event(VK_V, 0, 0, UIntPtr.Zero);
                keybd_event(VK_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

                // 粘贴后模拟回车键
                if (hasEnter)
                {
                    keybd_event(VK_RETURN, 0, 0, UIntPtr.Zero);
                    keybd_event(VK_RETURN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                }

                Logger.Info($"[模式0] 剪贴板粘贴完成{(hasEnter ? "（含回车）" : "")}");
            }
            finally
            {
                try { Clipboard.SetText(oldClipboard ?? ""); } catch { }
            }
        }

        /// <summary>
        /// 模式1: .NET SendKeys（不支持中文，但兼容性最好）
        /// </summary>
        static void Method_SendKeys(string text)
        {
            // SendKeys 会阻塞直到消息处理完
            SendKeys.SendWait(text);
            Logger.Info("[模式1] SendKeys 完成");
        }

        /// <summary>
        /// 模式2: SendInput Unicode 方式
        /// </summary>
        static void Method_SendInput(string text)
        {
            int structSize = Marshal.SizeOf(typeof(INPUT));
            Logger.Info($"[模式2] SendInput 结构体大小={structSize}");

            foreach (char ch in text)
            {
                if (ch == '\r' || ch == '\n')
                {
                    SendKey(0x0D, structSize);
                    continue;
                }
                if (ch == '\t')
                {
                    SendKey(0x09, structSize);
                    continue;
                }

                // Unicode 方式
                var inputs = new INPUT[2];
                inputs[0].type = INPUT_KEYBOARD;
                inputs[0].ki.wScan = ch;
                inputs[0].ki.dwFlags = KEYEVENTF_UNICODE;
                inputs[1].type = INPUT_KEYBOARD;
                inputs[1].ki.wScan = ch;
                inputs[1].ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;

                uint sent = SendInput(2, inputs, structSize);
                if (sent != 2)
                {
                    Logger.Info($"[模式2] SendInput char '{ch}' 失败，返回{sent} (LastError={Marshal.GetLastWin32Error()})");
                }
            }
            Logger.Info("[模式2] SendInput 完成");
        }

        /// <summary>
        /// 模式3: SendMessage WM_CHAR 直接发字符消息到窗口
        /// </summary>
        static void Method_SendMessageChar(string text)
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                Logger.Info("[模式3] 无前台窗口，跳过");
                return;
            }

            foreach (char ch in text)
            {
                if (ch == '\r')
                {
                    SendMessage(hwnd, WM_CHAR, (IntPtr)13, IntPtr.Zero);
                }
                else if (ch == '\n')
                {
                    // 跳过 \n，Windows 下通常是 \r\n
                }
                else if (ch == '\t')
                {
                    SendMessage(hwnd, WM_CHAR, (IntPtr)9, IntPtr.Zero);
                }
                else
                {
                    SendMessage(hwnd, WM_CHAR, (IntPtr)ch, IntPtr.Zero);
                }
            }
            Logger.Info($"[模式3] SendMessage WM_CHAR 完成 (hwnd={hwnd})");
        }

        /// <summary>
        /// 模式4: SendMessage WM_PASTE 直接发粘贴消息
        /// </summary>
        static void Method_SendMessagePaste(string text)
        {
            string? oldClipboard = null;
            try { oldClipboard = Clipboard.GetText(); } catch { }

            try
            {
                Clipboard.SetText(text);

                IntPtr target = GetForegroundWindow();
                if (target == IntPtr.Zero)
                {
                    Logger.Info("[模式4] 无目标窗口，跳过");
                    return;
                }

                SendMessage(target, WM_PASTE, IntPtr.Zero, IntPtr.Zero);
                Logger.Info($"[模式4] SendMessage WM_PASTE 完成 (hwnd={target})");
            }
            finally
            {
                try { Clipboard.SetText(oldClipboard ?? ""); } catch { }
            }
        }

        static void SendKey(ushort vk, int structSize)
        {
            var inputs = new INPUT[2];
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].ki.wVk = vk;
            inputs[0].ki.dwFlags = 0;
            inputs[1].type = INPUT_KEYBOARD;
            inputs[1].ki.wVk = vk;
            inputs[1].ki.dwFlags = KEYEVENTF_KEYUP;

            uint sent = SendInput(2, inputs, structSize);
            if (sent != 2)
            {
                Logger.Info($"SendKey VK=0x{vk:X4} 失败，只发送{sent}/2");
            }
        }
    }
}
