using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace BlockLibraryManager
{
    /// <summary>
    /// LibraryControl 的事件处理（partial 类的第三半）。
    /// </summary>
    public partial class LibraryControl
    {
        // ---------------- 分类树右键菜单 ----------------

        /// <summary>工具栏"显示 → 目录列表"开关：勾选显示、去勾隐藏左侧目录列表。</summary>
        private void OnShowTreeMenuClick(object sender, EventArgs e)
        {
            SetTreePanelVisible(_miShowTree != null && _miShowTree.Checked);
        }

        /// <summary>左侧标题栏上的"×"：收起整个目录列表。</summary>
        private void OnTreeHeaderCloseClick(object sender, EventArgs e)
        {
            SetTreePanelVisible(false);
        }

        /// <summary>插入参数面板右上角的 × 关闭按钮：收起参数面板。</summary>
        private void OnParamPanelCloseClick(object sender, EventArgs e)
        {
            SetParamPanelVisible(false);
        }

        /// <summary>工具栏"显示 → 插入参数面板"开关：勾选显示、去勾隐藏参数面板。</summary>
        private void OnShowParamsMenuClick(object sender, EventArgs e)
        {
            SetParamPanelVisible(_miShowParams != null && _miShowParams.Checked);
        }

        /// <summary>工具栏"语言"按钮：弹出语言选择对话框，确定后保存配置并立即刷新界面文字。</summary>
        private void OnLanguageClick(object sender, EventArgs e)
        {
            using (LanguageForm dialog = new LanguageForm(
                _config != null ? _config.Language : LanguageService.ConfiguredLanguage))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                string chosen = dialog.SelectedLanguage;

                if (_config != null)
                {
                    _config.Language = chosen;
                    _config.Save();
                }

                LanguageService.ConfiguredLanguage = chosen;
                ApplyLanguage();

                SetStatus(L.T("界面语言已更新。", "Interface language updated."));
            }
        }

        /// <summary>显示/收起左侧下方的插入参数面板，并同步"显示"下拉菜单的勾选状态。
        /// 收起时目录树自动占满整个左侧高度。</summary>
        private void SetParamPanelVisible(bool visible)
        {
            if (_leftSplit != null)
            {
                try { _leftSplit.Panel2Collapsed = !visible; }
                catch { return; }
            }

            if (_paramPanel != null) _paramPanel.Visible = visible;

            if (_miShowParams != null) _miShowParams.Checked = visible;
        }

        /// <summary>显示/收起左侧目录列表，并同步"显示"下拉菜单的勾选状态。</summary>
        private void SetTreePanelVisible(bool visible)
        {
            if (_split == null) return;

            try { _split.Panel1Collapsed = !visible; }
            catch { return; }

            if (_miShowTree != null) _miShowTree.Checked = visible;

            SetStatus(visible
                ? "已显示目录列表。"
                : "已关闭目录列表；可点工具栏「显示 → 目录列表」重新打开。");
        }

        /// <summary>TreeView 右键不会自动选中节点，这里只记录目标节点，不改变当前选中项。</summary>
        private void OnTreeNodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
                _menuTargetNode = e.Node;
        }

        /// <summary>右键按下时按坐标定位节点（比 NodeMouseClick 更靠前、更可靠）。</summary>
        private void OnTreeMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;

            try
            {
                TreeNode hit = _tree.GetNodeAt(e.X, e.Y);
                if (hit != null) _menuTargetNode = hit;
            }
            catch
            {
                // 取不到节点就沿用上一次的记录
            }
        }

        /// <summary>找到右键节点所属的一级图库目录（即配置里登记的根目录）。</summary>
        private static CategoryNode FindOwningRoot(CategoryNode category)
        {
            CategoryNode node = category;
            if (node == null) return null;

            while (node.Parent != null) node = node.Parent;
            return node;
        }

        private void OnTreeMenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_menuTargetNode == null && _tree != null)
                _menuTargetNode = _tree.SelectedNode;

            TreeNode target = _menuTargetNode;

            // ---- "此电脑"分支：打开文件夹位置 + 添加到图库 ----
            PcTag pc = target != null ? target.Tag as PcTag : null;
            if (pc != null)
            {
                if (string.IsNullOrEmpty(pc.Path))
                {
                    e.Cancel = true;               // "此电脑"根节点不弹菜单
                    return;
                }

                bool exists = Directory.Exists(pc.Path);

                _miTreeFavorite.Visible = false;
                _miTreeRemove.Visible = false;
                _miTreeAddLib.Visible = true;
                _miTreeOpenFolder.Visible = true;
                _miTreeOpenFolder.Enabled = exists;
                _miTreeAddLib.Enabled = exists;
                return;
            }

            _miTreeAddLib.Visible = false;

            // ---- 收藏节点：只允许"从收藏夹移除"和"打开文件夹位置" ----
            FavoriteTag fav = target != null ? target.Tag as FavoriteTag : null;
            if (fav != null)
            {
                if (string.IsNullOrEmpty(fav.Path))
                {
                    e.Cancel = true;               // "收藏"根节点不弹菜单
                    return;
                }

                _miTreeFavorite.Visible = true;
                _miTreeFavorite.Text = "从收藏夹移除";
                _miTreeOpenFolder.Visible = true;
                _miTreeOpenFolder.Enabled = Directory.Exists(fav.Path);
                _miTreeRemove.Visible = false;
                return;
            }

            _miTreeRemove.Visible = true;

            CategoryNode category = target != null ? target.Tag as CategoryNode : null;

            // "全部图库" / "最近使用" 上不弹菜单
            if (category == null || string.IsNullOrEmpty(category.FullPath))
            {
                e.Cancel = true;
                return;
            }

            // ---- 普通图库目录：添加 / 移除收藏 + 原有菜单项 ----
            _miTreeFavorite.Visible = true;
            _miTreeFavorite.Text = _config.IsFavorite(category.FullPath)
                ? "从收藏夹移除"
                : "添加到收藏夹";

            CategoryNode root = FindOwningRoot(category);
            bool isRoot = root == null || ReferenceEquals(root, category);

            // 根目录：整个从图库列表移除；子目录：只隐藏它自己，根目录保留
            _miTreeRemove.Enabled = true;
            _miTreeRemove.Text = isRoot
                ? "从图库中移除该文件夹"
                : "隐藏该子目录（根目录保留）";

            _miTreeOpenFolder.Visible = true;
            _miTreeOpenFolder.Enabled = !category.Missing;
        }

        /// <summary>目录树展开前："此电脑"分支的目录此时才真正枚举子目录。</summary>
        private void OnTreeBeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            if (e.Node == null) return;
            if (!(e.Node.Tag is PcTag)) return;

            EnsurePcLoaded(e.Node);
        }

        /// <summary>
        /// 目录树右键菜单 → 添加到图库：
        /// 把"此电脑"分支中选中的文件夹加入图库根目录列表。
        /// </summary>
        private void OnTreeAddToLibraryClick(object sender, EventArgs e)
        {
            if (_config == null) return;
            if (_menuTargetNode == null && _tree != null)
                _menuTargetNode = _tree.SelectedNode;

            PcTag pc = _menuTargetNode != null ? _menuTargetNode.Tag as PcTag : null;
            if (pc == null || string.IsNullOrEmpty(pc.Path)) return;

            if (!Directory.Exists(pc.Path))
            {
                SetStatus("文件夹不存在，无法添加到图库。");
                return;
            }

            _menuTargetNode = null;
            AddRootToLibrary(pc.Path);
        }

        /// <summary>
        /// 目录树右键菜单 → 收藏切换：
        /// 普通图库目录 = 添加 / 移除收藏；收藏节点 = 从收藏夹移除。
        /// </summary>
        private void OnTreeFavoriteClick(object sender, EventArgs e)
        {
            if (_config == null) return;
            if (_menuTargetNode == null && _tree != null)
                _menuTargetNode = _tree.SelectedNode;
            if (_menuTargetNode == null) return;

            string path = null;

            FavoriteTag fav = _menuTargetNode.Tag as FavoriteTag;
            if (fav != null && !string.IsNullOrEmpty(fav.Path))
            {
                path = fav.Path;                            // 收藏节点 → 移除
            }
            else
            {
                CategoryNode category = _menuTargetNode.Tag as CategoryNode;
                if (category != null && !string.IsNullOrEmpty(category.FullPath))
                    path = category.FullPath;               // 普通目录 → 按当前状态切换
            }

            if (string.IsNullOrEmpty(path)) return;

            string message;
            if (_config.IsFavorite(path))
            {
                _config.RemoveFavorite(path);
                message = "已从收藏夹移除：" + path;
            }
            else
            {
                _config.AddFavorite(path);
                message = "已添加到收藏夹：" + path;
            }

            _config.Save();
            _menuTargetNode = null;

            // 只重建树（收藏不影响图块数据，无需重新扫描），并尽量恢复原选中位置
            string keepPath = GetSelectedCategoryPath();
            BuildTree();
            if (!string.IsNullOrEmpty(keepPath)) SelectNodeByPath(keepPath);
            RefreshCurrentView();

            SetStatus(message);
        }

        private void OnTreeOpenFolderClick(object sender, EventArgs e)
        {
            if (_menuTargetNode == null && _tree != null)
                _menuTargetNode = _tree.SelectedNode;

            // 收藏节点 / 此电脑分支 / 普通图库目录 都支持"打开文件夹位置"
            string path = null;

            FavoriteTag fav = _menuTargetNode != null ? _menuTargetNode.Tag as FavoriteTag : null;
            if (fav != null) path = fav.Path;

            if (string.IsNullOrEmpty(path))
            {
                PcTag pc = _menuTargetNode != null ? _menuTargetNode.Tag as PcTag : null;
                if (pc != null) path = pc.Path;
            }

            if (string.IsNullOrEmpty(path))
            {
                CategoryNode category = _menuTargetNode != null ? _menuTargetNode.Tag as CategoryNode : null;
                if (category != null) path = category.FullPath;
            }

            if (string.IsNullOrEmpty(path)) return;

            if (!Directory.Exists(path))
            {
                SetStatus("文件夹不存在（可能已被移动或删除），请刷新。");
                return;
            }

            try
            {
                Process.Start("explorer.exe", path);
            }
            catch (System.Exception ex)
            {
                SetStatus("打开文件夹失败：" + ex.Message);
            }
        }

        private void OnTreeRemoveClick(object sender, EventArgs e)
        {
            if (_config == null) return;

            if (_menuTargetNode == null && _tree != null)
                _menuTargetNode = _tree.SelectedNode;

            CategoryNode category = _menuTargetNode != null ? _menuTargetNode.Tag as CategoryNode : null;
            if (category == null || string.IsNullOrEmpty(category.FullPath))
            {
                SetStatus("请在该文件夹的分类节点上右键，再选择移除。");
                return;
            }

            CategoryNode root = FindOwningRoot(category);
            if (root == null || string.IsNullOrEmpty(root.FullPath)) return;

            // ---- 子目录：只隐藏它自己，绝不移动根目录 ----
            if (!ReferenceEquals(root, category))
            {
                HideSubDirectory(category);
                return;
            }

            // ---- 一级根目录：整个从图库列表移除 ----
            string target = root.FullPath;

            DialogResult answer = MessageBox.Show(this,
                "从图库列表中移除这个文件夹？\n\n    " + target +
                "\n\n只移除图库列表里的显示，不会删除磁盘上的任何文件\n（以后可以用“添加”重新加回来）。",
                "从图库中移除", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (answer != DialogResult.Yes) return;

            int removed = RemoveRootFromConfig(target);
            _config.RemoveExcludedUnder(target);   // 根目录没了，它下面的隐藏记录一并清掉
            _config.Save();

            // 清掉指向被移除目录的位置记忆，避免刷新后又定位回去
            if (!string.IsNullOrEmpty(_config.LastCategoryPath) &&
                (string.Equals(_config.LastCategoryPath, target, StringComparison.OrdinalIgnoreCase) ||
                 IsPathUnder(target, _config.LastCategoryPath)))
            {
                _config.LastCategoryPath = string.Empty;
            }

            _menuTargetNode = null;
            ReloadWithProgress(null);

            if (_tree != null && _tree.Nodes.Count > 0)
            {
                try
                {
                    _tree.Nodes[0].Expand();
                    _tree.SelectedNode = _tree.Nodes[0];
                }
                catch { }
                RefreshCurrentView();
            }

            SetStatus(removed > 0
                ? "已从图库列表中移除：" + target + "（磁盘文件未删除）"
                : "已刷新，但配置里没找到该目录条目：" + target + "（可用“设置”检查图库根目录）");
        }

        /// <summary>
        /// 隐藏一个子目录：只把它加入隐藏列表，所属根目录和其它子目录全部保留。
        /// 磁盘文件不动；之后可在"设置 → 隐藏的子目录"里恢复显示。
        /// </summary>
        private void HideSubDirectory(CategoryNode category)
        {
            string path = category.FullPath;
            CategoryNode root = FindOwningRoot(category);

            DialogResult answer = MessageBox.Show(this,
                "把子目录\n\n    " + path +
                "\n\n从图库中隐藏？\n\n· 只隐藏这一个子目录，根目录「" + (root != null ? root.Name : "?") +
                "」及其它子目录都保留\n· 不删除磁盘上的任何文件\n· 以后可在「设置 → 隐藏的子目录」里恢复显示",
                "隐藏子目录", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (answer != DialogResult.Yes) return;

            if (!_config.AddExcluded(path))
            {
                SetStatus("该子目录已在隐藏列表中。");
                return;
            }

            _config.Save();

            // 位置记忆指向被隐藏目录时清掉，避免刷新后又定位回去
            if (!string.IsNullOrEmpty(_config.LastCategoryPath) &&
                (string.Equals(_config.LastCategoryPath, path, StringComparison.OrdinalIgnoreCase) ||
                 IsPathUnder(path, _config.LastCategoryPath)))
            {
                _config.LastCategoryPath = string.Empty;
            }

            _menuTargetNode = null;
            ReloadWithProgress(null);

            if (_tree != null && _tree.Nodes.Count > 0)
            {
                try
                {
                    _tree.Nodes[0].Expand();
                    _tree.SelectedNode = _tree.Nodes[0];
                }
                catch { }
                RefreshCurrentView();
            }

            SetStatus("已隐藏子目录：" + path + "（根目录保留；可在“设置”里恢复显示）");
        }

        /// <summary>
        /// 从配置里移除图库根目录。
        /// 直接比 + 规范化后再比一次（盘符大小写、结尾分隔符、相对路径写法都能兜住），
        /// 返回实际移除的条数。
        /// </summary>
        private int RemoveRootFromConfig(string path)
        {
            if (string.IsNullOrEmpty(path)) return 0;

            string normalized = NormalizePath(path);
            int count = 0;

            for (int i = _config.RootPaths.Count - 1; i >= 0; i--)
            {
                string existing = _config.RootPaths[i];
                if (string.IsNullOrEmpty(existing)) continue;

                bool hit = string.Equals(existing, path, StringComparison.OrdinalIgnoreCase);
                if (!hit) hit = string.Equals(NormalizePath(existing), normalized, StringComparison.OrdinalIgnoreCase);

                if (hit)
                {
                    _config.RootPaths.RemoveAt(i);
                    count++;
                }
            }

            // 兜底：Config.RemoveRoot 也走一遍（存在旧写法残留时有用）
            try { _config.RemoveRoot(path); } catch { }

            return count;
        }

        /// <summary>把路径规范成可比较的形式（去结尾分隔符 + 转小写）。</summary>
        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;

            string full;
            try { full = new DirectoryInfo(path).FullName; }
            catch { full = path; }

            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        }

        private void OnRefreshClick(object sender, EventArgs e)
        {
            // 走后台扫描 + 进度条，避免大图库刷新时界面假死
            ReloadWithProgress(GetSelectedCategoryPath());
        }

        // ---------------- 添加图库目录 ----------------

        /// <summary>
        /// 工具栏"添加"：打开文件树浏览对话框，选一个目录加入图库根目录列表。
        /// 加入后该目录会成为分类树的一级节点，其子文件夹自动成为下级分类。
        /// </summary>
        private void OnAddRootClick(object sender, EventArgs e)
        {
            if (_config == null) return;

            string initial = GetStartupFolderForPicker();

            string chosen;
            using (FolderPickerForm dialog = new FolderPickerForm(initial))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                chosen = dialog.SelectedPath;
            }

            if (string.IsNullOrEmpty(chosen)) return;

            AddRootToLibrary(chosen);
        }

        /// <summary>
        /// 把一个磁盘目录加入图库根目录列表（含重复 / 包含关系检查与确认）。
        /// "添加"按钮与"此电脑"文件树的右键菜单共用。
        /// </summary>
        private void AddRootToLibrary(string chosen)
        {
            if (_config == null || string.IsNullOrEmpty(chosen)) return;

            // ---- 重复 / 包含关系检查 ----
            string existingSame;
            string ancestor;
            List<string> descendants = new List<string>();

            ClassifyAgainstRoots(chosen, out existingSame, out ancestor, descendants);

            if (existingSame != null)
            {
                SetStatus("该文件夹已在图库中，无需重复添加：" + existingSame);
                SelectNodeByPath(existingSame);
                return;
            }

            if (ancestor != null)
            {
                MessageBox.Show(this,
                    "选择的文件夹已经在图库目录\n\n    " + ancestor + "\n\n的范围之内了，\n会被自动扫描到，不需要单独添加。",
                    "图库管理器", MessageBoxButtons.OK, MessageBoxIcon.Information);
                SelectNodeByPath(ancestor);
                return;
            }

            if (descendants.Count > 0)
            {
                string question = "选择的文件夹\n\n    " + chosen + "\n\n已经包含了现有的图库目录：\n\n";
                foreach (string desc in descendants) question += "    " + desc + "\n";
                question += "\n继续添加会导致这些目录被重复统计。是否移除这些旧目录，改用上层目录？";

                DialogResult answer = MessageBox.Show(this, question, "图库管理器",
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

                if (answer == DialogResult.Cancel) { SetStatus("已取消添加。"); return; }

                if (answer == DialogResult.Yes)
                {
                    foreach (string desc in descendants) _config.RemoveRoot(desc);
                }
                // No = 保留旧目录，照样添加（用户自己承担重复统计）
            }

            // 驱动器根目录会被整盘扫描，给个提示
            if (IsDriveRoot(chosen))
            {
                DialogResult answer = MessageBox.Show(this,
                    "你选择的是驱动器根目录\n\n    " + chosen + "\n\n加入后会递归扫描整个磁盘的全部 DWG/DWT 文件，\n首次打开可能非常慢。确定要添加吗？",
                    "图库管理器", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer != DialogResult.Yes) { SetStatus("已取消添加。"); return; }
            }

            _config.AddRoot(chosen);
            _config.Save();

            // 加进来之后要整盘扫描这个目录，用进度条把过程显示出来
            ReloadWithProgress(chosen);
        }

        /// <summary>对话框的起始目录：优先用当前选中的分类，退回第一个图库根目录，最后是我的文档。</summary>
        private string GetStartupFolderForPicker()
        {
            CategoryNode category = GetSelectedCategory();
            if (category != null && !string.IsNullOrEmpty(category.FullPath))
            {
                if (Directory.Exists(category.FullPath)) return category.FullPath;
            }

            if (_config.RootPaths.Count > 0)
            {
                string last = _config.RootPaths[_config.RootPaths.Count - 1];
                if (!string.IsNullOrEmpty(last) && Directory.Exists(last)) return last;
            }

            try
            {
                string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (!string.IsNullOrEmpty(docs) && Directory.Exists(docs)) return docs;
            }
            catch { }

            return null;
        }

        /// <summary>
        /// 判断待添加目录与现有根目录的关系：完全相同 / 被某个现有根目录包含 / 包含了某些现有根目录。
        /// </summary>
        private void ClassifyAgainstRoots(string candidate,
            out string same, out string ancestor, List<string> descendants)
        {
            same = null;
            ancestor = null;

            foreach (string root in _config.RootPaths)
            {
                if (string.IsNullOrEmpty(root)) continue;

                if (string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    same = root;
                    return;
                }

                if (IsPathUnder(root, candidate)) ancestor = root;
                else if (IsPathUnder(candidate, root)) descendants.Add(root);
            }
        }

        /// <summary>child 是否位于 parent 之下（或就是 parent）。</summary>
        private static bool IsPathUnder(string parent, string child)
        {
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(child)) return false;
            if (string.Equals(parent, child, StringComparison.OrdinalIgnoreCase)) return true;
            if (!child.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) return false;

            if (child.Length <= parent.Length) return false;

            char next = child[parent.Length];
            return next == Path.DirectorySeparatorChar || next == Path.AltDirectorySeparatorChar;
        }

        private static bool IsDriveRoot(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                string root = Path.GetPathRoot(path);
                return !string.IsNullOrEmpty(root) &&
                       string.Equals(root, path, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private void OnSettingsClick(object sender, EventArgs e)
        {
            if (_config == null) return;

            using (SettingsForm form = new SettingsForm(_config))
            {
                if (form.ShowDialog(this) != DialogResult.OK) return;
            }

            _config.Save();
            ApplyConfigToUi();
            ReloadWithProgress(GetSelectedCategoryPath());
        }

        private void OnViewModeClick(object sender, EventArgs e)
        {
            ToolStripMenuItem item = sender as ToolStripMenuItem;
            if (item == null) return;
            if (!(item.Tag is GalleryView)) return;

            GalleryView mode = (GalleryView)item.Tag;
            _config.ViewMode = mode;

            switch (mode)
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

            _config.Save();
            RefreshCurrentView();
        }

        private void OnSearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                ApplySearch();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                _txtSearch.Text = string.Empty;
                ApplySearch();
            }
        }

        private void ApplySearch()
        {
            _searchKeyword = _txtSearch.Text == null ? string.Empty : _txtSearch.Text.Trim();
            RefreshCurrentView();
        }

        // ---------------- 粘贴入库 ----------------

        /// <summary>
        /// 只有选中了具体的文件夹分类时"粘贴入库"才可用；
        /// "全部图库" / "最近使用"没有对应的磁盘目录，置灰。
        /// </summary>
        private void OnListMenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_miListPaste == null) return;

            CategoryNode category = GetSelectedCategory();
            _miListPaste.Enabled = category != null &&
                                   !string.IsNullOrEmpty(category.FullPath) &&
                                   !category.Missing;

            // 底色切换项：当前模式打勾
            bool white = IsWhiteThumbBackground;
            if (_miThumbWhite != null) _miThumbWhite.Checked = white;
            if (_miThumbBlack != null) _miThumbBlack.Checked = !white;
        }

        /// <summary>当前缩略图底色是否为白底（配置持久化，"black" 之外一律视为白底）。</summary>
        private bool IsWhiteThumbBackground
        {
            get { return _config == null || _config.ThumbBackground != "black"; }
        }

        /// <summary>右键菜单 → 白底缩略图：切换底色并重新生成当前所有缩略图。</summary>
        private void OnThumbWhiteClick(object sender, EventArgs e)
        {
            ApplyThumbBackground(true);
        }

        /// <summary>右键菜单 → 黑底缩略图：切换底色并重新生成当前所有缩略图。</summary>
        private void OnThumbBlackClick(object sender, EventArgs e)
        {
            ApplyThumbBackground(false);
        }

        /// <summary>应用新的缩略图底色：保存配置、清空缓存、刷新当前目录强制重新生成。</summary>
        private void ApplyThumbBackground(bool white)
        {
            if (IsWhiteThumbBackground == white) return;   // 与当前一致，无需处理

            if (_config != null)
            {
                _config.ThumbBackground = white ? "white" : "black";
                _config.Save();
            }

            if (_thumbnails != null)
            {
                _thumbnails.WhiteBackground = white;
                _thumbnails.ClearAllCaches();
            }

            SetStatus(white
                ? "已切换为白底缩略图，正在重新生成…"
                : "已切换为黑底缩略图，正在重新生成…");

            ReloadWithProgress(GetSelectedCategoryPath());
        }

        /// <summary>
        /// 右键菜单 → 粘贴入库：把 CAD 剪贴板里的图块保存成 DWG（2007 格式）
        /// 放进当前选中的分类目录，保存后面板自动刷新显示新图块。
        /// </summary>
        private void OnPasteIntoLibraryClick(object sender, EventArgs e)
        {
            CategoryNode category = GetSelectedCategory();
            if (category == null || string.IsNullOrEmpty(category.FullPath) || category.Missing)
            {
                SetStatus("请先在左侧目录列表中选择一个具体的文件夹，再执行粘贴入库。");
                return;
            }

            // 1. 暂存目标目录  2. 命令上下文中执行 BLOCKLIBPASTE
            PasteBridge.Request(category.FullPath);

            string message;
            if (!TrySendCommand("BLOCKLIBPASTE", out message))
            {
                PasteBridge.Clear();
                SetStatus(message);
                return;
            }

            SetStatus("正在把剪贴板中的图块保存到：" + category.FullPath + " …");
        }

        // ---------------- 批量入库 ----------------

        /// <summary>
        /// 工具栏「批量入库」：
        ///   1. 弹出系统文件树对话框，用户新建或选择目标文件夹；
        ///   2. 确定后面板自动最小化；
        ///   3. 触发 BLOCKLIBBATCH 命令，提示用户框选图块，
        ///      按块名逐个保存为 2007 格式 DWG 到所选目录。
        /// </summary>
        private void OnBatchImportClick(object sender, EventArgs e)
        {
            if (_config == null) return;

            // 初始目录：优先用当前选中的分类目录
            string initial = GetStartupFolderForPicker();
            CategoryNode category = GetSelectedCategory();
            if (category != null && !string.IsNullOrEmpty(category.FullPath) &&
                !category.Missing && System.IO.Directory.Exists(category.FullPath))
            {
                initial = category.FullPath;
            }

            string chosen;
            using (FolderPickerForm dialog = new FolderPickerForm(initial))
            {
                dialog.Text = "批量入库 - 选择保存图块的文件夹";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                chosen = dialog.SelectedPath;
            }

            if (string.IsNullOrEmpty(chosen)) return;
            if (!System.IO.Directory.Exists(chosen))
            {
                SetStatus("所选文件夹不存在：" + chosen);
                return;
            }

            // 1. 暂存目标目录  2. 面板最小化  3. 命令上下文中执行框选与保存
            BatchBridge.Request(chosen);
            LibraryPalette.Minimize();

            string message;
            if (!TrySendCommand("BLOCKLIBBATCH", out message))
            {
                BatchBridge.Clear();
                LibraryPalette.Restore();
                SetStatus(message);
                return;
            }

            SetStatus("已最小化图库窗口：请在 AutoCAD 中框选要入库的图块，回车结束选择。");
        }

        private void OnTreeAfterSelect(object sender, TreeViewEventArgs e)
        {
            if (_config == null || _index == null) return;
            if (_tree.SelectedNode == null) return;

            // ---- 选中"此电脑"分支节点：右侧列表改为显示该文件夹顶层的图块文件 ----
            PcTag pc = _tree.SelectedNode.Tag as PcTag;
            if (pc != null)
            {
                _pcSelectedPath = pc.Path != null ? pc.Path : string.Empty;

                SetStatus(string.IsNullOrEmpty(_pcSelectedPath)
                    ? "请在“此电脑”下选择要浏览的文件夹。"
                    : "正在浏览：" + _pcSelectedPath);

                RefreshCurrentView();
                return;
            }

            // ---- 选中收藏节点 / 收藏根：离开电脑文件夹浏览，保持上次图库位置记忆 ----
            if (_tree.SelectedNode.Tag is FavoriteTag)
            {
                _pcSelectedPath = string.Empty;
                RefreshCurrentView();
                return;
            }

            _pcSelectedPath = string.Empty;

            CategoryNode category = GetSelectedCategory();
            _config.LastCategoryPath = category != null ? category.FullPath : string.Empty;
            _config.Save();

            RefreshCurrentView();
        }

        private void OnListSelectionChanged(object sender, EventArgs e)
        {
            BlockItem item = GetSingleSelectedBlock();
            if (item == null) return;

            string text = item.Name;
            if (!string.IsNullOrEmpty(item.CategoryPath))
                text += "  —  " + item.CategoryPath;

            SetStatus(text);
        }

        private void OnListDoubleClick(object sender, EventArgs e)
        {
            DoInsert(GetSelectedBlocks());
        }

        private void OnInsertSelectedClick(object sender, EventArgs e)
        {
            DoInsert(GetSelectedBlocks());
        }

        /// <summary>连续插入：插入成功后重复提示定位，按 Esc 结束。</summary>
        private void OnInsertMultipleClick(object sender, EventArgs e)
        {
            BlockItem item = GetSingleSelectedBlock();
            if (item == null)
            {
                SetStatus("请先选择一个图块，再使用“插入多个（连续）”。");
                return;
            }

            List<BlockItem> one = new List<BlockItem>();
            one.Add(item);

            InsertOptions options = BuildInsertOptions();
            SendInsertCommand(one, options, true);
        }

        private void OnOpenFolderClick(object sender, EventArgs e)
        {
            string folder = ResolveTargetFolderForDrop();

            BlockItem item = GetSingleSelectedBlock();
            if (item != null && File.Exists(item.FilePath))
                folder = Path.GetDirectoryName(item.FilePath);

            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                SetStatus("目标文件夹不存在。");
                return;
            }

            try
            {
                Process.Start("explorer.exe", folder);
            }
            catch (System.Exception ex)
            {
                SetStatus("打开文件夹失败：" + ex.Message);
            }
        }

        private void OnCopyNameClick(object sender, EventArgs e)
        {
            BlockItem item = GetSingleSelectedBlock();
            if (item == null) return;

            try
            {
                Clipboard.SetText(item.Name);
                SetStatus("已复制块名：" + item.Name);
            }
            catch (System.Exception ex)
            {
                SetStatus("复制失败：" + ex.Message);
            }
        }

        // ---------------- 拖放：从资源管理器把 DWG 拖进来入库 ----------------

        /// <summary>
        /// 点击缩略图拖拽：把该图块的 DWG 以 FileDrop 拖出，
        /// 拖到 AutoCAD 图形窗口里松手即插入为图块（AutoCAD 原生支持拖入 DWG，
        /// 会在光标处提示插入点）；拖到面板目录树 / 列表则走入库逻辑。
        /// 拖出时面板自动最小化并把图形单位临时置"无单位"（保证按 1:1 插入），
        /// 插入命令结束后自动还原面板与单位；取消拖拽则立即还原。
        /// </summary>
        private void OnListItemDrag(object sender, ItemDragEventArgs e)
        {
            BlockItem item = (e.Item as ListViewItem) == null ? null : (e.Item as ListViewItem).Tag as BlockItem;
            if (item == null) return;
            if (!File.Exists(item.FilePath)) return;

            // 拖出前：面板最小化 + 图形单位临时置无单位（1:1 插入）
            try { LibraryPalette.BeginDragInsert(); } catch { }

            bool dropped = false;
            try
            {
                DataObject data = new DataObject();
                System.Collections.Specialized.StringCollection files =
                    new System.Collections.Specialized.StringCollection();
                files.Add(item.FilePath);
                data.SetFileDropList(files);

                dropped = DoDragDrop(data,
                    DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link)
                    != DragDropEffects.None;
            }
            catch
            {
                // 拖出到外部失败不影响其他功能
            }

            try { LibraryPalette.EndDragInsert(dropped); } catch { }
        }

        private void OnListDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data == null) return;
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effect = DragDropEffects.Copy;
            else
                e.Effect = DragDropEffects.None;
        }

        private void OnListDragDrop(object sender, DragEventArgs e)
        {
            string folder = ResolveTargetFolderForDrop();
            if (string.IsNullOrEmpty(folder))
            {
                SetStatus("请先选择要存入的分类目录。");
                return;
            }

            if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop))
                return;

            string[] dropped = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (dropped == null || dropped.Length == 0) return;

            int added = 0;
            int skipped = 0;

            foreach (string file in dropped)
            {
                if (string.IsNullOrEmpty(file)) continue;
                if (!IsLibraryFile(file)) { skipped++; continue; }

                try
                {
                    string target = Path.Combine(folder, Path.GetFileName(file));

                    if (string.Equals(target, file, StringComparison.OrdinalIgnoreCase))
                    {
                        skipped++;
                        continue;
                    }

                    File.Copy(file, target, true);
                    added++;
                }
                catch
                {
                    skipped++;
                }
            }

            if (added > 0) ReloadWithProgress(GetSelectedCategoryPath());

            SetStatus("入库完成：新增 " + added + " 个，跳过 " + skipped + " 个。");
        }

        /// <summary>确定拖放目标目录：优先当前分类，退回第一个图库根目录。</summary>
        private string ResolveTargetFolderForDrop()
        {
            CategoryNode category = GetSelectedCategory();
            if (category != null && !string.IsNullOrEmpty(category.FullPath))
            {
                if (!Directory.Exists(category.FullPath))
                {
                    try { Directory.CreateDirectory(category.FullPath); }
                    catch { return null; }
                }
                return category.FullPath;
            }

            if (_config != null && _config.RootPaths.Count > 0)
            {
                string root = _config.RootPaths[0];
                if (!Directory.Exists(root))
                {
                    try { Directory.CreateDirectory(root); }
                    catch { return null; }
                }
                return root;
            }

            return null;
        }

        private bool IsLibraryFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            string name = Path.GetFileName(path);
            foreach (string filter in _config.FileFilters)
            {
                if (string.IsNullOrEmpty(filter)) continue;

                string pattern = filter.StartsWith("*.", StringComparison.Ordinal) ? filter.Substring(1) : filter;
                if (name.EndsWith(pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
