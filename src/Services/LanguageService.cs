using System;
using System.Globalization;

namespace BlockLibraryManager
{
    /// <summary>
    /// 界面语言服务。
    /// 默认跟随系统语言（zh 开头用中文，其余用英文）；
    /// 用户可在"语言"对话框里固定为简体中文或 English，选择持久化到 config.json。
    /// 用法：L.T("中文文案", "English text")。
    /// </summary>
    public static class LanguageService
    {
        /// <summary>跟随系统语言（配置里保存为空串）。</summary>
        public const string FollowSystem = "";

        /// <summary>简体中文。</summary>
        public const string Chinese = "zh-CN";

        /// <summary>English。</summary>
        public const string English = "en-US";

        /// <summary>配置里保存的语言设置（空 = 跟随系统）。启动时由 LibraryControl 赋值。</summary>
        public static string ConfiguredLanguage = string.Empty;

        /// <summary>当前实际生效的语言代码。</summary>
        public static string Current
        {
            get
            {
                if (ConfiguredLanguage == Chinese) return Chinese;
                if (ConfiguredLanguage == English) return English;

                // 跟随系统：zh 开头（含 zh-TW / zh-HK）用中文，其余用英文
                try
                {
                    string ui = CultureInfo.CurrentUICulture.Name;
                    if (!string.IsNullOrEmpty(ui) &&
                        ui.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                        return Chinese;
                }
                catch
                {
                    // 取不到系统语言时用中文兜底
                    return Chinese;
                }

                return English;
            }
        }

        /// <summary>当前是否为英文界面。</summary>
        public static bool IsEnglish
        {
            get { return Current == English; }
        }

        /// <summary>按当前语言取文案。</summary>
        public static string T(string chinese, string english)
        {
            return IsEnglish ? english : chinese;
        }
    }

    /// <summary>LanguageService 的简写别名，调用处写 L.T(...)。</summary>
    public static class L
    {
        public static string T(string chinese, string english)
        {
            return LanguageService.T(chinese, english);
        }
    }
}
