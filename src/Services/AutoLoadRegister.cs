using System;
#if BLM_ZWCAD || BLM_GSTARCAD
using System.Globalization;
using Microsoft.Win32;
#endif

namespace BlockLibraryManager
{
    /// <summary>
    /// 把插件登记进中望 / 浩辰的"随 CAD 启动自动加载"注册表。
    ///
    /// 背景：AutoCAD 有 ApplicationPlugins bundle（PackageContents.xml）自动加载；
    /// 中望 / 浩辰对 bundle 目录的支持参差不齐，两家真正稳定、官方文档明载的
    /// 机制都是 AutoCAD 式的 HKCU 注册表 Applications 键：
    ///
    ///   中望（官方知识库）：
    ///     HKCU\Software\ZWSOFT\ZWCAD[变体]\&lt;版本&gt;\&lt;语言&gt;\Applications\&lt;名称&gt;
    ///     值：LOADER(字符串，DLL 全路径)、MANAGED(DWORD=1)、
    ///         LOADCTRLS(DWORD，0x02=随启动加载)、DESCRIPTION(字符串)
    ///
    ///   浩辰（官方 2024 自动加载指南）：
    ///     HKCU\SOFTWARE\Gstarsoft\GstarCAD\R&lt;xx&gt;\&lt;语言&gt;\Applications\&lt;名称&gt;
    ///     值：Managed(DWORD=1 表示 .NET 程序)、Loader(字符串，DLL 全路径)
    ///
    /// 用户第一次手动加载本插件后，Initialize 里在这里登记一次；
    /// 之后每次启动 CAD 自动加载，不再需要 netload / APPLOAD。
    ///
    /// 注意事项：
    /// 1. 全部写在 HKEY_CURRENT_USER——不需要管理员权限，也不会影响其它用户；
    /// 2. 版本 / 语言子键一律枚举现有键，不猜具体名字（各家命名不一：
    ///    中望有 ZWCAD / ZWCADM / ZWCADA 等变体，版本层下可能是语言名也可能是
    ///    "acad" 这类目录名，统一按"能建就建"处理）；
    /// 3. 已登记且 LOADER 路径没变时不重写，避免每次启动都碰注册表；
    /// 4. 任何失败只记日志——注册失败只是"重启后要重新手动加载一次"，
    ///    绝不能反过来影响插件本体的加载与运行。
    /// </summary>
    internal static class AutoLoadRegister
    {
        private const string AppName = "BlockLibraryManager";
        private const string Description = "图库管理器（随 CAD 启动自动加载）";

#if BLM_ZWCAD || BLM_GSTARCAD

        /// <summary>登记自身。在插件 Initialize 时调用。</summary>
        internal static void EnsureRegistered()
        {
            try
            {
                string loader = null;
                try { loader = System.Reflection.Assembly.GetExecutingAssembly().Location; }
                catch { }

                if (string.IsNullOrEmpty(loader) || !System.IO.File.Exists(loader)) return;

#if BLM_GSTARCAD
                RegisterGstarCAD(loader);
#else
                RegisterZWCAD(loader);
#endif
            }
            catch (System.Exception ex)
            {
                BlmLog.Write("登记随启动自动加载失败（不影响插件使用）", ex);
            }
        }

        // ---------------- 浩辰 GstarCAD ----------------

