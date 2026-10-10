using System;
using System.Drawing;
using System.Windows.Forms;

namespace BlockLibraryManager
{
    /// <summary>图库管理器的设置对话框。</summary>
    public class SettingsForm : Form
    {
        private readonly LibraryConfig _config;

        private ListBox _lstRoots;
        private Button _btnAddRoot;
        private Button _btnRemoveRoot;
        private ListBox _lstExcluded;
        private Button _btnRestoreExcluded;
        private NumericUpDown _numThumbSize;
        private ComboBox _cboThumbSize;
        private NumericUpDown _numRecentLimit;

        /// <summary>下拉框和数值框互相同步时的防抖标志，避免来回改值死循环。</summary>
        private bool _syncingThumb;

        /// <summary>缩略图尺寸的常用档位（像素）。</summary>
        private static readonly int[] ThumbPresets =
        {
            48, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256
        };
        private ComboBox _cboView;
        private CheckBox _chkExpandInner;
        private CheckBox _chkSearchSub;

        /// <summary>启动插件的自定义命令名输入框。</summary>
        private TextBox _txtLaunchCommand;

        private Button _btnOk;
        private Button _btnCancel;

        public SettingsForm(LibraryConfig config)
        {
            if (config == null) throw new ArgumentNullException("config");
            _config = config;

            InitializeForm();
            LoadFromConfig();
        }

