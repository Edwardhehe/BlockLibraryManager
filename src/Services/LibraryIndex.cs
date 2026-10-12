using System;
using System.Collections.Generic;
using System.IO;

namespace BlockLibraryManager
{
    /// <summary>
    /// 图库索引：扫描磁盘上的图库根目录，构建分类树并收集图块条目。
    /// 本类不依赖任何 AutoCAD 程序集，便于单独测试。
    /// </summary>
    public sealed class LibraryIndex
    {
        private readonly LibraryConfig _config;

        /// <summary>
        /// 可选的"读取 DWG 内部块定义"回调。传入 DWG 路径，返回其中的块名列表。
        /// 由 AutoCAD 层注入；为 null 时每个 DWG 只作为一个整体图块。
        /// </summary>
        private readonly Func<string, IList<string>> _blockNamesProvider;

        private readonly List<BlockItem> _allBlocks = new List<BlockItem>();
        private readonly Dictionary<string, List<BlockItem>> _byDirectory =
            new Dictionary<string, List<BlockItem>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>当前正在扫描的图库根路径（用于计算 BlockItem.CategoryPath）。</summary>
        private string _currentRoot;

        // ---- 扫描进度（仅在带进度的 Reload 重载里启用）----
        private Action<int, string> _progress;
        private Func<bool> _cancelCheck;
        private int _totalDirectories;
        private int _scannedDirectories;
        private int _reportedPercent = -1;

        public const int MaxDepth = 16;

        public LibraryIndex(LibraryConfig config, Func<string, IList<string>> blockNamesProvider)
        {
            if (config == null) throw new ArgumentNullException("config");
            _config = config;
            _blockNamesProvider = blockNamesProvider;
        }

        /// <summary>虚拟根节点。</summary>
        public CategoryNode Root { get; private set; }

        /// <summary>全部图块（不含分类层级）。</summary>
        public List<BlockItem> AllBlocks
        {
            get { return _allBlocks; }
        }

        /// <summary>最近一次扫描遇到的错误说明（权限不足等）。</summary>
        public string LastError { get; private set; }

        /// <summary>本次扫描是否被用户中途取消。</summary>
        public bool ScanCancelled { get; private set; }

        public void Reload()
        {
            Reload(null, null);
        }

        /// <summary>
        /// 扫描图库。
        /// </summary>
        /// <param name="progress">进度回调（percent 0~100，text 当前目录），可为 null。</param>
        /// <param name="cancelCheck">取消检查，返回 true 时尽快停止，可为 null。</param>
        /// <remarks>
        /// 注意：带进度的重载通常跑在后台线程上，此时一律不展开 DWG 内部块定义
        /// （那需要调用 AutoCAD 的 Database API，只能在主线程上做）。
        /// </remarks>
        /// <summary>本次扫描要跳过的子目录（用户隐藏的），键为规范化全路径。</summary>
        private HashSet<string> _excluded;

        public void Reload(Action<int, string> progress, Func<bool> cancelCheck)
        {
            _allBlocks.Clear();
            _byDirectory.Clear();
            LastError = null;
            ScanCancelled = false;

            _excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in _config.ExcludedPaths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                _excluded.Add(Normalize(path));
            }

            _progress = progress;
            _cancelCheck = cancelCheck;
            _scannedDirectories = 0;
            _reportedPercent = -1;
            _totalDirectories = progress == null ? 0 : CountDirectories();

            CategoryNode virtualRoot = new CategoryNode();
            virtualRoot.Name = "图库";
            virtualRoot.FullPath = null;
            virtualRoot.RelativePath = string.Empty;

            foreach (string rootPath in _config.RootPaths)
            {
                if (string.IsNullOrEmpty(rootPath)) continue;
                if (CheckCancel()) break;

                DirectoryInfo info;
                try { info = new DirectoryInfo(rootPath); }
                catch { continue; }

                CategoryNode rootNode = new CategoryNode();
                rootNode.Name = info.Name;
                rootNode.FullPath = info.FullName;
                rootNode.RelativePath = string.Empty;
                rootNode.Parent = null;
                virtualRoot.Children.Add(rootNode);

                // 目录已被删除/移动时也要建节点，否则用户没法从图库列表里把它移除
                if (!Directory.Exists(rootPath))
                {
                    rootNode.Missing = true;
                    _scannedDirectories++;
                    ReportProgress(rootPath);
                    continue;
                }

                _currentRoot = info.FullName;
                ScanDirectory(rootNode, rootPath, 0);
            }

            virtualRoot.TotalCount = _allBlocks.Count;
            virtualRoot.DirectCount = 0;

