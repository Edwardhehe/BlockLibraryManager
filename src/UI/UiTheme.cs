using System;
using System.Drawing;

namespace BlockLibraryManager
{
    /// <summary>
    /// 界面配色主题。默认主题完全复刻系统配色；
    /// 另有清新蓝、薄荷绿两种明快配色，以及跟随 CAD 深色界面的"深黑灰"。
    /// 点击工具栏"主题"按钮循环切换。
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

        /// <summary>描边色（搜索框等细边框）。浅色主题用深灰边，深色主题用中灰边。</summary>
        public Color Border;
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
            Accent = Color.FromArgb(0, 120, 215),
            Border = Color.FromArgb(0xAD, 0xB2, 0xBD)
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
            Accent = Color.FromArgb(0x2E, 0x7C, 0xD6),
            Border = Color.FromArgb(0x9B, 0xC0, 0xE6)
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
            Accent = Color.FromArgb(0x1F, 0xA9, 0x7C),
            Border = Color.FromArgb(0x9D, 0xCD, 0xB9)
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
            Accent = Color.FromArgb(0xE8, 0x87, 0x3C),
            Border = Color.FromArgb(0xD9, 0xBE, 0x9E)
        };

        /// <summary>
        /// 深黑灰：跟随 CAD 自身的深色界面（AutoCAD / 中望 / 浩辰 / BricsCAD 暗色主题的
        /// 通用观感）——工具栏中深灰、标题栏略亮一档、图块列表用最深的石墨黑，
        /// 文字与描边整体反转成亮灰，长时间看图不刺眼。
        /// </summary>
        public static readonly UiTheme DarkGray = new UiTheme
        {
            Name = "深黑灰",
            NameEn = "Dark Gray",
            ChromeBack = Color.FromArgb(0x2B, 0x2B, 0x2B),   // 工具栏 / 状态栏
            ChromeText = Color.FromArgb(0xE8, 0xE8, 0xE8),
            HeaderBack = Color.FromArgb(0x3C, 0x3C, 0x3C),   // 两个标题栏，比底色亮一档
            HeaderText = Color.FromArgb(0xF2, 0xF2, 0xF2),
            PanelBack = Color.FromArgb(0x33, 0x33, 0x33),    // 参数面板 / 底部行
            ListBack = Color.FromArgb(0x1F, 0x1F, 0x1F),     // 目录树 / 缩略图列表，最深的石墨黑
            BodyText = Color.FromArgb(0xD8, 0xD8, 0xD8),
            HintText = Color.FromArgb(0x90, 0x90, 0x90),
            Accent = Color.FromArgb(0x4A, 0x9F, 0xE0),
            Border = Color.FromArgb(0x5C, 0x5C, 0x5C)
        };

        /// <summary>全部主题（按切换顺序）。</summary>
        public static readonly UiTheme[] All = new UiTheme[] { Default, FreshBlue, Mint, DarkGray, WarmSun };

        /// <summary>按下标取主题，越界回退默认。</summary>
        public static UiTheme Get(int index)
        {
            if (index < 0 || index >= All.Length) index = 0;
            return All[index];
        }
    }
}
