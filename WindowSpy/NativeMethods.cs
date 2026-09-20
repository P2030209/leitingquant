using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowSpy
{
    public static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(POINT Point);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto, CallingConvention = CallingConvention.StdCall)]
        public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint cButtons, uint dwExtraInfo);

        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int X, int Y);

        public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        public const int WH_MOUSE_LL = 14;
        public const int WM_RBUTTONDOWN = 0x0204;
        public const int WM_RBUTTONUP = 0x0205;

        private const uint MOUSEEVENTF_LEFTDOWN = 0x02;
        private const uint MOUSEEVENTF_LEFTUP = 0x04;
        private const int MOUSEEVENTF_ABSOLUTE = 0x8000;
        public const uint MOUSEEVENTF_MOVE = 0x0001;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x08;
        public const uint MOUSEEVENTF_RIGHTUP = 0x10;
        public const uint MOUSEEVENTF_MIDDLEDOWN = 0x20;
        public const uint MOUSEEVENTF_MIDDLEUP = 0x40;
        public const uint MOUSEEVENTF_WHEEL = 0x0800;

        [DllImport("user32.dll")]
        public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

        public const int KEYEVENTF_EXTENDEDKEY = 0x0001;
        public const int KEYEVENTF_KEYUP = 0x0002;

        public static string GetWindowTitle(IntPtr hwnd)
        {
            int length = GetWindowTextLength(hwnd);
            if (length == 0) return "";
            StringBuilder sb = new StringBuilder(length + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static RECT GetRect(IntPtr hwnd)
        {
            GetWindowRect(hwnd, out RECT rect);
            return rect;
        }

        public static Bitmap CaptureWindow(IntPtr hwnd)
        {
            var rect = GetRect(hwnd);
            if (rect.Width <= 0 || rect.Height <= 0) return new Bitmap(1, 1);
            var bmp = new Bitmap(rect.Width, rect.Height);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(rect.Width, rect.Height), CopyPixelOperation.SourceCopy);
            }
            return bmp;
        }

        public static void ClickAtScreen(int x, int y, int dwell)
        {
            SetCursorPos(x, y);
            System.Threading.Thread.Sleep(Math.Max(10, dwell));
            mouse_event(MOUSEEVENTF_LEFTDOWN, (uint)x, (uint)y, 0, 0);
            System.Threading.Thread.Sleep(Math.Max(10, dwell));
            mouse_event(MOUSEEVENTF_LEFTUP, (uint)x, (uint)y, 0, 0);
        }

        /// <summary>移动鼠标到屏幕坐标（不点）</summary>
        public static void MoveToScreen(int x, int y) => SetCursorPos(x, y);

        /// <summary>鼠标按键按下/弹起：button 0=左 1=右 2=中</summary>
        public static void MouseButtonScreen(int button, bool down, int x, int y)
        {
            SetCursorPos(x, y);
            uint flag = button switch
            {
                1 => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
                2 => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
                _ => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP
            };            mouse_event(flag, (uint)x, (uint)y, 0, 0);
        }

        /// <summary>滚轮：delta 正=向上滚(通常+120的倍数)，负=向下</summary>
        public static void MouseWheelScreen(int delta)
            => mouse_event(MOUSEEVENTF_WHEEL, 0, 0, (uint)delta, 0);

        /// <summary>截取屏幕指定矩形区域（支持多显示器，坐标超出虚拟屏会被裁掉）</summary>
        public static Bitmap CaptureScreenRect(Rectangle r)
        {
            r.Intersect(GetVirtualScreen());
            if (r.Width <= 0 || r.Height <= 0) return new Bitmap(1, 1);
            var bmp = new Bitmap(r.Width, r.Height);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(r.Width, r.Height), CopyPixelOperation.SourceCopy);
            return bmp;
        }

        /// <summary>全部显示器组成的虚拟屏（物理像素，副屏在左侧时 Left/Top 为负）</summary>
        public static Rectangle GetVirtualScreen()
            => new(GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
                   GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

        /// <summary>让置顶覆盖层窗口铺满所有显示器（DPI 换算后赋给 WPF 坐标）</summary>
        public static void CoverVirtualScreen(System.Windows.Window w)
        {
            w.SourceInitialized += (s, e) =>
            {
                var vs = GetVirtualScreen();
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(w);
                w.Left = vs.Left / dpi.DpiScaleX;
                w.Top = vs.Top / dpi.DpiScaleY;
                w.Width = vs.Width / dpi.DpiScaleX;
                w.Height = vs.Height / dpi.DpiScaleY;
            };
        }

        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        public static string SaveBitmap(Bitmap bmp)
        {
            string dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "jietu");
            System.IO.Directory.CreateDirectory(dir);
            string name = $"jietu_{DateTime.Now:yyyyMMdd_HHmmssfff}.png";
            string path = System.IO.Path.Combine(dir, name);
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return path;
        }
    }
}
