using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace BlockLibraryManager
{
    /// <summary>
    /// “关于”对话框的展示内容 —— 想改信息直接改这里的常量即可，不用动界面代码。
    /// 留空（string.Empty）的行不会显示。
    /// </summary>
    internal static class AboutInfo
    {
        /// <summary>产品名称。</summary>
        public const string ProductName = "图库管理器";

        /// <summary>英文副标题。</summary>
        public const string SubTitle = "Block Library Manager for AutoCAD";

        /// <summary>插件版本号。</summary>
        public const string Version = "1.51.7";

        /// <summary>发布日期。</summary>
        public const string BuildDate = "2026-10-12";

        /// <summary>作者 / 开发者。留空则不显示该行。</summary>
        public const string Author = "悟空";

        /// <summary>QQ 号。留空则不显示该行。</summary>
        public const string QQ = "1972514296";

        /// <summary>QQ 群号。留空则不显示该行。</summary>
        public const string QQGroup = "617069563";

        /// <summary>联系方式（邮箱 / 电话 / 部门）。留空则不显示该行。</summary>
        public const string Contact = "";

        /// <summary>公司或组织名。留空则不显示该行。</summary>
        public const string Company = "";

        /// <summary>功能简介，可以写多行（\n 换行）。留空则不显示。</summary>
        public const string Description = "";
    }

    /// <summary>
    /// “关于”对话框。工具栏“关于”按钮弹出。
    /// 高度按内容动态计算，行数再多也不会显示不全。
    /// </summary>
    internal class AboutForm : Form
    {
        /// <summary>
        /// 当前界面主题。
        /// 以前这里所有颜色都是写死的（白底 + 系统灰字），切到"深黑灰"之后
        /// 关于窗口仍然是白底黑字，跟面板整体配色完全脱节。现在由调用方
        /// 把面板当前主题传进来，底色 / 正文 / 灰字 / 按钮全部跟着走。
        /// null 时回退默认主题，保证任何调用点都不会出现空引用白屏。
        /// </summary>
        private readonly UiTheme _theme;

        public AboutForm() : this(null) { }

        /// <param name="theme">面板当前主题；传 null 表示用默认主题（与旧版观感一致）。</param>
        public AboutForm(UiTheme theme)
        {
            _theme = theme ?? UiThemes.Default;
            InitializeForm();
        }

        private void InitializeForm()
        {
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            // 内容区底色跟随主题：默认主题 = SystemColors.Window（仍是白底，与旧版一致），
            // 深黑灰主题 = 石墨黑，清新蓝 / 薄荷绿 / 暖阳橙也各自跟随。
            BackColor = _theme.ListBack;
            Font = SystemFonts.MessageBoxFont;
            Text = "关于";

            const int Width = 480;      // 内容区宽度
            const int Margin = 26;      // 左右留白
            const int LineHeight = 24;  // 单行文本行高（实测行高与它取大者，保证不压字）
            int y = 24;

            // 先把所有控件的 y 坐标算出来，最后再定窗口高度
            // 产品名：居中显示、不加粗、字号 16 × 0.8 = 12.8；
            // 高度按实际字体测量（任何 DPI 下都不截字）
            Label name = new Label();
            name.Text = AboutInfo.ProductName;
            name.Font = new Font(Font.FontFamily, 12.8f, FontStyle.Regular);
            name.AutoSize = false;
            name.Location = new Point(Margin - 2, y);
            name.Size = new Size(Width - Margin * 2 + 4,
                TextRenderer.MeasureText(AboutInfo.ProductName, name.Font).Height + 10);
            name.TextAlign = ContentAlignment.MiddleCenter;
            name.ForeColor = _theme.BodyText;
            Controls.Add(name);
            y += name.Height + 8;

            if (!string.IsNullOrEmpty(AboutInfo.SubTitle))
            {
                Label sub = new Label();
                sub.Text = AboutInfo.SubTitle;
                sub.ForeColor = _theme.HintText;
                sub.AutoSize = true;
                sub.Location = new Point(Margin, y);
                Controls.Add(sub);
                y += sub.Height + 8;
            }

            y += 8;
            y = AddLine(Controls, y, Margin, LineHeight, "版本：" + AboutInfo.Version);
#if BLM_ZWCAD
            y = AddLine(Controls, y, Margin, LineHeight, "适用：中望 CAD（ZWCAD）");
#elif BLM_GSTARCAD
            y = AddLine(Controls, y, Margin, LineHeight, "适用：浩辰 CAD（GstarCAD）");
#elif BLM_BRICSCAD
            y = AddLine(Controls, y, Margin, LineHeight, "适用：BricsCAD V26 / V27");
#else
            y = AddLine(Controls, y, Margin, LineHeight, "适用：AutoCAD 2010 – 2027");
#endif

            if (!string.IsNullOrEmpty(AboutInfo.Author))
                y = AddLine(Controls, y, Margin, LineHeight, "作者：" + AboutInfo.Author);
            y = AddLine(Controls, y, Margin, LineHeight, "发布日期：" + AboutInfo.BuildDate);
            if (!string.IsNullOrEmpty(AboutInfo.QQ))
                y = AddLine(Controls, y, Margin, LineHeight, "QQ：" + AboutInfo.QQ);
            if (!string.IsNullOrEmpty(AboutInfo.QQGroup))
                y = AddLine(Controls, y, Margin, LineHeight, "QQ群：" + AboutInfo.QQGroup);
            if (!string.IsNullOrEmpty(AboutInfo.Company))
                y = AddLine(Controls, y, Margin, LineHeight, "单位：" + AboutInfo.Company);
            if (!string.IsNullOrEmpty(AboutInfo.Contact))
                y = AddLine(Controls, y, Margin, LineHeight, "联系：" + AboutInfo.Contact);

            if (!string.IsNullOrEmpty(AboutInfo.Description))
            {
                y += 10;

                Label desc = new Label();
                desc.Text = AboutInfo.Description;
                desc.Location = new Point(Margin, y);
                desc.ForeColor = _theme.BodyText;

                // 行数 × 行高（行高按字体实测，避免不同 DPI 下截字）
                int lines = 1;
                foreach (char c in AboutInfo.Description) if (c == '\n') lines++;
                desc.Height = LineHeight * lines + 4;
                desc.Width = Width - Margin * 2;
                Controls.Add(desc);
                y += desc.Height;
            }

            // 最终高度 = 内容 + 按钮区，宽度固定，保证不截内容
            int height = y + 16 + 30 + 18;
            ClientSize = new Size(Width, height);

            Button ok = new Button();
            ok.Text = "确定";
            ok.DialogResult = DialogResult.OK;
            ok.Size = new Size(96, 30);
            ok.Location = new Point(Width - 96 - Margin, height - 30 - 14);

            // 标准按钮的背景是系统色，深色主题下会是一块亮灰"补丁"。
            // 改成扁平按钮，底色 / 文字 / 描边全部取主题色（默认主题下与系统按钮观感一致）。
            ok.FlatStyle = FlatStyle.Flat;
            ok.BackColor = _theme.ChromeBack;
            ok.ForeColor = _theme.ChromeText;
            ok.FlatAppearance.BorderColor = _theme.Border;
            ok.FlatAppearance.BorderSize = 1;

            Controls.Add(ok);
            AcceptButton = ok;
            CancelButton = ok;
        }

        /// <summary>加一行文本，返回新的 y。行进按实测行高再留 6px 空隙，
        /// 任何 DPI / 字号下上下行都不会互相压字。</summary>
        private int AddLine(Control.ControlCollection controls, int y, int x, int lineHeight, string text)
        {
            if (string.IsNullOrEmpty(text)) return y;

            Label line = new Label();
            line.Text = text;
            line.AutoSize = true;
            line.Location = new Point(x, y);
            line.ForeColor = _theme.BodyText;
            controls.Add(line);
            return y + Math.Max(line.Height, lineHeight) + 6;
        }
    }
}