        private void InitializeForm()
        {
            // 布局说明：整体按像素手工排布，三列 ——
            //   标签列 x=16（AutoSize，给足空间不会被控件压住）
            //   数值列 x=210
            //   右边界统一 604（窗体宽 620 - 右边距 16）
            // 每个标签与它的列表/控件之间留 28px 空隙，行与行之间留足间距，不遮挡。
            // 用 AutoScaleMode.None + 显式字体，避免不同 DPI 下自动缩放把文字挤出控件。
            Text = "图库管理器 - 设置";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(620, 744);
            Padding = new Padding(16);

            const int LABEL_X = 16;
            const int CTRL_X = 210;
            const int BTN_X = 468;
            const int RIGHT = 604;

            // ---- 图库根目录 ----
            // 标签压在最上（y=12），与列表留 30px 以上空隙，任何 DPI 下都不会压住列表首行
            Label lblRoots = new Label();
            lblRoots.Text = "图库根目录（可多个）：";
            lblRoots.Location = new Point(LABEL_X, 12);
            lblRoots.AutoSize = true;

            _lstRoots = new ListBox();
            _lstRoots.Location = new Point(LABEL_X, 48);
            _lstRoots.Size = new Size(440, 150);
            _lstRoots.IntegralHeight = false;

            _btnAddRoot = new Button();
            _btnAddRoot.Text = "添加…";
            _btnAddRoot.Location = new Point(BTN_X, 48);
            _btnAddRoot.Size = new Size(108, 28);
            _btnAddRoot.Click += new EventHandler(OnAddRootClick);

            _btnRemoveRoot = new Button();
            _btnRemoveRoot.Text = "移除";
            _btnRemoveRoot.Location = new Point(BTN_X, 86);
            _btnRemoveRoot.Size = new Size(108, 28);
            _btnRemoveRoot.Click += new EventHandler(OnRemoveRootClick);

            // ---- 隐藏的子目录（右键"隐藏该子目录"产生，可在这里恢复） ----
            Label lblExcluded = new Label();
            lblExcluded.Text = "隐藏的子目录（不参与扫描与显示）：";
            lblExcluded.Location = new Point(LABEL_X, 214);
            lblExcluded.AutoSize = true;

            _lstExcluded = new ListBox();
            _lstExcluded.Location = new Point(LABEL_X, 250);
            _lstExcluded.Size = new Size(440, 110);
            _lstExcluded.IntegralHeight = false;

            _btnRestoreExcluded = new Button();
            _btnRestoreExcluded.Text = "恢复显示";
            _btnRestoreExcluded.Location = new Point(BTN_X, 250);
            _btnRestoreExcluded.Size = new Size(108, 28);
            _btnRestoreExcluded.Click += new EventHandler(OnRestoreExcludedClick);

            // ---- 缩略图尺寸：数值框 + 预设下拉（两个控件双向同步） ----
            Label lblThumb = new Label();
            lblThumb.Text = "缩略图尺寸（像素）：";
            lblThumb.Location = new Point(LABEL_X, 388);
            lblThumb.AutoSize = true;

            _numThumbSize = new NumericUpDown();
            _numThumbSize.Location = new Point(CTRL_X, 384);
            _numThumbSize.Size = new Size(80, 24);
            _numThumbSize.Minimum = 48;
            _numThumbSize.Maximum = 256;
            _numThumbSize.Increment = 8;
            _numThumbSize.ValueChanged += new EventHandler(OnThumbSizeValueChanged);

            Label lblUnit = new Label();
            lblUnit.Text = "像素";
            lblUnit.Location = new Point(296, 388);
            lblUnit.AutoSize = true;

            _cboThumbSize = new ComboBox();
            _cboThumbSize.Location = new Point(344, 384);
            _cboThumbSize.Size = new Size(RIGHT - 344, 24);
            _cboThumbSize.DropDownStyle = ComboBoxStyle.DropDownList;
            _cboThumbSize.SelectedIndexChanged += new EventHandler(OnThumbPresetChanged);

            foreach (int preset in ThumbPresets)
                _cboThumbSize.Items.Add(new ThumbItem(preset));
            _cboThumbSize.Items.Add(new ThumbItem(0));       // 0 = 自定义

            // ---- 默认视图 ----
            Label lblView = new Label();
            lblView.Text = "右侧列表默认视图：";
            lblView.Location = new Point(LABEL_X, 426);
            lblView.AutoSize = true;

            _cboView = new ComboBox();
            _cboView.Location = new Point(CTRL_X, 422);
            _cboView.Size = new Size(200, 24);
            _cboView.DropDownStyle = ComboBoxStyle.DropDownList;
            _cboView.Items.Add(new ViewItem("大图标", GalleryView.LargeIcon));
            _cboView.Items.Add(new ViewItem("详细信息", GalleryView.Details));
            _cboView.Items.Add(new ViewItem("列表", GalleryView.List));

            // ---- 最近使用条数 ----
            Label lblRecent = new Label();
            lblRecent.Text = "最近使用保留条数：";
            lblRecent.Location = new Point(LABEL_X, 464);
            lblRecent.AutoSize = true;

            _numRecentLimit = new NumericUpDown();
            _numRecentLimit.Location = new Point(CTRL_X, 460);
            _numRecentLimit.Size = new Size(80, 24);
            _numRecentLimit.Minimum = 0;
            _numRecentLimit.Maximum = 100;
            _numRecentLimit.Increment = 5;

            Label lblRecentHint = new Label();
            lblRecentHint.Text = "条（0 表示不记录最近使用）";
            lblRecentHint.ForeColor = SystemColors.GrayText;
            lblRecentHint.Location = new Point(296, 464);
            lblRecentHint.AutoSize = true;

            // ---- 复选框 ----
            _chkExpandInner = new CheckBox();
            _chkExpandInner.Text = "展开 DWG 内部的块定义（首次扫描较慢，可在需要时再开）";
            _chkExpandInner.Location = new Point(LABEL_X, 502);
            _chkExpandInner.AutoSize = true;

            _chkSearchSub = new CheckBox();
            _chkSearchSub.Text = "浏览分类时包含下级子目录";
            _chkSearchSub.Location = new Point(LABEL_X, 534);
            _chkSearchSub.AutoSize = true;

            // ---- 启动插件命令（行距加大，不与上方复选框挤在一起） ----
            Label lblCmd = new Label();
            lblCmd.Text = "启动插件命令：";
            lblCmd.Location = new Point(LABEL_X, 576);
            lblCmd.AutoSize = true;

            _txtLaunchCommand = new TextBox();
            _txtLaunchCommand.Location = new Point(CTRL_X, 572);
            _txtLaunchCommand.Size = new Size(200, 24);
            _txtLaunchCommand.CharacterCasing = CharacterCasing.Upper;

            Label lblNote = new Label();
            lblNote.Text = "说明：改动缩略图尺寸后会自动重新生成预览缓存，第一次稍慢，之后即秒开。";
            lblNote.ForeColor = SystemColors.GrayText;
            lblNote.Location = new Point(LABEL_X, 612);
            lblNote.Size = new Size(RIGHT - LABEL_X, 20);
            lblNote.AutoEllipsis = false;

            // 提示文字：给足两行高度，绝不遮挡
            Label lblCmdHint = new Label();
            lblCmdHint.Text = "提示：启动命令修改后将在下次启动 CAD 时生效；请勿与 CAD 已有命令重名，留空或输入无效时恢复为 BLOCKLIB。";
            lblCmdHint.ForeColor = SystemColors.GrayText;
            lblCmdHint.Location = new Point(LABEL_X, 640);
            lblCmdHint.Size = new Size(RIGHT - LABEL_X, 44);
            lblCmdHint.AutoEllipsis = false;

            // ---- 确定 / 取消 ----
            _btnOk = new Button();
            _btnOk.Text = "确定";
            _btnOk.Location = new Point(388, 700);
            _btnOk.Size = new Size(96, 30);
            _btnOk.DialogResult = DialogResult.None;
            _btnOk.Click += new EventHandler(OnOkClick);

            _btnCancel = new Button();
            _btnCancel.Text = "取消";
            _btnCancel.Location = new Point(508, 700);
            _btnCancel.Size = new Size(96, 30);
            _btnCancel.DialogResult = DialogResult.Cancel;

            Controls.Add(lblRoots);
            Controls.Add(_lstRoots);
            Controls.Add(_btnAddRoot);
            Controls.Add(_btnRemoveRoot);
            Controls.Add(lblExcluded);
            Controls.Add(_lstExcluded);
            Controls.Add(_btnRestoreExcluded);
            Controls.Add(lblThumb);
            Controls.Add(_numThumbSize);
            Controls.Add(lblUnit);
            Controls.Add(_cboThumbSize);
            Controls.Add(lblView);
            Controls.Add(_cboView);
            Controls.Add(lblRecent);
            Controls.Add(_numRecentLimit);
            Controls.Add(lblRecentHint);
            Controls.Add(_chkExpandInner);
            Controls.Add(_chkSearchSub);
            Controls.Add(lblCmd);
            Controls.Add(_txtLaunchCommand);
            Controls.Add(lblNote);
            Controls.Add(lblCmdHint);
            Controls.Add(_btnOk);
            Controls.Add(_btnCancel);

            AcceptButton = _btnOk;
            CancelButton = _btnCancel;
        }

