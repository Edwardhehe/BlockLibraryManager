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

            string previousPath = GetSelectedCategoryPath();
            bool previousWasRecent = (_tree != null && _tree.SelectedNode != null &&
                                      ReferenceEquals(_tree.SelectedNode.Tag, RecentTag));

            Cursor saved = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;
            try
            {
                _index.Reload();
                BuildTree();

                if (restoreSelection)
                {
                    if (previousWasRecent) SelectRecentNode();
                    else SelectNodeByPath(previousPath);
                }
                else
                {
                    SelectNodeByPath(_config.LastCategoryPath);
                }

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

        /// <summary>
        /// 添加图库目录后的重新加载：后台线程扫描 + 前台进度条，扫描结束后再重建界面。
        /// 扫描只碰文件系统，不碰 AutoCAD API（内部块展开会自动跳过），可以安全放在后台。
        /// </summary>
        public void ReloadWithProgress(string focusPath)
        {
            if (_config == null || _index == null) return;

            // 用宿主窗体（无边框的 LibraryForm）当模态对话框的宿主，比拿 UserControl 更稳
            IWin32Window owner = null;
            Form host = FindForm();
            if (host != null && host.Visible) owner = host;

            // 延迟 450ms 才显示窗体：小图库扫得快，根本不会看到进度条，也不会闪
            ProgressForm form = ProgressForm.Run(owner, "正在加载图库", "正在扫描图块文件，请稍候…", 450,
                delegate (ProgressForm.ProgressReport report, ProgressForm.CancelQuery isCancelled)
                {
                    _index.Reload(
                        delegate (int percent, string text) { report(percent, text); },
                        delegate { return isCancelled(); });
                });

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

            CategoryNode added = string.IsNullOrEmpty(focusPath) ? null : _index.FindNodeByPath(focusPath);
            if (added != null)
                SetStatus("已添加图库：" + added.FullPath + "（含子目录共 " + added.TotalCount + " 个图块）");
            else
                SetStatus("已添加图库：" + focusPath + "（该目录内暂无图块文件）");
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

        private void BuildTree()
        {
            if (_tree == null) return;

            _tree.BeginUpdate();
            try
            {
                _tree.Nodes.Clear();

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

        /// <summary>
        /// 取用于显示的缩略图：动态块在右上角画红色矢量闪电角标（缩小一半、
        /// 紧贴右上角，带白色描边保证深色缩略图上醒目）；
        /// 非动态块直接返回缓存原图（共享缓存不能就地修改）。
        /// </summary>
        private Image CreateDisplayImage(BlockItem item)
        {
            Image img = _thumbnails.GetThumbnail(item);
            if (img == null) return null;
            if (!_thumbnails.IsDynamicBlock(item)) return img;

            try
            {
                Bitmap bmp = new Bitmap(img);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    // 右上角闪电角标：手绘矢量闪电（细长锐利样式），
                    // 无底色、无边框，大小为原来的 0.5 倍，紧贴右上角
                    float s = Math.Max(8f, Math.Min(bmp.Width, bmp.Height) / 8.4f);
                    float boltW = s * 0.72f;
                    float x0 = bmp.Width - boltW - 2f;   // 右缘留 2px
                    float y0 = 2f;                       // 顶缘留 2px

                    // 闪电轮廓（归一化坐标，y 向下）：经典双折线闪电
                    PointF[] bolt = new PointF[]
                    {
                        new PointF(x0 + 0.90f * boltW, y0),                 // 顶端右上
                        new PointF(x0 + 0.08f * boltW, y0 + 0.58f * s),     // 左下斜边
                        new PointF(x0 + 0.52f * boltW, y0 + 0.58f * s),     // 中部右折
                        new PointF(x0 + 0.34f * boltW, y0 + s),             // 底尖
                        new PointF(x0 + 0.94f * boltW, y0 + 0.40f * s),     // 右上斜边
                        new PointF(x0 + 0.50f * boltW, y0 + 0.40f * s),     // 中部左折
                    };

                    using (GraphicsPath path = new GraphicsPath())
                    {
                        path.AddPolygon(bolt);

                        // 白色描边底影：在黑色/深色缩略图上也清晰可见
                        using (Pen halo = new Pen(Color.White, Math.Max(1.2f, s / 12f)))
                        {
                            halo.LineJoin = LineJoin.Round;
                            g.DrawPath(halo, path);
                        }

                        using (SolidBrush red = new SolidBrush(Color.Red))
                            g.FillPath(red, path);
                    }

                    // 可见性动态块：底部中央画"朝左 / 朝右"两个三角形按钮，
                    // 点击分别切换到上一个 / 下一个可见性状态并同步重生成缩略图
                    List<string> visStates = _thumbnails.GetVisibilityStates(item);
                    if (visStates != null && visStates.Count >= 2)
                    {
                        DrawTriangleButton(g, GetPrevTriangleRect(bmp.Width, bmp.Height), true);
                        DrawTriangleButton(g, GetNextTriangleRect(bmp.Width, bmp.Height), false);
                    }
                }
                return bmp;
            }
            catch
            {
                return img;   // 角标画不出来就显示原图
            }
        }

        /// <summary>
        /// 可见性切换的一对三角形按钮在缩略图（或图标矩形）里的整体范围：底部居中。
        /// 绘制与点击命中共用同一套比例，保证两者对得上。
        /// </summary>
        private static Rectangle GetTrianglePairBounds(int width, int height)
        {
            if (width < 8) width = 8;
            if (height < 8) height = 8;

            int h = height / 5;
            if (h < 15) h = Math.Min(15, height - 2);
            if (h > height - 2) h = height - 2;

            int w = h * 2 + 2;                       // 两个方形按钮 + 中间 2px 间隙
            if (w > width - 2) w = width - 2;

            return new Rectangle((width - w) / 2, height - h - 3, w, h);
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

                Rectangle iconRect = hit.Item.GetBounds(System.Windows.Forms.ItemBoundsPortion.Icon);

                Rectangle prev = GetPrevTriangleRect(iconRect.Width, iconRect.Height);
                prev.Offset(iconRect.Location);
                Rectangle next = GetNextTriangleRect(iconRect.Width, iconRect.Height);
                next.Offset(iconRect.Location);

                bool prevHit = prev.Contains(e.Location);
                bool nextHit = next.Contains(e.Location);
                if (!prevHit && !nextHit) return;

                hit.Item.Selected = true;
                hit.Item.Focused = true;

                _thumbnails.CycleVisibilityThumbnail(item, prevHit ? -1 : 1);
            }
            catch
            {
                // 切换失败不影响列表正常选择
            }
        }

        private void DisplayBlocks(List<BlockItem> items)
        {
            if (_list == null) return;

            _currentBlocks = items ?? new List<BlockItem>();
            _itemMap.Clear();

            // 不要每次刷新都把 ImageList 全清再全量重塞：
            // 上千条时那是最慢的一步。这里只补缺失的图，
            // 只有当图片数明显超过当前需要时才做一次清理，防止无限增长。
            try
            {
                if (_imageList.Images.Count > _currentBlocks.Count * 2 + 200)
                    _imageList.Images.Clear();
            }
            catch
            {
                // 清不掉就算了，不影响显示
            }

            // 本轮需要生成多少张缩略图（RequestThumbnails 现在只登记，不再同步解码 PNG）
            int requested = 0;

            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();

                foreach (BlockItem item in _currentBlocks)
                {
                    string key = item.Key;

                    if (!_imageList.Images.ContainsKey(key))
                        _imageList.Images.Add(key, CreateDisplayImage(item));

                    ListViewItem lvi = new ListViewItem(item.Name);
                    lvi.ImageKey = key;
                    lvi.Tag = item;
                    lvi.ToolTipText = item.FilePath;

                    lvi.SubItems.Add(FormatSize(item.FileSize));
                    lvi.SubItems.Add(FormatTime(item.ModifiedTime));
                    lvi.SubItems.Add(item.CategoryPath);

                    _list.Items.Add(lvi);
                    _itemMap[key] = lvi;
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
            if (IsDisposed) return;
            if (item == null || _list == null) return;

            _thumbDone++;

            try
            {
                ListViewItem lvi;
                if (!_itemMap.TryGetValue(item.Key, out lvi)) return;
                if (lvi.ListView == null) return;

                BeginInvoke(new Action(delegate
                {
                    try
                    {
                        Image img = CreateDisplayImage(item);
                        if (img == null) return;

                        string key = item.Key;
                        if (_imageList.Images.ContainsKey(key))
                            _imageList.Images.RemoveByKey(key);

                        _imageList.Images.Add(key, img);
                        lvi.ImageKey = key;
                        _list.Invalidate();
                    }
                    catch { }
                }));
            }
            catch
            {
                // 面板正在关闭时忽略
            }
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

            double rotation = ParseDouble(_txtRotation.Text, 0.0);
            if (double.IsNaN(rotation) || double.IsInfinity(rotation)) rotation = 0.0;
            options.RotationDegrees = rotation;

            // 炸开：勾选后插入时分解为基本图元（默认不勾选）
            options.ExplodeAfterInsert = _chkExplode != null && _chkExplode.Checked;
            options.UpdateExistingDefinition = _chkUpdateDef.Checked;

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

            // 命令已成功排队：先把面板最小化，把图形窗口让出来；
            // 插入完成后由 BLOCKLIBIN 回调 LibraryPalette.Restore() 还原窗口。
            try { LibraryPalette.Minimize(); } catch { }

            SetStatus(continuous ? "连续插入：依次指定插入点，按 Esc 结束。" : "请在图形窗口中指定插入点…");
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
        /// 插入操作结束后把最小化的面板自动还原（原大小）并带回前台。</summary>
        internal void ReportInsertResult(IList<BlockItem> items, string message, bool succeeded)
        {
            try { LibraryPalette.Restore(); } catch { }

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
