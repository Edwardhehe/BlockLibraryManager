using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace BlockLibraryManager
{
    /// <summary>
    /// 取 Windows 资源管理器的原生文件夹图标（shell32!SHGetFileInfo）。
    /// 供分类树等处使用；取不到时退回自绘，绝不抛异常。
    /// </summary>
    public static class SystemIconProvider
    {
        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_SMALLICON = 0x000000001;
        private const uint SHGFI_OPENICON = 0x000000002;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        private static Image _closedFolder;
        private static Image _openFolder;

        /// <summary>按真实路径取系统图标的缓存（键 = 路径小写）。</summary>
        private static readonly System.Collections.Generic.Dictionary<string, Image> _pathIconCache =
            new System.Collections.Generic.Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);

        /// <summary>关闭状态的文件夹图标（Explorer 原生，16x16）。</summary>
        public static Image ClosedFolder
        {
            get
            {
                if (_closedFolder == null)
                    _closedFolder = LoadFolderIcon(false) ?? DrawFallback(false);
                return _closedFolder;
            }
        }

        /// <summary>打开状态的文件夹图标（取不到时退回关闭图标）。</summary>
        public static Image OpenFolder
        {
            get
            {
                if (_openFolder == null)
                    _openFolder = LoadFolderIcon(true) ?? ClosedFolder;
                return _openFolder;
            }
        }

        /// <summary>
        /// 取某个真实路径（文件夹 / 驱动器 / CLSID）对应的系统图标。
        /// 供"此电脑"文件树使用：图标跟随系统（驱动器、特殊目录各有各的样子）。
        /// 取不到时退回普通文件夹图标，绝不抛异常。
        /// </summary>
        public static Image GetPathIcon(string path)
        {
            if (string.IsNullOrEmpty(path)) return ClosedFolder;

            Image cached;
            lock (_pathIconCache)
            {
                if (_pathIconCache.TryGetValue(path, out cached)) return cached;
            }

            Image icon = LoadPathIcon(path) ?? ClosedFolder;

            lock (_pathIconCache)
            {
                _pathIconCache[path] = icon;
            }
            return icon;
        }

        private static Image LoadPathIcon(string path)
        {
            // 真实路径必须优先不带 USEFILEATTRIBUTES：
            // 带上它 shell 就不会去读目录里的 desktop.ini，
            // "桌面 / 下载 / 文档 / 图片"等特殊文件夹会全部退化成通用文件夹图标；
            // 只有不带属性取不到时才换目录属性方式兜底。
            // CLSID（如 "::{20D04FE0-...}" 即"此电脑"）也必须不带属性才能取到。
            Image icon = TryShellIcon(path, false);

            // 兜底：目录属性方式（任何路径都能返回一个通用文件夹图标）
            if (icon == null) icon = TryShellIcon(path, true);

            return icon;
        }

        /// <summary>用一组 SHGetFileInfo 参数尝试取图标，失败返回 null。</summary>
        private static Image TryShellIcon(string path, bool useFileAttributes)
        {
            try
            {
                SHFILEINFO info = new SHFILEINFO();
                uint flags = SHGFI_ICON | SHGFI_SMALLICON;
                uint attributes = 0;

                if (useFileAttributes)
                {
                    flags |= SHGFI_USEFILEATTRIBUTES;
                    attributes = FILE_ATTRIBUTE_DIRECTORY;
                }

                uint size = (uint)Marshal.SizeOf(typeof(SHFILEINFO));
                IntPtr result = SHGetFileInfo(path, attributes, ref info, size, flags);

                if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

                Image bitmap = null;
                Icon original = Icon.FromHandle(info.hIcon);
                try
                {
                    using (Icon clone = (Icon)original.Clone())
                        bitmap = new Bitmap(clone.ToBitmap());
                }
                finally
                {
                    try { DestroyIcon(info.hIcon); } catch { }
                }
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private static Image LoadFolderIcon(bool open)
        {
            try
            {
                SHFILEINFO info = new SHFILEINFO();
                uint flags = SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES;
                if (open) flags |= SHGFI_OPENICON;

                uint size = (uint)Marshal.SizeOf(typeof(SHFILEINFO));
                IntPtr result = SHGetFileInfo(".dummy", FILE_ATTRIBUTE_DIRECTORY, ref info, size, flags);

                if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

                Image bitmap = null;
                Icon original = Icon.FromHandle(info.hIcon);
                try
                {
                    using (Icon clone = (Icon)original.Clone())
                        bitmap = new Bitmap(clone.ToBitmap());
                }
                finally
                {
                    try { DestroyIcon(info.hIcon); } catch { }
                }
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>系统图标取不到时的自绘兜底（简单黄色文件夹）。</summary>
        private static Image DrawFallback(bool open)
        {
            Bitmap bmp = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);

                Color face = open ? Color.FromArgb(255, 224, 160) : Color.FromArgb(255, 208, 112);
                Color edge = Color.FromArgb(200, 140, 60);

                using (Pen pen = new Pen(edge))
                using (SolidBrush brush = new SolidBrush(face))
                {
                    if (open)
                    {
                        // 打开态：斜盖板 + 后壁
                        Point[] lid = new Point[]
                        {
                            new Point(1, 7), new Point(6, 4), new Point(9, 7), new Point(4, 10)
                        };
                        g.FillPolygon(brush, lid);
                        g.DrawPolygon(pen, lid);

                        Point[] body = new Point[]
                        {
                            new Point(2, 8), new Point(14, 8), new Point(11, 13), new Point(1, 13)
                        };
                        g.FillPolygon(brush, body);
                        g.DrawPolygon(pen, body);
                    }
                    else
                    {
                        // 关闭态：标签耳 + 方体
                        g.FillRectangle(brush, 1, 4, 6, 3);
                        g.DrawRectangle(pen, 1, 4, 6, 3);
                        g.FillRectangle(brush, 1, 6, 13, 7);
                        g.DrawRectangle(pen, 1, 6, 13, 7);
                    }
                }
            }
            return bmp;
        }

        /// <summary>
        /// 自绘一个放大镜（搜索）图标。用于搜索框内。
        /// 不依赖系统图标字体（老系统可能缺字形），纯 GDI 绘制，任何平台都能显示。
        /// </summary>
        public static Image CreateMagnifierIcon(int size)
        {
            if (size < 8) size = 8;

            Bitmap bmp = new Bitmap(size, size);
            try
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;

                    float stroke = Math.Max(1.4f, size / 10f);
                    using (Pen pen = new Pen(Color.FromArgb(108, 118, 132), stroke))
                    {
                        // 镜片：左上方圆
                        float lens = size * 0.58f;
                        RectangleF circle = new RectangleF(
                            stroke * 0.6f, stroke * 0.6f, lens, lens);

                        // 镜片里铺一点浅色，更像玻璃
                        using (SolidBrush glass = new SolidBrush(Color.FromArgb(28, 130, 170, 220)))
                            g.FillEllipse(glass, circle);
                        g.DrawEllipse(pen, circle);

                        // 手柄：从圆边 45° 拉到右下角
                        float cx = circle.Left + lens / 2f;
                        float cy = circle.Top + lens / 2f;
                        float r = lens / 2f;
                        float k = 0.7071f;
                        float sx = cx + r * k;
                        float sy = cy + r * k;
                        float len = size * 0.30f;
                        g.DrawLine(pen, sx, sy, sx + len, sy + len);
                    }
                }
            }
            catch
            {
                // 绘制失败时返回空白图，不影响功能
            }
            return bmp;
        }
    }
}
