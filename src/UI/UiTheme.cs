using System;
using System.Drawing;

namespace BlockLibraryManager
{
    /// <summary>
    /// 界面配色主题。默认主题完全复刻系统配色；
    /// 另外四套为清新明快的流行配色。点击工具栏"主题"按钮循环切换。
    /// </summary>
    internal sealed class UiTheme
    {
        /// <summary>主题显示名（中文）。</summary>
        public string Name;

        /// <summary>主题显示名（英文）。</summary>
        public string NameEn;

        /// <summary>工具栏 / 状态栏背景。</summary>
        public Color ChromeBack;

        /// <summary>工具栏 / 状态栏文字。</summary>
        public Color ChromeText;

        /// <summary>"目录列表" / "插入参数设置"标题栏背景。</summary>
        public Color HeaderBack;

        /// <summary>标题栏文字。</summary>
        public Color HeaderText;

        /// <summary>参数面板 / 底部行等面板背景。</summary>
        public Color PanelBack;

        /// <summary>目录树 / 缩略图列表背景。</summary>
        public Color ListBack;

        /// <summary>正文标签文字。</summary>
        public Color BodyText;

        /// <summary>灰色提示文字。</summary>
        public Color HintText;

        /// <summary>强调色（预留）。</summary>
        public Color Accent;
    }

    internal static class UiThemes
    {
        /// <summary>默认：跟随系统配色（与旧版完全一致）。</summary>
        public static readonly UiTheme Default = new UiTheme
        {
            Name = "默认",
            NameEn = "Default",
            ChromeBack = SystemColors.Control,
            ChromeText = SystemColors.ControlText,
            HeaderBack = SystemColors.Control,
            HeaderText = SystemColors.ControlText,
            PanelBack = SystemColors.Control,
            ListBack = SystemColors.Window,
            BodyText = SystemColors.ControlText,
            HintText = Color.DimGray,
            Accent = Color.FromArgb(0, 120, 215)
        };

        /// <summary>清新蓝：浅蓝底 + 湖蓝标题，干净透亮。</summary>
        public static readonly UiTheme FreshBlue = new UiTheme
        {
            Name = "清新蓝",
            NameEn = "Fresh Blue",
            ChromeBack = Color.FromArgb(0xE3, 0xEE, 0xFB),
            ChromeText = Color.FromArgb(0x1F, 0x3B, 0x5C),
            HeaderBack = Color.FromArgb(0xC7, 0xDE, 0xF7),
            HeaderText = Color.FromArgb(0x17, 0x3A, 0x5E),
            PanelBack = Color.FromArgb(0xF4, 0xF9, 0xFF),
            ListBack = Color.FromArgb(0xE9, 0xF2, 0xFC),
            BodyText = Color.FromArgb(0x2A, 0x4A, 0x73),
            HintText = Color.FromArgb(0x5A, 0x7C, 0xA6),
            Accent = Color.FromArgb(0x2E, 0x7C, 0xD6)
        };

        /// <summary>薄荷绿：淡绿底 + 青绿标题，清凉护眼。</summary>
        public static readonly UiTheme Mint = new UiTheme
        {
            Name = "薄荷绿",
            NameEn = "Mint Green",
            ChromeBack = Color.FromArgb(0xE2, 0xF5, 0xEC),
            ChromeText = Color.FromArgb(0x14, 0x50, 0x3C),
            HeaderBack = Color.FromArgb(0xC6, 0xEB, 0xDC),
            HeaderText = Color.FromArgb(0x0F, 0x46, 0x34),
            PanelBack = Color.FromArgb(0xF3, 0xFB, 0xF7),
            ListBack = Color.FromArgb(0xE7, 0xF6, 0xEE),
            BodyText = Color.FromArgb(0x1D, 0x52, 0x40),
            HintText = Color.FromArgb(0x55, 0x87, 0x6F),
            Accent = Color.FromArgb(0x1F, 0xA9, 0x7C)
        };

        /// <summary>暖阳橙：米色底 + 暖橙标题，柔和温馨。</summary>
        public static readonly UiTheme WarmSun = new UiTheme
        {
            Name = "暖阳橙",
            NameEn = "Warm Sun",
            ChromeBack = Color.FromArgb(0xFB, 0xEF, 0xE2),
            ChromeText = Color.FromArgb(0x6B, 0x43, 0x22),
            HeaderBack = Color.FromArgb(0xF6, 0xE0, 0xC8),
            HeaderText = Color.FromArgb(0x5C, 0x3A, 0x1D),
            PanelBack = Color.FromArgb(0xFE, 0xF9, 0xF2),
            ListBack = Color.FromArgb(0xFD, 0xF3, 0xE7),
            BodyText = Color.FromArgb(0x5C, 0x3A, 0x1D),
            HintText = Color.FromArgb(0x9A, 0x7B, 0x58),
            Accent = Color.FromArgb(0xE8, 0x87, 0x3C)
        };

        /// <summary>浅绿：淡草绿底 + 嫩绿标题，比薄荷绿更淡雅清新。</summary>
        public static readonly UiTheme LightGreen = new UiTheme
        {
            Name = "浅绿",
            NameEn = "Light Green",
            ChromeBack = Color.FromArgb(0xEB, 0xF6, 0xE3),
            ChromeText = Color.FromArgb(0x33, 0x53, 0x1E),
            HeaderBack = Color.FromArgb(0xD5, 0xEC, 0xC6),
            HeaderText = Color.FromArgb(0x2C, 0x4A, 0x1A),
            PanelBack = Color.FromArgb(0xF7, 0xFC, 0xF2),
            ListBack = Color.FromArgb(0xF0, 0xF9, 0xEA),
            BodyText = Color.FromArgb(0x33, 0x53, 0x1E),
            HintText = Color.FromArgb(0x6E, 0x8A, 0x5A),
            Accent = Color.FromArgb(0x7C, 0xB3, 0x42)
        };

        /// <summary>全部主题（按切换顺序）。</summary>
        public static readonly UiTheme[] All = new UiTheme[] { Default, FreshBlue, Mint, LightGreen, WarmSun };

        /// <summary>按下标取主题，越界回退默认。</summary>
        public static UiTheme Get(int index)
        {
            if (index < 0 || index >= All.Length) index = 0;
            return All[index];
        }
    }
}
