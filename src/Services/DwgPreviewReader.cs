using System;
using System.Drawing;
using System.IO;

namespace BlockLibraryManager
{
    /// <summary>
    /// 直接读取 DWG 文件里内嵌的预览图——**完全不打开 Database**。
    ///
    /// 这是整个图库"秒开"的关键。AutoCAD（以及中望 / 浩辰 / BricsCAD）保存图纸时
    /// 都会把一张预览位图写进 DWG 文件本身，供文件对话框和 Windows 资源管理器显示。
    /// 以前本项目为了拿这张图要走
    ///     new Database(false, true) → ReadDwgFile → db.ThumbnailBitmap
    /// 这会把整张图纸解析进内存（几十毫秒级），而且 Database 只能在 AutoCAD
    /// 主线程调用，于是只能挂在 Application.Idle 上一批批挤时间。
    ///
    /// 预览图在文件里有固定位置，按字节就能取出来：
    ///   1. 文件头 0x0D 处的 4 字节 = 预览段定位器（它指的是"起始哨兵(16) + 总长度(4)"）；
    ///   2. 跳到 0x14 + 定位器，第一个字节就是图像个数（16+4 = 0x14，正好跳过哨兵和总长度）；
    ///   3. 紧跟其后是 个数 × { 类型(1), 绝对偏移(4), 长度(4) }，然后是图像数据；
    ///   4. 类型：1 = 头部（固定 80 字节，忽略）、2 = BMP/DIB（R13–R2010）、
    ///            3 = WMF、6 = PNG（R2013+）。
    ///
    /// 于是 I/O 从"整张 DWG"降到"预览段几十 KB"，而且**不碰任何 CAD API**，
    /// 因此不受主线程约束、可以丢进线程池并行跑。
    ///
    /// 本文件刻意不引用任何 CAD 命名空间：9 个平台共用同一份源码，
    /// 平台命名空间替换脚本不会也不需要改动它。
    /// </summary>
    public static class DwgPreviewReader
    {
        /// <summary>单张预览图的体积上限（32MB），防止遇到损坏文件时盲目分配内存。</summary>
        private const int MaxPreviewBytes = 32 * 1024 * 1024;

        /// <summary>
        /// 预览段定位器指向"起始哨兵(16 字节) + 总长度(4 字节)"，
        /// 所以真正的图像个数在 seeker + 0x14 处——即下面 basePos 的第一个字节。
        /// 实测（AC1015 / AC1018 / AC1021 / AC1024 均验证过）：把这里当成
        /// "哨兵 + 总长 + 个数"再往后读 21 字节会读到描述符里的 0，一张图都取不到。
        /// </summary>
        private const int SeekerToImageCount = 0x14;

        /// <summary>
        /// 取 DWG 内嵌预览图。取不到（文件没有预览段 / 格式不认识 / 预览图损坏）时返回 null，
        /// 调用方负责走兜底路径。返回的 Bitmap 与文件流完全脱离，可以放心长期持有。
        /// </summary>
        public static Bitmap TryRead(string dwgPath)
        {
            if (string.IsNullOrEmpty(dwgPath)) return null;

            FileStream fs = null;
            try
            {
                if (!File.Exists(dwgPath)) return null;

                // FileShare.ReadWrite：图纸可能正被 CAD 打开着，不能独占
                fs = new FileStream(dwgPath, FileMode.Open, FileAccess.Read,
                                    FileShare.ReadWrite, 4096, FileOptions.None);
                return ReadCore(fs);
            }
            catch
            {
                // 解析失败一律当作"没有预览"，交给兜底路径
                return null;
            }
            finally
            {
                if (fs != null)
                {
                    try { fs.Dispose(); } catch { }
                }
            }
        }

        private static Bitmap ReadCore(Stream fs)
        {
            long fileLength = fs.Length;
            if (fileLength < 0x40) return null;

            // ---- 1. 读文件头，取预览段定位器 ----
            byte[] head = new byte[0x18];
            fs.Position = 0;
            if (ReadFully(fs, head, 0, head.Length) != head.Length) return null;

            int seeker = BitConverter.ToInt32(head, 0x0D);
            long basePos = SeekerToImageCount + (long)seeker;
            if (basePos <= 0 || basePos + 1 >= fileLength) return null;

            // ---- 2. 图像个数就在 basePos 的第一个字节 ----
            fs.Position = basePos;
            int imageCount = fs.ReadByte();
            if (imageCount <= 0 || imageCount > 16) return null;

            // ---- 3. 读描述符表，挑出 PNG / BMP（每条 9 字节）----
            byte[] desc = new byte[imageCount * 9];
            if (ReadFully(fs, desc, 0, desc.Length) != desc.Length) return null;

            int pngStart = -1, pngSize = 0;
            int bmpStart = -1, bmpSize = 0;

            for (int i = 0; i < imageCount; i++)
            {
                int off = i * 9;
                byte code = desc[off];
                int start = BitConverter.ToInt32(desc, off + 1);
                int size = BitConverter.ToInt32(desc, off + 5);

                if (size <= 0 || size > MaxPreviewBytes) continue;
                if (start < basePos || (long)start + size > fileLength) continue;

                if (code == 6 && pngStart < 0) { pngStart = start; pngSize = size; }
                else if (code == 2 && bmpStart < 0) { bmpStart = start; bmpSize = size; }
            }

            // ---- 4. PNG（R2013+，无损）优先，其次 DIB ----
            if (pngStart >= 0)
            {
                Bitmap bmp = ReadPng(fs, pngStart, pngSize);
                if (bmp != null) return bmp;
            }

            if (bmpStart >= 0)
            {
                Bitmap bmp = ReadDib(fs, bmpStart, bmpSize);
                if (bmp != null) return bmp;
            }

            return null;
        }

