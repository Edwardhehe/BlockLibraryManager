using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BlockLibraryManager
{
    /// <summary>
    /// 文件夹选择对话框（自绘系统文件树）。
    ///
    /// 为什么不用系统自带的 FolderBrowserDialog：
    ///   1. net35（AutoCAD 2010/2011）上的 FolderBrowserDialog 是老式控件，
    ///      窗口不能拉伸、没有地址栏、不能粘贴路径，体验很差；
    ///   2. 需要"新建文件夹""显示隐藏文件夹""从任意驱动器/UNC 路径浏览"这些能力。
    ///
    /// 实现要点：
    ///   - 惰性加载：只有节点真正展开时才枚举子目录，打开瞬间不卡；
    ///   - 全程 try/catch：遍历到无权访问的目录不会抛异常，只是不显示子节点；
    ///   - 图标走 shell32!SHGetFileInfo（拿真正的系统图标），失败时退回自绘，绝不崩。
    /// </summary>
    internal sealed class FolderPickerForm : Form
    {
        private const string DummyText = "载入中…";

        private const string IconFolder = "folder";
        private const string IconDesktop = "desktop";
        private const string IconComputer = "computer";
        private const string IconDocuments = "documents";

        private readonly string _initialPath;

        private TreeView _tree;
        private ImageList _icons;
        private string _currentPath;
        private Button _btnNewFolder;
        private CheckBox _chkHidden;
        private Button _btnOk;
        private Button _btnCancel;
        private Label _lblHint;

        /// <summary>用户最终选择的目录（点"确定"后才有效）。</summary>
        public string SelectedPath { get; private set; }

        public FolderPickerForm(string initialPath)
        {
            _initialPath = initialPath;
            SelectedPath = string.Empty;

            InitializeUi();
            BuildIcons();
            BuildRoots();

            if (!IsEmpty(initialPath) && Directory.Exists(initialPath))
            {
                _currentPath = Normalize(initialPath);
                TrySelectPath(initialPath);
            }

            UpdateOkState();
        }

        // ---------------- 界面 ----------------

        private void InitializeUi()
        {
            SuspendLayout();

            Text = "添加图库目录";
            Font = SystemFonts.MessageBoxFont;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = true;
            Size = new Size(640, 720);
            MinimumSize = new Size(440, 500);
            ShowInTaskbar = false;

            Label lblTitle = new Label();
            lblTitle.Text = "浏览并选择要加入图库的文件夹：";
            lblTitle.AutoSize = true;
            lblTitle.Top = 12;
            lblTitle.Left = 12;

            _tree = new TreeView();
            _tree.HideSelection = false;
            _tree.ShowLines = true;
            _tree.ShowPlusMinus = true;
            _tree.ShowRootLines = true;
            _tree.HotTracking = true;
            _tree.LabelEdit = true;
            _tree.PathSeparator = Path.DirectorySeparatorChar.ToString();
            _tree.BeforeExpand += new TreeViewCancelEventHandler(OnBeforeExpand);
            _tree.AfterSelect += new TreeViewEventHandler(OnAfterSelect);
            _tree.AfterLabelEdit += new NodeLabelEditEventHandler(OnAfterLabelEdit);

            _icons = new ImageList();
            _icons.ColorDepth = ColorDepth.Depth32Bit;
            _icons.ImageSize = new Size(16, 16);
            _tree.ImageList = _icons;

            Panel treePanel = new Panel();
            treePanel.BorderStyle = BorderStyle.Fixed3D;
            treePanel.Padding = new Padding(1);
            treePanel.Controls.Add(_tree);
            _tree.Dock = DockStyle.Fill;

            _btnNewFolder = new Button();
            _btnNewFolder.Text = "新建文件夹";
            _btnNewFolder.AutoSize = true;
            _btnNewFolder.MinimumSize = new Size(96, 26);
            _btnNewFolder.Click += new EventHandler(OnNewFolderClick);

            _chkHidden = new CheckBox();
            _chkHidden.Text = "显示隐藏文件夹";
            _chkHidden.AutoSize = true;
            _chkHidden.CheckedChanged += new EventHandler(OnHiddenChanged);

            _btnOk = new Button();
            _btnOk.Text = "确定";
            _btnOk.Size = new Size(88, 28);
            _btnOk.DialogResult = DialogResult.OK;
            _btnOk.Click += new EventHandler(OnOkClick);

            _btnCancel = new Button();
            _btnCancel.Text = "取消";
            _btnCancel.Size = new Size(88, 28);
            _btnCancel.DialogResult = DialogResult.Cancel;

            _lblHint = new Label();
            _lblHint.AutoSize = false;
            _lblHint.AutoEllipsis = true;
            _lblHint.ForeColor = Color.DimGray;
            _lblHint.TextAlign = ContentAlignment.MiddleLeft;
            _lblHint.Text = "提示：双击节点展开；选中文件夹后点“确定”即可入库。";

            // ---- 底部第 2 行（贴边）：确定 / 取消，右对齐 ----
            Panel buttonPanel = new Panel();
            buttonPanel.Dock = DockStyle.Bottom;
            buttonPanel.Height = 44;
            buttonPanel.Padding = new Padding(12, 8, 12, 8);
            buttonPanel.Controls.Add(_btnOk);
            buttonPanel.Controls.Add(_btnCancel);
            buttonPanel.Resize += new EventHandler(delegate (object s, EventArgs e) { LayoutButtonRow(buttonPanel); });

            // ---- 底部第 1 行：新建文件夹 / 显示隐藏 ----
            Panel toolPanel = new Panel();
            toolPanel.Dock = DockStyle.Bottom;
            toolPanel.Height = 40;
            toolPanel.Padding = new Padding(12, 6, 12, 6);
            toolPanel.Controls.Add(_chkHidden);
            toolPanel.Controls.Add(_btnNewFolder);
            toolPanel.Resize += new EventHandler(delegate (object s, EventArgs e) { LayoutToolRow(toolPanel); });

            // ---- 底部第 2 行：提示文字（单独一行，位于新建文件夹下方） ----
            Panel hintPanel = new Panel();
            hintPanel.Dock = DockStyle.Bottom;
            hintPanel.Height = 28;
            hintPanel.Padding = new Padding(12, 2, 12, 2);
            hintPanel.Controls.Add(_lblHint);
            hintPanel.Resize += new EventHandler(delegate (object s, EventArgs e) { LayoutHintRow(hintPanel); });

            // ---- 顶部：标题 ----
            Panel pathPanel = new Panel();
            pathPanel.Dock = DockStyle.Top;
            pathPanel.Height = 34;
            pathPanel.Padding = new Padding(12, 0, 12, 0);
            lblTitle.Location = new Point(12, 8);
            lblTitle.AutoSize = true;
            pathPanel.Controls.Add(lblTitle);

            AcceptButton = _btnOk;
            CancelButton = _btnCancel;

            // 添加顺序即 Dock 布局顺序：Fill 的必须最先加；
            // Bottom 的几个面板，后加的贴边（buttonPanel 在最下面）
            Controls.Add(treePanel);
            treePanel.Dock = DockStyle.Fill;
            Controls.Add(pathPanel);
            Controls.Add(hintPanel);
            Controls.Add(toolPanel);
            Controls.Add(buttonPanel);

            ResumeLayout(false);
            PerformLayout();

            // 首次定位（Resize 事件只在尺寸变化时才触发）
            LayoutToolRow(toolPanel);
            LayoutHintRow(hintPanel);
            LayoutButtonRow(buttonPanel);
        }

        private void LayoutButtonRow(Panel panel)
        {
            try
            {
                int right = panel.ClientSize.Width - panel.Padding.Right;
                int top = panel.Padding.Top;
                _btnCancel.Location = new Point(right - _btnCancel.Width, top);
                _btnOk.Location = new Point(_btnCancel.Left - _btnOk.Width - 8, top);
            }
            catch { }
        }

        private void LayoutToolRow(Panel panel)
        {
            try
            {
                int left = panel.Padding.Left;
                int top = panel.Padding.Top;
                _btnNewFolder.Location = new Point(left, top);
                _chkHidden.Location = new Point(_btnNewFolder.Right + 12, top + 3);
            }
            catch { }
        }

        private void LayoutHintRow(Panel panel)
        {
            try
            {
                _lblHint.Location = new Point(panel.Padding.Left, panel.Padding.Top);
                int width = panel.ClientSize.Width - panel.Padding.Left - panel.Padding.Right;
                _lblHint.Size = new Size(Math.Max(0, width),
                                         panel.ClientSize.Height - panel.Padding.Top - panel.Padding.Bottom);
            }
            catch { }
        }

        // ---------------- 图标 ----------------

        private void BuildIcons()
        {
            try
            {
                Icon folder = GetShellIcon(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows), true, false);
                if (folder != null) _icons.Images.Add(IconFolder, folder);
            }
            catch { }

            if (!_icons.Images.ContainsKey(IconFolder))
                _icons.Images.Add(IconFolder, DrawFallbackIcon(Color.FromArgb(255, 196, 142), Color.FromArgb(232, 168, 74)));

            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                Icon icon = GetShellIcon(desktop, true, false);
                if (icon != null) _icons.Images.Add(IconDesktop, icon);
            }
            catch { }

            try
            {
                string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                Icon icon = GetShellIcon(docs, true, false);
                if (icon != null) _icons.Images.Add(IconDocuments, icon);
            }
            catch { }

            try
            {
                // 尝试拿"我的电脑"的系统图标（CLSID 路径），失败就退回文件夹图标
                Icon icon = GetShellIcon("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", false, true);
                if (icon != null) _icons.Images.Add(IconComputer, icon);
            }
            catch { }

            if (!_icons.Images.ContainsKey(IconComputer))
                _icons.Images.Add(IconComputer, DrawFallbackIcon(Color.FromArgb(150, 190, 235), Color.FromArgb(64, 120, 190)));
        }

        /// <summary>绘制一个兜底的文件夹色块图标（只在取不到系统图标时使用）。</summary>
        private static Icon DrawFallbackIcon(Color light, Color dark)
        {
            Bitmap bmp = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                using (SolidBrush brush = new SolidBrush(dark))
                    g.FillRectangle(brush, 1, 4, 5, 3);
                using (SolidBrush brush = new SolidBrush(light))
                    g.FillRectangle(brush, 1, 7, 14, 7);
                using (Pen pen = new Pen(dark))
                    g.DrawRectangle(pen, 1, 7, 14, 7);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        // ---------------- 根节点 ----------------

        private void BuildRoots()
        {
            _tree.BeginUpdate();
            try
            {
                _tree.Nodes.Clear();

                TreeNode desktop = MakeNode("桌面", SafeSpecialFolder(Environment.SpecialFolder.DesktopDirectory), IconDesktop);
                _tree.Nodes.Add(desktop);

                TreeNode computer = MakeNode("计算机", null, IconComputer);
                computer.Tag = new FolderTag(null, true);          // 虚拟节点：本身不是目录
                _tree.Nodes.Add(computer);

                TreeNode documents = MakeNode("我的文档", SafeSpecialFolder(Environment.SpecialFolder.MyDocuments), IconDocuments);
                if (documents.Tag != null) _tree.Nodes.Add(documents);

                AddDrives(computer);

                AddDummy(desktop);
                if (documents.Tag != null) AddDummy(documents);

                try { desktop.Expand(); } catch { }
                try { computer.Expand(); } catch { }
            }
            finally
            {
                _tree.EndUpdate();
            }
        }

        private void AddDrives(TreeNode parent)
        {
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch { return; }

            foreach (DriveInfo drive in drives)
            {
                string root = drive.Name;
                if (IsEmpty(root)) continue;

                string label = drive.Name.TrimEnd(Path.DirectorySeparatorChar);
                string display = label;
                string iconKey = IconFolder;

                bool ready;
                try { ready = drive.IsReady; }
                catch { ready = false; }

                if (ready)
                {
                    // 形如「本地磁盘 (C:)」；有卷标时优先用卷标，和 Windows 资源管理器一致
                    string typeName = DescribeDriveType(drive.DriveType);
                    if (IsEmpty(typeName)) typeName = "本地磁盘";

                    string volume = Safe(drive.VolumeLabel);
                    display = IsEmpty(volume) ? typeName + " (" + label + ")"
                                              : volume + " (" + label + ")";

                    if (!_icons.Images.ContainsKey(root))
                    {
                        try
                        {
                            Icon icon = GetShellIcon(root, true, false);
                            if (icon != null) _icons.Images.Add(root, icon);
                        }
                        catch { }
                    }
                    if (_icons.Images.ContainsKey(root)) iconKey = root;
                }
                else
                {
                    display = label;
                }

                TreeNode node = MakeNode(display, root, iconKey);
                parent.Nodes.Add(node);
                if (ready) AddDummy(node);
            }
        }

        private static string DescribeDriveType(DriveType type)
        {
            switch (type)
            {
                case DriveType.Removable: return "可移动磁盘";
                case DriveType.Fixed: return "本地磁盘";
                case DriveType.CDRom: return "光盘驱动器";
                case DriveType.Network: return "网络驱动器";
                case DriveType.Ram: return "RAM 磁盘";
                default: return string.Empty;
            }
        }

        private TreeNode MakeNode(string text, string path, string iconKey)
        {
            TreeNode node = new TreeNode(text);
            node.Tag = new FolderTag(path, false);
            ApplyIcon(node, iconKey);
            return node;
        }

        /// <summary>
        /// 给节点设置图标。图标键不存在时不设置（WinForms 对未知 key 会退化成不显示，
        /// 但显式判断更可控，也避免不同 .NET 版本行为差异）。
        /// </summary>
        private void ApplyIcon(TreeNode node, string iconKey)
        {
            if (node == null || IsEmpty(iconKey)) return;
            try
            {
                if (_icons == null || !_icons.Images.ContainsKey(iconKey)) return;
                node.ImageKey = iconKey;
                node.SelectedImageKey = iconKey;
            }
            catch { }
        }

        private static void AddDummy(TreeNode node)
        {
            FolderTag tag = node.Tag as FolderTag;
            if (tag != null && tag.Loaded) return;
            if (node.Nodes.Count > 0) return;

            TreeNode dummy = new TreeNode(DummyText);
            dummy.Tag = null;                    // Tag == null 即为占位节点
            node.Nodes.Add(dummy);
        }

        // ---------------- 惰性加载 ----------------

        private void OnBeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            EnsureLoaded(e.Node);
        }

        private void EnsureLoaded(TreeNode node)
        {
            if (node == null) return;

            FolderTag tag = node.Tag as FolderTag;
            if (tag == null) return;              // 占位节点
            if (tag.Loaded) return;
            if (IsEmpty(tag.Path)) return;

            tag.Loaded = true;

            node.Nodes.Clear();
            LoadChildren(node, tag.Path);

            // 一个子节点都没有时，把占位符摘掉免得一直显示加号
            if (node.Nodes.Count == 0 && tag.Virtual == false)
            {
                // 保持为空即可
            }
        }

        private void LoadChildren(TreeNode node, string path)
        {
            string[] dirs;
            try { dirs = Directory.GetDirectories(path); }
            catch { return; }

            List<string> visible = new List<string>();
            foreach (string dir in dirs)
            {
                if (!IsVisible(dir)) continue;
                visible.Add(dir);
            }

            try { visible.Sort(ComparePathByName); }
            catch { }

            foreach (string dir in visible)
            {
                string name;
                try { name = new DirectoryInfo(dir).Name; }
                catch { name = dir; }
                if (IsEmpty(name)) name = dir;

                TreeNode child = new TreeNode(name);
                child.Tag = new FolderTag(dir, false);
                ApplyIcon(child, IconFolder);
                node.Nodes.Add(child);
                AddDummy(child);
            }
        }

        private static int ComparePathByName(string a, string b)
        {
            string x = a, y = b;
            try { x = new DirectoryInfo(a).Name; } catch { }
            try { y = new DirectoryInfo(b).Name; } catch { }
            return string.Compare(x, y, StringComparison.CurrentCultureIgnoreCase);
        }

        private bool IsVisible(string dir)
        {
            if (_chkHidden != null && _chkHidden.Checked) return true;

            try
            {
                DirectoryInfo info = new DirectoryInfo(dir);
                FileAttributes attr = info.Attributes;
                if ((attr & FileAttributes.Hidden) == FileAttributes.Hidden) return false;
                if ((attr & FileAttributes.System) == FileAttributes.System) return false;
            }
            catch
            {
                // 取不到属性就放过，交给后续枚举去失败
            }
            return true;
        }

        // ---------------- 路径定位 ----------------

        private void OnAfterSelect(object sender, TreeViewEventArgs e)
        {
            FolderTag tag = e.Node != null ? (e.Node.Tag as FolderTag) : null;
            if (tag == null || tag.Virtual || IsEmpty(tag.Path)) return;

            _currentPath = tag.Path;
            UpdateOkState();
        }

        /// <summary>把树定位到指定目录（逐层展开）。找不到返回 false。</summary>
        private bool TrySelectPath(string path)
        {
            if (IsEmpty(path)) return false;

            string full;
            try { full = Path.GetFullPath(path); }
            catch { return false; }

            foreach (TreeNode root in _tree.Nodes)
            {
                TreeNode hit = SelectRecursive(root, full);
                if (hit != null)
                {
                    try
                    {
                        hit.EnsureVisible();
                        _tree.SelectedNode = hit;
                    }
                    catch { }
                    return true;
                }
            }
            return false;
        }

        private TreeNode SelectRecursive(TreeNode node, string path)
        {
            FolderTag tag = node.Tag as FolderTag;

            if (tag != null && !IsEmpty(tag.Path))
            {
                if (string.Equals(tag.Path, path, StringComparison.OrdinalIgnoreCase))
                    return node;

                // 目标不在本节点之下就不用往下找了，避免无谓枚举
                if (!IsUnder(tag.Path, path)) return null;
            }

            EnsureLoaded(node);

            foreach (TreeNode child in node.Nodes)
            {
                TreeNode hit = SelectRecursive(child, path);
                if (hit != null) return hit;
            }
            return null;
        }

        private static bool IsUnder(string parentPath, string childPath)
        {
            if (IsEmpty(parentPath) || IsEmpty(childPath)) return false;
            if (!childPath.StartsWith(parentPath, StringComparison.OrdinalIgnoreCase)) return false;

            if (childPath.Length == parentPath.Length) return true;

            char next = childPath[parentPath.Length];
            return next == Path.DirectorySeparatorChar || next == Path.AltDirectorySeparatorChar;
        }

        // ---------------- 新建文件夹 ----------------

        private void OnNewFolderClick(object sender, EventArgs e)
        {
            string parent = _currentPath == null ? string.Empty : _currentPath.Trim();
            if (parent.Length == 0 || !Directory.Exists(parent))
            {
                SetHint("请先选择一个有效的父文件夹。");
                return;
            }

            string target;
            try
            {
                target = MakeUniqueName(parent, "新建文件夹");
                Directory.CreateDirectory(target);
            }
            catch (System.Exception ex)
            {
                SetHint("创建失败：" + ex.Message);
                return;
            }

            TreeNode parentNode = _tree.SelectedNode;
            if (parentNode == null) TrySelectPath(parent);
            parentNode = _tree.SelectedNode;

            if (parentNode != null)
            {
                FolderTag tag = parentNode.Tag as FolderTag;
                if (tag != null)
                {
                    tag.Loaded = false;
                    EnsureLoaded(parentNode);
                    parentNode.Expand();
                }

                foreach (TreeNode child in parentNode.Nodes)
                {
                    FolderTag childTag = child.Tag as FolderTag;
                    if (childTag == null || IsEmpty(childTag.Path)) continue;
                    if (string.Equals(childTag.Path, target, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            _tree.SelectedNode = child;
                            child.EnsureVisible();
                            child.BeginEdit();
                        }
                        catch { }
                        break;
                    }
                }
            }

            _currentPath = target;
            UpdateOkState();
            SetHint("已创建新文件夹，可直接改名。");
        }

        private static string MakeUniqueName(string parent, string baseName)
        {
            string candidate = Path.Combine(parent, baseName);
            if (!Directory.Exists(candidate)) return candidate;

            for (int i = 2; i < 1000; i++)
            {
                candidate = Path.Combine(parent, baseName + " (" + i + ")");
                if (!Directory.Exists(candidate)) return candidate;
            }
            return Path.Combine(parent, baseName);
        }

        private void OnAfterLabelEdit(object sender, NodeLabelEditEventArgs e)
        {
            if (e.Node == null) return;
            if (e.Label == null) return;                    // 用户取消编辑

            FolderTag tag = e.Node.Tag as FolderTag;
            if (tag == null || tag.Virtual || IsEmpty(tag.Path)) { e.CancelEdit = true; return; }

            string newName = e.Label.Trim();
            if (newName.Length == 0) { e.CancelEdit = true; return; }
            if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                e.CancelEdit = true;
                SetHint("名称含有非法字符。");
                return;
            }

            DirectoryInfo parent;
            try { parent = Directory.GetParent(tag.Path); }
            catch { e.CancelEdit = true; return; }
            if (parent == null) { e.CancelEdit = true; return; }

            string target = Path.Combine(parent.FullName, newName);
            if (Directory.Exists(target))
            {
                e.CancelEdit = true;
                SetHint("已存在同名文件夹。");
                return;
            }

            try
            {
                Directory.Move(tag.Path, target);
                tag.Path = target;
                _currentPath = target;
                UpdateOkState();
                SetHint(string.Empty);
            }
            catch (System.Exception ex)
            {
                e.CancelEdit = true;
                SetHint("重命名失败：" + ex.Message);
            }
        }

        private void OnHiddenChanged(object sender, EventArgs e)
        {
            string keep = _currentPath;

            foreach (TreeNode root in _tree.Nodes)
                ResetLoaded(root);

            BuildRoots();

            if (!IsEmpty(keep) && Directory.Exists(keep)) TrySelectPath(keep);
        }

        private static void ResetLoaded(TreeNode node)
        {
            FolderTag tag = node.Tag as FolderTag;
            if (tag != null) tag.Loaded = false;
            foreach (TreeNode child in node.Nodes)
                ResetLoaded(child);
        }

        // ---------------- 确定 / 取消 ----------------

        private void UpdateOkState()
        {
            if (_btnOk == null) return;

            string text = _currentPath == null ? string.Empty : _currentPath.Trim();
            bool ok = text.Length > 0 && Directory.Exists(text);

            _btnOk.Enabled = ok;
            _btnNewFolder.Enabled = ok;
        }

        private void OnOkClick(object sender, EventArgs e)
        {
            string text = _currentPath == null ? string.Empty : _currentPath.Trim();
            if (text.Length == 0) { DialogResult = DialogResult.None; return; }

            if (!Directory.Exists(text))
            {
                SetHint("目录不存在，请重新选择。");
                DialogResult = DialogResult.None;
                return;
            }

            SelectedPath = Normalize(text);
            DialogResult = DialogResult.OK;
        }

        private void SetHint(string text)
        {
            if (_lblHint == null) return;
            _lblHint.Text = IsEmpty(text)
                ? "提示：双击节点展开；选中文件夹后点“确定”即可入库。"
                : text;
        }

        // ---------------- 工具 ----------------

        private static bool IsEmpty(string value)
        {
            return value == null || value.Length == 0;
        }

        private static string Safe(string value)
        {
            return value == null ? string.Empty : value;
        }

        /// <summary>去掉结尾多余的分隔符（根目录除外）。</summary>
        private static string Normalize(string path)
        {
            if (IsEmpty(path)) return path;

            try
            {
                DirectoryInfo info = new DirectoryInfo(path);
                return info.FullName;
            }
            catch
            {
                return path.Trim();
            }
        }

        private static string SafeSpecialFolder(Environment.SpecialFolder folder)
        {
            try
            {
                string path = Environment.GetFolderPath(folder);
                if (!IsEmpty(path) && Directory.Exists(path)) return path;
            }
            catch { }
            return null;
        }

        // ---------------- Shell 图标 ----------------

        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_SMALLICON = 0x000000001;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        /// <summary>取系统图标。取不到返回 null（调用方自行兜底），绝不抛异常。</summary>
        private static Icon GetShellIcon(string path, bool directory, bool useAttributes)
        {
            try
            {
                SHFILEINFO info = new SHFILEINFO();
                uint flags = SHGFI_ICON | SHGFI_SMALLICON;

                uint attributes = 0;
                if (useAttributes || IsEmpty(path))
                {
                    flags |= SHGFI_USEFILEATTRIBUTES;
                    attributes = directory ? FILE_ATTRIBUTE_DIRECTORY : 0;
                }

                uint size = (uint)Marshal.SizeOf(typeof(SHFILEINFO));
                IntPtr result = SHGetFileInfo(path, attributes, ref info, size, flags);

                if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

                Icon original = Icon.FromHandle(info.hIcon);
                Icon clone = (Icon)original.Clone();
                try { DestroyIcon(info.hIcon); } catch { }
                return clone;
            }
            catch
            {
                return null;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _icons != null)
            {
                _icons.Dispose();
                _icons = null;
            }
            base.Dispose(disposing);
        }

        // ---------------- 节点数据 ----------------

        private sealed class FolderTag
        {
            public string Path;
            public bool Loaded;
            public bool Virtual;

            public FolderTag(string path, bool isVirtual)
            {
                Path = path;
                Loaded = false;
                Virtual = isVirtual;
            }
        }
    }
}
