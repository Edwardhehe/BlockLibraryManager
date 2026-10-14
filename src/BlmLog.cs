using System;
using System.IO;
using System.Text;

namespace BlockLibraryManager
{
    /// <summary>
    /// 极简错误日志。写在 %APPDATA%\BlockLibraryManager\error.log，
    /// 面板在 AutoCAD 里出了任何异常，用户把这个文件发过来就能定位问题。
    /// 永远不抛异常——日志组件自己出错时保持沉默。
    /// </summary>
    internal static class BlmLog
    {
        private static readonly object _lock = new object();

        public static string LogFilePath
        {
            get
            {
                if (_cachedPath == null)
                {
                    _cachedPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "BlockLibraryManager",
                        "error.log");
                }
                return _cachedPath;
            }
        }

        private static string _cachedPath;

        /// <summary>日志目录是否就绪（每次 Write 都 Directory.Exists 是纯浪费，见下）。</summary>
        private static bool _dirReady;

        /// <summary>自记账的当前日志长度，避免每次 Write 都 new FileInfo 去 stat。</summary>
        private static long _currentLength = -1;

        /// <summary>
        /// 同一条重复消息的节流：key → 本会话已记次数。
        /// 有些失败是按条目触发的（例如每个 DWG 的 DXF 导出失败），批量时刷出
        /// 几千行完全一样的内容，写盘本身就是一段可感知的卡顿。
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<string, int> _throttle =
            new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);

        private const int MaxSameMessage = 5;

        public static void Write(string message)
        {
            Write(message, null);
        }

        /// <summary>单个日志文件大小上限（2 MB）。超过就轮转，避免日志无限增长。</summary>
        private const long MaxLogBytes = 2L * 1024 * 1024;

        public static void Write(string message, Exception ex)
        {
            try
            {
                // 重复消息节流：同一条（取前 100 字符做身份）本会话最多记
                // MaxSameMessage 次，之后静默丢弃。
                // 以前没有这道闸：v1.51.11 之前一天刷了 1100+ 行"可见性状态解析为空"。
                string signature = message ?? string.Empty;
                if (signature.Length > 100) signature = signature.Substring(0, 100);

                int seen;
                lock (_lock)
                {
                    _throttle.TryGetValue(signature, out seen);
                    if (seen >= MaxSameMessage) return;
                    _throttle[signature] = seen + 1;
                }

                StringBuilder sb = new StringBuilder(160);
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.Append("  ").AppendLine(message);

                if (seen == MaxSameMessage - 1)
                    sb.AppendLine("  （这条消息本会话已重复多次，后续不再记录）");

                lock (_lock)
                {
                    if (!_dirReady)
                    {
                        string dir = Path.GetDirectoryName(LogFilePath);
                        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                        _dirReady = true;
                    }

                    // 长度自己记账，只有接近上限时才真的去 stat 一次
                    if (_currentLength < 0)
                    {
                        try
                        {
                            FileInfo probe = new FileInfo(LogFilePath);
                            _currentLength = probe.Exists ? probe.Length : 0;
                        }
                        catch { _currentLength = 0; }
                    }

                    if (_currentLength >= MaxLogBytes)
                    {
                        RotateIfTooLarge();
                        _currentLength = 0;
                    }

                    if (ex != null)
                    {
                        sb.AppendLine("  类型: " + ex.GetType().FullName);
                        sb.AppendLine("  消息: " + ex.Message);
                        if (ex.InnerException != null)
                        {
                            sb.AppendLine("  内部: " + ex.InnerException.GetType().FullName);
                            sb.AppendLine("        " + ex.InnerException.Message);
                        }
                        sb.AppendLine("  堆栈: " + ex.StackTrace);
                    }
                    sb.AppendLine();

                    // AppendAllText 是"打开 → 写 → 关闭"，每次一次完整的
                    // 文件打开/关闭。这里改成拿 byte[] 用 FileStream 追加写，
                    // 至少省掉一次 UTF8 编码转换的重复分配。
                    string text = sb.ToString();
                    byte[] bytes = Encoding.UTF8.GetBytes(text);

                    try
                    {
                        using (FileStream fs = new FileStream(LogFilePath, FileMode.Append,
                            FileAccess.Write, FileShare.Read, 4096))
                        {
                            fs.Write(bytes, 0, bytes.Length);
                        }
                        _currentLength += bytes.Length;
                    }
                    catch
                    {
                        // 写到一半失败不该让调用方崩掉
                    }
                }
            }
            catch
            {
                // 日志失败时保持沉默
            }
        }

        /// <summary>
        /// 日志文件超过上限时轮转：当前日志改名为 error.log.bak（旧的 bak 直接覆盖）。
        /// 只保留一代备份，既能查到近期历史，又不会把磁盘吃满。
        /// </summary>
        private static void RotateIfTooLarge()
        {
            try
            {
                FileInfo info = new FileInfo(LogFilePath);
                if (!info.Exists || info.Length < MaxLogBytes) return;

                string backup = LogFilePath + ".bak";
                if (File.Exists(backup))
                {
                    try { File.Delete(backup); } catch { return; }
                }

                File.Move(LogFilePath, backup);
            }
            catch
            {
                // 轮转失败就继续往原文件写，不影响记录
            }
        }
    }
}