        private static Bitmap ReadPng(Stream fs, int start, int size)
        {
            byte[] buffer = ReadAt(fs, start, size);
            if (buffer == null || buffer.Length < 8) return null;

            // PNG 魔数：0x89 'P' 'N' 'G'
            if (!(buffer[0] == 0x89 && buffer[1] == 0x50 && buffer[2] == 0x4E && buffer[3] == 0x47))
                return null;

            return Decode(buffer);
        }

        private static Bitmap ReadDib(Stream fs, int start, int size)
        {
            byte[] dib = ReadAt(fs, start, size);
            if (dib == null || dib.Length < 40) return null;
            return BuildBitmapFromDib(dib);
        }

        /// <summary>
        /// 把"无文件头的 DIB"（R13–R2010 存的就是这个）拼成完整 BMP 再解码。
        /// DIB 开头就是一个 BITMAPINFOHEADER，颜色表紧随其后，
        /// 需要补一个 14 字节的 BITMAPFILEHEADER 才能交给 GDI+。
        /// </summary>
        private static Bitmap BuildBitmapFromDib(byte[] dib)
        {
            try
            {
                int biWidth = BitConverter.ToInt32(dib, 4);
                int biHeight = BitConverter.ToInt32(dib, 8);
                ushort biBitCount = BitConverter.ToUInt16(dib, 14);
                uint biSizeImage = BitConverter.ToUInt32(dib, 20);

                if (biWidth <= 0 || biHeight == 0) return null;
                if (biBitCount != 1 && biBitCount != 4 && biBitCount != 8 &&
                    biBitCount != 16 && biBitCount != 24 && biBitCount != 32) return null;

                // 低位图（≤8bpp）后面跟着调色板，要一起算进文件头偏移
                int colorTableSize = biBitCount < 9 ? (1 << biBitCount) * 4 : 0;

                // 某些 DWG 不填 biSizeImage，用实际数据长度兜底
                uint dataLength = biSizeImage;
                if (dataLength == 0)
                {
                    long rest = dib.Length - 40 - colorTableSize;
                    if (rest <= 0) return null;
                    dataLength = (uint)rest;
                }

                using (MemoryStream ms = new MemoryStream())
                using (BinaryWriter bw = new BinaryWriter(ms))
                {
                    bw.Write((ushort)0x4D42);                                  // 'BM'
                    bw.Write((uint)(54 + colorTableSize + dataLength));        // 文件总长
                    bw.Write((ushort)0);                                       // 保留
                    bw.Write((ushort)0);
                    bw.Write((uint)(54 + colorTableSize));                     // 像素数据起点
                    bw.Write(dib);
                    bw.Flush();

                    ms.Position = 0;
                    return Decode(ms);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 解码并复制一份。为什么要复制：GDI+ 从流构造的 Bitmap 理论上要求
        /// 流在其生命周期内保持有效，而这里的 MemoryStream 马上就要释放。
        /// 复制一份彻底断开关联，后续缩放 / 裁剪 / 落盘都安全。
        /// </summary>
        private static Bitmap Decode(byte[] bytes)
        {
            try
            {
                using (MemoryStream ms = new MemoryStream(bytes, false))
                {
                    return Decode(ms);
                }
            }
            catch
            {
                return null;
            }
        }

        private static Bitmap Decode(Stream stream)
        {
            try
            {
                using (Image tmp = Image.FromStream(stream))
                {
                    return new Bitmap(tmp);
                }
            }
            catch
            {
                return null;
            }
        }

        private static byte[] ReadAt(Stream fs, int start, int size)
        {
            fs.Position = start;
            byte[] buffer = new byte[size];
            if (ReadFully(fs, buffer, 0, size) != size) return null;
            return buffer;
        }

        /// <summary>循环读满为止（FileStream 单次 Read 不保证读够）。</summary>
        private static int ReadFully(Stream fs, byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = fs.Read(buffer, offset + total, count - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }
    }
}
