using System;
using System.Runtime.InteropServices;

namespace BlockLibraryManager
{
    /// <summary>
    /// 取"下载"文件夹路径。
    /// .NET Framework 的 Environment.SpecialFolder 枚举里没有 Downloads，
    /// 只能走 shell32!SHGetKnownFolderPath（known folder GUID 全版本通用）。
    /// 取不到时退回 用户目录\Downloads。
    /// </summary>
    internal static class KnownFolders
    {
        /// <summary>Downloads 的 known folder GUID：{374DE290-123F-4565-9164-39C4925E467B}</summary>
        private static readonly Guid DownloadsFolderId =
            new Guid("374DE290-123F-4565-9164-39C4925E467B");

        private const uint KF_FLAG_DEFAULT = 0;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetKnownFolderPath(
            ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr pv);

        /// <summary>当前用户的"下载"文件夹完整路径。取不到时退回 Profile\Downloads。</summary>
        public static string GetDownloadsPath()
        {
            try
            {
                Guid folderId = DownloadsFolderId;   // ref 参数不能传 readonly 字段，先拷贝
                IntPtr ptr;
                int hr = SHGetKnownFolderPath(ref folderId, KF_FLAG_DEFAULT, IntPtr.Zero, out ptr);
                if (hr == 0 && ptr != IntPtr.Zero)
                {
                    string path = Marshal.PtrToStringUni(ptr);
                    CoTaskMemFree(ptr);
                    if (!string.IsNullOrEmpty(path)) return path;
                }
            }
            catch { }

            try
            {
                return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads");
            }
            catch
            {
                return null;
            }
        }
    }
}
