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
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "BlockLibraryManager",
                    "error.log");
            }
        }

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
                lock (_lock)
                {
                    string dir = Path.GetDirectoryName(LogFilePath);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                    RotateIfTooLarge();

                    StringBuilder sb = new StringBuilder();
                    sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    sb.Append("  ").AppendLine(message);
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

                    File.AppendAllText(LogFilePath, sb.ToString(), Encoding.UTF8);
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