        private void LoadFromConfig()
        {
            _lstRoots.Items.Clear();
            foreach (string root in _config.RootPaths)
                _lstRoots.Items.Add(root);

            _lstExcluded.Items.Clear();
            foreach (string path in _config.ExcludedPaths)
                _lstExcluded.Items.Add(path);
            _btnRestoreExcluded.Enabled = _lstExcluded.Items.Count > 0;

            _numThumbSize.Value = _config.ThumbSize;
            SyncPresetToValue();
            _numRecentLimit.Value = _config.RecentLimit;
            _chkExpandInner.Checked = _config.ExpandInnerBlocks;
            _chkSearchSub.Checked = _config.SearchSubCategories;

            string launch = _config.LaunchCommand;
            _txtLaunchCommand.Text = string.IsNullOrEmpty(launch) ? "BLOCKLIB" : launch;

            for (int i = 0; i < _cboView.Items.Count; i++)
            {
                ViewItem item = _cboView.Items[i] as ViewItem;
                if (item != null && item.Mode == _config.ViewMode)
                {
                    _cboView.SelectedIndex = i;
                    break;
                }
            }
            if (_cboView.SelectedIndex < 0 && _cboView.Items.Count > 0)
                _cboView.SelectedIndex = 0;
        }

        // ---------------- 缩略图尺寸：数值框 <-> 预设下拉 双向同步 ----------------

