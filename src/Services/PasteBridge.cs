using System;

namespace BlockLibraryManager
{
    /// <summary>
    /// 面板 → 命令 的"粘贴入库"请求桥（与 InsertBridge 同一模式）。
    ///
    /// 面板是非模态的，保存剪贴板图块需要运行在命令上下文中，
    /// 这里只暂存目标目录，由 Document.SendStringToExecute 触发
    /// BLOCKLIBPASTE 命令去真正执行。
    /// </summary>
    internal static class PasteBridge
    {
        private static string _targetDir;
        private static bool _pending;

        /// <summary>暂存一次粘贴入库请求。</summary>
        public static void Request(string targetDir)
        {
            _targetDir = string.IsNullOrEmpty(targetDir) ? null : targetDir;
            _pending = _targetDir != null;
        }

        /// <summary>取出请求。取走后自动清空，避免重复执行。</summary>
        public static bool Take(out string targetDir)
        {
            targetDir = _targetDir;
            _targetDir = null;
            _pending = false;
            return !string.IsNullOrEmpty(targetDir);
        }

        public static void Clear()
        {
            _targetDir = null;
            _pending = false;
        }
    }
}