        private static void RegisterGstarCAD(string loader)
        {
            using (RegistryKey root = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Gstarsoft\GstarCAD", true))
            {
                if (root == null) return;

                foreach (string ver in root.GetSubKeyNames())
                {
                    using (RegistryKey verKey = root.OpenSubKey(ver, true))
                    {
                        if (verKey == null) continue;

                        // 官方结构：R24\<语言>\Applications。版本层下没有子键时，
                        // 退而求其次直接把 Applications 建在版本层。
                        string[] langs = verKey.GetSubKeyNames();
                        if (langs == null || langs.Length == 0)
                        {
                            WriteGstarApp(verKey, loader);
                            continue;
                        }

                        foreach (string lang in langs)
                        {
                            using (RegistryKey langKey = verKey.OpenSubKey(lang, true))
                            {
                                if (langKey == null) continue;
                                WriteGstarApp(langKey, loader);
                            }
                        }
                    }
                }
            }
        }

        private static void WriteGstarApp(RegistryKey langKey, string loader)
        {
            using (RegistryKey app = langKey.CreateSubKey("Applications\\" + AppName))
            {
                if (app == null) return;

                // 官方指南要求的两个值（Managed=1 表示 .NET 程序）
                if (GetStr(app, "Loader") != loader) app.SetValue("Loader", loader);
                if (GetInt(app, "Managed") != 1) app.SetValue("Managed", 1, RegistryValueKind.DWord);

                // AutoCAD 风格的同义键一并补齐：浩辰兼容 AutoCAD 体系，
                // 多写无害，且老版本若只认大写键名也能生效
                if (GetStr(app, "LOADER") != loader) app.SetValue("LOADER", loader);
                if (GetInt(app, "MANAGED") != 1) app.SetValue("MANAGED", 1, RegistryValueKind.DWord);
                if (GetInt(app, "LOADCTRLS") != 2) app.SetValue("LOADCTRLS", 2, RegistryValueKind.DWord);
                if (GetStr(app, "DESCRIPTION") != Description) app.SetValue("DESCRIPTION", Description);
            }
        }

        // ---------------- 中望 ZWCAD ----------------

        private static void RegisterZWCAD(string loader)
        {
            using (RegistryKey root = Registry.CurrentUser.OpenSubKey(@"Software\ZWSOFT", true))
            {
                if (root == null) return;

                foreach (string product in root.GetSubKeyNames())
                {
                    // 标准版 ZWCAD、机械版 ZWCADM、建筑版 ZWCADA……结构一致
                    if (product.IndexOf("ZWCAD", StringComparison.OrdinalIgnoreCase) != 0) continue;

                    using (RegistryKey prodKey = root.OpenSubKey(product, true))
                    {
                        if (prodKey == null) continue;

                        foreach (string ver in prodKey.GetSubKeyNames())
                        {
                            using (RegistryKey verKey = prodKey.OpenSubKey(ver, true))
                            {
                                if (verKey == null) continue;

                                // 官方结构：<版本>\<语言>\Applications（语言层在部分
                                // 版本上叫 zh-CN / en-US，也有叫 acad 的，都照建）
                                string[] langs = verKey.GetSubKeyNames();
                                if (langs == null || langs.Length == 0)
                                {
                                    WriteZwApp(verKey, loader);
                                    continue;
                                }

                                foreach (string lang in langs)
                                {
                                    using (RegistryKey langKey = verKey.OpenSubKey(lang, true))
                                    {
                                        if (langKey == null) continue;
                                        WriteZwApp(langKey, loader);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        private static void WriteZwApp(RegistryKey langKey, string loader)
        {
            using (RegistryKey app = langKey.CreateSubKey("Applications\\" + AppName))
            {
                if (app == null) return;

                if (GetStr(app, "LOADER") != loader) app.SetValue("LOADER", loader);
                if (GetInt(app, "MANAGED") != 1) app.SetValue("MANAGED", 1, RegistryValueKind.DWord);
                if (GetInt(app, "LOADCTRLS") != 2) app.SetValue("LOADCTRLS", 2, RegistryValueKind.DWord);
                if (GetStr(app, "DESCRIPTION") != Description) app.SetValue("DESCRIPTION", Description);
            }
        }

        // ---------------- 安全读取 ----------------

        private static string GetStr(RegistryKey key, string name)
        {
            try
            {
                object v = key.GetValue(name);
                return v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
            }
            catch { return null; }
        }

        private static int? GetInt(RegistryKey key, string name)
        {
            try
            {
                object v = key.GetValue(name);
                return v is int ? (int)v : (int?)null;
            }
            catch { return null; }
        }

#else
        // AutoCAD / BricsCAD 不需要：前者走 ApplicationPlugins bundle 自动加载，
        // 后者用户未要求（其自加载机制与本实现不同）。
        internal static void EnsureRegistered()
        {
        }
#endif
    }
}