        private void OnThumbPresetChanged(object sender, EventArgs e)
        {
            if (_syncingThumb) return;

            ThumbItem item = _cboThumbSize.SelectedItem as ThumbItem;
            if (item == null || item.Size <= 0) return;      // “自定义”不改数值

            _syncingThumb = true;
            try
            {
                decimal value = item.Size;
                if (value < _numThumbSize.Minimum) value = _numThumbSize.Minimum;
                if (value > _numThumbSize.Maximum) value = _numThumbSize.Maximum;
                _numThumbSize.Value = value;
            }
            finally
            {
                _syncingThumb = false;
            }
        }

        private void OnThumbSizeValueChanged(object sender, EventArgs e)
        {
            if (_syncingThumb) return;
            SyncPresetToValue();
        }

        /// <summary>按当前数值框的值选中对应档位，没有精确档位就选“自定义”。</summary>
        private void SyncPresetToValue()
        {
            _syncingThumb = true;
            try
            {
                int size = (int)_numThumbSize.Value;
                int index = -1;

                for (int i = 0; i < _cboThumbSize.Items.Count; i++)
                {
                    ThumbItem item = _cboThumbSize.Items[i] as ThumbItem;
                    if (item != null && item.Size == size) { index = i; break; }
                }

                if (index < 0) index = _cboThumbSize.Items.Count - 1;   // 最后一项是“自定义”
                _cboThumbSize.SelectedIndex = index;
            }
            finally
            {
                _syncingThumb = false;
            }
        }

        private sealed class ThumbItem
        {
            public int Size { get; private set; }

            public ThumbItem(int size)
            {
                Size = size;
            }

            public override string ToString()
            {
                if (Size <= 0) return "自定义（用左侧数值）";
                return Size + " × " + Size + " 像素";
            }
        }

        private void OnAddRootClick(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择图库根目录";
                dialog.ShowNewFolderButton = true;

                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                string path = dialog.SelectedPath;
                if (string.IsNullOrEmpty(path)) return;

                foreach (string existing in _config.RootPaths)
                {
                    if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
                        return;
                }

                _config.AddRoot(path);
                _lstRoots.Items.Add(path);
            }
        }

        private void OnRemoveRootClick(object sender, EventArgs e)
        {
            object selected = _lstRoots.SelectedItem;
            if (selected == null) return;

            string path = selected.ToString();
            _config.RemoveRoot(path);
            _config.RemoveExcludedUnder(path);   // 根目录没了，其下的隐藏记录一并清掉
            _lstRoots.Items.Remove(selected);
        }

        /// <summary>把选中的隐藏子目录恢复显示（从隐藏列表移除，重新参与扫描）。</summary>
        private void OnRestoreExcludedClick(object sender, EventArgs e)
        {
            object selected = _lstExcluded.SelectedItem;
            if (selected == null)
            {
                MessageBox.Show(this, "请先在列表里选中要恢复的子目录。", "恢复显示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string path = selected.ToString();
            _config.RemoveExcluded(path);
            _lstExcluded.Items.Remove(selected);
            _btnRestoreExcluded.Enabled = _lstExcluded.Items.Count > 0;
        }

        private void OnOkClick(object sender, EventArgs e)
        {
            _config.ThumbSize = (int)_numThumbSize.Value;
            _config.RecentLimit = (int)_numRecentLimit.Value;
            _config.ExpandInnerBlocks = _chkExpandInner.Checked;
            _config.SearchSubCategories = _chkSearchSub.Checked;

            ViewItem item = _cboView.SelectedItem as ViewItem;
            if (item != null) _config.ViewMode = item.Mode;

            // 启动命令：非法输入（空 / 含空白 / 含命令非法字符）直接恢复默认 BLOCKLIB。
            // 命令本身不会立即生效——插件只在启动时读取该值，符合"下次启动生效"。
            string cmd = Commands.NormalizeLaunchCommand(_txtLaunchCommand.Text);
            _config.LaunchCommand = string.IsNullOrEmpty(cmd) ? "BLOCKLIB" : cmd;

            DialogResult = DialogResult.OK;
            Close();
        }

        private sealed class ViewItem
        {
            public string Text { get; private set; }
            public GalleryView Mode { get; private set; }

            public ViewItem(string text, GalleryView mode)
            {
                Text = text;
                Mode = mode;
            }

            public override string ToString()
            {
                return Text;
            }
        }
    }
}
