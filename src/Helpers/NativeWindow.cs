using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace BlockLibraryManager
{
    /// <summary>
    /// 窗口落点相关的 Win32 封装（多联屏 / 混合 DPI 专用）。
    ///
    /// 为什么必须绕开 WinForms 的 Screen / Bounds：
    /// 插件窗口在创建时被切到 Per-Monitor V2 DPI 感知（见 DpiAwareness），
    /// 这种窗口与系统之间交换的是**物理像素**；而 WinForms 运行在宿主进程
    /// 的 System-Aware 口径下，Screen.WorkingArea / Form.Bounds 报的是按主屏
    /// DPI 缩放后的**逻辑坐标**。两套口径一旦混用（拿逻辑工作区去摆物理窗口），
    /// 在缩放比不同的副屏上就会直接把窗口摆到屏幕外——用户看到的就是
    /// "点最大化后窗口消失"。
    ///
    /// 因此凡是与"窗口实际落在哪、铺没铺满"有关的判断，一律走这里的 Win32 接口：
    /// GetWindowRect / MonitorFromWindow / GetMonitorInfo / SetWindowPos 全是物理像素，
    /// 彼此自洽。
    /// </summary>
    internal static class NativeWindow
    {
        internal const int WM_GETMINMAXINFO = 0x0024;
        internal const int WM_DPICHANGED = 0x02E0;

        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
        private const int SW_SHOW = 5;
        private const int SW_RESTORE = 9;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public int Width { get { return Right - Left; } }
            public int Height { get { return Bottom - Top; } }
            public Rectangle ToRectangle()
            {
                return new Rectangle(Left, Top, Right - Left, Bottom - Top);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int X;
            public int Y;
        }

        /// <summary>WM_GETMINMAXINFO 的 lParam 结构，用来指定最大化的位置与尺寸。</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        // ---------------- 查询 ----------------

        /// <summary>取窗口当前所在（或最近）显示器的工作区（物理像素，已排除任务栏）。</summary>
        internal static bool TryGetWorkArea(IntPtr hwnd, out RECT work)
        {
            work = new RECT();
            if (hwnd == IntPtr.Zero) return false;

            try
            {
                IntPtr hmon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (hmon == IntPtr.Zero) return false;

                MONITORINFO mi = new MONITORINFO();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                if (!GetMonitorInfo(hmon, ref mi)) return false;

                work = mi.rcWork;
                return work.Width > 0 && work.Height > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>取窗口真实矩形（物理像素）。</summary>
        internal static bool TryGetWindowRect(IntPtr hwnd, out RECT rect)
        {
            rect = new RECT();
            if (hwnd == IntPtr.Zero) return false;

            try { return GetWindowRect(hwnd, out rect); }
            catch { return false; }
        }

        /// <summary>窗口是否处于最小化（Win32 口径，比 Form.WindowState 更可靠）。</summary>
        internal static bool IsMinimized(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try { return IsIconic(hwnd); }
            catch { return false; }
        }

        /// <summary>窗口在系统层面是否真的显示着。</summary>
        internal static bool IsVisible(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try { return IsWindowVisible(hwnd); }
            catch { return false; }
        }

        /// <summary>
        /// 窗口与它所在显示器工作区的重合面积占窗口自身面积的比例（0~100）。
        /// 用来判断"窗口是不是跑到屏幕外了"——最大化后消失就是这个值接近 0。
        /// </summary>
        internal static int VisiblePercent(IntPtr hwnd)
        {
            RECT rect, work;
            if (!TryGetWindowRect(hwnd, out rect)) return 100;      // 取不到就当正常，避免误判
            if (!TryGetWorkArea(hwnd, out work)) return 100;

            long self = (long)Math.Max(1, rect.Width) * (long)Math.Max(1, rect.Height);

            int ix1 = Math.Max(rect.Left, work.Left);
            int iy1 = Math.Max(rect.Top, work.Top);
            int ix2 = Math.Min(rect.Right, work.Right);
            int iy2 = Math.Min(rect.Bottom, work.Bottom);

            int iw = ix2 - ix1;
            int ih = iy2 - iy1;
            if (iw <= 0 || ih <= 0) return 0;

            long inter = (long)iw * (long)ih;
            long pct = inter * 100L / self;
            return (int)(pct > 100L ? 100L : pct);
        }

        /// <summary>
        /// 窗口是否"找得着"：在工作区里至少露出一块够大的面积。
        /// 只按屏幕几何判断，被别的窗口挡住不算（那是正常的层叠）。
        /// </summary>
        internal static bool IsOnScreen(IntPtr hwnd, int minWidth, int minHeight)
        {
            RECT rect, work;
            if (!TryGetWindowRect(hwnd, out rect)) return true;
            if (!TryGetWorkArea(hwnd, out work)) return true;

            int ix1 = Math.Max(rect.Left, work.Left);
            int iy1 = Math.Max(rect.Top, work.Top);
            int ix2 = Math.Min(rect.Right, work.Right);
            int iy2 = Math.Min(rect.Bottom, work.Bottom);

            int iw = ix2 - ix1;
            int ih = iy2 - iy1;
            if (iw <= 0 || ih <= 0) return false;

            return iw >= Math.Min(minWidth, rect.Width) &&
                   ih >= Math.Min(minHeight, rect.Height);
        }

        // ---------------- 摆放 ----------------

        /// <summary>把窗口摆到指定矩形（物理像素）。</summary>
        internal static bool Place(IntPtr hwnd, int x, int y, int width, int height)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                return SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height,
                    SWP_NOZORDER | SWP_NOACTIVATE);
            }
            catch { return false; }
        }

        internal static bool Place(IntPtr hwnd, RECT rect)
        {
            return Place(hwnd, rect.Left, rect.Top, rect.Width, rect.Height);
        }

        /// <summary>把窗口拉回它所在显示器的工作区（保持尺寸，只做平移与夹取）。</summary>
        internal static bool ClampIntoWorkArea(IntPtr hwnd)
        {
            RECT rect, work;
            if (!TryGetWindowRect(hwnd, out rect)) return false;
            if (!TryGetWorkArea(hwnd, out work)) return false;

            int width = Math.Min(rect.Width, work.Width);
            int height = Math.Min(rect.Height, work.Height);
            int left = Math.Max(work.Left, Math.Min(rect.Left, work.Right - width));
            int top = Math.Max(work.Top, Math.Min(rect.Top, work.Bottom - height));

            return Place(hwnd, left, top, width, height);
        }

        /// <summary>
        /// 强制在系统层面显示窗口并提到前台。
        /// 窗口跑飞 / 被宿主藏起来时，WinForms 的 Visible 可能仍是 true，
        /// 于是 Show() 里"没显示才显示"的判断会整个跳过——这里无条件补一次。
        /// </summary>
        internal static bool ForceShow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;

            try
            {
                ShowWindow(hwnd, SW_SHOW);
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_SHOWWINDOW);
                return true;
            }
            catch { return false; }
        }

        /// <summary>从最小化恢复（不激活，避免抢焦点）。</summary>
        internal static bool RestoreFromMinimized(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try { return ShowWindow(hwnd, SW_RESTORE); }
            catch { return false; }
        }

        // ---------------- WM_GETMINMAXINFO ----------------

        /// <summary>
        /// 把最大化位置 / 尺寸写成"窗口当前所在显示器的工作区"。
        ///
        /// 这是多联屏下让原生最大化不跑偏最权威的做法：系统在真正摆放最大化窗口
        /// 之前一定会问 WM_GETMINMAXINFO，由窗口自己给出落点，宿主和 DPI 都插不了手。
        /// 坐标全部是物理像素（GetMonitorInfo 与 MINMAXINFO 同一口径）。
        /// </summary>
        internal static bool ApplyMaxBoundsToMonitor(IntPtr hwnd, IntPtr lParam)
        {
            if (hwnd == IntPtr.Zero || lParam == IntPtr.Zero) return false;

            RECT work;
            if (!TryGetWorkArea(hwnd, out work)) return false;

            try
            {
                MINMAXINFO mmi = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO));
                mmi.ptMaxPosition.X = work.Left;
                mmi.ptMaxPosition.Y = work.Top;
                mmi.ptMaxSize.X = work.Width;
                mmi.ptMaxSize.Y = work.Height;
                Marshal.StructureToPtr(mmi, lParam, false);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
