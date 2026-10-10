using System;
using System.Drawing;
using System.Windows.Forms;

namespace BlockLibraryManager
{
    /// <summary>
    /// 语言选择对话框：跟随系统语言 / 简体中文 / English。
    /// 点确定后由 LibraryControl 保存配置并立即刷新界面文字。
    /// </summary>
    public sealed class LanguageForm : Form
    {
        private RadioButton _rbSystem;
        private RadioButton _rbChinese;
        private RadioButton _rbEnglish;
        private Button _btnOk;
        private Button _btnCancel;

        /// <summary>对话框确定后选中的语言值（"" / "zh-CN" / "en-US"）。</summary>
        public string SelectedLanguage
        {
            get
            {
                if (_rbChinese != null && _rbChinese.Checked) return LanguageService.Chinese;
                if (_rbEnglish != null && _rbEnglish.Checked) return LanguageService.English;
                return LanguageService.FollowSystem;
            }
        }

        public LanguageForm(string current)
        {
            Text = L.T("语言", "Language");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(320, 178);
            Font = SystemFonts.MessageBoxFont;

            Label prompt = new Label();
            prompt.Text = L.T("请选择界面语言：", "Select interface language:");
            prompt.AutoSize = true;
            prompt.Location = new Point(14, 12);

            _rbSystem = new RadioButton();
            _rbSystem.Text = L.T("跟随系统语言（默认）", "Follow system language (default)");
            _rbSystem.AutoSize = true;
            _rbSystem.Location = new Point(16, 40);

            _rbChinese = new RadioButton();
            _rbChinese.Text = "简体中文";
            _rbChinese.AutoSize = true;
            _rbChinese.Location = new Point(16, 66);

            _rbEnglish = new RadioButton();
            _rbEnglish.Text = "English";
            _rbEnglish.AutoSize = true;
            _rbEnglish.Location = new Point(16, 92);

            // 按当前配置选中
            if (current == LanguageService.Chinese) _rbChinese.Checked = true;
            else if (current == LanguageService.English) _rbEnglish.Checked = true;
            else _rbSystem.Checked = true;

            _btnOk = new Button();
            _btnOk.Text = L.T("确定", "OK");
            _btnOk.Size = new Size(75, 26);
            _btnOk.Location = new Point(ClientSize.Width - 75 * 2 - 14 - 8, ClientSize.Height - 34);
            _btnOk.DialogResult = DialogResult.OK;

            _btnCancel = new Button();
            _btnCancel.Text = L.T("取消", "Cancel");
            _btnCancel.Size = new Size(75, 26);
            _btnCancel.Location = new Point(ClientSize.Width - 75 - 14, ClientSize.Height - 34);
            _btnCancel.DialogResult = DialogResult.Cancel;

            Controls.Add(prompt);
            Controls.Add(_rbSystem);
            Controls.Add(_rbChinese);
            Controls.Add(_rbEnglish);
            Controls.Add(_btnOk);
            Controls.Add(_btnCancel);

            AcceptButton = _btnOk;
            CancelButton = _btnCancel;
        }
    }
}
