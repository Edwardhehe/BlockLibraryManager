using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BlockLibraryManager
{
    /// <summary>
    /// 标题栏上的一个按钮（最小化 / 最大化·还原 / 关闭）。
    /// 
    /// 图标全部用 GDI 画出来，不用任何特殊字符——
    /// 这样既不受字体影响（有些机器上 "×" "□" 会变成豆腐块），
    /// 在 AutoCAD 2010 到 2027 的所有版本上表现也一致。
    /// </summary>
    internal class TitleBarButton : Control
    {
        public enum ButtonKind
        {
            Minimize,
            Maximize,
            Restore,
            Close
        }

        private ButtonKind _kind;
        private bool _hover;
        private bool _pressed;
        /// <summary>本轮"按下→抬起"是否已经执行过动作，防止一次点击触发两遍。</summary>
        private bool _pressConsumed;

        public TitleBarButton(ButtonKind kind)
        {
            _kind = kind;

            SetStyle(ControlStyles.UserPaint, true);
            SetStyle(ControlStyles.AllPaintingInWmPaint, true);
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
            SetStyle(ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.Selectable, false);

            Size = new Size(34, 26);
            Margin = Padding.Empty;
            BackColor = Color.Transparent;
            TabStop = false;
            Cursor = Cursors.Default;
        }

        public ButtonKind Kind
        {
            get { return _kind; }
            set
            {
                if (_kind == value) return;
                _kind = value;
                Invalidate();
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            _pressed = false;
            _pressConsumed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        //
        // 触发时机：左键"按下"就立刻执行，不用等抬起（v1.49.0）。
        //
        // 以前的写法是 MouseDown 只记状态、等 MouseUp 才触发，
        // 但 AutoCAD 给非模态窗口装了消息过滤器（标题栏拖动那节注释里提过），
        // WM_LBUTTONUP 有时到不了按钮 —— 用户点一下窗口毫无反应，
        // 连点两下才切一次，看起来就像"这个按钮要双击才管用"。
        // 现在按下即执行，单击一定生效；
        // MouseUp 再来时因为 _pressConsumed 已置位而不会重复执行一遍。
        //
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _pressed = true;
                _pressConsumed = false;
                Invalidate();
                FireClick();
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool wasPressed = _pressed;
            _pressed = false;
            Invalidate();
            base.OnMouseUp(e);

            // 按下那一下万一被宿主吞掉（过滤器只放行了抬起），这里补一次；
            // 正常情况 _pressConsumed 已为 true，直接跳过。
            if (wasPressed && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location))
            {
                FireClick();
            }

            _pressConsumed = false;
        }

        /// <summary>执行一次动作；同一轮按下只认第一次。</summary>
        private void FireClick()
        {
            if (_pressConsumed) return;
            _pressConsumed = true;
            OnClick(EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle rect = ClientRectangle;

            // 悬停 / 按下时的底色；关闭按钮悬停用红色警示
            Color back = Color.Transparent;
            if (_hover || _pressed)
            {
                if (_kind == ButtonKind.Close)
                {
                    int alpha = _pressed ? 235 : 200;
                    back = Color.FromArgb(alpha, 196, 43, 28);
                }
                else
                {
                    int alpha = _pressed ? 70 : 40;
                    back = Color.FromArgb(alpha, 255, 255, 255);
                }
            }

            if (back.A > 0)
            {
                using (SolidBrush brush = new SolidBrush(back))
                {
                    g.FillRectangle(brush, rect);
                }
            }

            g.SmoothingMode = SmoothingMode.AntiAlias;

            bool closeHoverRed = _kind == ButtonKind.Close && back.A > 0 &&
                                 (back.R > 150 && back.G < 120);
            Color ink = closeHoverRed ? Color.White : ForeColor;

            using (Pen pen = new Pen(ink, 1.3f))
            {
                switch (_kind)
                {
                    case ButtonKind.Minimize:
                        // 底部一条横线
                        g.DrawLine(pen, 12, 17, 22, 17);
                        break;

                    case ButtonKind.Maximize:
                        // 空心矩形
                        g.DrawRectangle(pen, 12, 9, 10, 8);
                        break;

                    case ButtonKind.Restore:
                        // 双层矩形：后面一个 + 前面一个的部分边
                        g.DrawRectangle(pen, 14, 8, 8, 4);
                        using (GraphicsPath path = new GraphicsPath())
                        {
                            path.AddLine(14, 13, 14, 19);
                            path.AddLine(14, 19, 22, 19);
                            path.AddLine(22, 19, 22, 13);
                            path.AddLine(22, 13, 14, 13);
                            g.DrawPath(pen, path);
                        }
                        break;

                    case ButtonKind.Close:
                        // 交叉的两条线
                        g.DrawLine(pen, 12, 10, 22, 18);
                        g.DrawLine(pen, 22, 10, 12, 18);
                        break;
                }
            }

            base.OnPaint(e);
        }
    }

    /// <summary>
    /// 标题栏左侧的 CAD 应用图标：从当前宿主进程的 exe（acad.exe / zwcad.exe /
    /// gcad.exe / bricscad.exe）提取图标 —— 装的是哪家 CAD 就显示哪家的图标，
    /// 天然"跟随当前版本 CAD"。
    /// 尺寸随标题栏行高自适应（正方形，上下留边），文字用 Dock 排在图标后面，
    /// 互不遮挡。
    /// </summary>
    internal sealed class TitleIconBox : Control
    {
        private readonly Icon _icon;

        public TitleIconBox(Icon icon)
        {
            _icon = icon;

            SetStyle(ControlStyles.UserPaint, true);
            SetStyle(ControlStyles.AllPaintingInWmPaint, true);
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
            SetStyle(ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.Selectable, false);

            Dock = DockStyle.Left;
            Width = 26;
            BackColor = Color.Transparent;
            TabStop = false;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);

            // 高度随标题栏行高变化（Dock=Left 自动撑满），宽度跟随取同样大小；
            // 夹在 18~30，标题栏再高图标也不无限放大。
            // 判断过相等再赋值，避免 Resize 处理器里改尺寸造成无限递归。
            int target = Math.Max(18, Math.Min(30, Height));
            if (Width != target) Width = target;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_icon == null) return;

            try
            {
                // 上下左右各留 3px，居中画一个正方形区域
                int side = Math.Max(8, Math.Min(ClientSize.Width, ClientSize.Height) - 6);
                int x = (ClientSize.Width - side) / 2;
                int y = (ClientSize.Height - side) / 2;
                e.Graphics.DrawIcon(_icon, new Rectangle(x, y, side, side));
            }
            catch { }
        }
    }

    /// <summary>
    /// 面板顶部自定义标题栏：左边显示名称，右边是 最小化 / 最大化·还原 / 关闭 三个按钮。
    /// 宿主（PaletteSet）的行为由外部通过事件接管，这里只负责外观与点击转发。
    /// </summary>
    internal class TitleBar : Panel
    {
        public event EventHandler MinimizeClicked;
        public event EventHandler MaximizeClicked;
        public event EventHandler CloseClicked;

        private readonly Label _label;
        private readonly TitleBarButton _btnMinimize;
        private readonly TitleBarButton _btnMaximize;
        private readonly TitleBarButton _btnClose;
        private readonly ToolTip _tip;

        private static Icon _cadIcon;
        private static bool _cadIconTried;

        /// <summary>
        /// 提取当前 CAD 主程序的图标（只做一次，结果静态缓存）。
        /// 插件就跑在 CAD 进程里，MainModule.FileName 就是 acad.exe / zwcad.exe /
        /// gcad.exe / bricscad.exe，ExtractAssociatedIcon 拿到的就是该版本 CAD
        /// 在资源管理器里显示的那个图标。失败（权限 / 路径异常）返回 null，
        /// 标题栏就退回"无图标"，不影响任何功能。
        /// </summary>
        private static Icon GetCadAppIcon()
        {
            if (_cadIconTried) return _cadIcon;
            _cadIconTried = true;

            try
            {
                string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                if (!string.IsNullOrEmpty(exe) && System.IO.File.Exists(exe))
                    _cadIcon = Icon.ExtractAssociatedIcon(exe);
            }
            catch
            {
                _cadIcon = null;
            }
            return _cadIcon;
        }

        /// <summary>标题栏渐变起止色与文字色（主题切换时更新，默认跟随系统）。</summary>
        private Color _chromeBack1 = SystemColors.GradientActiveCaption;
        private Color _chromeBack2 = SystemColors.ActiveCaption;

        /// <summary>主题切换时更新标题栏配色（渐变背景 + 文字/按钮线条颜色）。</summary>
        public void ApplyChrome(Color back, Color fore)
        {
            _chromeBack1 = ControlPaint.Light(back, 0.6f);
            _chromeBack2 = back;

            BackColor = back;
            ForeColor = fore;
            _label.ForeColor = fore;
            _btnMinimize.ForeColor = fore;
            _btnMaximize.ForeColor = fore;
            _btnClose.ForeColor = fore;

            Invalidate();
        }

        public TitleBar(string title)
        {
            Dock = DockStyle.Top;
            Height = 32;
            Padding = new Padding(0, 0, 2, 1);
            BackColor = SystemColors.ActiveCaption;
            ForeColor = SystemColors.ActiveCaptionText;

            SetStyle(ControlStyles.UserPaint, true);
            SetStyle(ControlStyles.AllPaintingInWmPaint, true);
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
            SetStyle(ControlStyles.ResizeRedraw, true);

            Font sysFont = SystemFonts.CaptionFont != null ? SystemFonts.CaptionFont : SystemFonts.MessageBoxFont;
            Font = new Font(sysFont.FontFamily, sysFont.SizeInPoints > 0 ? sysFont.SizeInPoints : 9f, FontStyle.Bold);

            _label = new Label();
            _label.Text = title;
            _label.Dock = DockStyle.Fill;
            _label.AutoSize = false;
            _label.TextAlign = ContentAlignment.MiddleLeft;
            // 左侧留给 CAD 图标（图标控件自身已留 3px 右缘），文字不再额外缩进
            _label.Padding = new Padding(2, 0, 0, 0);
            _label.ForeColor = SystemColors.ActiveCaptionText;
            _label.BackColor = Color.Transparent;
            _label.Font = Font;

            _btnMinimize = new TitleBarButton(TitleBarButton.ButtonKind.Minimize);
            _btnMinimize.Dock = DockStyle.Right;
            _btnMinimize.ForeColor = SystemColors.ActiveCaptionText;

            _btnMaximize = new TitleBarButton(TitleBarButton.ButtonKind.Maximize);
            _btnMaximize.Dock = DockStyle.Right;
            _btnMaximize.ForeColor = SystemColors.ActiveCaptionText;

            _btnClose = new TitleBarButton(TitleBarButton.ButtonKind.Close);
            _btnClose.Dock = DockStyle.Right;
            _btnClose.ForeColor = SystemColors.ActiveCaptionText;

            _btnMinimize.Click += new EventHandler(delegate(object s, EventArgs e) { Raise(MinimizeClicked); });
            _btnMaximize.Click += new EventHandler(delegate(object s, EventArgs e) { Raise(MaximizeClicked); });
            _btnClose.Click += new EventHandler(delegate(object s, EventArgs e) { Raise(CloseClicked); });

            // 标题文字盖住了标题栏的大部分区域，鼠标事件要转发到拖动逻辑
            _label.MouseDown += new MouseEventHandler(OnTitleMouseDown);

            _tip = new ToolTip();
            _tip.SetToolTip(_btnMinimize, "最小化（折叠）");
            _tip.SetToolTip(_btnMaximize, "最大化 / 还原");
            _tip.SetToolTip(_btnClose, "关闭面板");

            // 注意添加顺序：Fill 先加；三个按钮按 最小化 → 最大化 → 关闭 依次加，
            // WinForms 的 Right 停靠按加入顺序反向排列，最终从左到右正好是 _ □ X。
            // CAD 图标最后加（Dock=Left）—— 布局时它先于标题文字占住最左侧，
            // 文字自动从图标右侧开始，两者互不遮挡。
            Controls.Add(_label);
            Controls.Add(_btnMinimize);
            Controls.Add(_btnMaximize);
            Controls.Add(_btnClose);

            Icon appIcon = GetCadAppIcon();
            if (appIcon != null)
                Controls.Add(new TitleIconBox(appIcon));
        }

        public string TitleText
        {
            get { return _label.Text; }
            set { _label.Text = value; }
        }

        /// <summary>切换最大化 / 还原按钮的图标。</summary>
        public void SetMaximized(bool maximized)
        {
            try
            {
                _btnMaximize.Kind = maximized
                    ? TitleBarButton.ButtonKind.Restore
                    : TitleBarButton.ButtonKind.Maximize;
                _tip.SetToolTip(_btnMaximize, maximized ? "还原" : "最大化");
            }
            catch { }
        }

        // ---------------- 窗口拖动（无边框窗口） ----------------
        //
        // 必须用 WM_NCLBUTTONDOWN + HTCAPTION 的系统拖动：
        //   AutoCAD 给非模态窗口装了消息过滤器，"自己捕获鼠标跟拖"的方式
        //   鼠标一出窗体就被 CAD 重定向，拖动完全失效（v1.33 已验证的 BUG）。
        //   HTCAPTION 走系统模态移动循环，任何宿主下都可靠。
        //
        //   双击标题栏的"系统原生最大化/还原"失步问题（v1.32 老 BUG）另由
        //   LibraryForm.WndProc 拦截 WM_NCLBUTTONDBLCLK / WM_SYSCOMMAND 解决，
        //   全部路由到 LibraryPalette.ToggleMaximize，状态机始终一致。

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HT_CAPTION = 0x2;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) StartWindowDrag();
        }

        private void OnTitleMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) StartWindowDrag();
        }

        private void StartWindowDrag()
        {
            try
            {
                Form host = FindForm();
                if (host == null) return;
                if (host.WindowState != FormWindowState.Normal) return;
                if (LibraryPalette.IsManualMaximized) return;   // 最大化状态下不允许拖动

                ReleaseCapture();
                SendMessage(host.Handle, WM_NCLBUTTONDOWN, (IntPtr)HT_CAPTION, IntPtr.Zero);
            }
            catch (System.Exception ex)
            {
                BlmLog.Write("拖动标题栏失败", ex);
            }
        }

        private void Raise(EventHandler handler)
        {
            if (handler != null)
            {
                try { handler(this, EventArgs.Empty); }
                catch (System.Exception ex) { BlmLog.Write("标题栏按钮处理失败", ex); }
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 标题栏用渐变，从标题色过渡到普通控件色，底部再画一条分隔线
            try
            {
                Rectangle rect = ClientRectangle;
                using (LinearGradientBrush brush = new LinearGradientBrush(
                    rect,
                    _chromeBack1,
                    _chromeBack2,
                    LinearGradientMode.Horizontal))
                {
                    e.Graphics.FillRectangle(brush, rect);
                }

                using (Pen pen = new Pen(SystemColors.ControlDarkDark))
                {
                    e.Graphics.DrawLine(pen, 0, rect.Height - 1, rect.Width, rect.Height - 1);
                }
            }
            catch
            {
                base.OnPaintBackground(e);
            }
        }

        // 双击标题栏的最大化/还原由 LibraryForm.WndProc 拦截
        // WM_NCLBUTTONDBLCLK / WM_SYSCOMMAND 统一路由，这里不再处理。

        protected override void Dispose(bool disposing)
        {
            if (disposing && _tip != null) _tip.Dispose();
            base.Dispose(disposing);
        }
    }
}
