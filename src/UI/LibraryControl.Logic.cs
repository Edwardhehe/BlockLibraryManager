using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace BlockLibraryManager
{
    /// <summary>
    /// LibraryControl 的数据 / 逻辑部分（partial 类的第二半）：
    /// 分类树构建、列表渲染、搜索、最近使用、插入参数收集。
    /// </summary>
    public partial class LibraryControl
    {
        /// <summary>"最近使用"节点的标签对象，用于和普通分类节点区分。</summary>
        private readonly object RecentTag = new object();

        /// <summary>"收藏"根节点的标签（Path 为空）。</summary>
        private readonly FavoriteTag FavoritesRootTag = new FavoriteTag(null);

        private readonly Dictionary<string, ListViewItem> _itemMap =
            new Dictionary<string, ListViewItem>(StringComparer.OrdinalIgnoreCase);

        public void ReloadLibrary(bool restoreSelection)
        {
            if (_config == null) return;
            if (_index == null) return;      // Initialize 半途抛过异常时这里会 NRE（v1.51.12）

            // v1.51.12：这条路径以前是**主线程同步整盘重扫**（几千个 DWG，
            // 无进度提示），命令 BLOCKLIBR 就是走它 → CAD 在命令执行期间完全无响应。
            // 现在已经没有一个调用点需要同步语义了，前面加过守卫之后直接
            // 转到后台扫描路径，防止以后有人再被它坑一次。
            ReloadWithProgress(restoreSelection ? GetSelectedCategoryPath() : null);
        }

        /// <summary>
        /// 添加图库目录后的重新加载：后台线程扫描 + 前台进度条，扫描结束后再重建界面。
        /// 扫描只碰文件系统，不碰 AutoCAD API（内部块展开会自动跳过），可以安全放在后台。
        /// </summary>
        /// <param name="focusPath">扫描结束后要定位到的目录。</param>
        /// <param name="announceAdd">
        /// 是否播报"已添加图库"。只有真的新加了图库目录才该播；
        /// 刷新 / 粘贴入库 / 批量入库这些走同一条加载流程，谎报会误导用户。
        /// </param>
        public void ReloadWithProgress(string focusPath, bool announceAdd = false)
        {
            if (_config == null || _index == null) return;

            // 用宿主窗体（无边框的 LibraryForm）当模态对话框的宿主，比拿 UserControl 更稳。
            // 注意：Form.Visible 在最小化时依然是 true，必须再判 WindowState，
            // 否则进度框会以一个最小化窗口为宿主，定位到屏幕外。
            IWin32Window owner = null;
            Form host = FindForm();
            if (host != null && host.Visible && host.WindowState != FormWindowState.Minimized)
                owner = host;

            // 延迟 450ms 才显示窗体：小图库扫得快，根本不会看到进度条，也不会闪
            using (ProgressForm form = ProgressForm.Run(owner, "正在加载图库", "正在扫描图块文件，请稍候…", 450,
                delegate (ProgressForm.ProgressReport report, ProgressForm.CancelQuery isCancelled)
                {
                    _index.Reload(
                        delegate (int percent, string text) { report(percent, text); },
                        delegate { return isCancelled(); });
                }))
            {
                // 回到主线程：_index 已经就绪，重建树和列表
                RebuildAfterScan(focusPath);

                if (form.Error != null)
                {
                    SetStatus("扫描出错：" + form.Error.Message);
                    return;
                }

                if (form.Cancelled || _index.ScanCancelled)
                {
                    SetStatus("已取消加载，只加载了部分目录（点“刷新”可重新完整加载）。");
                    return;
                }
            }

            if (announceAdd)
            {
                CategoryNode added = string.IsNullOrEmpty(focusPath) ? null : _index.FindNodeByPath(focusPath);
                if (added != null)
                    SetStatus("已添加图库：" + added.FullPath + "（含子目录共 " + added.TotalCount + " 个图块）");
                else
                    SetStatus("已添加图库：" + focusPath + "（该目录内暂无图块文件）");
                return;
            }

            // 普通刷新：报"当前显示多少个图块"，不要谎报"已添加图库"
            if (_currentBlocks != null)
                SetStatus("图库已刷新，当前显示 " + _currentBlocks.Count + " 个图块。");
        }

        /// <summary>扫描结束后重建分类树与右侧列表（必须在主线程调用）。</summary>
        private void RebuildAfterScan(string focusPath)
        {
            Cursor saved = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;
            try
            {
                BuildTree();

                if (!string.IsNullOrEmpty(focusPath))
                    SelectNodeByPath(focusPath);

                if (_tree != null && _tree.SelectedNode == null)
                    SelectNodeByPath(_config.LastCategoryPath);

                if (_tree != null && _tree.SelectedNode == null && _tree.Nodes.Count > 0)
                    _tree.SelectedNode = _tree.Nodes[0];

                RefreshCurrentView();
            }
            finally
            {
                Cursor.Current = saved;

                string error = _index.LastError;
                if (!string.IsNullOrEmpty(error))
                    SetStatus("扫描完成，部分目录无法访问：" + error);
            }
        }

        // ---------------- 分类树 ----------------

        /// <summary>清掉"此电脑"分支逐目录缓存的图标，避免 ImageList 无上限增长。</summary>
        private void ClearPcIcons()
        {
            if (_treeIcons == null) return;

            try
            {
                List<string> stale = new List<string>();
                foreach (string key in _treeIcons.Images.Keys)
                {
                    if (!string.IsNullOrEmpty(key) && key.StartsWith("p:", StringComparison.Ordinal))
                        stale.Add(key);
                }

                foreach (string key in stale) _treeIcons.Images.RemoveByKey(key);
            }
            catch
            {
                // 清不掉最多是多占点内存，树本身照常
            }
        }

        private void BuildTree()
        {
            if (_tree == null) return;

            _tree.BeginUpdate();
            try
            {
                _tree.Nodes.Clear();

                // "此电脑"分支每展开一个目录就往 ImageList 塞一张图标（键以 "p:" 开头），
                // 树是重建了但图标一直没清——翻盘翻久了会攒下成百上千张用不到的位图。
                ClearPcIcons();

                CategoryNode root = _index.Root;

                TreeNode allNode = new TreeNode(root.DisplayName);
                allNode.Tag = null;                       // null 表示"全部图库"
                allNode.ImageKey = IconFolderClosed;      // 虚拟根节点也用文件夹图标，视觉一致
                allNode.SelectedImageKey = IconFolderOpen;
                _tree.Nodes.Add(allNode);

                TreeNode recentNode = new TreeNode(L.T("最近使用", "Recent"));
                recentNode.Tag = RecentTag;
                recentNode.ImageKey = IconFolderClosed;
                recentNode.SelectedImageKey = IconFolderOpen;
                _tree.Nodes.Add(recentNode);

                // 收藏根节点：下面列出用户收藏的常用图库文件夹
                TreeNode favRoot = new TreeNode(L.T("收藏", "Favorites"));
                favRoot.Tag = FavoritesRootTag;
                favRoot.ImageKey = IconFolderClosed;
                favRoot.SelectedImageKey = IconFolderOpen;
                _tree.Nodes.Add(favRoot);

                foreach (string favPath in _config.FavoritePaths)
                {
                    if (string.IsNullOrEmpty(favPath)) continue;

                    string name = favPath;
                    try { name = new DirectoryInfo(favPath).Name; } catch { }
                    if (string.IsNullOrEmpty(name)) name = favPath;

                    TreeNode favItem = new TreeNode(name);
                    favItem.Tag = new FavoriteTag(favPath);
                    favItem.ImageKey = IconFolderClosed;
                    favItem.SelectedImageKey = IconFolderOpen;
                    favItem.ToolTipText = favPath;
                    favRoot.Nodes.Add(favItem);
                }

                foreach (CategoryNode child in root.Children)
                    AddCategoryNode(allNode, child);

                // "此电脑"根分支：与图库根目录并行，参照资源管理器导航窗格
                // （桌面 / 下载 / 文档 / 图片 / 音乐 / 视频 + 各磁盘驱动器）
                _tree.Nodes.Add(BuildPcRootNode());

                allNode.Expand();
                favRoot.Expand();
            }
            finally
            {
                _tree.EndUpdate();
            }
        }

        private void AddCategoryNode(TreeNode parent, CategoryNode category)
        {
            TreeNode node = new TreeNode(category.DisplayName);
            node.Tag = category;
            node.ImageKey = IconFolderClosed;         // Explorer 原生文件夹图标
            node.SelectedImageKey = IconFolderOpen;   // 展开选中时显示打开态
            parent.Nodes.Add(node);

            foreach (CategoryNode child in category.Children)
                AddCategoryNode(node, child);
        }

        private void SelectNodeByPath(string path)
        {
            if (_tree == null) return;
            if (string.IsNullOrEmpty(path)) return;

            foreach (TreeNode node in EnumerateNodes(_tree.Nodes))
            {
                CategoryNode category = node.Tag as CategoryNode;
                if (category == null) continue;
                if (string.Equals(category.FullPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    ExpandTo(node);
                    _tree.SelectedNode = node;
                    return;
                }
            }
        }

        private void SelectRecentNode()
        {
            if (_tree == null) return;

            foreach (TreeNode node in EnumerateNodes(_tree.Nodes))
            {
                if (ReferenceEquals(node.Tag, RecentTag))
                {
                    _tree.SelectedNode = node;
                    return;
                }
            }
        }

        private static IEnumerable<TreeNode> EnumerateNodes(TreeNodeCollection nodes)
        {
            foreach (TreeNode node in nodes)
            {
                yield return node;
                foreach (TreeNode child in EnumerateNodes(node.Nodes))
                    yield return child;
            }
        }

        private static void ExpandTo(TreeNode node)
        {
            TreeNode parent = node.Parent;
            while (parent != null)
            {
                parent.Expand();
                parent = parent.Parent;
            }
        }

        private string GetSelectedCategoryPath()
        {
            CategoryNode category = GetSelectedCategory();
            return category != null ? category.FullPath : null;
        }

        private CategoryNode GetSelectedCategory()
        {
            if (_tree == null || _tree.SelectedNode == null) return null;
            return _tree.SelectedNode.Tag as CategoryNode;
        }

        private bool IsRecentSelected()
        {
            return _tree != null && _tree.SelectedNode != null &&
                   ReferenceEquals(_tree.SelectedNode.Tag, RecentTag);
        }

        private bool IsWholeLibrarySelected()
        {
            return _tree != null && _tree.SelectedNode != null && _tree.SelectedNode.Tag == null;
        }

        // ---------------- 列表渲染 ----------------

        private void RefreshCurrentView()
        {
            if (_index == null) return;

            List<BlockItem> items = BuildCurrentList();
            DisplayBlocks(items);
        }

        private List<BlockItem> BuildCurrentList()
        {
            // 关键词搜索：在整个图库（全部根目录、全部子目录）范围内匹配 dwg 文件名，
            // 不受当前选中分类限制；清空关键词后恢复显示当前选中位置的内容
            if (!string.IsNullOrEmpty(_searchKeyword))
                return LibraryIndex.FilterByName(_index.AllBlocks, _searchKeyword);

            // 浏览"此电脑"文件树：动态列出该文件夹顶层的图块文件
            if (!string.IsNullOrEmpty(_pcSelectedPath))
                return ListFolderBlocks(_pcSelectedPath);

            if (IsRecentSelected())
                return _index.BuildRecentList(GetRootPathForSelected());

            // 收藏节点：定位到收藏的文件夹，按其分类显示图块
            if (_tree != null && _tree.SelectedNode != null)
            {
                FavoriteTag fav = _tree.SelectedNode.Tag as FavoriteTag;
                if (fav != null)
                {
                    if (string.IsNullOrEmpty(fav.Path)) return new List<BlockItem>();

                    CategoryNode favCategory = _index.FindNodeByPath(fav.Path);
                    if (favCategory == null)
                    {
                        SetStatus("收藏的文件夹不在当前图库中：" + fav.Path);
                        return new List<BlockItem>();
                    }

                    return _config.SearchSubCategories
                        ? _index.GetBlocksRecursive(favCategory)
                        : _index.GetBlocksInCategory(favCategory);
                }
            }

            CategoryNode category = GetSelectedCategory();

            if (category == null)
                return _index.AllBlocks;

            if (_config.SearchSubCategories)
                return _index.GetBlocksRecursive(category);

            return _index.GetBlocksInCategory(category);
        }

        /// <summary>列出电脑文件夹顶层的图块文件（仅浏览，不入库）。</summary>
        private List<BlockItem> ListFolderBlocks(string folder)
        {
            List<BlockItem> result = new List<BlockItem>();
            if (string.IsNullOrEmpty(folder)) return result;
            if (!Directory.Exists(folder)) return result;

            try
            {
                string[] files = Directory.GetFiles(folder);
                foreach (string file in files)
                {
                    if (!IsLibraryFile(file)) continue;
                    result.Add(BlockItem.FromFile(null, file));
                }

                result.Sort(delegate (BlockItem a, BlockItem b)
                {
                    return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
                });
            }
            catch
            {
                // 无权访问 / 目录刚被删除时给空列表即可
            }
            return result;
        }

        private string GetRootPathForSelected()
        {
            CategoryNode category = GetSelectedCategory();
            if (category == null)
                return _config.RootPaths.Count > 0 ? _config.RootPaths[0] : null;

            CategoryNode node = category;
            while (node.Parent != null) node = node.Parent;
            return node.FullPath;
        }

        // 注：原来的 CreateDisplayImage（把动态块角标"烤"进一张复制出来的 Bitmap）
        // 已删除。自绘之后角标改成绘制期叠加，见 LibraryControl.Draw.cs 的
        // DrawItemOverlays / DrawDynamicBadge —— 零拷贝，也不再需要
        // ImageList 的反复增删。

        /// <summary>
        /// 可见性切换的一对三角形按钮在缩略图（实际绘制矩形）里的整体范围：
        /// 水平居中、垂直贴底，**离底边留 10px**（用户指定，v1.51.9）。
        /// 绘制与点击命中共用同一套比例，保证两者对得上。
        /// </summary>
        private static Rectangle GetTrianglePairBounds(int width, int height)
        {
            if (width < 8) width = 8;
            if (height < 8) height = 8;

            int bottomGap = 10;                     // 离缩略图底边的间距
            const float scale = 0.36f;              // 用户指定：在 0.3 倍基础上再放大 1.2 倍（0.3 × 1.2）

            // v1.51.12：小缩略图（列表视图只有几十像素高）时，10px 底边间距 + 6px
            // 按钮会把 y 算成负数 —— 按钮画到图的上边界之外，
            // 既看不见又点不中。间距让位于图宽高：最多占图高的 1/6。
            int maxGap = Math.Max(0, height / 6);
            if (bottomGap > maxGap) bottomGap = maxGap;

            int h = (int)Math.Round(height / 5f * scale);
            if (h < 8) h = 8;                       // 再小就看不见了，保底 8px（命中判定另有 3px 放宽）
            if (h > height - bottomGap - 2) h = height - bottomGap - 2;   // 留出底边后仍要在图内
            if (h < 6) h = 6;
            if (h > height) h = height;

            int w = h * 2 + 2;                       // 两个方形按钮 + 中间 2px 间隙
            if (w > width - 2) w = width - 2;
            if (w < 4) w = 4;

            // x / y 都要 clamp 回图内：h 被顶到接近 height、或 w 大于 width 时
            // 上面算出来的坐标可能是负的，那样按钮就跑到图上边界之外了。
            int y = height - h - bottomGap;
            if (y < 0) y = Math.Max(0, height - h);
            int x = (width - w) / 2;
            if (x < 0) x = 0;

            return new Rectangle(x, y, w, h);
        }

        /// <summary>朝左三角形按钮（上一个可见性状态）的位置：底部居中、靠左。</summary>
        internal static Rectangle GetPrevTriangleRect(int width, int height)
        {
            Rectangle pair = GetTrianglePairBounds(width, height);
            return new Rectangle(pair.X, pair.Y, pair.Width / 2 - 1, pair.Height);
        }

        /// <summary>朝右三角形按钮（下一个可见性状态）的位置：底部居中、靠右。</summary>
        internal static Rectangle GetNextTriangleRect(int width, int height)
        {
            Rectangle pair = GetTrianglePairBounds(width, height);
            return new Rectangle(pair.X + pair.Width / 2 + 1, pair.Y, pair.Width / 2 - 1, pair.Height);
        }

        /// <summary>画一个三角形切换按钮：白底 + 蓝边框 + 蓝色实心三角形。</summary>
        private static void DrawTriangleButton(Graphics g, Rectangle r, bool pointLeft)
        {
            if (r.Width < 4 || r.Height < 4) return;

            using (SolidBrush bg = new SolidBrush(Color.White))
                g.FillRectangle(bg, r);

            using (Pen border = new Pen(Color.FromArgb(0, 120, 215), 1f))
                g.DrawRectangle(border, r.X, r.Y, r.Width - 1, r.Height - 1);

            float cx = r.X + r.Width / 2f;
            float cy = r.Y + r.Height / 2f;
            float s = Math.Min(r.Width, r.Height) * 0.30f;

            PointF[] tri;
            if (pointLeft)
            {
                tri = new PointF[]
                {
                    new PointF(cx + s * 0.6f, cy - s),
                    new PointF(cx + s * 0.6f, cy + s),
                    new PointF(cx - s * 0.8f, cy),
                };
            }
            else
            {
                tri = new PointF[]
                {
                    new PointF(cx - s * 0.6f, cy - s),
                    new PointF(cx - s * 0.6f, cy + s),
                    new PointF(cx + s * 0.8f, cy),
                };
            }

            using (SolidBrush fg = new SolidBrush(Color.FromArgb(0, 120, 215)))
                g.FillPolygon(fg, tri);
        }

        /// <summary>
        /// 列表左键按下：命中缩略图底部中央的"◀ / ▶"三角形按钮时，
        /// 切换该可见性动态块的显示状态（◀ 上一个 / ▶ 下一个）并同步重生成缩略图
        /// （不触发插入）。
        /// </summary>
        private void OnListMouseDown(object sender, MouseEventArgs e)
        {
            try
            {
                if (e.Button != MouseButtons.Left) return;
                if (_thumbnails == null || _list == null) return;

                System.Windows.Forms.ListViewHitTestInfo hit = _list.HitTest(e.Location);
                if (hit.Item == null) return;

                BlockItem item = hit.Item.Tag as BlockItem;
                if (item == null) return;

                List<string> states = _thumbnails.GetVisibilityStates(item);
                if (states == null || states.Count < 2) return;

                // 与绘制侧共用同一个矩形算法：贴"实际画出来的缩略图"定位，
                // 不再按图标格子算（v1.51.9）——否则按钮画在图下方留白里，
                // 画出来的位置和点的位置对不上，看着就像功能没实现。
                Rectangle imageRect = GetDrawnImageRect(hit.Item, hit.Item.Bounds,
                    hit.Item.GetBounds(System.Windows.Forms.ItemBoundsPortion.Icon));

                Rectangle prev = GetPrevTriangleRect(imageRect.Width, imageRect.Height);
                prev.Offset(imageRect.Location);
                Rectangle next = GetNextTriangleRect(imageRect.Width, imageRect.Height);
                next.Offset(imageRect.Location);

                // 按钮本身不大，命中判定向外放宽 3px，避免"看得见却点不中"
                Rectangle prevPad = Rectangle.Inflate(prev, 3, 3);
                Rectangle nextPad = Rectangle.Inflate(next, 3, 3);

                bool prevHit = prevPad.Contains(e.Location);
                bool nextHit = nextPad.Contains(e.Location);
                if (!prevHit && !nextHit) return;

                hit.Item.Selected = true;
                hit.Item.Focused = true;

                _thumbnails.CycleVisibilityThumbnail(item, prevHit ? -1 : 1);

                // 切换是同步完成的（没有排队），这里立刻把重绘刷出去，
                // 不用等下一次空闲，点一下三角形按钮缩略图就跟着换
                FlushListPaint();
            }
            catch
            {
                // 切换失败不影响列表正常选择
            }
        }

        /// <summary>
        /// 判定列表客户区某点是否落在可见性 ◀ ▶ 按钮上（与 OnListMouseDown
        /// 完全同一套几何：实际绘制矩形 + 3px 命中放宽）。
        /// 双击插入前必须先挡一下：在按钮上快速点两下切换状态，
        /// ListView 会把第二次点击当双击触发"插入到当前图形"（v1.51.9 用户反馈）。
        /// </summary>
        private bool IsVisibilityButtonHit(Point listPoint)
        {
            try
            {
                if (_thumbnails == null || _list == null) return false;

                System.Windows.Forms.ListViewHitTestInfo hit = _list.HitTest(listPoint);
                if (hit.Item == null) return false;

                BlockItem item = hit.Item.Tag as BlockItem;
                if (item == null) return false;

                List<string> states = _thumbnails.GetVisibilityStates(item);
                if (states == null || states.Count < 2) return false;

                Rectangle imageRect = GetDrawnImageRect(hit.Item, hit.Item.Bounds,
                    hit.Item.GetBounds(System.Windows.Forms.ItemBoundsPortion.Icon));

                Rectangle prev = GetPrevTriangleRect(imageRect.Width, imageRect.Height);
                prev.Offset(imageRect.Location);
                Rectangle next = GetNextTriangleRect(imageRect.Width, imageRect.Height);
                next.Offset(imageRect.Location);

                return Rectangle.Inflate(prev, 3, 3).Contains(listPoint)
                    || Rectangle.Inflate(next, 3, 3).Contains(listPoint);
            }
            catch
            {
                return false;
            }
        }

        private void DisplayBlocks(List<BlockItem> items)
        {
            if (_list == null) return;

            _currentBlocks = items ?? new List<BlockItem>();
            _itemMap.Clear();
            ResetVisibleStamp();

            // 自绘之后这里**完全不碰 ImageList**：
            // 以前每加一项都要 CreateDisplayImage + Images.Add（把一整张缩略图塞进
            // ImageList），几千项时就是几千次位图转换，是刷新列表最慢的一步。
            // 现在只建 ListViewItem，图由绘制事件按需去缓存取，屏幕外的项根本不取。
            EnsurePlaceholderImage();

            // 本轮需要生成多少张缩略图（RequestThumbnails 现在只登记，不再同步解码 PNG）
            int requested = 0;

            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();

                int count = _currentBlocks.Count;
                if (count > 0)
                {
                    // 一次性 AddRange 比逐条 Add 快得多（少发几千次原生通知）
                    ListViewItem[] buffer = new ListViewItem[count];

                    for (int i = 0; i < count; i++)
                    {
                        BlockItem item = _currentBlocks[i];

                        ListViewItem lvi = new ListViewItem(item.Name);
                        lvi.ImageIndex = 0;          // 指向那张占位图，只为撑出图标尺寸
                        lvi.Tag = item;
                        lvi.ToolTipText = item.FilePath;

                        lvi.SubItems.Add(FormatSize(item.FileSize));
                        lvi.SubItems.Add(FormatTime(item.ModifiedTime));
                        lvi.SubItems.Add(item.CategoryPath);

                        buffer[i] = lvi;
                        _itemMap[item.Key] = lvi;
                    }

                    _list.Items.AddRange(buffer);
                }
            }
            finally
            {
                _list.EndUpdate();
            }

            requested = _thumbnails.RequestThumbnails(_currentBlocks);
            BeginThumbRound(requested);

            string tip = "共 " + _currentBlocks.Count + " 个图块";
            if (!string.IsNullOrEmpty(_searchKeyword))
                tip += "（搜索 “" + _searchKeyword + "”）";

            // 缩略图策略：已有配对缓存（内存/磁盘）的一律复用，只为缺失的生成
            if (requested > 0)
                tip += "；缩略图复用 " + (_currentBlocks.Count - requested) + " 张、需生成 " + requested + " 张";
            else if (_currentBlocks.Count > 0)
                tip += "；缩略图全部复用，无需生成";

            SetStatus(tip);
        }

        private void OnThumbnailReady(BlockItem item)
        {
            OnThumbnailReady(item, true);
        }

        /// <summary>
        /// counted = false 表示这条通知不属于本轮缩略图进度（元数据补测完成的通知）。
        /// 以前一律 _thumbDone++，而元数据条目从来没被登记进 _thumbTotal，
        /// 于是进度会被推着走 —— 状态栏出现"已全部生成"但数字对不上。
        /// </summary>
        private void OnThumbnailReady(BlockItem item, bool counted)
        {
            if (IsDisposed) return;
            if (item == null || _list == null) return;

            if (counted) _thumbDone++;

            // 自绘之后这里几乎什么都不用做：图已经进内存缓存，
            // 绘制事件自己会去取，屏幕外的项压根不会取。
            //
            // 原来这里是"CreateDisplayImage → RemoveByKey → Add → 每条 BeginInvoke"，
            // 三个问题：
            //   1. 每条都 BeginInvoke，生成 2000 张就排 2000 个 UI 消息，界面被自己拖住；
            //   2. RemoveByKey + Add 会让 ImageList 里后面所有项的索引整体错位；
            //   3. 每个动态块都要复制一张 Bitmap 把角标烤进去。
            // 现在只打个脏标记，由空闲处理每帧统一刷一次重绘。
            _listDirty = true;
        }

        internal static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "-";
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MB";
            return (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("0.00") + " GB";
        }

        private static string FormatTime(DateTime time)
        {
            if (time == DateTime.MinValue) return "-";
            return time.ToString("yyyy-MM-dd HH:mm");
        }

        // ---------------- 选中项 ----------------

        private List<BlockItem> GetSelectedBlocks()
        {
            List<BlockItem> result = new List<BlockItem>();
            if (_list == null) return result;

            foreach (ListViewItem lvi in _list.SelectedItems)
            {
                BlockItem item = lvi.Tag as BlockItem;
                if (item != null) result.Add(item);
            }
            return result;
        }

        private BlockItem GetSingleSelectedBlock()
        {
            List<BlockItem> items = GetSelectedBlocks();
            if (items.Count == 0) return null;
            return items[0];
        }

        // ---------------- 插入参数 ----------------

        private InsertOptions BuildInsertOptions()
        {
            InsertOptions options = new InsertOptions();

            // 插入比例 1:X（X 用户自定义，默认 1），X/Y/Z 统一缩放
            double x = ParseDouble(_txtScaleX.Text, 1.0);
            if (Math.Abs(x) < 1e-6) x = 1.0;

            options.ScaleX = x;
            options.ScaleY = x;
            options.ScaleZ = x;
            options.UniformScale = true;

            // 旋转角度（v1.51.2）：面板上的角度输入框已按用户要求移除。
            // 默认 0 度（不旋转）；插入时的两段式 Jig 交互保留 —— 单击定插入点后
            // 仍可移动鼠标转角度、单击固定，回车 = 0 度（默认角度）。
            options.RotationDegrees = 0.0;

            // 炸开：勾选后插入时分解为基本图元（默认不勾选）
            options.ExplodeAfterInsert = _chkExplode != null && _chkExplode.Checked;
            // v1.51.12：这里漏判了 null —— 同方法上下几行的 CheckBox 都做了保护，
            // 只有它没有，控件创建失败或 Initialize 半途出错时会 NRE，
            // 直接中断"插入"这条主流程。
            options.UpdateExistingDefinition = _chkUpdateDef != null && _chkUpdateDef.Checked;

            // 图层：勾选后插入的图块归到 0 图层；不勾选保持图块所在图层
            options.ForceLayerZero = _chkLayerZero != null && _chkLayerZero.Checked;

            // 颜色：勾选后图块颜色跟随图层颜色；不勾选保持图块原有颜色
            options.ColorByLayer = _chkColorByLayer != null && _chkColorByLayer.Checked;

            return options;
        }

        private static double ParseDouble(string text, double fallback)
        {
            if (string.IsNullOrEmpty(text)) return fallback;

            string value = text.Trim();
            double result;
            if (double.TryParse(value, out result))
            {
                if (double.IsNaN(result) || double.IsInfinity(result)) return fallback;
                return result;
            }
            return fallback;
        }

        /// <summary>
        /// 发起一次插入。真正的插入在命令 BLOCKLIBIN 里完成（见 InsertBridge 的说明）。
        /// </summary>
        private void DoInsert(List<BlockItem> items)
        {
            if (items == null || items.Count == 0)
            {
                SetStatus("请先选择要插入的图块。");
                return;
            }

            InsertOptions options = BuildInsertOptions();
            SendInsertCommand(items, options, false);
        }

        /// <summary>
        /// 把请求交给 InsertBridge，再让 AutoCAD 执行命令。
        /// 这样交互式取点发生在正确的命令上下文中，2010~2027 通吃。
        /// 命令发出后面板自动最小化（方便在图形窗口里取点），
        /// 插入结束（BLOCKLIBIN 回调 ReportInsertResult）后自动还原。
        /// </summary>
        private void SendInsertCommand(List<BlockItem> items, InsertOptions options, bool continuous)
        {
            InsertBridge.Request(items, options, continuous);

            string message;
            if (!TrySendCommand("BLOCKLIBIN", out message))
            {
                InsertBridge.Clear();
                SetStatus(message);
                return;
            }

            // 命令已成功排队：把面板临时收起，让出图形窗口；
            // 插入完成后由 BLOCKLIBIN 回调 LibraryPalette.Restore() 原样还原。
            // 用 PushAside 而不是 Minimize：最小化后的窗口可能藏在 CAD 主窗口后面
            // 或缩在任务栏里，万一收尾回调没走到就再也找不回来了。
            try { LibraryPalette.PushAside(); } catch { }

            SetStatus(continuous
                ? "连续插入：单击定插入点，再移动鼠标定角度后单击固定；按 Esc 结束。"
                : "请在图形窗口单击指定插入点，再移动鼠标确定旋转角度后单击固定…");
        }

        private static bool TrySendCommand(string commandText, out string message)
        {
            message = null;

            try
            {
                Autodesk.AutoCAD.ApplicationServices.Document doc =
                    Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;

                if (doc == null)
                {
                    message = "请先在 AutoCAD 中打开一个图形文件。";
                    return false;
                }

                // 参数：activate=true, wrapUpInactiveDoc=false, echoCmd=false
                doc.SendStringToExecute(commandText + "\n", true, false, false);
                return true;
            }
            catch (System.Exception ex)
            {
                message = "无法启动插入命令：" + ex.Message;
                return false;
            }
        }

        /// <summary>由命令 BLOCKLIBIN 回调，用于在面板上显示结果。
        /// 插入操作结束后把最小化的面板自动还原（原大小）并带回前台；
        /// 若参数面板勾选了"插入图形后关闭窗口"且插入成功，则改为直接收起窗口。</summary>
        internal void ReportInsertResult(IList<BlockItem> items, string message, bool succeeded)
        {
            // 右键菜单里那个一次性的"插入图形后关闭"已经移除，
            // 现在只看参数面板第 6 行的勾选（状态已持久化进 BlockLibrary.ini）。
            bool closeAfterInsert = _chkCloseAfter != null && _chkCloseAfter.Checked;

            if (closeAfterInsert && succeeded)
            {
                // 插入成功 → 收起图库管理器窗口（保留浏览状态，下次 BLOCKLIB 原样唤起）
                try { LibraryPalette.Hide(); } catch { }
            }
            else
            {
                try { LibraryPalette.Restore(); } catch { }
            }

            SetStatus(message);

            if (succeeded && _config != null && items != null)
            {
                foreach (BlockItem item in items)
                {
                    if (item == null) continue;
                    _config.TouchRecent(item.FilePath);
                }
                try { _config.Save(); } catch { }
            }
        }

        /// <summary>由命令 BLOCKLIBPASTE 回调：粘贴入库完成后刷新当前目录显示新图块。</summary>
        internal void ReportPasteResult(bool succeeded, string message)
        {
            SetStatus(message);

            if (succeeded)
            {
                try { ReloadWithProgress(GetSelectedCategoryPath()); }
                catch (System.Exception ex)
                {
                    BlmLog.Write("粘贴入库后刷新失败: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// 由命令 BLOCKLIBBATCH 回调：批量入库完成后（窗口已恢复显示）刷新当前目录，
        /// 让新入库的图块立即出现在缩略图列表里。
        /// </summary>
        internal void ReportBatchResult(bool succeeded, string message)
        {
            SetStatus(message);

            if (succeeded)
            {
                try { ReloadWithProgress(GetSelectedCategoryPath()); }
                catch (System.Exception ex)
                {
                    BlmLog.Write("批量入库后刷新失败: " + ex.Message);
                }
            }
        }
    }
}
