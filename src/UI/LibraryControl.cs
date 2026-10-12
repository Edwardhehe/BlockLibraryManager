using System;
using System.Drawing;
using System.Collections.Generic;
using System.Windows.Forms;

namespace BlockLibraryManager
{
    /// <summary>
    /// 图库管理器的主界面控件（WinForms UserControl，宿主是 AutoCAD 的 PaletteSet）。
    /// 
    /// 布局：
    ///   标题栏（左：图库管理器名称；右：最小化 / 最大化·还原 / 关闭）
    ///   顶部工具栏（添加 / 刷新 / 上级 / 视图 / 设置 / 搜索 / 关于）
    ///   中部左右分栏（左边分类树，右边图块缩略图列表）
    ///   底部插入参数（比例 / 旋转 / 炸开 / 更新定义）+ 插入按钮
    /// 
    /// 本文件负责控件创建与布局；数据加载见 LibraryControl.Logic.cs，
    /// 事件响应见 LibraryControl.Events.cs。
    /// </summary>
    public partial class LibraryControl : UserControl
    {
        // ---- 数据 ----
        private LibraryConfig _config;
        private LibraryIndex _index;
        private ThumbnailService _thumbnails;

        private List<BlockItem> _currentBlocks = new List<BlockItem>();
        private string _searchKeyword;

        // ---- 控件 ----
        private ToolStrip _toolStrip;
        private ToolStripButton _btnAdd;

        /// <summary>"显示"下拉按钮：分"目录列表"与"插入参数面板"两级，分别控制两个窗体的显示/隐藏。</summary>
        private ToolStripDropDownButton _btnShow;

        /// <summary>"显示"下拉里的两个开关项（与视图菜单的同名项状态保持同步）。</summary>
        private ToolStripMenuItem _miShowTree;
        private ToolStripMenuItem _miShowParams;
        private ToolStripButton _btnRefresh;
        private ToolStripButton _btnSettings;
        private ToolStripButton _btnLanguage;
        private ToolStripButton _btnTheme;
        private ToolStripDropDownButton _btnViewMode;
        private ToolStripButton _btnBatch;

        /// <summary>搜索框（带放大镜图标）。输入文字即在全图库范围内匹配 dwg 文件名。</summary>
        private TextBox _txtSearch;

        /// <summary>搜索框宿主面板（内含放大镜图标 + 文本框），挂在工具栏上。</summary>
        private Panel _searchPanel;

        /// <summary>输入防抖：停止输入 300ms 后才真正执行搜索，避免每个按键都重刷列表。</summary>
        private System.Windows.Forms.Timer _searchTimer;

        private ToolStripButton _btnAbout;

        private SplitContainer _split;
        private TreeView _tree;
        private ListView _list;
        private ImageList _imageList;

        /// <summary>
        /// 当前在目录树"此电脑"分支中浏览的文件夹。空串 = 未在浏览电脑文件夹。
        /// </summary>
        private string _pcSelectedPath = string.Empty;

        /// <summary>左侧目录列表的小标题栏（标题"目录列表" + 关闭按钮）。</summary>
        private Panel _treeHeader;

        /// <summary>"目录列表"标题栏上的标题文字（语言切换时更新）。</summary>
        private Label _treeHeaderTitle;

        /// <summary>"目录列表"标题栏右上角的 × 关闭按钮（主题切换时更新配色）。</summary>
        private Button _treeHeaderClose;

        /// <summary>分类树节点图标（资源管理器原生文件夹，关闭/打开两态）。</summary>
        private ImageList _treeIcons;

        /// <summary>_treeIcons 里关闭/打开文件夹图标（Explorer 原生）的键名。</summary>
        private const string IconFolderClosed = "folderClosed";
        private const string IconFolderOpen = "folderOpen";

        /// <summary>希望给左侧分类树留的宽度（像素）。控件有真实尺寸后生效。</summary>
        private int _treeWidth = 180;

        /// <summary>是否已经成功设置过 SplitterDistance（成功一次后就不再干预）。</summary>
        private bool _splitterApplied;

        private Panel _bottomPanel;
        private Label _lblScale;
        private TextBox _txtScaleX;
        private CheckBox _chkExplode;
        private CheckBox _chkUpdateDef;
        private Label _lblHint;

        /// <summary>左侧内部的上下分割：上目录树，下"插入参数设置"面板，默认各占一半高度。</summary>
        private SplitContainer _leftSplit;

        /// <summary>是否已成功设置过 _leftSplit 的分割位置。</summary>
        private bool _leftSplitApplied;

        /// <summary>
        /// 左侧上下分割（目录树 / 插入参数面板）上次的位置（目录树高度）。
        /// 0 = 从未保存过，按默认各占一半。
        /// </summary>
        private int _leftSplitDistance;

        /// <summary>左侧目录列表下方的插入参数设置面板（标题栏 + 比例/旋转/图层/颜色 各一行）。</summary>
        private Panel _paramPanel;
        private CheckBox _chkLayerZero;
        private CheckBox _chkColorByLayer;

        /// <summary>参数面板的标题栏（与"目录列表"标题栏同色同样式）。</summary>
        private Panel _paramHeaderPanel;

        /// <summary>参数面板标题栏上的标题文字（语言切换时更新）。</summary>
        private Label _paramTitleLabel;

        /// <summary>参数面板标题栏右上角的 × 关闭按钮（主题切换时更新配色）。</summary>
        private Button _paramHeaderClose;

        /// <summary>参数面板的悬浮提示组件（语言切换时更新文案）。</summary>
        private ToolTip _paramTips;

        /// <summary>图层/颜色/分解行复选框后面的提示文字。</summary>
        private Label _lblLayerHint;
        private Label _lblColorHint;
        private Label _lblExplodeHint;

        /// <summary>"插入图形后关闭窗口"行：勾选后每次插入成功自动收起图库窗口（配置持久化）。</summary>
        private CheckBox _chkCloseAfter;
        private Label _lblCloseName;
        private Label _lblCloseHint;

        /// <summary>图层/颜色/分解行的参数名文字（与勾选框分开，保证各行值列对齐）。</summary>
        private Label _lblLayerName;
        private Label _lblColorName;
        private Label _lblExplodeName;

        private StatusStrip _statusBar;
        private ToolStripStatusLabel _lblStatus;
        private ToolStripStatusLabel _lblThumbInfo;
        private ToolStripProgressBar _pbThumbs;

        /// <summary>本轮需要生成缩略图的图块总数（用于底部进度条）。</summary>
        private int _thumbTotal;

        /// <summary>本轮已经生成好的缩略图数量。</summary>
        private int _thumbDone;

        /// <summary>本轮的"已全部生成"提示是否已经播过（避免每帧重复刷状态栏）。</summary>
        private bool _thumbRoundAnnounced;

        private ContextMenuStrip _menuList;
        private ContextMenuStrip _menuTree;
        private ToolStripMenuItem _miTreeOpenFolder;
        private ToolStripMenuItem _miTreeRemove;

        /// <summary>目录树右键菜单里的"添加到收藏夹 / 从收藏夹移除"项。</summary>
        private ToolStripMenuItem _miTreeFavorite;

        /// <summary>目录树右键菜单里的"添加到图库"项（"此电脑"分支的文件夹用）。</summary>
        private ToolStripMenuItem _miTreeAddLib;

        /// <summary>右侧列表右键菜单里的"粘贴入库"项。</summary>
        private ToolStripMenuItem _miListPaste;

        /// <summary>右侧列表右键菜单里的"重命名块"项：修改选中图块的 DWG 文件名。</summary>
        private ToolStripMenuItem _miListRename;
        private ToolStripMenuItem _miListOpenDrawing;

        /// <summary>右侧列表右键菜单里的"白底缩略图 / 黑底缩略图"切换项。</summary>
        private ToolStripMenuItem _miThumbWhite;
        private ToolStripMenuItem _miThumbBlack;

        /// <summary>右键菜单的目标节点（TreeView 右键不会自动改变 SelectedNode，需单独记录）。</summary>
        private TreeNode _menuTargetNode;

        public LibraryControl()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 初始化数据与服务。必须在主线程调用一次（创建 PaletteSet 之后）。
        /// </summary>
        public void Initialize(LibraryConfig config)
        {
            if (config == null) throw new ArgumentNullException("config");
            _config = config;

            // 语言设置要在创建/刷新任何界面文字之前生效
            LanguageService.ConfiguredLanguage = config.Language == null
                ? string.Empty
                : config.Language;

            // 恢复上次记忆的目录列表宽度（没有记录过时保持默认 180）
            if (config.TreePanelWidth >= 100)
                _treeWidth = config.TreePanelWidth;

            // 恢复上次记忆的左侧上下分割位置（目录树 / 插入参数面板）
            _leftSplitDistance = Math.Max(0, config.LeftSplitDistance);

            if (_thumbnails != null)
            {
                _thumbnails.ThumbnailReady -= OnThumbnailReady;
                _thumbnails.Dispose();
            }

            _thumbnails = new ThumbnailService(_config);
            _thumbnails.WhiteBackground = IsWhiteThumbBackground;
            _thumbnails.ThumbnailReady += OnThumbnailReady;

            _index = new LibraryIndex(_config, new Func<string, IList<string>>(BlockInsertService.GetBlockNames));

            ApplyConfigToUi();
            ApplyLanguage();
            ApplyTheme();

            // 整盘扫描推迟到窗口真正显示出来之后再做：
            // 一来不会在 BLOCKLIB 命令执行过程中把 AutoCAD 卡住，
            // 二来可以显示进度条（见 ScheduleInitialLoad / OnVisibleChanged）。
            _pendingInitialLoad = true;
            if (Visible && IsHandleCreated) ScheduleInitialLoad();
        }

        /// <summary>是否还欠着一次首次加载。</summary>
        private bool _pendingInitialLoad;

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible && _pendingInitialLoad) ScheduleInitialLoad();

