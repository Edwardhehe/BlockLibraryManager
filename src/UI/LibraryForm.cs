using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BlockLibraryManager
{
    /// <summary>
    /// 无边框主窗口：宿主 LibraryControl，替代 AutoCAD 的 PaletteSet。
    ///
    /// 为什么不用 PaletteSet：
    ///   PaletteSet 自带的标题栏（停靠时是左侧一条黑竖条）没有任何 API 可以隐藏
    ///   （PaletteSetTitleBarLocation 枚举只有 Left/Right），用户明确要求去掉它，
    ///   所以整体换成自绘无边框窗口——我们自己的 TitleBar 就是唯一标题栏。
    ///
/// 能力：
///   · 按住标题栏拖动窗口（WM_NCLBUTTONDOWN + HTCAPTION）
///   · 八个方向拖边缩放（WM_NCHITTEST）
///   · 双击标题栏最大化/还原（拦截 WM_NCLBUTTONDBLCLK / WM_SYSCOMMAND，
///     路由到 LibraryPalette.ToggleMaximize，与按钮共用同一状态机）
///   · 位置尺寸记忆（通过 SaveBounds 回调写进 LibraryConfig）
    /// </summary>
    internal class LibraryForm : Form
    {
        private const int WM_NCHITTEST = 0x84;
        private const int WM_NCLBUTTONDBLCLK = 0xA3;
        private const int WM_SYSCOMMAND = 0x0112;
        private const int WM_DPICHANGED = 0x02E0;
        private const int SC_MAXIMIZE = 0xF030;
        private const int SC_RESTORE = 0xF120;
        private const int HTCAPTION = 0x2;
        private const int HTTRANSPARENT = -1;

        // WinForms 内部就是这些常量：HTLEFT..HTBOTTOMRIGHT
        private const int HTLEFT = 10;
        private const int HTRIGHT = 11;
        private const int HTTOP = 12;
        private const int HTTOPLEFT = 13;
        private const int HTTOPRIGHT = 14;
        private const int HTBOTTOM = 15;
        private const int HTBOTTOMLEFT = 16;
        private const int HTBOTTOMRIGHT = 17;

        private const int GripSize = 6;

        private readonly TitleBar _bar;
        private readonly LibraryControl _control;
        private bool _capturingBounds;

        /// <summary>窗口位置/尺寸变化后回调（用于持久化）。仅在 Normal 状态触发。</summary>
        public event EventHandler BoundsChangedByUser;

        /// <summary>
        /// 双击标题栏 / 系统对标题栏发出最大化·还原指令时触发。
        /// 宿主把它接到 LibraryPalette.ToggleMaximize，保证与按钮共用同一状态机。
        /// </summary>
        public event EventHandler CaptionMaximizeToggle;

        /// <summary>
        /// 窗口从一块显示器被移到另一块时触发。
        /// 多联屏 + 不同缩放（DPI）下，跨屏后 GDI 内容经常整块不重画
        /// （表现：一大块只剩背景色的白窗）；宿主借此重新校正最大化范围并强制重绘。
        /// </summary>
        public event EventHandler ScreenSwitched;

        /// <summary>
        /// CAD 主窗口句柄（由宿主在显示时填入）。
        /// 只在窗口自身已经跑出屏幕时才作为"最大化到哪块屏"的参照，
        /// 常规情况下最大化一律跟随窗口自己所在的显示器。
        /// </summary>
        private IntPtr _hostWindow = IntPtr.Zero;

        /// <summary>窗口上次检测到所在屏幕的设备名；null = 还没检测过。</summary>
        private string _lastScreenName;
        /// <summary>跨屏同步进行中的标志（OnMove 里改 MaximizedBounds 会再触发 Move，防重入）。</summary>
        private bool _screenSyncBusy;

        /// <summary>
        /// 程序化改变 WindowState 期间置 true。
        /// 代码里改 WindowState 时 Windows 会随带 WM_SYSCOMMAND(SC_RESTORE / SC_MAXIMIZE)，
        /// 若不加区分地路由到插件自己的状态机，就会"要还原、结果被最大化"。
        /// 置位期间这类消息一律交回系统默认处理。
        /// </summary>
        internal bool SuppressStateToggle;

        public LibraryForm(LibraryControl control, string title)
        {
            if (control == null) throw new ArgumentNullException("control");

            _control = control;
            _control.Dock = DockStyle.Fill;

            _bar = new TitleBar(title);

            FormBorderStyle = FormBorderStyle.None;
            Text = title;                       // 任务栏 / Alt+Tab 里显示的名字
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.Manual;
            MinimumSize = new Size(340, 420);
            Size = new Size(440, 680);
            BackColor = SystemColors.ControlLightLight;
            Font = SystemFonts.MessageBoxFont;
            KeyPreview = false;

            // 关键：给窗口留出边缘热区。
            // 内容控件是 Dock 填满的，会把整个窗口表面盖住，
            // WM_NCHITTEST 就到不了窗口本身，边缘缩放会失效。
            // Padding 让四周露出 5px 的窗口表面供缩放热区使用。
            Padding = new Padding(5);

            // 注意添加顺序：Fill 的主控件先加，标题栏后加才能占据顶部
            Controls.Add(_control);
            Controls.Add(_bar);

            // 位置尺寸持久化（防抖：拖动中会连续触发，用标志合并）
            LocationChanged += new EventHandler(OnBoundsMaybeChanged);
            Resize += new EventHandler(OnBoundsMaybeChanged);
        }

        public TitleBar Bar
        {
            get { return _bar; }
        }

        /// <summary>设置最大化范围（Form.MaximizedBounds 是受保护属性，此处代设）。
        /// 无边框窗口最大化时用它限定范围，避免盖住任务栏。</summary>
        public void SetMaximizedBounds(Rectangle bounds)
        {
            MaximizedBounds = bounds;
        }

        /// <summary>当前最大化范围（Form.MaximizedBounds 是受保护属性，此处代读）。
        /// 宿主用它做"最大化落点自愈"的期望范围。</summary>
        public Rectangle MaximizedBoundsValue
        {
            get { return MaximizedBounds; }
        }

        /// <summary>
        /// 窗口真实矩形（Win32 物理像素）。
        /// 多联屏 / 混合 DPI 下它与 Form.Bounds 可能不是同一口径，
        /// 判断"窗口到底在不在屏幕上"必须用这个。
        /// </summary>
        public Rectangle NativeBounds
        {
            get
            {
                NativeWindow.RECT r;
                if (NativeWindow.TryGetWindowRect(Handle, out r)) return r.ToRectangle();
                return Bounds;
            }
        }

        /// <summary>窗口与所在显示器工作区的重合比例（0~100）。最大化跑飞时接近 0。</summary>
        public int NativeVisiblePercent
        {
            get { return NativeWindow.VisiblePercent(Handle); }
        }

        /// <summary>窗口在屏幕几何上是否找得着（跑到屏幕外 = false）。</summary>
        public bool IsNativeOnScreen
        {
            get { return NativeWindow.IsOnScreen(Handle, 200, 120); }
        }

        public LibraryControl BlockControl
        {
            get { return _control; }
        }

        /// <summary>记下 CAD 主窗口句柄，供落点跑偏时参照它所在的显示器。</summary>
        internal IntPtr HostWindowHandle
        {
            get { return _hostWindow; }
            set { _hostWindow = value; }
        }

        /// <summary>
        /// 无论外面谁（例如 ShowModelessDialog）动过窗口样式，
        /// 创建时都强制去掉 WS_CAPTION，保证不会出现系统标题栏。
        /// </summary>
        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_CAPTION = 0x00C00000;
                CreateParams cp = base.CreateParams;
                cp.Style &= ~WS_CAPTION;
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 双保险：显示后再断言一次无边框
            try { if (FormBorderStyle != FormBorderStyle.None) FormBorderStyle = FormBorderStyle.None; }
            catch { }
        }

        private void OnBoundsMaybeChanged(object sender, EventArgs e)
        {
            if (_capturingBounds) return;
            if (WindowState != FormWindowState.Normal) return;

            _capturingBounds = true;
            try
            {
                EventHandler handler = BoundsChangedByUser;
                if (handler != null) handler(this, EventArgs.Empty);
            }
            finally
            {
                _capturingBounds = false;
            }
        }

        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            SyncScreenState();
        }

        /// <summary>
        /// 跨屏检测 + 最大化范围跟随当前屏幕。
        ///
        /// 多联屏错乱的两个根源都在这里堵住：
        /// 1. 无边框窗口的最大化范围（MaximizedBounds）只在切换时赋值一次——
        ///    之后窗口被拖到另一块屏，系统仍按旧屏的工作区摆放最大化窗口，
        ///    结果窗口"最大化"了却出现在另一块屏 / 尺寸和屏幕对不上。
        ///    这里在移动时实时把 MaximizedBounds 同步成当前屏的工作区。
        /// 2. 跨 DPI 屏（如主屏 100% + 副屏 150%）后 GDI 内容经常不重画，
        ///    表现为整个窗口只剩背景色的大白块。跨屏时强制 PerformLayout +
        ///    整窗 Invalidate，把内容重新画出来。
        /// </summary>
        private void SyncScreenState()
        {
            if (_screenSyncBusy) return;
            _screenSyncBusy = true;

            try
            {
                Screen now = null;
                try { now = Screen.FromControl(this); }
                catch { }   // 句柄未创建等场合取不到，按"没换屏"处理

                string name = now != null ? now.DeviceName : null;

                // 注意：这里**不再**把 MaximizedBounds 改成 WinForms 口径的工作区。
                // 窗口是 Per-Monitor V2 感知，与系统交换的是物理像素；而 WinForms
                // 的 Screen.WorkingArea 是逻辑坐标。两者混用会把最大化窗口摆到
                // 屏幕外（用户在双屏下"点最大化窗口消失"的根因）。
                // 现在最大化落点一律由 WM_GETMINMAXINFO 用 Win32 物理坐标给出。

                bool switched = !string.Equals(name, _lastScreenName, StringComparison.OrdinalIgnoreCase);
                _lastScreenName = name;

                if (switched)
                {
                    try { PerformLayout(); } catch { }
                    Invalidate(true);

                    EventHandler handler = ScreenSwitched;
                    if (handler != null) handler(this, EventArgs.Empty);
                }
            }
            catch
            {
                // 跨屏检测失败不影响窗口本身的移动
            }
            finally
            {
                _screenSyncBusy = false;
            }
        }

        /// <summary>还原成 Normal 后，Windows 会把窗口恢复到还原矩形。
        /// 最大化状态结束时同样会触发 BoundsChangedByUser，但状态不是 Normal 会被跳过。</summary>
        protected override void WndProc(ref Message m)
        {
            // 最大化落点由窗口自己说了算：系统在摆放最大化窗口前一定会问
            // WM_GETMINMAXINFO，这里直接回答"窗口当前所在显示器的工作区"
            // （Win32 物理像素，与窗口同一口径）。
            //
            // 这是多联屏下"点最大化窗口就消失"的根本解法：以前靠
            // MaximizedBounds（WinForms 逻辑坐标）指定范围，宿主 / DPI 缩放
            // 一插手就会把窗口摆到别的屏幕甚至屏幕外。现在无论窗口在哪块屏、
            // 缩放比是多少，最大化都必然铺满它自己所在的那块屏。
            if (m.Msg == NativeWindow.WM_GETMINMAXINFO)
            {
                // 先交给 WinForms 填最小尺寸等限制，再把最大化落点覆盖成
                // 指定显示器的工作区（后写覆盖前者）
                base.WndProc(ref m);

                // 常规：铺满窗口自己所在的显示器。
                // 例外：窗口自身已经跑出屏幕（分屏 / 混合 DPI 落点跑偏）时，
                // 参照 CAD 主窗口所在的显示器，保证最大化后一定在用户眼前这块屏。
                IntPtr target = Handle;
                if (_hostWindow != IntPtr.Zero && NativeWindow.VisiblePercent(Handle) < 50)
                    target = _hostWindow;

                NativeWindow.ApplyMaxBoundsToMonitor(target, m.LParam);
                m.Result = IntPtr.Zero;
                return;
            }

            // 双击标题栏：HTCAPTION 拖动（TitleBar.StartWindowDrag）的副产品。
            // 必须吞掉，否则系统会对窗口做"原生"最大化/还原，
            // 与插件手动计算的最大化状态失步（窗口卡在全屏的老 BUG）。
            if (m.Msg == WM_NCLBUTTONDBLCLK && m.WParam.ToInt32() == HTCAPTION)
            {
                RaiseCaptionToggle();
                return;
            }

            // Aero Snap：把标题栏拖到屏幕顶部 / 边缘时系统发 SC_MAXIMIZE，
            // 还原时发 SC_RESTORE。同样拦截并路由到自己的状态机。
            //
            // 两个例外必须放行，否则窗口状态会串台：
            //   1. 代码正在改 WindowState（SuppressStateToggle）——交回系统，按原意生效；
            //   2. 当前是最小化、用户点任务栏 / 系统菜单还原（SC_RESTORE）——
            //      必须让系统正常还原成 Normal。以前这里也路由到 ToggleMaximize，
            //      而 ToggleMaximize 看到"非最大化"就切到最大化，
            //      于是"从任务栏还原"直接变成铺满全屏，就是窗口莫名最大化的根因。
            if (m.Msg == WM_SYSCOMMAND && !SuppressStateToggle)
            {
                int sc = (int)(m.WParam.ToInt64() & 0xFFF0);

                bool handled = false;
                if (sc == SC_MAXIMIZE)
                    handled = true;
                else if (sc == SC_RESTORE && WindowState == FormWindowState.Maximized)
                    handled = true;

                if (handled)
                {
                    RaiseCaptionToggle();
                    return;
                }
            }

            // 只在 Normal 状态下提供边缘缩放；最大化/最小化时交回默认处理
            if (m.Msg == WM_NCHITTEST && WindowState == FormWindowState.Normal)
            {
                // LParam 打包了屏幕坐标（带符号的 16 位两个）
                int raw = m.LParam.ToInt32();
                short sx = (short)(raw & 0xFFFF);
                short sy = (short)((raw >> 16) & 0xFFFF);
                Point client = PointToClient(new Point(sx, sy));

                // 标题栏按钮优先：按钮顶边离窗口上沿只有几个像素，
                // 若先判缩放热区，按钮最上面那一小条会变成"拖上边框"，
                // 点上去窗口不最大化反而开始改高度（v1.49.0 修）。
                if (IsOverTitleBarButton(client))
                {
                    base.WndProc(ref m);
                    return;
                }

                bool overLeft = client.X <= GripSize;
                bool overRight = client.X >= ClientSize.Width - GripSize;
                bool overTop = client.Y <= GripSize;
                bool overBottom = client.Y >= ClientSize.Height - GripSize;

                if (overTop && overLeft) { m.Result = (IntPtr)HTTOPLEFT; return; }
                if (overTop && overRight) { m.Result = (IntPtr)HTTOPRIGHT; return; }
                if (overBottom && overLeft) { m.Result = (IntPtr)HTBOTTOMLEFT; return; }
                if (overBottom && overRight) { m.Result = (IntPtr)HTBOTTOMRIGHT; return; }
                if (overLeft) { m.Result = (IntPtr)HTLEFT; return; }
                if (overRight) { m.Result = (IntPtr)HTRIGHT; return; }
                if (overTop) { m.Result = (IntPtr)HTTOP; return; }
                if (overBottom) { m.Result = (IntPtr)HTBOTTOM; return; }

                // 其余区域交给默认处理（子控件自己会收到消息，不会走到这里）
            }

            // 窗口跨到不同 DPI 的屏幕（Per-Monitor V2 生效时系统会发这条消息）：
            // 按系统建议矩形重摆位置和尺寸。net47 的 WinForms 不会自动处理，
            // 不处理的话窗口在新屏上保持旧物理尺寸（过大或过小）。
            if (m.Msg == WM_DPICHANGED)
            {
                try
                {
                    RECT sug = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                    SetBounds(sug.Left, sug.Top, sug.Right - sug.Left, sug.Bottom - sug.Top);
                }
                catch { }
                return;     // 已按系统建议落位，交给默认处理反而会被改回去
            }

            base.WndProc(ref m);
        }

        /// <summary>WM_DPICHANGED 的 lParam 是系统建议的 RECT（左/上/右/下）。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        /// <summary>
        /// 判断客户区坐标是否落在标题栏的某个按钮（最小化 / 最大化·还原 / 关闭）上。
        /// 落在按钮上时不参与边缘缩放热区，保证单击按钮一定被按钮自己收到。
        /// </summary>
        private bool IsOverTitleBarButton(Point client)
        {
            try
            {
                if (_bar == null) return false;

                foreach (Control child in _bar.Controls)
                {
                    if (child == null || !child.Visible) continue;
                    if (child.Width <= 0 || child.Height <= 0) continue;

                    // 按钮的 Bounds 相对标题栏，标题栏的 Bounds 相对本窗口客户区
                    Rectangle rect = new Rectangle(
                        _bar.Left + child.Left,
                        _bar.Top + child.Top,
                        child.Width,
                        child.Height);

                    if (rect.Contains(client)) return true;
                }
            }
            catch
            {
                // 探测失败就按"不在按钮上"处理，退回原来的热区逻辑
            }

            return false;
        }

        private void RaiseCaptionToggle()
        {
            try
            {
                EventHandler handler = CaptionMaximizeToggle;
                if (handler != null) handler(this, EventArgs.Empty);
            }
            catch
            {
                // 事件里的异常绝不能冒到 AutoCAD 主循环
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // 只隐藏不销毁：BLOCKLIB 还能再次打开（LibraryPalette 控制）
            base.OnFormClosed(e);
        }
    }
}
