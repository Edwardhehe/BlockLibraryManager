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
        public const string Version = "1.36.0";

        /// <summary>发布日期。</summary>
        public const string BuildDate = "2026-10-10";

        /// <summary>作者 / 开发者。留空则不显示该行。</summary>
        public const string Author = "悟空";

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
        public AboutForm()
        {
            InitializeForm();
        }

        private void InitializeForm()
        {
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.White;
            Font = SystemFonts.MessageBoxFont;
            Text = "关于";

            const int Width = 480;      // 内容区宽度
            const int Margin = 26;      // 左右留白
            const int LineHeight = 20;  // 单行文本行高
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
            Controls.Add(name);
            y += name.Height + 8;

            if (!string.IsNullOrEmpty(AboutInfo.SubTitle))
            {
                Label sub = new Label();
                sub.Text = AboutInfo.SubTitle;
                sub.ForeColor = SystemColors.GrayText;
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
#else
            y = AddLine(Controls, y, Margin, LineHeight, "适用：AutoCAD 2010 – 2027");
#endif

            if (!string.IsNullOrEmpty(AboutInfo.Author))
                y = AddLine(Controls, y, Margin, LineHeight, "作者：" + AboutInfo.Author);
            y = AddLine(Controls, y, Margin, LineHeight, "发布日期：" + AboutInfo.BuildDate);
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
                desc.ForeColor = SystemColors.ControlText;

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
            Controls.Add(ok);
            AcceptButton = ok;
            CancelButton = ok;
        }

        /// <summary>加一行文本，返回新的 y。</summary>
        private static int AddLine(Control.ControlCollection controls, int y, int x, int lineHeight, string text)
        {
            if (string.IsNullOrEmpty(text)) return y;

            Label line = new Label();
            line.Text = text;
            line.AutoSize = true;
            line.Location = new Point(x, y);
            line.ForeColor = SystemColors.ControlText;
            controls.Add(line);
            return y + Math.Max(line.Height, lineHeight) + 2;
        }
    }
}