            // Initialize 阶段控件还没挂到窗体上（FindForm 为空），
            // 窗口真正显示时把主题（含窗口标题栏配色）补刷一次
            if (Visible) ApplyTheme();
        }

        /// <summary>
        /// 延后一点点再开始首次扫描：BeginInvoke 会把这次调用排到消息队列里，
        /// 等窗口先画出来（用户立刻看到界面），再弹进度条做后台扫描。
        /// </summary>
        private void ScheduleInitialLoad()
        {
            if (!_pendingInitialLoad) return;
            if (!IsHandleCreated) return;

            _pendingInitialLoad = false;

            try { BeginInvoke(new MethodInvoker(delegate { ReloadWithProgress(null); })); }
            catch (System.Exception ex) { BlmLog.Write("排定首次加载失败: " + ex.Message); }
        }

        internal void SetStatus(string text)
        {
            if (_lblStatus == null) return;
            try { _lblStatus.Text = text; } catch { }
        }

        /// <summary>按当前语言刷新界面上的固定文字（启动与切换语言时调用）。</summary>
        private void ApplyLanguage()
        {
            try
            {
                // ---- 工具栏 ----
                if (_btnAdd != null)
                {
                    _btnAdd.Text = L.T("添加", "Add");
                    _btnAdd.ToolTipText = L.T("浏览电脑文件夹，把一个目录加入图库根目录列表",
                        "Browse the computer and add a folder to the library root list");
                }
                if (_btnShow != null)
                {
                    _btnShow.Text = L.T("显示", "Show");
                    _btnShow.ToolTipText = L.T("选择显示或隐藏目录列表 / 插入参数设置面板",
                        "Show or hide the directory list / insert parameter panel");
                }
                if (_miShowTree != null)
                    _miShowTree.Text = L.T("目录列表", "Directory List");
                if (_miShowParams != null)
                    _miShowParams.Text = L.T("插入参数面板", "Insert Parameters");
                if (_btnRefresh != null) _btnRefresh.Text = L.T("刷新", "Refresh");
                if (_btnBatch != null)
                {
                    _btnBatch.Text = L.T("批量入库", "Batch Import");
                    _btnBatch.ToolTipText = L.T("选择目标文件夹后框选图块，按块名逐个保存为 AutoCAD 2007 格式 DWG 入库",
                        "Select a target folder, then window-select blocks to save each one as an AutoCAD 2007 DWG");
                }
                if (_btnSettings != null) _btnSettings.Text = L.T("设置", "Settings");
                if (_btnTheme != null)
                {
                    _btnTheme.Text = L.T("主题", "Theme");
                    _btnTheme.ToolTipText = BuildThemeTooltip();
                }
                if (_btnLanguage != null)
                {
                    _btnLanguage.Text = L.T("语言", "Language");
                    _btnLanguage.ToolTipText = L.T("设置界面语言（默认跟随系统语言）",
                        "Set the interface language (defaults to the system language)");
                }
                if (_btnAbout != null) _btnAbout.Text = L.T("关于", "About");

                // ---- 视图菜单 ----
                if (_btnViewMode != null && _btnViewMode.DropDownItems != null)
                {
                    foreach (ToolStripItem item in _btnViewMode.DropDownItems)
                    {
                        ToolStripMenuItem mi = item as ToolStripMenuItem;
                        if (mi == null) continue;

                        if (mi.Tag is GalleryView)
                        {
                            GalleryView mode = (GalleryView)mi.Tag;
                            if (mode == GalleryView.LargeIcon) mi.Text = L.T("大图标", "Large Icons");
                            else if (mode == GalleryView.Details) mi.Text = L.T("详细信息", "Details");
                            else if (mode == GalleryView.List) mi.Text = L.T("列表", "List");
                        }
                    }
                }

                // ---- 目录列表标题栏 ----
                if (_treeHeaderTitle != null)
                    _treeHeaderTitle.Text = L.T("目录列表", "Directory List");

                // ---- 插入参数面板 ----
                if (_paramTitleLabel != null)
                    _paramTitleLabel.Text = L.T("插入参数设置", "Insert Parameters");
                if (_lblScale != null) _lblScale.Text = L.T("插入比例", "Insert Scale");
                if (_lblLayerName != null) _lblLayerName.Text = L.T("图层为0", "Layer 0");
                if (_lblColorName != null) _lblColorName.Text = L.T("颜色跟随层", "Color by Layer");
                if (_lblExplodeName != null) _lblExplodeName.Text = L.T("插入后分解图块", "Explode on Insert");
                if (_lblCloseName != null)
                    _lblCloseName.Text = L.T("插入图形后关闭窗口", "Close Panel After Insert");
                if (_lblCloseHint != null)
                    _lblCloseHint.Text = L.T("—— 勾选后插入成功自动关闭图库管理器窗口",
                        "— checked: close the panel automatically after a successful insert");
                if (_chkLayerZero != null) _chkLayerZero.Text = string.Empty;
                if (_chkColorByLayer != null) _chkColorByLayer.Text = string.Empty;
                if (_chkExplode != null) _chkExplode.Text = string.Empty;
                if (_lblLayerHint != null)
                    _lblLayerHint.Text = L.T("—— 勾选后图块归到 0 图层；不勾选使用图块所在图层",
                        "— checked: insert to layer 0; unchecked: keep the block's own layer");
                if (_lblColorHint != null)
                    _lblColorHint.Text = L.T("—— 勾选后颜色跟随图层颜色；不勾选保持图块原有颜色",
                        "— checked: color follows layer color; unchecked: keep the block's own colors");
                if (_lblExplodeHint != null)
                    _lblExplodeHint.Text = L.T("—— 勾选后插入时分解图块；不勾选保持图块整体",
                        "— checked: explode the block on insert; unchecked: keep it as a block");

                if (_paramTips != null)
                {
                    _paramTips.SetToolTip(_chkLayerZero,
                        L.T("勾选：插入的图块归到 0 图层；不勾选：使用图块所在图层",
                            "Checked: insert the block on layer 0; unchecked: keep the block's own layer"));
                    _paramTips.SetToolTip(_chkColorByLayer,
                        L.T("勾选：图块颜色跟随图层颜色；不勾选：保持图块原有颜色",
                            "Checked: colors follow layer colors; unchecked: keep the block's own colors"));
                    _paramTips.SetToolTip(_chkExplode,
                        L.T("勾选：插入时把图块分解为基本图元；不勾选：保持为整体图块",
                            "Checked: explode the block into basic entities on insert; unchecked: insert as a block"));
                }

                // ---- 底部参数行 ----
                if (_chkUpdateDef != null)
                    _chkUpdateDef.Text = L.T("更新已有定义", "Update Existing Definitions");
                if (_lblHint != null)
                    _lblHint.Text = L.T("双击条目 / 右键“插入到当前图形”；也可把 DWG 拖进列表入库",
                        "Double-click an item or right-click \"Insert to Current Drawing\"; drag DWG files here to import");

                // ---- 目录树固定根节点 ----
                if (_tree != null)
                {
                    foreach (TreeNode node in _tree.Nodes)
                    {
                        if (node.Tag == null)
                            node.Text = L.T("全部图库", "All Blocks");
                        else if (ReferenceEquals(node.Tag, RecentTag))
                            node.Text = L.T("最近使用", "Recent");
                        else if (node.Tag is FavoriteTag &&
                                 string.IsNullOrEmpty(((FavoriteTag)node.Tag).Path))
                            node.Text = L.T("收藏", "Favorites");
                        else if (node.Tag is PcTag &&
                                 string.IsNullOrEmpty(((PcTag)node.Tag).Path))
                            node.Text = L.T("此电脑", "This PC");
                    }
                }

                // 文字长度变了，参数面板的提示宽度要重排
                LayoutParamPanel();
                LayoutBottomRow();
            }
            catch
            {
                // 文字刷新失败不影响功能
            }
        }

        internal ThumbnailService Thumbnails
        {
            get { return _thumbnails; }
        }

        /// <summary>
        /// 由 Application.Idle 驱动：处理缩略图队列。
        /// 返回 false 表示队列已空，可以取消订阅以提高空闲时的性能。
        /// </summary>
        public bool ProcessPendingThumbnails()
        {
            if (_thumbnails == null) return false;

            // 面板已关闭 / 最小化：后台不做任何事，把 AutoCAD 主线程完整还给用户
            if (_thumbnails.IsSuspended) return false;

            // 先按"当前屏幕可见区间"把队列重排一次（区间没变就直接返回），
            // 保证后台一直在做用户马上要看的那几张，而不是闷头从头做到尾。
            PrioritizeVisibleThumbnails();

            // 刚才那批缩略图的重绘请求在这里一次性刷出去
            FlushListPaint();

            if (_thumbnails.PendingCount <= 0)
            {
                HideThumbProgress();
                return false;
            }

            bool more = _thumbnails.ProcessQueue(6);

            // 缩略图本体出完就可以收进度条——剩下的"动态块 / 可见性"补测只是角标，
            // 不该让进度条一直挂在那儿（也不能每帧 UpdateThumbProgress 把刚藏起来的
            // 进度条又拉回来）。结束提示每轮只播一次，免得反复刷状态栏。
            if (!_thumbnails.HasPendingThumbnails)
            {
                HideThumbProgress();

                if (!_thumbRoundAnnounced)
                {
                    _thumbRoundAnnounced = true;

                    // 播报真实完成数：重试用尽的条目不会再有进度，不能拿请求数冒充
                    if (_thumbDone >= _thumbTotal)
                        SetStatus("缩略图已全部生成，共 " + _thumbTotal + " 张。");
                    else
                        SetStatus("缩略图已生成 " + _thumbDone + " 张，另有 " +
                                  (_thumbTotal - _thumbDone) + " 张无法生成（已显示占位图）。");
                }
            }
            else
            {
                UpdateThumbProgress();
            }

            return more;
        }

        /// <summary>
        /// 面板不再可见（关闭 / 最小化）时调用：立刻停止后台缩略图的生成与缓存。
        ///
        /// 为什么必须显式停：缩略图是挂在 AutoCAD 的 Application.Idle 上分批做的，
        /// 每批最多占用 ~40ms 主线程去打开 DWG、解码、重采样。面板都关了还在跑，
        /// 用户就会感觉"关掉图库之后 CAD 一直卡"——队列里攒着几千张图时尤其明显。
        /// 这里清空队列，Idle 回调随即空转，主线程立刻还给用户。
        /// </summary>
        public void SuspendThumbnailWork()
        {
            if (_thumbnails == null) return;

            try
            {
                _thumbnails.Suspend();

                // 可见区间作废：下次显示时重新按视口排优先级
                ResetVisibleStamp();

                // 隐藏期间攒下的重绘请求一并作废（界面看不见，刷了也白刷）
                _listDirty = false;

                HideThumbProgress();
            }
            catch
            {
                // 暂停失败不影响窗口正常关闭
            }
        }

        /// <summary>
        /// 面板重新可见时调用：恢复后台生成，并把当前列表里还缺图的条目重新排队。
        /// 已在内存缓存里的条目会被 RequestThumbnails 自动跳过，所以恢复不会重复劳动。
        /// </summary>
        public void ResumeThumbnailWork()
        {
            if (_thumbnails == null) return;

            try
            {
                _thumbnails.Resume();
                ResetVisibleStamp();

                if (_currentBlocks == null || _currentBlocks.Count == 0) return;

                int requested = _thumbnails.RequestThumbnails(_currentBlocks);
                BeginThumbRound(requested);
            }
            catch
            {
                // 恢复失败最多是缩略图不出来，不影响正常使用
            }
        }

        /// <summary>后台缩略图工作当前是否处于暂停状态。</summary>
        public bool ThumbnailWorkSuspended
        {
            get { return _thumbnails != null && _thumbnails.IsSuspended; }
        }

        // ---------------- 缩略图生成进度条 ----------------

        /// <summary>本轮缩略图开始时的计数准备。</summary>
        private void BeginThumbRound(int requested)
        {
            _thumbTotal = Math.Max(0, requested);
            _thumbDone = 0;
            _thumbRoundAnnounced = false;
            UpdateThumbProgress();
        }

        private void UpdateThumbProgress()
        {
            if (_pbThumbs == null || _lblThumbInfo == null) return;
            if (_thumbTotal <= 0) { HideThumbProgress(); return; }

            int done = _thumbDone;
            if (done > _thumbTotal) done = _thumbTotal;

            // 本轮计数走完就收掉进度条。不能一直挂着 568/599：
            // 剩下的是"重试用尽、无法生成"的条目，它们不会再有进度了。
            if (done >= _thumbTotal)
            {
                HideThumbProgress();
                return;
            }

            try
            {
                int percent = (int)(done * 100L / _thumbTotal);
                if (percent < 0) percent = 0;
                if (percent > 100) percent = 100;

                // 值没变就不碰控件（v1.49.1）：空闲循环每帧都会走到这里，
                // 反复设置相同的文本 / 可见性 / 进度值会让右下角这块区域
                // 不停重绘、看起来像在闪。
                string text = "缩略图 " + done + "/" + _thumbTotal;
                if (_lblThumbInfo.Text != text) _lblThumbInfo.Text = text;
                if (!_lblThumbInfo.Visible) _lblThumbInfo.Visible = true;
                if (!_pbThumbs.Visible) _pbThumbs.Visible = true;
                if (_pbThumbs.Value != percent) _pbThumbs.Value = percent;
            }
            catch
            {
                // 刷新进度出错无所谓
            }
        }

        private void HideThumbProgress()
        {
            try
            {
                if (_lblThumbInfo != null) _lblThumbInfo.Visible = false;
                if (_pbThumbs != null) _pbThumbs.Visible = false;
            }
            catch
            {
                // 忽略
            }
        }

        // ---------------- 控件创建 ----------------

        private void InitializeComponent()
        {
            SuspendLayout();

            BackColor = SystemColors.Window;
            Font = SystemFonts.MessageBoxFont;
            MinimumSize = new Size(320, 400);

            _imageList = new ImageList();
            _imageList.ColorDepth = ColorDepth.Depth32Bit;
            _imageList.ImageSize = new Size(96, 96);

            CreateToolStrip();
            CreateParamPanel();     // 必须先于 CreateSplit：面板要被放进左侧分割容器
            CreateSplit();
            CreateBottom();

            _statusBar = new StatusStrip();

            _lblStatus = new ToolStripStatusLabel();
            _lblStatus.Spring = true;
            _lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            _lblStatus.Text = "就绪";
            _statusBar.Items.Add(_lblStatus);

            // 缩略图生成进度（只在真的有图要生成时才显示）
            _lblThumbInfo = new ToolStripStatusLabel();
            _lblThumbInfo.AutoSize = true;
            _lblThumbInfo.Visible = false;
            _lblThumbInfo.Text = "缩略图 0/0";
            _statusBar.Items.Add(_lblThumbInfo);

            _pbThumbs = new ToolStripProgressBar();
            _pbThumbs.Minimum = 0;
            _pbThumbs.Maximum = 100;
            _pbThumbs.Value = 0;
            _pbThumbs.Width = 120;
            _pbThumbs.Visible = false;
            _statusBar.Items.Add(_pbThumbs);

            _statusBar.SizingGrip = false;
            _statusBar.Dock = DockStyle.Bottom;

            // 注意添加顺序：Fill 控件要先加，后续 Dock 的控件才能正确占据剩余空间
            Controls.Add(_split);
            Controls.Add(_bottomPanel);
            Controls.Add(_statusBar);
            Controls.Add(_toolStrip);

            ResumeLayout(false);
            PerformLayout();
        }

        private void OnAboutClick(object sender, EventArgs e)
        {
            LibraryPalette.ShowAbout();
        }

        // ---------------- 界面主题 ----------------

        /// <summary>当前主题。</summary>
        private UiTheme CurrentTheme
        {
            get { return UiThemes.Get(_config != null ? _config.Theme : 0); }
        }

        private string CurrentThemeName()
        {
            UiTheme t = CurrentTheme;
            return L.T(t.Name, t.NameEn);
        }

        private string BuildThemeTooltip()
        {
            return L.T("点击切换界面整体配色（当前：", "Click to switch the UI color scheme (current: ") +
                   CurrentThemeName() +
                   L.T("；共 " + UiThemes.All.Length + " 种：默认 / 清新蓝 / 薄荷绿 / 深黑灰 / 暖阳橙）",
                       "; " + UiThemes.All.Length + " in total: Default / Fresh Blue / Mint Green / Dark Gray / Warm Sun)");
        }

        /// <summary>工具栏"主题"按钮：循环切换 界面整体配色，选择记忆到配置。</summary>
        private void OnThemeClick(object sender, EventArgs e)
        {
            if (_config == null) return;

            int next = (_config.Theme + 1) % UiThemes.All.Length;
            _config.Theme = next;
            _config.Save();

            ApplyTheme();

            if (_btnTheme != null) _btnTheme.ToolTipText = BuildThemeTooltip();
            SetStatus(L.T("界面主题：", "UI theme: ") + CurrentThemeName());
        }

        /// <summary>
        /// 把当前主题应用到整个面板：窗口标题栏、工具栏、状态栏、
        /// 两个标题栏、目录树、缩略图列表、参数面板、底部行。
        /// 默认主题与旧版配色完全一致。
        /// </summary>
        private void ApplyTheme()
        {
            UiTheme t = CurrentTheme;

            try
            {
                BackColor = t.ListBack;

                // ---- 窗口标题栏（无边框窗口的自绘标题栏） ----
                Form host = FindForm();
                if (host != null)
                {
                    foreach (Control c in host.Controls)
                    {
                        TitleBar tb = c as TitleBar;
                        if (tb != null) tb.ApplyChrome(t.HeaderBack, t.HeaderText);
                    }
                }

                // ---- 工具栏 / 状态栏 ----
                if (_toolStrip != null)
                {
                    _toolStrip.BackColor = t.ChromeBack;
                    _toolStrip.ForeColor = t.ChromeText;
                }
                if (_statusBar != null)
                {
                    _statusBar.BackColor = t.ChromeBack;
                    _statusBar.ForeColor = t.ChromeText;
                }

                // ---- "目录列表"标题栏 ----
                if (_treeHeader != null) _treeHeader.BackColor = t.HeaderBack;
                if (_treeHeaderTitle != null)
                {
                    _treeHeaderTitle.BackColor = t.HeaderBack;
                    _treeHeaderTitle.ForeColor = t.HeaderText;
                }
                if (_treeHeaderClose != null)
                {
                    _treeHeaderClose.BackColor = t.HeaderBack;
                    _treeHeaderClose.ForeColor = t.HeaderText;
                    _treeHeaderClose.FlatAppearance.MouseOverBackColor = ControlPaint.Light(t.HeaderBack);
                }

                // ---- 目录树 / 缩略图列表 ----
                if (_leftSplit != null)
                {
                    _leftSplit.Panel1.BackColor = t.ListBack;
                    _leftSplit.Panel2.BackColor = t.PanelBack;
                }
                if (_tree != null)
                {
                    _tree.BackColor = t.ListBack;
                    _tree.ForeColor = t.BodyText;   // 节点文字跟随主题
                }
                if (_list != null)
                {
                    _list.BackColor = t.ListBack;   // 缩略图区域背景跟随主题
                    _list.ForeColor = t.BodyText;   // 图块名称文字跟随主题
                }

                // ---- 搜索框 ----
                if (_searchPanel != null)
                {
                    _searchPanel.BackColor = t.ListBack;
                    foreach (Control c in _searchPanel.Controls) c.BackColor = t.ListBack;
                }

                // ---- 底部行 ----
                if (_bottomPanel != null) _bottomPanel.BackColor = t.PanelBack;
                if (_chkUpdateDef != null) _chkUpdateDef.BackColor = t.PanelBack;
                if (_lblHint != null)
                {
                    _lblHint.BackColor = t.PanelBack;
                    _lblHint.ForeColor = t.HintText;
                }

                // ---- 插入参数设置面板 ----
                if (_paramPanel != null) _paramPanel.BackColor = t.PanelBack;
                if (_paramHeaderPanel != null) _paramHeaderPanel.BackColor = t.HeaderBack;
                if (_paramTitleLabel != null)
                {
                    _paramTitleLabel.BackColor = t.HeaderBack;
                    _paramTitleLabel.ForeColor = t.HeaderText;
                }
                if (_paramHeaderClose != null)
                {
                    _paramHeaderClose.BackColor = t.HeaderBack;
                    _paramHeaderClose.ForeColor = t.HeaderText;
                    _paramHeaderClose.FlatAppearance.MouseOverBackColor = ControlPaint.Light(t.HeaderBack);
                }

                if (_paramPanel != null)
                {
                    foreach (Control c in _paramPanel.Controls)
                    {
                        if (ReferenceEquals(c, _paramHeaderPanel)) continue;

                        c.BackColor = t.PanelBack;

                        Label lbl = c as Label;
                        if (lbl == null) continue;

                        if (ReferenceEquals(lbl, _lblLayerHint) ||
                            ReferenceEquals(lbl, _lblColorHint) ||
                            ReferenceEquals(lbl, _lblExplodeHint))
                            lbl.ForeColor = t.HintText;
                        else
                            lbl.ForeColor = t.BodyText;
                    }
                }
            }
            catch
            {
                // 主题应用失败保持原样即可
            }
        }

        private void CreateToolStrip()
        {
            _toolStrip = new ToolStrip();
            _toolStrip.GripStyle = ToolStripGripStyle.Hidden;
            _toolStrip.ImageScalingSize = new Size(16, 16);
            _toolStrip.Dock = DockStyle.Top;
            // 行高调大：关闭自动高度，固定更舒适的行高（控件会自动垂直居中）
            _toolStrip.AutoSize = false;
            _toolStrip.Height = 36;

            _btnAdd = new ToolStripButton(L.T("添加", "Add"));
            _btnAdd.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _btnAdd.AutoToolTip = true;
            _btnAdd.ToolTipText = L.T("浏览电脑文件夹，把一个目录加入图库根目录列表",
                "Browse the computer and add a folder to the library root list");
            _btnAdd.Click += new EventHandler(OnAddRootClick);

            // "显示"下拉按钮：点击分级出"目录列表"和"插入参数面板"两个开关，
            // 分别控制左侧两个窗体的显示/隐藏
            _btnShow = new ToolStripDropDownButton(L.T("显示", "Show"));
            _btnShow.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _btnShow.AutoToolTip = true;
            _btnShow.ToolTipText = L.T("选择显示或隐藏目录列表 / 插入参数设置面板",
                "Show or hide the directory list / insert parameter panel");

            _miShowTree = new ToolStripMenuItem(L.T("目录列表", "Directory List"));
            _miShowTree.CheckOnClick = true;
            _miShowTree.Checked = true;
            _miShowTree.Click += new EventHandler(OnShowTreeMenuClick);
            _btnShow.DropDownItems.Add(_miShowTree);

            _miShowParams = new ToolStripMenuItem(L.T("插入参数面板", "Insert Parameters"));
            _miShowParams.CheckOnClick = true;
            _miShowParams.Checked = true;
            _miShowParams.Click += new EventHandler(OnShowParamsMenuClick);
            _btnShow.DropDownItems.Add(_miShowParams);

            _btnRefresh = new ToolStripButton(L.T("刷新", "Refresh"));
            _btnRefresh.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _btnRefresh.Click += new EventHandler(OnRefreshClick);

            _btnSettings = new ToolStripButton(L.T("设置", "Settings"));
            _btnSettings.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _btnSettings.Click += new EventHandler(OnSettingsClick);

            // 主题：设置和语言之间，点击循环切换界面整体配色
            _btnTheme = new ToolStripButton(L.T("主题", "Theme"));
            _btnTheme.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _btnTheme.AutoToolTip = true;
            _btnTheme.Click += new EventHandler(OnThemeClick);

            // 语言：设置和关于之间，点击弹出语言选择对话框
            _btnLanguage = new ToolStripButton(L.T("语言", "Language"));
            _btnLanguage.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _btnLanguage.AutoToolTip = true;
            _btnLanguage.ToolTipText = L.T("设置界面语言（默认跟随系统语言）",
                "Set the interface language (defaults to the system language)");
            _btnLanguage.Click += new EventHandler(OnLanguageClick);

            _btnViewMode = new ToolStripDropDownButton(L.T("视图", "View"));
            _btnViewMode.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _btnViewMode.DropDownItems.Add(CreateViewMenuItem(L.T("大图标", "Large Icons"), GalleryView.LargeIcon));
            _btnViewMode.DropDownItems.Add(CreateViewMenuItem(L.T("详细信息", "Details"), GalleryView.Details));
            _btnViewMode.DropDownItems.Add(CreateViewMenuItem(L.T("列表", "List"), GalleryView.List));

            // 批量入库：紧跟在"视图"后面。选目标文件夹 → 面板最小化 →
            // 框选 CAD 里的图块 → 按块名逐个保存为 2007 格式 DWG。
            _btnBatch = new ToolStripButton(L.T("批量入库", "Batch Import"));
            _btnBatch.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _btnBatch.AutoToolTip = true;
            _btnBatch.ToolTipText = L.T("选择目标文件夹后框选图块，按块名逐个保存为 AutoCAD 2007 格式 DWG 入库",
                "Select a target folder, then window-select blocks to save each one as an AutoCAD 2007 DWG");
            _btnBatch.Click += new EventHandler(OnBatchImportClick);

            _txtSearch = new TextBox();
            _txtSearch.BorderStyle = BorderStyle.None;
            _txtSearch.KeyDown += new KeyEventHandler(OnSearchKeyDown);
            _txtSearch.TextChanged += new EventHandler(OnSearchTextChanged);

            // 输入防抖：停止输入 300ms 后才真正执行搜索
            _searchTimer = new System.Windows.Forms.Timer();
            _searchTimer.Interval = 300;
            _searchTimer.Tick += new EventHandler(OnSearchTimerTick);

            _btnAbout = new ToolStripButton(L.T("关于", "About"));
            _btnAbout.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _btnAbout.Click += new EventHandler(OnAboutClick);

            _toolStrip.Items.Add(_btnAdd);
            _toolStrip.Items.Add(_btnShow);
            _toolStrip.Items.Add(_btnRefresh);
            _toolStrip.Items.Add(_btnViewMode);
            _toolStrip.Items.Add(_btnBatch);
            _toolStrip.Items.Add(new ToolStripSeparator());
            _toolStrip.Items.Add(_btnSettings);
            _toolStrip.Items.Add(_btnTheme);
            _toolStrip.Items.Add(_btnLanguage);
            _toolStrip.Items.Add(_btnAbout);
            _toolStrip.Items.Add(new ToolStripSeparator());
            _toolStrip.Items.Add(CreateSearchBoxHost());
        }

        /// <summary>
        /// 工具栏上的搜索框：一个带边框的小面板，内部左侧是放大镜图标、右侧是文本框。
        /// 用 ToolStripControlHost 挂进工具栏。点放大镜图标立即执行搜索。
        /// </summary>
        private ToolStripItem CreateSearchBoxHost()
        {
            _searchPanel = new Panel();
            _searchPanel.Width = 200;
            _searchPanel.Height = 22;
            _searchPanel.BackColor = SystemColors.Window;
            _searchPanel.Paint += new PaintEventHandler(OnSearchPanelPaint);

            Label icon = new Label();
            icon.Image = SystemIconProvider.CreateMagnifierIcon(14);
            icon.AutoSize = false;
            icon.Size = new Size(19, 18);
            icon.Location = new Point(2, 2);
            icon.BackColor = SystemColors.Window;
            icon.Cursor = Cursors.Hand;
            icon.Margin = Padding.Empty;
            icon.Click += new EventHandler(OnSearchIconClick);
            _searchPanel.Controls.Add(icon);

            _txtSearch.Parent = _searchPanel;
            _txtSearch.Location = new Point(22, 4);
            _txtSearch.Size = new Size(_searchPanel.Width - 26, 15);
            _txtSearch.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;

            ToolStripControlHost host = new ToolStripControlHost(_searchPanel);
            host.AutoSize = false;
            host.Size = new Size(_searchPanel.Width + 4, 24);
            host.Margin = new Padding(1, 1, 1, 1);
            return host;
        }

        /// <summary>给搜索面板画一圈边框（颜色跟随主题，深色主题下自动换成中灰）。</summary>
        private void OnSearchPanelPaint(object sender, PaintEventArgs e)
        {
            try
            {
                Panel p = sender as Panel;
                if (p == null) return;
                using (Pen pen = new Pen(CurrentTheme.Border))
                    e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);
            }
            catch
            {
                // 边框画不出来就算了
            }
        }

        private void OnSearchTimerTick(object sender, EventArgs e)
        {
            _searchTimer.Stop();
            ApplySearch();
        }

        private void OnSearchTextChanged(object sender, EventArgs e)
        {
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        private void OnSearchIconClick(object sender, EventArgs e)
        {
            _searchTimer.Stop();
            ApplySearch();
        }

        private ToolStripMenuItem CreateViewMenuItem(string text, GalleryView mode)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Tag = mode;
            item.Click += new EventHandler(OnViewModeClick);
            return item;
        }

        private void CreateSplit()
        {
            _split = new SplitContainer();
            _split.Dock = DockStyle.Fill;
            _split.Orientation = Orientation.Vertical;
            _split.FixedPanel = FixedPanel.Panel1;

            // 重大坑：SplitContainer 刚 new 出来时 Width 只有默认的 150、
            // SplitterDistance 默认 50。此时设置 Panel1MinSize / Panel2MinSize /
            // SplitterDistance 任何一个都会触发内部重算并抛出
            //   “SplitterDistance 必须在 Panel1MinSize 和 Width - Panel2MinSize 之间”
            // 在 AutoCAD 的 PaletteSet 里这个异常会变成未处理异常弹窗。
            // 所以这里一个都不设，全部推迟到 ApplySplitterDistance()（拿到真实宽度之后）。
            _split.Resize += new EventHandler(OnSplitResize);
            // 宽度记忆：用户拖动分割条后保存，下次启动按上次的宽度恢复
            _split.SplitterMoved += new SplitterEventHandler(OnSplitterMoved);

            // 左侧分类树
            _tree = new TreeView();
            _tree.Dock = DockStyle.Fill;
            _tree.HideSelection = false;
            _tree.ShowLines = true;
            _tree.ShowPlusMinus = true;
            _tree.ShowRootLines = true;
            _tree.FullRowSelect = true;
            _tree.HotTracking = true;

            // 节点图标：Windows 资源管理器原生的文件夹图标（关闭 / 打开两态）
            _treeIcons = new ImageList();
            _treeIcons.ColorDepth = ColorDepth.Depth32Bit;
            _treeIcons.ImageSize = new Size(16, 16);
            _treeIcons.Images.Add(IconFolderClosed, SystemIconProvider.ClosedFolder);
            _treeIcons.Images.Add(IconFolderOpen, SystemIconProvider.OpenFolder);
            _tree.ImageList = _treeIcons;

            _tree.AfterSelect += new TreeViewEventHandler(OnTreeAfterSelect);
            _tree.ContextMenuStrip = CreateTreeMenu();
            _tree.NodeMouseClick += new TreeNodeMouseClickEventHandler(OnTreeNodeMouseClick);
            // 双保险：有些 .NET 版本 / 主题下 NodeMouseClick 不一定先于菜单弹出，
            // 这里在 MouseDown 阶段就用坐标把目标节点定下来。
            _tree.MouseDown += new MouseEventHandler(OnTreeMouseDown);
            // "此电脑"分支的文件夹在展开时才惰性加载
            _tree.BeforeExpand += new TreeViewCancelEventHandler(OnTreeBeforeExpand);

            // ---- 左侧上下分割：上目录树 / 下"插入参数设置"面板，默认平分高度 ----
            // 同样避开 SplitterDistance 的初始化坑，推迟到有真实尺寸后再设。
            _leftSplit = new SplitContainer();
            _leftSplit.Dock = DockStyle.Fill;
            _leftSplit.Orientation = Orientation.Horizontal;
            _leftSplit.FixedPanel = FixedPanel.Panel2;
            _leftSplit.Panel1.BackColor = SystemColors.Window;
            _leftSplit.Panel2.BackColor = SystemColors.Control;
            _leftSplit.Panel1.Controls.Add(_tree);
            _leftSplit.Panel2.Controls.Add(_paramPanel);
            _leftSplit.Resize += new EventHandler(OnLeftSplitResize);
            // 高度记忆：用户拖动上下分割条后保存，下次启动按上次位置恢复
            _leftSplit.SplitterMoved += new SplitterEventHandler(OnLeftSplitterMoved);

            // 标题栏要贴顶：Dock 布局按加入顺序反向排布，
            // Fill 的树先进，Top 的标题栏后进（最后加 = 最上方），树自动填充剩余高度。
            _leftSplit.Panel1.Controls.Add(CreateTreeHeader());

            // 左侧整体放进主分割容器的 Panel1
            _split.Panel1.Controls.Add(_leftSplit);

            // 右侧图块列表
            _list = new ListView();
            _list.Dock = DockStyle.Fill;
            _list.View = View.LargeIcon;
            _list.LargeImageList = _imageList;
            _list.SmallImageList = _imageList;
            _list.MultiSelect = false;
            _list.HideSelection = false;
            _list.LabelEdit = false;
            _list.AllowDrop = true;
            _list.Sorting = SortOrder.None;

            // ★ 自绘：缩略图不再逐项塞进 ImageList，而是"谁要被画出来才取谁的图"。
            //   详见 UI/LibraryControl.Draw.cs 的头部说明。
            _list.OwnerDraw = true;
            _list.DrawItem += new DrawListViewItemEventHandler(OnListDrawItem);
            _list.DrawSubItem += new DrawListViewSubItemEventHandler(OnListDrawSubItem);
            _list.DrawColumnHeader += new DrawListViewColumnHeaderEventHandler(OnListDrawColumnHeader);

            // 自绘不开双缓冲会明显闪
            SetDoubleBuffered(_list, true);

            _list.DoubleClick += new EventHandler(OnListDoubleClick);
            _list.ItemDrag += new ItemDragEventHandler(OnListItemDrag);
            _list.DragEnter += new DragEventHandler(OnListDragEnter);
            _list.DragDrop += new DragEventHandler(OnListDragDrop);
            _list.SelectedIndexChanged += new EventHandler(OnListSelectionChanged);
            _list.MouseDown += new MouseEventHandler(OnListMouseDown);
            _list.Columns.Add("名称", 180);
            _list.Columns.Add("大小", 80);
            _list.Columns.Add("修改时间", 140);
            _list.Columns.Add("路径", 260);
            _list.ContextMenuStrip = CreateListMenu();
            _split.Panel2.Controls.Add(_list);
        }

        /// <summary>
        /// 左侧目录列表的小标题栏：左边"目录列表"文字，右边"×"关闭按钮。
        /// 点 × 收起整个左侧列表；恢复入口在工具栏「视图 → 目录列表」。
        /// </summary>
        private Control CreateTreeHeader()
        {
            _treeHeader = new Panel();
            _treeHeader.Dock = DockStyle.Top;
            _treeHeader.Height = 38;
            _treeHeader.BackColor = SystemColors.Control;

            Label title = new Label();
            title.Text = L.T("目录列表", "Directory List");
            // 与图库列表（TreeView）同字体、不加粗
            title.Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Regular);
            title.ForeColor = SystemColors.ControlText;
            title.BackColor = SystemColors.Control;
            title.AutoSize = true;
            title.Location = new Point(8, 10);
            _treeHeaderTitle = title;

            Button close = new Button();
            close.Text = "×";
            close.Size = new Size(24, 22);
            close.FlatStyle = FlatStyle.Flat;
            close.FlatAppearance.BorderSize = 0;
            close.FlatAppearance.MouseOverBackColor = Color.FromArgb(224, 224, 224);
            close.Font = new Font(SystemFonts.MessageBoxFont.Name, 11f);
            close.ForeColor = SystemColors.ControlText;
            close.BackColor = SystemColors.Control;
            close.TabStop = false;
            close.Click += new EventHandler(OnTreeHeaderCloseClick);
            _treeHeaderClose = close;

            _treeHeader.Controls.Add(title);
            _treeHeader.Controls.Add(close);

            // 关闭按钮始终贴右上角
            EventHandler placeClose = delegate
            {
                try
                {
                    close.Location = new Point(
                        _treeHeader.ClientSize.Width - close.Width - 4,
                        (_treeHeader.ClientSize.Height - close.Height) / 2);
                }
                catch { }
            };
            _treeHeader.Resize += placeClose;
            placeClose(null, EventArgs.Empty);

            return _treeHeader;
        }

        private ContextMenuStrip CreateListMenu()
        {
            _menuList = new ContextMenuStrip();

            ToolStripMenuItem insert = new ToolStripMenuItem("插入到当前图形");
            insert.Click += new EventHandler(OnInsertSelectedClick);
            _menuList.Items.Add(insert);

            // 说明：原先这里还有一项"插入图形后关闭"，已移除——
            // 插入参数面板第 6 行"插入图形后关闭窗口"勾选框就是同一个功能，且会写进配置持久化。

            ToolStripMenuItem addRecursion = new ToolStripMenuItem("插入多个（连续）");
            addRecursion.Click += new EventHandler(OnInsertMultipleClick);
            _menuList.Items.Add(addRecursion);

            _menuList.Items.Add(new ToolStripSeparator());

            // 打开该图形文件：用当前 CAD 打开选中的 DWG，并自动收起图库管理器
            _miListOpenDrawing = new ToolStripMenuItem("打开该图形文件");
            _miListOpenDrawing.Click += new EventHandler(OnOpenDrawingClick);
            _menuList.Items.Add(_miListOpenDrawing);

            ToolStripMenuItem openFolder = new ToolStripMenuItem("打开所在文件夹");
            openFolder.Click += new EventHandler(OnOpenFolderClick);
            _menuList.Items.Add(openFolder);

            ToolStripMenuItem copyName = new ToolStripMenuItem("复制块名");
            copyName.Click += new EventHandler(OnCopyNameClick);
            _menuList.Items.Add(copyName);

            _menuList.Items.Add(new ToolStripSeparator());

            // 粘贴入库：把 CAD 里 Ctrl+C 复制的图块保存到当前浏览的目录（右键空白处也可用）
            _miListPaste = new ToolStripMenuItem("粘贴入库");
            _miListPaste.Click += new EventHandler(OnPasteIntoLibraryClick);
            _menuList.Items.Add(_miListPaste);

            // 重命名块：修改选中图块的 DWG 文件名（在"粘贴入库"与"刷新"之间）
            _miListRename = new ToolStripMenuItem("重命名块");
            _miListRename.Click += new EventHandler(OnRenameBlockClick);
            _menuList.Items.Add(_miListRename);

            ToolStripMenuItem refresh = new ToolStripMenuItem("刷新");
            refresh.Click += new EventHandler(OnRefreshClick);
            _menuList.Items.Add(refresh);

            _menuList.Items.Add(new ToolStripSeparator());

            // 底色切换：白底 / 黑底缩略图，选择后当前所有缩略图重新生成
            _miThumbWhite = new ToolStripMenuItem("白底缩略图");
            _miThumbWhite.Click += new EventHandler(OnThumbWhiteClick);
            _menuList.Items.Add(_miThumbWhite);

            _miThumbBlack = new ToolStripMenuItem("黑底缩略图");
            _miThumbBlack.Click += new EventHandler(OnThumbBlackClick);
            _menuList.Items.Add(_miThumbBlack);

            // 弹出前根据当前选中的分类决定"粘贴入库"是否可用
            _menuList.Opening += new System.ComponentModel.CancelEventHandler(OnListMenuOpening);
            return _menuList;
        }

        /// <summary>分类树右键菜单：收藏 / 打开文件夹位置 / 添加到图库 / 从图库中移除。</summary>
        private ContextMenuStrip CreateTreeMenu()
        {
            _menuTree = new ContextMenuStrip();

            _miTreeFavorite = new ToolStripMenuItem("添加到收藏夹");
            _miTreeFavorite.Click += new EventHandler(OnTreeFavoriteClick);
            _menuTree.Items.Add(_miTreeFavorite);

            _miTreeOpenFolder = new ToolStripMenuItem("打开文件夹位置");
            _miTreeOpenFolder.Click += new EventHandler(OnTreeOpenFolderClick);
            _menuTree.Items.Add(_miTreeOpenFolder);

            _miTreeAddLib = new ToolStripMenuItem("添加到图库");
            _miTreeAddLib.Click += new EventHandler(OnTreeAddToLibraryClick);
            _menuTree.Items.Add(_miTreeAddLib);

            _miTreeRemove = new ToolStripMenuItem("从图库中移除");
            _miTreeRemove.Click += new EventHandler(OnTreeRemoveClick);
            _menuTree.Items.Add(_miTreeRemove);

            _menuTree.Opening += new System.ComponentModel.CancelEventHandler(OnTreeMenuOpening);
            return _menuTree;
        }

        private void CreateBottom()
        {
            _bottomPanel = new Panel();
            _bottomPanel.Dock = DockStyle.Bottom;
            _bottomPanel.Height = 34;
            _bottomPanel.BackColor = SystemColors.Control;
            _bottomPanel.Resize += new EventHandler(delegate (object s, EventArgs e) { LayoutBottomRow(); });

            // 比例 / 旋转 / 炸开 / 等比 已移到左侧目录列表下方的插入参数面板（CreateParamPanel）。
            // 底部只保留"更新已有定义"与提示文字，避免两处参数状态不同步。

            _chkUpdateDef = new CheckBox();
            _chkUpdateDef.Text = "更新已有定义";
            _chkUpdateDef.Checked = true;
            _chkUpdateDef.AutoSize = true;
            _chkUpdateDef.BackColor = SystemColors.Control;

            _lblHint = new Label();
            _lblHint.AutoSize = false;
            _lblHint.AutoEllipsis = true;
            _lblHint.ForeColor = Color.DimGray;
            _lblHint.BackColor = SystemColors.Control;
            _lblHint.TextAlign = ContentAlignment.MiddleLeft;
            _lblHint.Text = "双击条目 / 右键“插入到当前图形”；也可把 DWG 拖进列表入库";

            _bottomPanel.Controls.Add(_chkUpdateDef);
            _bottomPanel.Controls.Add(_lblHint);
        }

        /// <summary>
        /// 底部提示行的布局：更新定义 + 提示文字同一条水平线。
        /// </summary>
        private void LayoutBottomRow()
        {
            if (_bottomPanel == null || _chkUpdateDef == null) return;

            try
            {
                int x = 10;
                _chkUpdateDef.Location = new Point(x, 6); x += _chkUpdateDef.Width + 12;

                _lblHint.SetBounds(x, 7,
                    Math.Max(0, _bottomPanel.ClientSize.Width - x - 10), 20);
            }
            catch
            {
                // 布局失败不影响功能
            }
        }

        /// <summary>
        /// 左侧目录列表下方的"插入参数设置"面板：
        ///   标题栏（颜色样式与"目录列表"标题栏一致）：左侧标题文字，右上角 × 关闭按钮
        ///   参数行两列布局（左参数名 / 右值），各占一行：
        ///     插入比例   1:X（X 由用户输入，默认 1）
        ///     图层       勾选后插入的图块归到 0 图层；不勾选使用图块所在图层
        ///     颜色       勾选后图块颜色跟随图层颜色；不勾选保持图块原有颜色
        ///   （旋转角度输入行已在 v1.51.2 移除；插入默认 0 度，Jig 交互旋转保留）
        /// </summary>
        private void CreateParamPanel()
        {
            _paramPanel = new Panel();
            _paramPanel.Dock = DockStyle.Fill;   // 填充左侧分割容器的下半部
            _paramPanel.BackColor = SystemColors.Control;

            const int headerH = 32;

            // ---- 标题栏：颜色样式与"目录列表"标题栏一致，右 × 关闭 ----
            _paramHeaderPanel = new Panel();
            _paramHeaderPanel.Dock = DockStyle.Top;
            _paramHeaderPanel.Height = headerH;
            _paramHeaderPanel.BackColor = SystemColors.Control;

            Label title = new Label();
            title.Text = L.T("插入参数设置", "Insert Parameters");
            title.Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Regular);
            title.ForeColor = SystemColors.ControlText;
            title.BackColor = SystemColors.Control;
            title.AutoSize = true;
            _paramTitleLabel = title;

            // 标题落在标题栏的"左中"位置：x 固定靠左，y 按实测文字高度垂直居中
            //（不再写死 y=9，任何 DPI / 字号下都居中）
            EventHandler placeTitle = delegate
            {
                try
                {
                    title.Location = new Point(8,
                        Math.Max(2, (_paramHeaderPanel.ClientSize.Height - title.Height) / 2));
                }
                catch { }
            };

            Button close = new Button();
            close.Text = "×";
            close.Size = new Size(24, 22);
            close.FlatStyle = FlatStyle.Flat;
            close.FlatAppearance.BorderSize = 0;
            close.FlatAppearance.MouseOverBackColor = Color.FromArgb(224, 224, 224);
            close.Font = new Font(SystemFonts.MessageBoxFont.Name, 11f);
            close.ForeColor = SystemColors.ControlText;
            close.BackColor = SystemColors.Control;
            close.TabStop = false;
            close.Click += new EventHandler(OnParamPanelCloseClick);
            _paramHeaderClose = close;

            _paramHeaderPanel.Controls.Add(title);
            _paramHeaderPanel.Controls.Add(close);

            // 关闭按钮贴右上角；标题文字随高度变化保持垂直居中
            EventHandler placeClose = delegate
            {
                try
                {
                    close.Location = new Point(
                        _paramHeaderPanel.ClientSize.Width - close.Width - 4,
                        (_paramHeaderPanel.ClientSize.Height - close.Height) / 2);
                    placeTitle(null, EventArgs.Empty);
                }
                catch { }
            };
            _paramHeaderPanel.Resize += placeClose;
            _paramHeaderPanel.HandleCreated += delegate { placeClose(null, EventArgs.Empty); };
            placeClose(null, EventArgs.Empty);

            // ---- 参数行控件 ----
            _lblScale = new Label();
            _lblScale.Text = L.T("插入比例", "Insert Scale");
            _lblScale.AutoSize = true;
            _lblScale.BackColor = SystemColors.Control;

            Label lblScalePrefix = new Label();
            lblScalePrefix.Text = "1:";
            lblScalePrefix.AutoSize = true;
            lblScalePrefix.BackColor = SystemColors.Control;
            lblScalePrefix.Name = "lblScalePrefix";
            _paramPanel.Controls.Add(lblScalePrefix);   // 先挂上，布局时再定位

            _txtScaleX = new TextBox();
            _txtScaleX.Width = 44;
            _txtScaleX.Text = "1";
            _txtScaleX.BorderStyle = BorderStyle.FixedSingle;

            // 图层 / 颜色 / 分解：参数名与勾选框分成两个控件，
            // 名字列与"插入比例"左对齐，勾选框统一落在值列上，各行整齐对齐
            _chkLayerZero = new CheckBox();
            _chkLayerZero.Text = string.Empty;
            _chkLayerZero.AutoSize = false;
            _chkLayerZero.Size = new Size(20, 18);
            _chkLayerZero.BackColor = SystemColors.Control;
            _chkLayerZero.TabStop = false;

            _lblLayerName = new Label();
            _lblLayerName.Text = L.T("图层为0", "Layer 0");
            _lblLayerName.AutoSize = true;
            _lblLayerName.BackColor = SystemColors.Control;

            _lblLayerHint = new Label();
            _lblLayerHint.Text = L.T("—— 勾选后图块归到 0 图层；不勾选使用图块所在图层",
                "— checked: insert to layer 0; unchecked: keep the block's own layer");
            _lblLayerHint.ForeColor = Color.DimGray;
            _lblLayerHint.BackColor = SystemColors.Control;
            _lblLayerHint.TextAlign = ContentAlignment.MiddleLeft;
            _lblLayerHint.AutoEllipsis = true;

            _chkColorByLayer = new CheckBox();
            _chkColorByLayer.Text = string.Empty;
            _chkColorByLayer.AutoSize = false;
            _chkColorByLayer.Size = new Size(20, 18);
            _chkColorByLayer.BackColor = SystemColors.Control;
            _chkColorByLayer.TabStop = false;

            _lblColorName = new Label();
            _lblColorName.Text = L.T("颜色跟随层", "Color by Layer");
            _lblColorName.AutoSize = true;
            _lblColorName.BackColor = SystemColors.Control;

            _lblColorHint = new Label();
            _lblColorHint.Text = L.T("—— 勾选后颜色跟随图层颜色；不勾选保持图块原有颜色",
                "— checked: color follows layer color; unchecked: keep the block's own colors");
            _lblColorHint.ForeColor = Color.DimGray;
            _lblColorHint.BackColor = SystemColors.Control;
            _lblColorHint.TextAlign = ContentAlignment.MiddleLeft;
            _lblColorHint.AutoEllipsis = true;

            _chkExplode = new CheckBox();
            _chkExplode.Text = string.Empty;
            _chkExplode.AutoSize = false;
            _chkExplode.Size = new Size(20, 18);
            _chkExplode.BackColor = SystemColors.Control;
            _chkExplode.TabStop = false;

            _lblExplodeName = new Label();
            _lblExplodeName.Text = L.T("插入后分解图块", "Explode on Insert");
            _lblExplodeName.AutoSize = true;
            _lblExplodeName.BackColor = SystemColors.Control;

            _lblExplodeHint = new Label();
            _lblExplodeHint.Text = L.T("—— 勾选后插入时分解图块；不勾选保持图块整体",
                "— checked: explode the block on insert; unchecked: keep it as a block");
            _lblExplodeHint.ForeColor = Color.DimGray;
            _lblExplodeHint.BackColor = SystemColors.Control;
            _lblExplodeHint.TextAlign = ContentAlignment.MiddleLeft;
            _lblExplodeHint.AutoEllipsis = true;

            // 行6：插入图形后关闭窗口（勾选后每次插入成功自动收起图库窗口，配置持久化）
            _chkCloseAfter = new CheckBox();
            _chkCloseAfter.Text = string.Empty;
            _chkCloseAfter.AutoSize = false;
            _chkCloseAfter.Size = new Size(20, 18);
            _chkCloseAfter.BackColor = SystemColors.Control;
            _chkCloseAfter.TabStop = false;
            _chkCloseAfter.CheckedChanged += new EventHandler(OnCloseAfterCheckedChanged);

            _lblCloseName = new Label();
            _lblCloseName.Text = L.T("插入图形后关闭窗口", "Close Panel After Insert");
            _lblCloseName.AutoSize = true;
            _lblCloseName.BackColor = SystemColors.Control;

            _lblCloseHint = new Label();
            _lblCloseHint.Text = L.T("—— 勾选后插入成功自动关闭图库管理器窗口",
                "— checked: close the panel automatically after a successful insert");
            _lblCloseHint.ForeColor = Color.DimGray;
            _lblCloseHint.BackColor = SystemColors.Control;
            _lblCloseHint.TextAlign = ContentAlignment.MiddleLeft;
            _lblCloseHint.AutoEllipsis = true;

            // CheckBox 没有自带的 ToolTipText，用 ToolTip 组件悬浮说明
            _paramTips = new ToolTip();
            _paramTips.SetToolTip(_chkLayerZero,
                L.T("勾选：插入的图块归到 0 图层；不勾选：使用图块所在图层",
                    "Checked: insert the block on layer 0; unchecked: keep the block's own layer"));
            _paramTips.SetToolTip(_chkColorByLayer,
                L.T("勾选：图块颜色跟随图层颜色；不勾选：保持图块原有颜色",
                    "Checked: colors follow layer colors; unchecked: keep the block's own colors"));
            _paramTips.SetToolTip(_chkExplode,
                L.T("勾选：插入时把图块分解为基本图元；不勾选：保持为整体图块",
                    "Checked: explode the block into basic entities on insert; unchecked: insert as a block"));
            _paramTips.SetToolTip(_chkCloseAfter,
                L.T("勾选：每次插入成功后自动关闭图库管理器窗口；不勾选：插入后窗口自动还原",
                    "Checked: close the panel automatically after each successful insert; unchecked: restore the panel after insert"));

            _paramPanel.Controls.Add(_lblScale);
            _paramPanel.Controls.Add(_txtScaleX);
            _paramPanel.Controls.Add(_lblLayerName);
            _paramPanel.Controls.Add(_chkLayerZero);
            _paramPanel.Controls.Add(_lblLayerHint);
            _paramPanel.Controls.Add(_lblColorName);
            _paramPanel.Controls.Add(_chkColorByLayer);
            _paramPanel.Controls.Add(_lblColorHint);
            _paramPanel.Controls.Add(_lblExplodeName);
            _paramPanel.Controls.Add(_chkExplode);
            _paramPanel.Controls.Add(_lblExplodeHint);
            _paramPanel.Controls.Add(_lblCloseName);
            _paramPanel.Controls.Add(_chkCloseAfter);
            _paramPanel.Controls.Add(_lblCloseHint);

            // 面板宽度变化时提示文字宽度要跟着变
            _paramPanel.Resize += new EventHandler(delegate (object s, EventArgs e) { LayoutParamPanel(); });

            _paramPanel.Controls.Add(_paramHeaderPanel);   // 最后加 = 贴顶
        }

        /// <summary>标题栏高度（参数行从此往下排）。</summary>
        private const int ParamHeaderTotalHeight = 32;

        /// <summary>每个参数行的行高（加大行距，确保互不遮挡）。</summary>
        private const int ParamRowHeight = 44;

        /// <summary>参数面板完整显示所需的最低高度（标题栏 + 5 行 + 底部余量）。</summary>
        private int ParamPanelMinHeight
        {
            get { return ParamHeaderTotalHeight + 5 * ParamRowHeight + 10; }
        }

        /// <summary>参数面板内容的固定纵向布局：五行，每行一个参数，行后跟说明提示。</summary>
        private void LayoutParamPanel()
        {
            if (_paramPanel == null || _lblScale == null) return;

            Label lblScalePrefix = null;
            foreach (Control c in _paramPanel.Controls)
            {
                if (c.Name == "lblScalePrefix") { lblScalePrefix = c as Label; break; }
            }

            try
            {
                int labelX = 12;        // 参数名列（所有行左对齐）
                int valueX = 152;       // 值列（输入框 / 勾选框统一对齐到这里，避开最长的参数名文字）
                int panelWidth = _paramPanel.ClientSize.Width;
                int row = ParamHeaderTotalHeight + 3;

                // 行1：插入比例 1:X
                _lblScale.Location = new Point(labelX, row + 3);
                int cx = valueX;
                if (lblScalePrefix != null)
                {
                    lblScalePrefix.Location = new Point(cx, row + 3);
                    cx += lblScalePrefix.Width + 2;
                }
                _txtScaleX.Location = new Point(cx, row);

                // 勾选框统一右对齐到"插入比例"输入框的右边缘
                int chkRight = _txtScaleX.Right;

                // 行2：图层为0（勾选框与参数名文字同一水平中心线，且右对齐输入框）
                row += ParamRowHeight;
                _lblLayerName.Location = new Point(labelX, row + 3);
                int center3 = _lblLayerName.Top + Math.Max(1, _lblLayerName.Height) / 2;
                _chkLayerZero.Location = new Point(chkRight - _chkLayerZero.Width,
                    center3 - _chkLayerZero.Height / 2);
                _lblLayerHint.SetBounds(chkRight + 6, row + 3,
                    Math.Max(0, panelWidth - chkRight - 12), 18);

                // 行3：颜色跟随层
                row += ParamRowHeight;
                _lblColorName.Location = new Point(labelX, row + 3);
                int center4 = _lblColorName.Top + Math.Max(1, _lblColorName.Height) / 2;
                _chkColorByLayer.Location = new Point(chkRight - _chkColorByLayer.Width,
                    center4 - _chkColorByLayer.Height / 2);
                _lblColorHint.SetBounds(chkRight + 6, row + 3,
                    Math.Max(0, panelWidth - chkRight - 12), 18);

                // 行4：插入后分解图块
                row += ParamRowHeight;
                _lblExplodeName.Location = new Point(labelX, row + 3);
                int center5 = _lblExplodeName.Top + Math.Max(1, _lblExplodeName.Height) / 2;
                _chkExplode.Location = new Point(chkRight - _chkExplode.Width,
                    center5 - _chkExplode.Height / 2);
                _lblExplodeHint.SetBounds(chkRight + 6, row + 3,
                    Math.Max(0, panelWidth - chkRight - 12), 18);

                // 行5：插入图形后关闭窗口（勾选框与其余各行同一列对齐）
                row += ParamRowHeight;
                _lblCloseName.Location = new Point(labelX, row + 3);
                int center6 = _lblCloseName.Top + Math.Max(1, _lblCloseName.Height) / 2;
                _chkCloseAfter.Location = new Point(chkRight - _chkCloseAfter.Width,
                    center6 - _chkCloseAfter.Height / 2);
                _lblCloseHint.SetBounds(chkRight + 6, row + 3,
                    Math.Max(0, panelWidth - chkRight - 12), 18);
            }
            catch
            {
                // 布局失败不影响功能
            }
        }

        /// <summary>
        /// 左侧上下分割（目录树 / 插入参数面板）的分割位置：
        /// 拿到真实高度后，默认让两者平分高度（各 50%）。
        /// </summary>
        private void ApplyLeftSplitDistance()
        {
            if (_leftSplitApplied || _leftSplit == null) return;
            if (_leftSplit.Height <= 0 || _leftSplit.Width <= 0) return;

            try
            {
                int total = _leftSplit.Height;
                int splitter = _leftSplit.SplitterWidth > 0 ? _leftSplit.SplitterWidth : 4;

                // 优先恢复上次记忆的位置；没保存过才默认平分高度
                int half = _leftSplitDistance > 0 ? _leftSplitDistance : total / 2;

                // 参数面板（Panel2）至少要能完整放下标题栏 + 4 行参数，不够就压缩目录树
                int max = total - splitter - ParamPanelMinHeight;
                if (max < _leftSplit.Panel1MinSize) max = _leftSplit.Panel1MinSize;

                if (half > max) half = max;
                if (half < _leftSplit.Panel1MinSize) half = _leftSplit.Panel1MinSize;

                _leftSplit.SplitterDistance = half;

                _leftSplitApplied = true;
            }
            catch (System.Exception ex)
            {
                // 布局异常绝不能冒到 AutoCAD 主循环
                BlmLog.Write("ApplyLeftSplitDistance 失败: " + ex.Message);
                _leftSplitApplied = true;     // 失败也不再重试，避免反复抛
            }
        }

        private void OnLeftSplitResize(object sender, EventArgs e)
        {
            ApplyLeftSplitDistance();
        }

        /// <summary>
        /// 用户拖动左侧上下分割条后把位置写进配置，下次启动按上次的分割位置恢复。
        /// 程序化初始设置（_leftSplitApplied 尚为 false）不落盘；
        /// 值没变化时不写盘，避免拖动窗口大小时频繁写配置。
        /// </summary>
        private void OnLeftSplitterMoved(object sender, SplitterEventArgs e)
        {
            if (_config == null || _leftSplit == null) return;
            if (!_leftSplitApplied) return;

            try
            {
                int height = _leftSplit.SplitterDistance;
                if (height >= 60 && height != _config.LeftSplitDistance)
                {
                    _leftSplitDistance = height;
                    _config.LeftSplitDistance = height;
                    _config.Save();
                }
            }
            catch
            {
                // 保存失败不影响界面
            }
        }

        /// <summary>
        /// 构建目录树里的"此电脑"根分支（与图库根目录并行），
        /// 参照资源管理器导航窗格：桌面 / 下载 / 文档 / 图片 / 音乐 / 视频 + 各磁盘驱动器。
        /// 图标全部使用系统图标；子目录展开时惰性加载。
        /// </summary>
        private TreeNode BuildPcRootNode()
        {
            TreeNode computer = new TreeNode(L.T("此电脑", "This PC"));
            computer.Tag = new PcTag(null);
            ApplyPcIcon(computer, "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}");

            // ---- 常用特殊文件夹（与资源管理器导航窗格一致） ----
            AddPcSpecialFolder(computer, "桌面", Environment.SpecialFolder.DesktopDirectory);
            AddPcSpecialFolder(computer, "下载", KnownFolders.GetDownloadsPath());
            AddPcSpecialFolder(computer, "文档", Environment.SpecialFolder.MyDocuments);
            AddPcSpecialFolder(computer, "图片", Environment.SpecialFolder.MyPictures);
            AddPcSpecialFolder(computer, "音乐", Environment.SpecialFolder.MyMusic);
            AddPcSpecialFolder(computer, "视频", Environment.SpecialFolder.MyVideos);

            // ---- 磁盘驱动器 ----
            try
            {
                System.IO.DriveInfo[] drives = System.IO.DriveInfo.GetDrives();
                foreach (System.IO.DriveInfo drive in drives)
                {
                    string root = drive.Name;
                    if (string.IsNullOrEmpty(root)) continue;

                    string label = root.TrimEnd(System.IO.Path.DirectorySeparatorChar);
                    string display = label;

                    bool ready = false;
                    try { ready = drive.IsReady; } catch { }

                    if (ready)
                    {
                        string typeName = DescribeDriveType(drive.DriveType);
                        if (string.IsNullOrEmpty(typeName)) typeName = "本地磁盘";

                        string volume = null;
                        try { volume = drive.VolumeLabel; } catch { }

                        display = string.IsNullOrEmpty(volume)
                            ? typeName + " (" + label + ")"
                            : volume + " (" + label + ")";
                    }

                    TreeNode node = new TreeNode(display);
                    node.Tag = new PcTag(root);
                    ApplyPcIcon(node, root);
                    computer.Nodes.Add(node);

                    if (ready) AddPcDummy(node);
                }
            }
            catch { }

            return computer;
        }

        /// <summary>往"此电脑"分支加一个特殊文件夹节点（路径无效时跳过）。</summary>
        private void AddPcSpecialFolder(TreeNode parent, string display, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { if (!System.IO.Directory.Exists(path)) return; }
            catch { return; }

            TreeNode node = new TreeNode(display);
            node.Tag = new PcTag(path);
            ApplyPcIcon(node, path);
            node.ToolTipText = path;
            parent.Nodes.Add(node);
            AddPcDummy(node);
        }

        private void AddPcSpecialFolder(TreeNode parent, string display, Environment.SpecialFolder folder)
        {
            string path = null;
            try { path = Environment.GetFolderPath(folder); } catch { }
            AddPcSpecialFolder(parent, display, path);
        }

        private static string DescribeDriveType(System.IO.DriveType type)
        {
            switch (type)
            {
                case System.IO.DriveType.Removable: return "可移动磁盘";
                case System.IO.DriveType.Fixed: return "本地磁盘";
                case System.IO.DriveType.CDRom: return "光盘驱动器";
                case System.IO.DriveType.Network: return "网络驱动器";
                case System.IO.DriveType.Ram: return "RAM 磁盘";
                default: return string.Empty;
            }
        }

        /// <summary>
        /// 给"此电脑"分支的节点挂系统图标（按真实路径取，带缓存），
        /// 取不到时退回普通文件夹图标。图标挂在与目录树共用的 _treeIcons 上。
        /// </summary>
        private void ApplyPcIcon(TreeNode node, string path)
        {
            string fallbackKey = IconFolderClosed;
            try
            {
                Image icon = SystemIconProvider.GetPathIcon(path);
                if (icon != null)
                {
                    string key = "p:" + path.ToLowerInvariant();
                    if (!_treeIcons.Images.ContainsKey(key))
                        _treeIcons.Images.Add(key, icon);

                    if (_treeIcons.Images.ContainsKey(key))
                    {
                        node.ImageKey = key;
                        node.SelectedImageKey = key;
                        return;
                    }
                }
            }
            catch { }

            if (!string.IsNullOrEmpty(fallbackKey))
            {
                node.ImageKey = fallbackKey;
                node.SelectedImageKey = fallbackKey;
            }
        }

        private static void AddPcDummy(TreeNode node)
        {
            PcTag tag = node.Tag as PcTag;
            if (tag == null || string.IsNullOrEmpty(tag.Path)) return;
            if (node.Nodes.Count > 0) return;

            TreeNode dummy = new TreeNode("载入中…");
            dummy.Tag = null;                    // Tag == null 即占位节点
            node.Nodes.Add(dummy);
        }

        /// <summary>惰性加载"此电脑"分支的子目录。</summary>
        private void EnsurePcLoaded(TreeNode node)
        {
            if (node == null) return;

            PcTag tag = node.Tag as PcTag;
            if (tag == null || string.IsNullOrEmpty(tag.Path)) return;

            // 只有还挂着占位符时才需要真正加载
            if (node.Nodes.Count == 1 && node.Nodes[0].Tag == null)
            {
                node.Nodes.Clear();

                string[] dirs;
                try { dirs = System.IO.Directory.GetDirectories(tag.Path); }
                catch (System.Exception ex)
                {
                    // 节点已经被清空了，直接走人会让"载入中…"占位符也不再回来，
                    // 而这个分支的展开条件正是"还挂着占位符"——该目录从此再也展不开，
                    // 界面上还没有任何提示。这里把占位符补回去并说明原因。
                    AddPcDummy(node);
                    SetStatus("无法访问该目录：" + tag.Path + "（" + ex.Message + "）");
                    return;
                }

                List<string> visible = new List<string>();
                foreach (string dir in dirs)
                {
                    try
                    {
                        System.IO.FileAttributes attr = System.IO.File.GetAttributes(dir);
                        if ((attr & System.IO.FileAttributes.Hidden) == System.IO.FileAttributes.Hidden) continue;
                        if ((attr & System.IO.FileAttributes.System) == System.IO.FileAttributes.System) continue;
                    }
                    catch
                    {
                        continue;    // 无权访问的目录直接跳过
                    }
                    visible.Add(dir);
                }

                try
                {
                    visible.Sort(delegate (string a, string b)
                    {
                        string x, y;
                        try { x = new System.IO.DirectoryInfo(a).Name; } catch { x = a; }
                        try { y = new System.IO.DirectoryInfo(b).Name; } catch { y = b; }
                        return string.Compare(x, y, StringComparison.CurrentCultureIgnoreCase);
                    });
                }
                catch { }

                foreach (string dir in visible)
                {
                    string name = dir;
                    try { name = new System.IO.DirectoryInfo(dir).Name; } catch { }
                    if (string.IsNullOrEmpty(name)) name = dir;

                    TreeNode child = new TreeNode(name);
                    child.Tag = new PcTag(dir);
                    ApplyPcIcon(child, dir);
                    node.Nodes.Add(child);
                    AddPcDummy(child);
                }
            }
        }

        private void ApplyConfigToUi()
        {
            if (_config == null) return;

            int size = _config.ThumbSize;
            try
            {
                // ImageList 里已有图片时改尺寸会失败（句柄已建），
                // 所以先清空，再重新挂到 ListView 上，新尺寸才真正生效。
                // 面板反复初始化时这里可能抛异常，不能让它冒出去拖垮 AutoCAD。
                if (_imageList.Images.Count > 0) _imageList.Images.Clear();
                _imageList.ImageSize = new Size(size, size);

                _list.LargeImageList = null;
                _list.SmallImageList = null;
                _list.LargeImageList = _imageList;
                _list.SmallImageList = _imageList;

                // 自绘之后 ImageList 里只需要一张透明占位图来撑尺寸
                EnsurePlaceholderImage();
            }
            catch (System.Exception ex)
            {
                BlmLog.Write("ApplyConfigToUi 缩略图尺寸 " + size + " 失败: " + ex.Message);
            }

            // 规格变了，占位图（"DWG" / "块"）必须按新尺寸重画
            if (_thumbnails != null)
            {
                try { _thumbnails.DropPlaceholders(); }
                catch { }
            }

            switch (_config.ViewMode)
            {
                case GalleryView.Details:
                    _list.View = View.Details;
                    break;
                case GalleryView.List:
                    _list.View = View.List;
                    break;
                default:
                    _list.View = View.LargeIcon;
                    break;
            }

            // 恢复"插入图形后关闭窗口"的勾选状态（配置持久化）
            if (_chkCloseAfter != null)
            {
                _chkCloseAfter.Checked = _config.InsertCloseAfter;
            }

            SetStatus("就绪。当前图库根目录 " + _config.RootPaths.Count + " 个。");
        }

        /// <summary>把控件真正显示出来之后（拿到实际宽度）再设置分割条位置。</summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            ApplySplitterDistance();
            ApplyLeftSplitDistance();
            LayoutBottomRow();      // AutoSize 标签的宽度只有此时才准确
            LayoutParamPanel();
        }

        private void OnSplitResize(object sender, EventArgs e)
        {
            ApplySplitterDistance();
        }

        /// <summary>
        /// 用户拖动分割条（或程序设置 SplitterDistance）后把宽度写进配置，
        /// 下次启动按这个宽度恢复。值没变化时不写盘，避免拖动窗口时频繁落盘。
        /// </summary>
        private void OnSplitterMoved(object sender, SplitterEventArgs e)
        {
            if (_config == null || _split == null) return;

            try
            {
                int width = _split.SplitterDistance;
                if (width >= 100 && width != _config.TreePanelWidth)
                {
                    _treeWidth = width;
                    _config.TreePanelWidth = width;
                    _config.Save();
                }
            }
            catch
            {
                // 保存失败不影响界面
            }
        }

        /// <summary>
        /// 设置分割条位置与两侧最小宽度。
        /// SplitterDistance 的合法区间是 [Panel1MinSize, Width - Panel2MinSize]；
        /// 且 Panel2MinSize 的 setter 内部也会重算 SplitterDistance，同样可能抛异常。
        /// 因此先确认控件宽度足以容纳两个最小值，再依次赋值，全程 try/catch 兜底。
        /// 面板还很小的时候什么都不做，等下一次 Resize 再试。
        /// </summary>
        private void ApplySplitterDistance()
        {
            if (_splitterApplied || _split == null) return;
            if (_split.Width <= 0 || _split.Height <= 0) return;

            const int wantPanel1Min = 100;
            const int wantPanel2Min = 160;
            int splitter = _split.SplitterWidth > 0 ? _split.SplitterWidth : 4;

            // 宽度不足以同时容纳两侧最小值 + 分割条时，维持默认布局，等下次 Resize
            if (_split.Width < wantPanel1Min + wantPanel2Min + splitter) return;

            try
            {
                _split.Panel1MinSize = wantPanel1Min;
                _split.Panel2MinSize = wantPanel2Min;

                int min = _split.Panel1MinSize;
                int max = _split.Width - _split.Panel2MinSize;
                if (max >= min)
                {
                    int desired = _treeWidth;
                    if (desired > max) desired = max;
                    if (desired < min) desired = min;
                    _split.SplitterDistance = desired;
                }

                _splitterApplied = true;
            }
            catch (System.Exception ex)
            {
                // 绝不让布局异常冒到 AutoCAD 主循环
                BlmLog.Write("ApplySplitterDistance 失败: " + ex.Message);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_thumbnails != null)
                {
                    _thumbnails.ThumbnailReady -= OnThumbnailReady;
                    _thumbnails.Dispose();
                    _thumbnails = null;
                }
                if (_imageList != null)
                {
                    _imageList.Dispose();
                    _imageList = null;
                }
                if (_treeIcons != null)
                {
                    _treeIcons.Dispose();
                    _treeIcons = null;
                }
                if (_searchTimer != null)
                {
                    _searchTimer.Stop();
                    _searchTimer.Dispose();
                    _searchTimer = null;
                }

                // 下面这几个不是子控件，base.Dispose 不会替我们释放
                if (_paramTips != null)
                {
                    _paramTips.Dispose();
                    _paramTips = null;
                }
                if (_menuList != null)
                {
                    _menuList.Dispose();
                    _menuList = null;
                }
                if (_menuTree != null)
                {
                    _menuTree.Dispose();
                    _menuTree = null;
                }
            }
            base.Dispose(disposing);
        }
    }
}