            Root = virtualRoot;

            SortRecursive(Root);

            ReportProgress(null);

            _progress = null;
            _cancelCheck = null;
            _excluded = null;
        }

        /// <summary>把路径规范成可比较形式（取全路径 + 去结尾分隔符 + 转小写）。</summary>
        private static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;

            string full;
            try { full = new DirectoryInfo(path).FullName; }
            catch { full = path; }

            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       .ToLowerInvariant();
        }

        /// <summary>该目录是否被用户隐藏（不参与扫描与显示）。</summary>
        private bool IsExcluded(string fullPath)
        {
            if (_excluded == null || _excluded.Count == 0 || string.IsNullOrEmpty(fullPath)) return false;
            return _excluded.Contains(Normalize(fullPath));
        }

        private bool CheckCancel()
        {
            if (_cancelCheck == null) return false;
            try
            {
                if (!_cancelCheck()) return false;
            }
            catch
            {
                return false;
            }
            ScanCancelled = true;
            return true;
        }

        /// <summary>向界面汇报一次进度（百分比按已扫描目录数估算）。</summary>
        private void ReportProgress(string currentDirectory)
        {
            if (_progress == null) return;

            try
            {
                int percent = 0;
                if (_totalDirectories > 0)
                {
                    percent = (int)(_scannedDirectories * 100L / _totalDirectories);
                    if (percent < 0) percent = 0;
                    if (percent > 99) percent = 99;
                }

                string text = string.IsNullOrEmpty(currentDirectory)
                    ? "共找到 " + _allBlocks.Count + " 个图块"
                    : currentDirectory + "   （已找到 " + _allBlocks.Count + " 个图块）";

                _progress(percent, text);
                _reportedPercent = percent;
            }
            catch
            {
                // 进度汇报失败绝不能影响扫描本身
            }
        }

        /// <summary>先快速把目录总数统计出来，进度条才有真实的百分比（只列目录，不列文件，很快）。</summary>
        private int CountDirectories()
        {
            int count = 0;

            foreach (string rootPath in _config.RootPaths)
            {
                if (string.IsNullOrEmpty(rootPath)) continue;
                count++;
                try
                {
                    if (Directory.Exists(rootPath)) CountDirectoriesRecursive(rootPath, 0, ref count);
                }
                catch
                {
                    // 统计失败就用当前值，进度条会不够准但不影响功能
                }
            }

            if (count < 1) count = 1;
            _progress(2, "正在统计目录…");
            return count;
        }

        private void CountDirectoriesRecursive(string directory, int depth, ref int count)
        {
            if (depth > MaxDepth) return;

            string[] subs;
            try { subs = Directory.GetDirectories(directory); }
            catch { return; }

            foreach (string sub in subs)
            {
                try
                {
                    if (IsIgnoredDirectory(new DirectoryInfo(sub))) continue;
                    if (_excluded != null && _excluded.Contains(Normalize(sub))) continue;
                }
                catch { continue; }

                count++;
                CountDirectoriesRecursive(sub, depth + 1, ref count);
            }
        }

        private void ScanDirectory(CategoryNode node, string directory, int depth)
        {
            if (depth > MaxDepth) return;
            if (CheckCancel()) return;

            _scannedDirectories++;
            if (_progress != null && (_scannedDirectories % 4 == 0 || _reportedPercent < 0))
                ReportProgress(directory);

            // 1. 收集本层文件
            //    用 DirectoryInfo.GetFiles()：它返回的 FileInfo 已带 size/time，
            //    比 Directory.GetFiles() 拿路径再逐个 new FileInfo 少一半的磁盘查询。
            List<FileInfo> files = new List<FileInfo>();
            HashSet<string> seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                DirectoryInfo dirInfo = new DirectoryInfo(directory);

                foreach (string filter in _config.FileFilters)
                {
                    if (string.IsNullOrEmpty(filter)) continue;

                    try
                    {
                        FileInfo[] found = dirInfo.GetFiles(filter);
                        foreach (FileInfo fi in found)
                        {
                            if (fi == null) continue;
                            if (seenFiles.Add(fi.FullName)) files.Add(fi);   // 过滤条件重复时不重复计数
                        }
                    }
                    catch (Exception ex)
                    {
                        LastError = ex.Message;
                    }
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }

            List<BlockItem> items = new List<BlockItem>();
            foreach (FileInfo file in files)
            {
                if (CheckCancel()) break;
                if (IsTempFile(file.Name)) continue;

                items.Add(BlockItem.FromFileInfo(_currentRoot, file));
                _allBlocks.Add(items[items.Count - 1]);

                // 后台线程（_progress != null）里绝不调用 AutoCAD API，跳过内部块展开
                if (_config.ExpandInnerBlocks && _blockNamesProvider != null && _progress == null)
                {
                    try
                    {
                        IList<string> names = _blockNamesProvider(file.FullName);
                        if (names != null)
                        {
                            foreach (string name in names)
                            {
                                BlockItem inner = BlockItem.FromBlock(_currentRoot, file.FullName, name);
                                items.Add(inner);
                                _allBlocks.Add(inner);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LastError = ex.Message;
                    }
                }
            }

            // 键统一走 Normalize（去尾部分隔符 + 转小写）：手工改过的根路径
            // 可能带尾部 "\" 或大小写不一致，直接拿原始串当键会和查询时对不上，
            // 表现为"这个分类显示 0 个图块"，而总数却是非零。
            string key = Normalize(directory);
            if (!_byDirectory.ContainsKey(key))
                _byDirectory.Add(key, items);
            else
                _byDirectory[key].AddRange(items);

            node.DirectCount = items.Count;

            // 2. 递归子目录
            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(directory);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                node.TotalCount = node.DirectCount;
                return;
            }

            int total = node.DirectCount;
            foreach (string sub in subDirs)
            {
                if (CheckCancel()) break;

                DirectoryInfo subInfo;
                try
                {
                    subInfo = new DirectoryInfo(sub);
                }
                catch
                {
                    continue;
                }

                // 拒绝访问 / 重解析点上读 Attributes 会抛异常，不能让它冒出去中断整个刷新
                bool ignored;
                try { ignored = IsIgnoredDirectory(subInfo); }
                catch { continue; }
                if (ignored) continue;

                if (IsExcluded(subInfo.FullName)) continue;   // 用户隐藏的子目录：不建节点、不扫描

                CategoryNode child = new CategoryNode();
                child.Name = subInfo.Name;
                child.FullPath = subInfo.FullName;
                child.RelativePath = BuildRelativePath(node, subInfo.Name);
                child.Parent = node;
                node.Children.Add(child);

                ScanDirectory(child, sub, depth + 1);
                total += child.TotalCount;
            }

            node.TotalCount = total;
        }

        private static string BuildRelativePath(CategoryNode parent, string name)
        {
            if (parent == null) return name;
            if (string.IsNullOrEmpty(parent.RelativePath)) return name;
            return parent.RelativePath + Path.DirectorySeparatorChar + name;
        }

        private static bool IsIgnoredDirectory(DirectoryInfo info)
        {
            string name = info.Name;

            FileAttributes attr = info.Attributes;
            if ((attr & FileAttributes.Hidden) == FileAttributes.Hidden) return true;
            if ((attr & FileAttributes.System) == FileAttributes.System) return true;

            if (string.Equals(name, "$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(name, "System Volume Information", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith(".", StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        private static bool IsTempFile(string filePath)
        {
            // AutoCAD 临时文件 / 备份文件
            string name = Path.GetFileName(filePath);
            if (name.StartsWith("~$", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith("~", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void SortRecursive(CategoryNode node)
        {
            if (node == null) return;
            node.Children.Sort(CompareByName);
            foreach (CategoryNode child in node.Children)
                SortRecursive(child);
        }

        private static int CompareByName(CategoryNode a, CategoryNode b)
        {
            string x = a != null ? a.Name : string.Empty;
            string y = b != null ? b.Name : string.Empty;
            return string.Compare(x, y, StringComparison.CurrentCultureIgnoreCase);
        }

        /// <summary>取得某个分类目录下的图块（不含子目录）。</summary>
        public List<BlockItem> GetBlocksInCategory(CategoryNode node)
        {
            List<BlockItem> result = new List<BlockItem>();
            if (node == null || string.IsNullOrEmpty(node.FullPath)) return result;

            List<BlockItem> found;
            if (_byDirectory.TryGetValue(Normalize(node.FullPath), out found))
                result.AddRange(found);

            SortBlockItems(result);
            return result;
        }

        /// <summary>取得某个分类目录及其所有子目录下的图块。</summary>
        public List<BlockItem> GetBlocksRecursive(CategoryNode node)
        {
            List<BlockItem> result = new List<BlockItem>();
            if (node == null) return result;

            if (!string.IsNullOrEmpty(node.FullPath))
            {
                List<BlockItem> found;
                if (_byDirectory.TryGetValue(Normalize(node.FullPath), out found))
                    result.AddRange(found);
            }

            foreach (CategoryNode child in node.Children)
                result.AddRange(GetBlocksRecursive(child));

            SortBlockItems(result);
            return result;
        }

        /// <summary>
        /// 按名字关键字过滤（v1.49.1 优化）。
        /// 调用方传 _index.AllBlocks 即在用户加入图库的所有根目录、所有子目录里搜：
        /// 1. 关键词允许带 .dwg / .dwt 扩展名（输入"门.dwg"与"门"等效）；
        /// 2. 同时匹配显示名、磁盘文件名（含/不含扩展名）与 DWG 内部块名；
        /// 3. 支持空格分隔多个关键字，全部命中才显示（AND）。
        /// 命中的条目显示在右侧缩略图区域。
        /// </summary>
        public static List<BlockItem> FilterByName(List<BlockItem> source, string keyword)
        {
            List<BlockItem> result = new List<BlockItem>();
            if (source == null) return result;

            keyword = keyword == null ? string.Empty : keyword.Trim();
            if (keyword.Length == 0)
            {
                result.AddRange(source);
                return result;
            }

            // 空格分隔多关键字；每个关键字都去掉图纸扩展名再比
            string[] keys = keyword.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            string[] normalized = new string[keys.Length];
            for (int i = 0; i < keys.Length; i++)
                normalized[i] = TrimDwgExtension(keys[i]);

            foreach (BlockItem item in source)
            {
                if (item == null) continue;
                if (MatchesAllKeywords(item, normalized)) result.Add(item);
            }

            SortBlockItems(result);
            return result;
        }

        /// <summary>关键词末尾的 .dwg / .dwt / .dws 后缀不算搜索内容的一部分。</summary>
        private static string TrimDwgExtension(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;

            string[] exts = new string[] { ".dwg", ".dwt", ".dws" };
            foreach (string ext in exts)
            {
                if (key.Length > ext.Length &&
                    key.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                {
                    return key.Substring(0, key.Length - ext.Length);
                }
            }
            return key;
        }

        /// <summary>所有关键字都命中（显示名 / 文件名 / 内部块名任一字段包含即可）才算匹配。</summary>
        private static bool MatchesAllKeywords(BlockItem item, string[] keys)
        {
            string name = item.Name ?? string.Empty;
            string fileName = string.IsNullOrEmpty(item.FilePath)
                ? string.Empty
                : Path.GetFileName(item.FilePath);
            string fileNameNoExt = string.IsNullOrEmpty(item.FilePath)
                ? string.Empty
                : Path.GetFileNameWithoutExtension(item.FilePath);
            string blockName = item.BlockName ?? string.Empty;

            foreach (string key in keys)
            {
                bool hit =
                    (key.Length == 0) ||
                    name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    fileNameNoExt.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    fileName.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    blockName.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0;

                if (!hit) return false;   // AND：任一关键字没命中就整条排除
            }
            return true;
        }

        public CategoryNode FindNodeByPath(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return null;
            return SearchNodeByPath(Root, fullPath);
        }

        private static CategoryNode SearchNodeByPath(CategoryNode node, string fullPath)
        {
            if (node == null) return null;

            if (!string.IsNullOrEmpty(node.FullPath) &&
                string.Equals(node.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
                return node;

            foreach (CategoryNode child in node.Children)
            {
                CategoryNode hit = SearchNodeByPath(child, fullPath);
                if (hit != null) return hit;
            }
            return null;
        }

        /// <summary>取出配置里记录的最近使用项对应的 BlockItem。</summary>
        public List<BlockItem> BuildRecentList(string currentRoot)
        {
            List<BlockItem> result = new List<BlockItem>();
            foreach (string file in _config.RecentFiles)
            {
                if (string.IsNullOrEmpty(file)) continue;
                if (!File.Exists(file)) continue;

                bool filterHit = false;
                foreach (string filter in _config.FileFilters)
                {
                    if (MatchesFilter(file, filter)) { filterHit = true; break; }
                }
                if (!filterHit) continue;

                result.Add(BlockItem.FromFile(currentRoot, file));
            }
            return result;
        }

        private static bool MatchesFilter(string file, string filter)
        {
            string name = Path.GetFileName(file);
            string pattern = filter.StartsWith("*.") ? filter.Substring(1) : filter;
            return name.EndsWith(pattern, StringComparison.OrdinalIgnoreCase);
        }

        private static void SortBlockItems(List<BlockItem> items)
        {
            items.Sort(delegate (BlockItem a, BlockItem b)
            {
                string x = a != null ? a.Name : string.Empty;
                string y = b != null ? b.Name : string.Empty;
                return string.Compare(x, y, StringComparison.CurrentCultureIgnoreCase);
            });
        }
    }
}
