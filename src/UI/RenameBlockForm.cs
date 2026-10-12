using System;
using System.Drawing;
using System.Windows.Forms;

namespace BlockLibraryManager
{
    /// <summary>
    /// 重命名块用的小输入框。
    /// 项目里没有通用的 InputBox，手写一个最简模态框：标签 + 文本框 + 确定/取消。
    /// 返回 null 表示用户点了取消 / 关闭。
    /// </summary>
    internal static class RenameBlockForm
    {
        public static string Show(IWin32Window owner, string title, string labelText, string initialText)
        {
            string result = null;
            Form form = new Form();

            try
            {
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.CenterParent;
                form.Text = title;
                form.BackColor = SystemColors.Window;
                form.Font = SystemFonts.MessageBoxFont;
                form.ClientSize = new Size(360, 110);

                Label label = new Label();
                label.Text = labelText;
                label.Location = new Point(12, 12);
                label.AutoSize = true;
                form.Controls.Add(label);

                TextBox text = new TextBox();
                text.Location = new Point(14, 38);
                text.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                text.Size = new Size(332, 23);
                text.Text = initialText == null ? string.Empty : initialText;
                form.Controls.Add(text);

                Button ok = new Button();
                ok.Text = "确定";
                ok.DialogResult = DialogResult.OK;
                ok.Size = new Size(88, 26);
                ok.Location = new Point(form.ClientSize.Width - 88 - 12, form.ClientSize.Height - 26 - 12);
                ok.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
                form.Controls.Add(ok);

                Button cancel = new Button();
                cancel.Text = "取消";
                cancel.DialogResult = DialogResult.Cancel;
                cancel.Size = new Size(88, 26);
                cancel.Location = new Point(ok.Left - 88 - 8, ok.Top);
                cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
                form.Controls.Add(cancel);

                form.AcceptButton = ok;
                form.CancelButton = cancel;

                // 弹出后全选预填文字，方便直接输入新名字覆盖
                form.Shown += new EventHandler(delegate(object s, EventArgs e)
                {
                    text.Focus();
                    text.SelectAll();
                });

                if (owner != null)
                {
                    if (form.ShowDialog(owner) != DialogResult.OK) return null;
                }
                else
                {
                    if (form.ShowDialog() != DialogResult.OK) return null;
                }

                result = text.Text;
                return result;
            }
            finally
            {
                form.Dispose();
            }
        }
    }
}
