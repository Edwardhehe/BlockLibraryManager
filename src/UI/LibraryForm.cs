using System;
using System.Drawing;
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

        public LibraryControl BlockControl
        {
            get { return _control; }
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

        /// <summary>还原成 Normal 后，Windows 会把窗口恢复到还原矩形。
        /// 最大化状态结束时同样会触发 BoundsChangedByUser，但状态不是 Normal 会被跳过。</summary>
        protected override void WndProc(ref Message m)
        {
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
            if (m.Msg == WM_SYSCOMMAND)
            {
                int sc = (int)(m.WParam.ToInt64() & 0xFFF0);
                if (sc == SC_MAXIMIZE || sc == SC_RESTORE)
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

            base.WndProc(ref m);
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
