using System;
using System.IO;
using System.Text;
using System.Collections.Generic;

namespace BlockLibraryManager
{
    /// <summary>
    /// 图库配置。存放位置：%APPDATA%\BlockLibraryManager\config.json
    /// 这里刻意不用 DataContractJsonSerializer / System.Text.Json，
    /// 因为它们在 net35(CAD2010) 与 net8(CAD2025+) 之间的可用性不一致；
    /// 自己拼配 JSON 反而是全版本通用的最小方案。
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
        /// 0 = 默认（跟随系统），1 = 清新蓝，2 = 薄荷绿，3 = 浅绿，4 = 暖阳橙。
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

        private static string AppDataFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlockLibraryManager"); }
        }

        private static string ConfigFilePath
        {
            get { return Path.Combine(AppDataFolder, "config.json"); }
        }

        public static LibraryConfig Load()
        {
            LibraryConfig config = new LibraryConfig();
            try
            {
                string path = ConfigFilePath;
                if (!File.Exists(path))
                {
                    // 首次运行：给一个默认的“我的图库”目录并落盘
                    config.RootPaths.Add(DefaultRootPath);
                    try { if (!Directory.Exists(DefaultRootPath)) Directory.CreateDirectory(DefaultRootPath); } catch { }
                    config.Save();
                    return config;
                }

                string json = File.ReadAllText(path, Encoding.UTF8);
                MiniJson root = MiniJson.ParseObject(json);
                if (root == null) return config;

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

                // 构造函数里已有默认值，这里必须先清空再填充，否则会重复叠加
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
                if (config.ThumbBackground != "black") config.ThumbBackground = "white";
                if (string.IsNullOrEmpty(config.LaunchCommand)) config.LaunchCommand = "BLOCKLIB";
                if (config.ThumbSize < 48) config.ThumbSize = 48;
                if (config.ThumbSize > 256) config.ThumbSize = 256;
                if (config.PaletteWidth < 200) config.PaletteWidth = 200;
                if (config.RecentLimit < 0) config.RecentLimit = 0;
                if (config.TreePanelWidth < 0) config.TreePanelWidth = 0;
                if (config.LeftSplitDistance < 0) config.LeftSplitDistance = 0;
            }
            catch
            {
                // 配置损坏时退回默认值，不能因为配置问题导致插件加载失败
            }
            return config;
        }

        public void Save()
        {
            try
            {
                string folder = AppDataFolder;
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

                int recentLimit = RecentLimit > 0 ? RecentLimit : 0;
                if (recentLimit > 0)
                {
                    while (RecentFiles.Count > recentLimit)
                        RecentFiles.RemoveAt(RecentFiles.Count - 1);
                }

                StringBuilder sb = new StringBuilder();
                sb.Append("{\n");
                MiniJson.AppendStringList(sb, "RootPaths", RootPaths);
                sb.Append(",\n");
                MiniJson.AppendStringList(sb, "RecentFiles", RecentFiles);
                sb.Append(",\n");
                MiniJson.AppendStringList(sb, "FileFilters", FileFilters);
                sb.Append(",\n");
                MiniJson.AppendStringList(sb, "ExcludedPaths", ExcludedPaths);
                sb.Append(",\n");
                MiniJson.AppendStringList(sb, "FavoritePaths", FavoritePaths);
                sb.Append(",\n");
                sb.Append(MiniJson.MakeString("LastCategoryPath", LastCategoryPath)).Append(",\n");
                sb.Append(MiniJson.MakeNumber("ThumbSize", ThumbSize)).Append(",\n");
                sb.Append(MiniJson.MakeNumber("ViewMode", (int)ViewMode)).Append(",\n");
                sb.Append(MiniJson.MakeNumber("PaletteWidth", PaletteWidth)).Append(",\n");
                sb.Append(MiniJson.MakeBool("ExpandInnerBlocks", ExpandInnerBlocks)).Append(",\n");
                sb.Append(MiniJson.MakeBool("SearchSubCategories", SearchSubCategories)).Append(",\n");
                sb.Append(MiniJson.MakeNumber("RecentLimit", RecentLimit)).Append(",\n");
                sb.Append(MiniJson.MakeNumber("WindowLeft", WindowLeft)).Append(",\n");
                sb.Append(MiniJson.MakeNumber("WindowTop", WindowTop)).Append(",\n");
                sb.Append(MiniJson.MakeNumber("WindowWidth", WindowWidth)).Append(",\n");
                sb.Append(MiniJson.MakeNumber("WindowHeight", WindowHeight)).Append(",\n");
                sb.Append(MiniJson.MakeNumber("TreePanelWidth", TreePanelWidth)).Append(",\n");
                sb.Append(MiniJson.MakeNumber("LeftSplitDistance", LeftSplitDistance)).Append(",\n");
                sb.Append(MiniJson.MakeString("Language", Language == null ? string.Empty : Language)).Append(",\n");
                sb.Append(MiniJson.MakeNumber("Theme", Theme)).Append(",\n");
                sb.Append(MiniJson.MakeString("ThumbBackground", ThumbBackground == null ? "white" : ThumbBackground)).Append(",\n");
                sb.Append(MiniJson.MakeString("LaunchCommand", LaunchCommand == null ? "BLOCKLIB" : LaunchCommand)).Append("\n");
                sb.Append("}\n");

                File.WriteAllText(ConfigFilePath, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                // 配置写入失败不影响主流程
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

            int limit = RecentLimit > 0 ? RecentLimit : 0;
            if (limit > 0)
            {
                while (RecentFiles.Count > limit)
                    RecentFiles.RemoveAt(RecentFiles.Count - 1);
            }
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
