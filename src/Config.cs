using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace BlockLibraryManager
{
    /// <summary>
    /// 图库配置。存放位置：**插件 DLL 所在目录**下的 BlockLibrary.ini（INI 格式）。
    /// 例如 DLL 在 ...\ApplicationPlugins\BlockLibraryManager.bundle\Contents\CAD2019-2024\，
    /// 配置文件就在同一个文件夹里，方便整包拷贝 / 备份。
    /// DLL 目录不可写（装在受保护目录）时自动回退到 %APPDATA%\BlockLibraryManager\BlockLibrary.ini。
    ///
    /// 这里刻意不用 DataContractJsonSerializer / System.Text.Json，
    /// 因为它们在 net35(CAD2010) 与 net8(CAD2025+) 之间的可用性不一致；
    /// 手写的 INI 读写反而是全版本通用、且能直接用记事本改的最小方案。
    /// </summary>
    public sealed class LibraryConfig
    {
        /// <summary>图库根目录。支持多个（例如公司服务器图库 + 本机常用图库）。</summary>
        public StringList RootPaths { get; private set; }

        /// <summary>最近使用的图块文件路径，最多保留 RecentLimit 条。</summary>
        public StringList RecentFiles { get; private set; }

        /// <summary>上次选中的分类目录，下次打开面板时自动定位。</summary>
        public string LastCategoryPath { get; set; }

        /// <summary>缩略图边长（像素）。建议 96~160。</summary>
        public int ThumbSize { get; set; }

        /// <summary>右侧列表显示方式。</summary>
        public GalleryView ViewMode { get; set; }

        /// <summary>面板初始宽度（停靠后可自行拖动）。</summary>
        public int PaletteWidth { get; set; }

        /// <summary>是否显示每个 DWG 内部的块定义（关闭则每个 DWG 只算一个块）。</summary>
        public bool ExpandInnerBlocks { get; set; }

        /// <summary>搜索时是否包含下级分类。</summary>
        public bool SearchSubCategories { get; set; }

        /// <summary>
        /// 插入参数面板的"插入图形后关闭窗口"勾选状态（持久化）。
        /// 勾选后每次插入成功自动收起图库管理器窗口。
        /// </summary>
        public bool InsertCloseAfter { get; set; }

        /// <summary>图块文件扩展名过滤，大小写不敏感。</summary>
        public StringList FileFilters { get; private set; }

        /// <summary>
        /// 被用户隐藏的子目录（不参与扫描与显示）。
        /// 右键子目录"移除"时只把该子目录加进来，其所属的根目录保留。
        /// 在"设置"里可恢复显示。
        /// </summary>
        public StringList ExcludedPaths { get; private set; }

        /// <summary>
        /// 收藏的图库文件夹路径。
        /// 在目录列表的"收藏"根节点下显示，方便快速跳到常用目录。
        /// </summary>
        public StringList FavoritePaths { get; private set; }

        /// <summary>最近使用记录保留条数。</summary>
        public int RecentLimit { get; set; }

        // ---- 窗口位置记忆（无边框窗口模式）----

        /// <summary>窗口上次的 X 坐标。-1 表示从未保存过（用默认位置）。</summary>
        public int WindowLeft { get; set; }

        /// <summary>窗口上次的 Y 坐标。-1 表示从未保存过。</summary>
        public int WindowTop { get; set; }

        /// <summary>窗口上次的宽度。-1 表示从未保存过。</summary>
        public int WindowWidth { get; set; }

        /// <summary>窗口上次的高度。-1 表示从未保存过。</summary>
        public int WindowHeight { get; set; }

        /// <summary>左侧目录列表上次的宽度（SplitterDistance）。0 表示从未保存过。</summary>
        public int TreePanelWidth { get; set; }

        /// <summary>
        /// 左侧上下分割（目录树 / 插入参数面板）上次的分割位置
        /// （目录树区域高度）。0 表示从未保存过。
        /// </summary>
        public int LeftSplitDistance { get; set; }

        /// <summary>
        /// 界面语言。空 = 跟随系统语言（默认）；
        /// "zh-CN" = 简体中文；"en-US" = English。
        /// </summary>
        public string Language { get; set; }

        /// <summary>
        /// 界面配色主题下标（对应 UiThemes.All）。
        /// 0 = 默认（跟随系统），1 = 清新蓝，2 = 薄荷绿，3 = 深黑灰，4 = 暖阳橙。
        /// </summary>
        public int Theme { get; set; }

        /// <summary>
        /// 缩略图底色模式："white" = 白底（默认），"black" = 黑底。
        /// 切换后所有缩略图按所选底色重新生成。
        /// </summary>
        public string ThumbBackground { get; set; }

        /// <summary>
        /// 启动插件的自定义命令名（设置里可改）。空 / "BLOCKLIB" = 使用默认命令 BLOCKLIB。
        /// 自定义命令通过 UnknownCommand 事件实现，修改后下次启动 CAD 时生效。
        /// </summary>
        public string LaunchCommand { get; set; }

        public LibraryConfig()
        {
            RootPaths = new StringList();
            RecentFiles = new StringList();
            FileFilters = new StringList();
            ExcludedPaths = new StringList();
            FavoritePaths = new StringList();
            LastCategoryPath = string.Empty;
            ThumbSize = 120;
            ViewMode = GalleryView.LargeIcon;
            PaletteWidth = 320;
            ExpandInnerBlocks = false;
            SearchSubCategories = true;
            InsertCloseAfter = false;
            RecentLimit = 20;
            WindowLeft = -1;
            WindowTop = -1;
            WindowWidth = -1;
            WindowHeight = -1;
            TreePanelWidth = 0;
            LeftSplitDistance = 0;
            Language = string.Empty;
            Theme = 0;
            ThumbBackground = "white";
            LaunchCommand = "BLOCKLIB";

            FileFilters.Add("*.dwg");
            FileFilters.Add("*.dwt");
        }

        /// <summary>默认的“我的图库”目录。首次运行时自动创建。</summary>
        public static string DefaultRootPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "AutoCAD BlockLibrary");
            }
        }

        /// <summary>配置文件名（INI 格式，与 DLL 同目录）。</summary>
        private const string ConfigFileName = "BlockLibrary.ini";

        /// <summary>本插件 DLL 所在目录。取不到时返回空串。</summary>
        public static string AssemblyDirectory
        {
            get
            {
                try
                {
                    string location = Assembly.GetExecutingAssembly().Location;
                    if (string.IsNullOrEmpty(location)) return string.Empty;

                    string dir = Path.GetDirectoryName(location);
                    return dir == null ? string.Empty : dir;
                }
                catch
                {
                    return string.Empty;
                }
            }
        }

        /// <summary>回退目录：DLL 所在目录不可写时用。</summary>
        private static string AppDataFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlockLibraryManager"); }
        }

        /// <summary>首选配置路径：DLL 同目录的 BlockLibrary.ini。</summary>
        public static string ConfigFilePath
        {
            get
            {
                string dir = AssemblyDirectory;
                if (string.IsNullOrEmpty(dir)) return Path.Combine(AppDataFolder, ConfigFileName);
                return Path.Combine(dir, ConfigFileName);
            }
        }

        /// <summary>回退配置路径（DLL 目录不可写时用）。</summary>
        private static string FallbackConfigPath
        {
            get { return Path.Combine(AppDataFolder, ConfigFileName); }
        }

        /// <summary>旧版配置路径，仅用于首次启动迁移历史设置。</summary>
        private static string LegacyConfigPath
        {
            get { return Path.Combine(AppDataFolder, "config.json"); }
        }

        public static LibraryConfig Load()
        {
            LibraryConfig config = new LibraryConfig();
            try
            {
                string path = ConfigFilePath;
                bool hasIni = File.Exists(path);

                if (!hasIni)
                {
                    string fallback = FallbackConfigPath;
                    if (File.Exists(fallback)) { path = fallback; hasIni = true; }
                }

                if (!hasIni)
                {
                    // 第一次用新版：如果旧版 config.json 还在，把它迁过来，别让用户重新配一遍
                    if (File.Exists(LegacyConfigPath))
                    {
                        LoadLegacyJson(config);
                        config.Save();
                        return config;
                    }

                    // 首次运行：给一个默认的“我的图库”目录并落盘
                    config.RootPaths.Add(DefaultRootPath);
                    try { if (!Directory.Exists(DefaultRootPath)) Directory.CreateDirectory(DefaultRootPath); } catch { }
                    config.Save();
                    return config;
                }

                string text = File.ReadAllText(path, Encoding.UTF8);
                Dictionary<string, List<KeyValuePair<string, string>>> ini = ParseIni(text);

                config.LastCategoryPath = GetIniString(ini, "General", "LastCategoryPath", string.Empty);
                config.ThumbSize = GetIniInt(ini, "General", "ThumbSize", 120);
                config.ViewMode = (GalleryView)GetIniInt(ini, "General", "ViewMode", 0);
                config.PaletteWidth = GetIniInt(ini, "General", "PaletteWidth", 320);
                config.ExpandInnerBlocks = GetIniBool(ini, "General", "ExpandInnerBlocks", false);
                config.SearchSubCategories = GetIniBool(ini, "General", "SearchSubCategories", true);
                config.InsertCloseAfter = GetIniBool(ini, "General", "InsertCloseAfter", false);
                config.RecentLimit = GetIniInt(ini, "General", "RecentLimit", 20);
                config.WindowLeft = GetIniInt(ini, "General", "WindowLeft", -1);
                config.WindowTop = GetIniInt(ini, "General", "WindowTop", -1);
                config.WindowWidth = GetIniInt(ini, "General", "WindowWidth", -1);
                config.WindowHeight = GetIniInt(ini, "General", "WindowHeight", -1);
                config.TreePanelWidth = GetIniInt(ini, "General", "TreePanelWidth", 0);
                config.LeftSplitDistance = GetIniInt(ini, "General", "LeftSplitDistance", 0);
                config.Language = GetIniString(ini, "General", "Language", string.Empty);
                config.Theme = GetIniInt(ini, "General", "Theme", 0);
                config.ThumbBackground = GetIniString(ini, "General", "ThumbBackground", "white");
                config.LaunchCommand = GetIniString(ini, "General", "LaunchCommand", "BLOCKLIB");

                // 构造函数里已有默认值，这里必须先清空再填充，否则会重复叠加
                config.RootPaths.Clear();
                config.RecentFiles.Clear();
                config.FileFilters.Clear();
                config.ExcludedPaths.Clear();
                config.FavoritePaths.Clear();

                GetIniList(ini, "RootPaths", config.RootPaths);
                GetIniList(ini, "RecentFiles", config.RecentFiles);
                GetIniList(ini, "FileFilters", config.FileFilters);
                GetIniList(ini, "ExcludedPaths", config.ExcludedPaths);
                GetIniList(ini, "FavoritePaths", config.FavoritePaths);

                if (config.RootPaths.Count == 0)
                    config.RootPaths.Add(DefaultRootPath);
                if (config.FileFilters.Count == 0)
                {
                    config.FileFilters.Add("*.dwg");
                    config.FileFilters.Add("*.dwt");
                }
                // 上界取主题表实际长度，以后新增配色不用再回来改这个数字
                int maxTheme = UiThemes.All.Length - 1;
                if (config.Theme < 0) config.Theme = 0;
                if (config.Theme > maxTheme) config.Theme = maxTheme;
                // 大小写不敏感：手填 "Black" 以前会被当成非法值静默改回 white
                config.ThumbBackground = string.Equals(config.ThumbBackground, "black",
                    StringComparison.OrdinalIgnoreCase) ? "black" : "white";

                if (string.IsNullOrEmpty(config.LaunchCommand)) config.LaunchCommand = "BLOCKLIB";
                if (config.ThumbSize < 48) config.ThumbSize = 48;
                if (config.ThumbSize > 256) config.ThumbSize = 256;
                if (config.PaletteWidth < 200) config.PaletteWidth = 200;

                // 上界必须夹住：设置窗体的数字框上限就是 100，INI 里写 500 会让
                // "设置"一打开就抛 ArgumentOutOfRangeException，一路冒到 AutoCAD。
                if (config.RecentLimit < 0) config.RecentLimit = 0;
                if (config.RecentLimit > 100) config.RecentLimit = 100;
                if (config.TreePanelWidth < 0) config.TreePanelWidth = 0;
                if (config.LeftSplitDistance < 0) config.LeftSplitDistance = 0;
            }
            catch
            {
                // 配置损坏时退回默认值，不能因为配置问题导致插件加载失败
            }
            return config;
        }

        /// <summary>读取旧版 %APPDATA%\config.json，把历史设置搬过来（只在新 ini 不存在时调用）。</summary>
        private static void LoadLegacyJson(LibraryConfig config)
        {
            try
            {
                string json = File.ReadAllText(LegacyConfigPath, Encoding.UTF8);
                MiniJson root = MiniJson.ParseObject(json);
                if (root == null) return;

                config.LastCategoryPath = MiniJson.GetString(root, "LastCategoryPath", string.Empty);
                config.ThumbSize = MiniJson.GetInt(root, "ThumbSize", 120);
                config.ViewMode = (GalleryView)MiniJson.GetInt(root, "ViewMode", 0);
                config.PaletteWidth = MiniJson.GetInt(root, "PaletteWidth", 320);
                config.ExpandInnerBlocks = MiniJson.GetBool(root, "ExpandInnerBlocks", false);
                config.SearchSubCategories = MiniJson.GetBool(root, "SearchSubCategories", true);
                config.RecentLimit = MiniJson.GetInt(root, "RecentLimit", 20);
                config.WindowLeft = MiniJson.GetInt(root, "WindowLeft", -1);
                config.WindowTop = MiniJson.GetInt(root, "WindowTop", -1);
                config.WindowWidth = MiniJson.GetInt(root, "WindowWidth", -1);
                config.WindowHeight = MiniJson.GetInt(root, "WindowHeight", -1);
                config.TreePanelWidth = MiniJson.GetInt(root, "TreePanelWidth", 0);
                config.LeftSplitDistance = MiniJson.GetInt(root, "LeftSplitDistance", 0);
                config.Language = MiniJson.GetString(root, "Language", string.Empty);
                config.Theme = MiniJson.GetInt(root, "Theme", 0);
                config.ThumbBackground = MiniJson.GetString(root, "ThumbBackground", "white");
                config.LaunchCommand = MiniJson.GetString(root, "LaunchCommand", "BLOCKLIB");

                config.RootPaths.Clear();
                config.RecentFiles.Clear();
                config.FileFilters.Clear();
                config.ExcludedPaths.Clear();
                config.FavoritePaths.Clear();

                MiniJson.GetStringList(root, "RootPaths", config.RootPaths);
                MiniJson.GetStringList(root, "RecentFiles", config.RecentFiles);
                MiniJson.GetStringList(root, "FileFilters", config.FileFilters);
                MiniJson.GetStringList(root, "ExcludedPaths", config.ExcludedPaths);
                MiniJson.GetStringList(root, "FavoritePaths", config.FavoritePaths);
            }
            catch
            {
                // 迁移失败就用默认值，不影响启动
            }
        }

        // ---------------- INI 读写 ----------------

        /// <summary>
        /// 解析 INI 文本。返回 分区名 → (键, 值) 列表，分区名与键名均不区分大小写。
        /// 支持 ; 与 # 开头的注释行；列表型分区靠重复键表达（顺序保留）。
        /// </summary>
        private static Dictionary<string, List<KeyValuePair<string, string>>> ParseIni(string text)
        {
            Dictionary<string, List<KeyValuePair<string, string>>> data =
                new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrEmpty(text)) return data;

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            string section = "General";
            List<KeyValuePair<string, string>> bucket = null;

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line[0] == ';' || line[0] == '#') continue;

                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    if (section.Length == 0) section = "General";
                    bucket = null;
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                if (key.Length == 0) continue;

                if (bucket == null)
                {
                    if (!data.TryGetValue(section, out bucket))
                    {
                        bucket = new List<KeyValuePair<string, string>>();
                        data[section] = bucket;
                    }
                }

                bucket.Add(new KeyValuePair<string, string>(key, value));
            }

            return data;
        }

        private static string GetIniString(
            Dictionary<string, List<KeyValuePair<string, string>>> data,
            string section, string key, string defaultValue)
        {
            List<KeyValuePair<string, string>> bucket;
            if (data == null || !data.TryGetValue(section, out bucket)) return defaultValue;

            foreach (KeyValuePair<string, string> kv in bucket)
            {
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            }
            return defaultValue;
        }

        private static int GetIniInt(
            Dictionary<string, List<KeyValuePair<string, string>>> data,
            string section, string key, int defaultValue)
        {
            string text = GetIniString(data, section, key, null);
            if (string.IsNullOrEmpty(text)) return defaultValue;

            int value;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return value;

            return defaultValue;
        }

        private static bool GetIniBool(
            Dictionary<string, List<KeyValuePair<string, string>>> data,
            string section, string key, bool defaultValue)
        {
            string text = GetIniString(data, section, key, null);
            if (string.IsNullOrEmpty(text)) return defaultValue;

            if (string.Equals(text, "1", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(text, "0", StringComparison.OrdinalIgnoreCase)) return false;

            bool value;
            if (bool.TryParse(text, out value)) return value;

            return defaultValue;
        }

        /// <summary>取整个分区的所有值（列表型配置，按文件里的顺序）。</summary>
        private static void GetIniList(
            Dictionary<string, List<KeyValuePair<string, string>>> data,
            string section, StringList target)
        {
            if (target == null) return;

            List<KeyValuePair<string, string>> bucket;
            if (data == null || !data.TryGetValue(section, out bucket)) return;

            foreach (KeyValuePair<string, string> kv in bucket)
            {
                if (string.IsNullOrEmpty(kv.Value)) continue;
                target.Add(kv.Value);
            }
        }

        /// <summary>把列表型配置写成一个分区（重复的键）。</summary>
        private static void AppendIniList(StringBuilder sb, string section, string keyName, StringList items)
        {
            sb.Append('[').Append(section).Append("]\n");

            if (items != null)
            {
                foreach (string item in items)
                {
                    if (string.IsNullOrEmpty(item)) continue;
                    sb.Append(keyName).Append('=').Append(item).Append('\n');
                }
            }

            sb.Append('\n');
        }

        /// <summary>写一行 键=值。</summary>
        private static void AppendIniValue(StringBuilder sb, string key, string value)
        {
            sb.Append(key).Append('=').Append(value == null ? string.Empty : value).Append('\n');
        }

        public void Save()
        {
            try
            {
                int recentLimit = RecentLimit > 0 ? RecentLimit : 0;
                if (recentLimit > 0)
                {
                    while (RecentFiles.Count > recentLimit)
                        RecentFiles.RemoveAt(RecentFiles.Count - 1);
                }

                StringBuilder sb = new StringBuilder();
                sb.Append("; 图库管理器 BlockLibraryManager 配置文件（INI 格式）\n");
                sb.Append("; 与插件 DLL 同目录。修改前请先关闭 CAD —— 运行时插件会整体重写本文件。\n");
                sb.Append("; 列表型配置（RootPaths / FileFilters 等）靠重复的键表达，可自行增删行。\n\n");

                sb.Append("[General]\n");
                AppendIniValue(sb, "LastCategoryPath", LastCategoryPath);
                AppendIniValue(sb, "ThumbSize", ThumbSize.ToString(CultureInfo.InvariantCulture));
                AppendIniValue(sb, "ViewMode", ((int)ViewMode).ToString(CultureInfo.InvariantCulture));
                AppendIniValue(sb, "PaletteWidth", PaletteWidth.ToString(CultureInfo.InvariantCulture));
                AppendIniValue(sb, "ExpandInnerBlocks", ExpandInnerBlocks ? "true" : "false");
                AppendIniValue(sb, "SearchSubCategories", SearchSubCategories ? "true" : "false");
                AppendIniValue(sb, "InsertCloseAfter", InsertCloseAfter ? "true" : "false");
                AppendIniValue(sb, "RecentLimit", RecentLimit.ToString(CultureInfo.InvariantCulture));
                AppendIniValue(sb, "WindowLeft", WindowLeft.ToString(CultureInfo.InvariantCulture));
                AppendIniValue(sb, "WindowTop", WindowTop.ToString(CultureInfo.InvariantCulture));
                AppendIniValue(sb, "WindowWidth", WindowWidth.ToString(CultureInfo.InvariantCulture));
                AppendIniValue(sb, "WindowHeight", WindowHeight.ToString(CultureInfo.InvariantCulture));
                AppendIniValue(sb, "TreePanelWidth", TreePanelWidth.ToString(CultureInfo.InvariantCulture));
                AppendIniValue(sb, "LeftSplitDistance", LeftSplitDistance.ToString(CultureInfo.InvariantCulture));
                AppendIniValue(sb, "Language", Language == null ? string.Empty : Language);
                AppendIniValue(sb, "Theme", Theme.ToString(CultureInfo.InvariantCulture));
                AppendIniValue(sb, "ThumbBackground", ThumbBackground == null ? "white" : ThumbBackground);
                AppendIniValue(sb, "LaunchCommand", LaunchCommand == null ? "BLOCKLIB" : LaunchCommand);
                sb.Append('\n');

                AppendIniList(sb, "RootPaths", "Path", RootPaths);
                AppendIniList(sb, "FileFilters", "Filter", FileFilters);
                AppendIniList(sb, "RecentFiles", "File", RecentFiles);
                AppendIniList(sb, "ExcludedPaths", "Path", ExcludedPaths);
                AppendIniList(sb, "FavoritePaths", "Path", FavoritePaths);

                string content = sb.ToString();

                // 首选：写在 DLL 旁边；装在受保护目录（Program Files 等）写不进去时回退到 APPDATA
                try
                {
                    string dir = AssemblyDirectory;
                    if (string.IsNullOrEmpty(dir)) throw new IOException("无法定位插件目录");

                    File.WriteAllText(Path.Combine(dir, ConfigFileName), content, Encoding.UTF8);
                }
                catch (System.Exception primaryEx)
                {
                    // 首选位置写不进去（装在 Program Files 等受保护目录）时回退 APPDATA。
                    // 记一笔日志：否则"改了设置重启又变回去"这个问题无从查起。
                    BlmLog.Write("配置写入 DLL 目录失败，改用 %APPDATA%：" + primaryEx.Message);

                    string folder = AppDataFolder;
                    if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                    File.WriteAllText(FallbackConfigPath, content, Encoding.UTF8);
                }
            }
            catch (System.Exception ex)
            {
                // 配置写入失败不影响主流程，但要留痕
                BlmLog.Write("保存配置失败", ex);
            }
        }

        /// <summary>把最近使用的文件提到最前面。</summary>
        public void TouchRecent(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;

            for (int i = 0; i < RecentFiles.Count; i++)
            {
                if (string.Equals(RecentFiles[i], filePath, StringComparison.OrdinalIgnoreCase))
                {
                    RecentFiles.RemoveAt(i);
                    break;
                }
            }
            RecentFiles.Insert(0, filePath);

            // limit 为 0 表示"不限制"是错的——那样 RecentFiles 会无限增长并整份写回 INI。
            // 这里按默认 20 兜底，保证列表总有上界。
            int limit = RecentLimit > 0 ? RecentLimit : 20;
            while (RecentFiles.Count > limit)
                RecentFiles.RemoveAt(RecentFiles.Count - 1);
        }

        /// <summary>新增图库根目录，已存在则忽略。</summary>
        public void AddRoot(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            foreach (string existing in RootPaths)
            {
                if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)) return;
            }
            RootPaths.Add(path);
        }

        public void RemoveRoot(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            for (int i = RootPaths.Count - 1; i >= 0; i--)
            {
                if (string.Equals(RootPaths[i], path, StringComparison.OrdinalIgnoreCase))
                    RootPaths.RemoveAt(i);
            }
        }

        // ---------------- 收藏的图库文件夹 ----------------

        /// <summary>把文件夹加入收藏（已存在则忽略）。返回是否新增。</summary>
        public bool AddFavorite(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            foreach (string existing in FavoritePaths)
            {
                if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            FavoritePaths.Add(path);
            return true;
        }

        /// <summary>把文件夹从收藏移除。返回是否移除。</summary>
        public bool RemoveFavorite(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            for (int i = FavoritePaths.Count - 1; i >= 0; i--)
            {
                if (string.Equals(FavoritePaths[i], path, StringComparison.OrdinalIgnoreCase))
                {
                    FavoritePaths.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>该文件夹是否已收藏。</summary>
        public bool IsFavorite(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            foreach (string existing in FavoritePaths)
            {
                if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // ---------------- 隐藏的子目录 ----------------

        /// <summary>把子目录加入隐藏列表（已存在则忽略）。返回是否新增。</summary>
        public bool AddExcluded(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            foreach (string existing in ExcludedPaths)
            {
                if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            ExcludedPaths.Add(path);
            return true;
        }

        /// <summary>把子目录从隐藏列表移除（恢复显示）。返回移除条数。</summary>
        public int RemoveExcluded(string path)
        {
            if (string.IsNullOrEmpty(path)) return 0;

            int count = 0;
            for (int i = ExcludedPaths.Count - 1; i >= 0; i--)
            {
                if (string.Equals(ExcludedPaths[i], path, StringComparison.OrdinalIgnoreCase))
                {
                    ExcludedPaths.RemoveAt(i);
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// 移除位于 rootPath 之下（含等于）的所有隐藏条目。
        /// 根目录本身从图库移除时，它下面的隐藏记录一并清掉，避免残留垃圾。
        /// </summary>
        public void RemoveExcludedUnder(string rootPath)
        {
            if (string.IsNullOrEmpty(rootPath)) return;

            for (int i = ExcludedPaths.Count - 1; i >= 0; i--)
            {
                string path = ExcludedPaths[i];
                if (string.IsNullOrEmpty(path)) continue;

                bool under = string.Equals(path, rootPath, StringComparison.OrdinalIgnoreCase);
                if (!under && path.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                {
                    char next = path[rootPath.Length];
                    under = next == Path.DirectorySeparatorChar || next == Path.AltDirectorySeparatorChar;
                }

                if (under) ExcludedPaths.RemoveAt(i);
            }
        }
    }
}
