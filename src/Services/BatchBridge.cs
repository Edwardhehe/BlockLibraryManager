using System;

namespace BlockLibraryManager
{
    /// <summary>
    /// 面板 → 命令 的"批量入库"请求桥（与 InsertBridge / PasteBridge 同一模式）。
    ///
    /// 面板是非模态的，框选图块必须发生在命令上下文中，
    /// 这里只暂存目标目录，由 Document.SendStringToExecute 触发
    /// BLOCKLIBBATCH 命令去真正执行。
    /// </summary>
    internal static class BatchBridge
    {
        private static string _targetDir;

        /// <summary>暂存一次批量入库请求。</summary>
        public static void Request(string targetDir)
        {
            _targetDir = string.IsNullOrEmpty(targetDir) ? null : targetDir;
        }

        /// <summary>取出请求。取走后自动清空，避免重复执行。</summary>
        public static bool Take(out string targetDir)
        {
            targetDir = _targetDir;
            _targetDir = null;
            return !string.IsNullOrEmpty(targetDir);
        }

        public static void Clear()
        {
            _targetDir = null;
        }
    }
}
