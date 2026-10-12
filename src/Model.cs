using System;
using System.Collections.Generic;
using System.IO;

namespace BlockLibraryManager
{
    /// <summary>
    /// 右侧图块列表的显示方式。数值即持久化到 BlockLibrary.ini 的整数。
    /// </summary>
    public enum GalleryView
    {
        LargeIcon = 0,
        Details = 1,
        List = 2
    }

    /// <summary>
    /// 一个图库条目：可以是磁盘上的一个 DWG 文件，也可以是 DWG 文件内部的某个块定义。
    /// </summary>
    public sealed class BlockItem
    {
        /// <summary>条目的标识键，列表去重 / 缓存索引时使用。</summary>
        public string Key { get; set; }

        /// <summary>显示名称。</summary>
        public string Name { get; set; }

        /// <summary>来源 DWG 文件的完整路径。</summary>
        public string FilePath { get; set; }

        /// <summary>若为 DWG 内部块定义，则为块名；否则为空。</summary>
        public string BlockName { get; set; }

        /// <summary>所属分类（磁盘上的目录相对图库根的路径）。</summary>
        public string CategoryPath { get; set; }

        /// <summary>文件大小（字节）。DWG 内部块定义时为 0。</summary>
        public long FileSize { get; set; }

        /// <summary>最后修改时间。</summary>
        public DateTime ModifiedTime { get; set; }

        /// <summary>是否整份 DWG 作为一个块插入。</summary>
        public bool IsWholeDrawing
        {
            get { return string.IsNullOrEmpty(BlockName); }
        }

        /// <summary>是否参与列表渲染（例如缓存缩略图失败也照样显示）。</summary>
        public bool IsValid
        {
            get
            {
                if (string.IsNullOrEmpty(Key)) return false;
                return File.Exists(FilePath);
            }
        }

        public override string ToString()
        {
            return Name;
        }

        /// <summary>由 DWG 文件创建条目。</summary>
        public static BlockItem FromFile(string rootPath, string filePath)
        {
            if (filePath == null) throw new ArgumentNullException("filePath");

            FileInfo info = new FileInfo(filePath);

            string category = GetCategoryPath(rootPath, info.DirectoryName);

            return new BlockItem
            {
                Key = "file:" + info.FullName.ToLowerInvariant(),
                Name = Path.GetFileNameWithoutExtension(info.Name),
                FilePath = info.FullName,
                BlockName = null,
                CategoryPath = category,
                FileSize = info.Length,
                ModifiedTime = info.LastWriteTime
            };
        }

        /// <summary>
        /// 由已有的 FileInfo 创建条目。
        /// DirectoryInfo.GetFiles() 返回的 FileInfo 已经在枚举时填好了大小和时间，
        /// 直接用它比「拿到路径再 new FileInfo(path)」少一次磁盘查询，扫描大图库快很多。
        /// </summary>
        public static BlockItem FromFileInfo(string rootPath, FileInfo info)
        {
            if (info == null) throw new ArgumentNullException("info");

            string fullName = info.FullName;
            string category = GetCategoryPath(rootPath, info.DirectoryName);

            long size = 0;
            DateTime modified = DateTime.MinValue;
            try { size = info.Length; } catch { }
            try { modified = info.LastWriteTime; } catch { }

            return new BlockItem
            {
                Key = "file:" + fullName.ToLowerInvariant(),
                Name = Path.GetFileNameWithoutExtension(info.Name),
                FilePath = fullName,
                BlockName = null,
                CategoryPath = category,
                FileSize = size,
                ModifiedTime = modified
            };
        }

        /// <summary>由 DWG 内部的块定义创建条目。</summary>
        public static BlockItem FromBlock(string rootPath, string filePath, string blockName)
        {
            FileInfo info = new FileInfo(filePath);

            string category = GetCategoryPath(rootPath, info.DirectoryName);

            string suffix = blockName;
            if (suffix == null) suffix = string.Empty;

            return new BlockItem
            {
                Key = "blk:" + info.FullName.ToLowerInvariant() + "|" + suffix.ToLowerInvariant(),
                Name = Path.GetFileNameWithoutExtension(info.Name) + " [" + suffix + "]",
                FilePath = info.FullName,
                BlockName = suffix,
                CategoryPath = category,
                FileSize = info.Exists ? info.Length : 0L,
                ModifiedTime = info.Exists ? info.LastWriteTime : DateTime.MinValue
            };
        }

        private static string GetCategoryPath(string rootPath, string directory)
        {
            if (string.IsNullOrEmpty(rootPath)) return directory ?? string.Empty;
            if (string.IsNullOrEmpty(directory)) return string.Empty;

            string root = Safe(rootPath);
            string dir = Safe(directory);

            if (dir.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                string tail = dir.Substring(root.Length);
                return tail.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            return directory;
        }

        private static string Safe(string path)
        {
            string result = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // 保证结尾统一，便于 StartsWith 比较
            return result + Path.DirectorySeparatorChar;
        }
    }

    /// <summary>
    /// 分类树节点，与磁盘目录一一对应。
    /// </summary>
    public sealed class CategoryNode
    {
        public CategoryNode()
        {
            Children = new List<CategoryNode>();
            DirectCount = 0;
            TotalCount = 0;
        }

        /// <summary>目录名（显示用）。</summary>
        public string Name { get; set; }

        /// <summary>目录完整路径。</summary>
        public string FullPath { get; set; }

        /// <summary>相对图库根目录的路径。</summary>
        public string RelativePath { get; set; }

        public CategoryNode Parent { get; set; }

        public List<CategoryNode> Children { get; private set; }

        /// <summary>磁盘上的目录已经不存在（被移动 / 删除 / 断网）。仍然显示，好让用户能移除它。</summary>
        public bool Missing { get; set; }

        /// <summary>本层目录下的图块数量。</summary>
        public int DirectCount { get; set; }

        /// <summary>含子目录的图块总数量。</summary>
        public int TotalCount { get; set; }

        public bool HasChildren
        {
            get { return Children != null && Children.Count > 0; }
        }

        public string DisplayName
        {
            get
            {
                if (Missing) return Name + "（目录不存在）";
                if (TotalCount > 0) return Name + " (" + TotalCount + ")";
                return Name;
            }
        }

        /// <summary>节点在图库中的完整显示路径（不含计数）。</summary>
        public string PathChain
        {
            get
            {
                string result = Name;
                CategoryNode node = Parent;
                while (node != null)
                {
                    result = node.Name + Path.DirectorySeparatorChar + result;
                    node = node.Parent;
                }
                return result;
            }
        }

        public override string ToString()
        {
            return DisplayName;
        }
    }

    /// <summary>
    /// 目录列表"收藏"节点的 Tag。
    /// Path 为空表示"收藏"根节点本身；否则记录收藏的文件夹完整路径。
    /// </summary>
    public sealed class FavoriteTag
    {
        public string Path { get; set; }

        public FavoriteTag(string path)
        {
            Path = path;
        }
    }

    /// <summary>
    /// 电脑文件树节点的 Tag。
    /// Path 为空表示"此电脑"等虚拟节点（不是磁盘目录）。
    /// </summary>
    public sealed class PcTag
    {
        public string Path { get; set; }

        public PcTag(string path)
        {
            Path = path;
        }
    }
}
