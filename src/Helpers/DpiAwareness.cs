using System;
using System.Runtime.InteropServices;

namespace BlockLibraryManager
{
    /// <summary>
    /// 窗口级 Per-Monitor V2 DPI 感知切换。
    ///
    /// 为什么要它：多联屏的缩放比例往往不同（主屏 100% + 副屏 150%）。
    /// 插件窗口默认继承宿主进程的 DPI 感知（System-Aware），这种窗口被移到
    /// 更高 DPI 的屏幕上时，Windows 对它做**位图拉伸**——内容变糊，
    /// 且物理尺寸被放大，直接溢出屏幕（用户看到的"最大化后显示不全 /
    /// 底部超出屏幕"）。而坐标又是按主屏 DPI 报告的，最大化落点在另一块
    /// 屏上就会"消失"。
    ///
    /// 做法：插件窗口是独立的顶层窗口，可以在**创建它的一瞬间**把当前线程
    /// 的 DPI 感知上下文切到 Per-Monitor V2，窗口创建完立刻恢复——
    /// 只影响这一个窗口，不碰 CAD 进程其余部分的 DPI 行为。
    /// 需要 Win10 1607+（SetThreadDpiAwarenessContext）；老系统上调用失败
    /// 只是退回原行为，不影响任何功能。
    /// </summary>
    internal static class DpiAwareness
    {
        // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        private static readonly IntPtr PerMonitorV2 = new IntPtr(-4);

        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

        /// <summary>
        /// 把当前线程切到 Per-Monitor V2。返回需要恢复的旧上下文句柄；
        /// 切换失败（Win10 1607 以前）返回 IntPtr.Zero，此时无需恢复。
        /// </summary>
        internal static IntPtr EnterPerMonitorV2()
        {
            try
            {
                // 失败时 API 返回 NULL 且不改变上下文，直接按"未切换"处理
                return SetThreadDpiAwarenessContext(PerMonitorV2);
            }
            catch (EntryPointNotFoundException)
            {
                return IntPtr.Zero;     // Win8.1 及更早：没有该 API，保持原状
            }
            catch (System.Exception)
            {
                return IntPtr.Zero;
            }
        }

        /// <summary>恢复 EnterPerMonitorV2 返回的旧上下文。</summary>
        internal static void Restore(IntPtr oldContext)
        {
            if (oldContext == IntPtr.Zero) return;
            try { SetThreadDpiAwarenessContext(oldContext); }
            catch { }
        }
    }
}
