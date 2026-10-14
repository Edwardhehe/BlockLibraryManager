using System;
using Autodesk.AutoCAD.Runtime;

// 声明插件入口与命令所在类。
// CommandClass 让 AutoCAD 只扫描这一个类型里的 CommandMethod，加载更快。
[assembly: ExtensionApplication(typeof(BlockLibraryManager.Plugin))]
[assembly: CommandClass(typeof(BlockLibraryManager.Commands))]

namespace BlockLibraryManager
{
    /// <summary>
    /// 插件入口。
    ///
    /// 这里刻意不做任何"重活"：面板延迟到用户第一次执行 BLOCKLIB 时才创建，
    /// 避免拖慢 AutoCAD 启动。Initialize 里也不抛异常——
    /// 一旦这里抛错，AutoCAD 会认为插件加载失败，后续命令就都注册不上了。
    /// </summary>
    public class Plugin : IExtensionApplication
    {
        public void Initialize()
        {
            try
            {
                // 自定义启动命令：读配置挂 UnknownCommand 事件（修改后下次启动生效）
                Commands.HookCustomLaunchCommand(LibraryConfig.Load().LaunchCommand);
            }
            catch
            {
                // 自定义命令挂不上不影响插件其它功能
            }

            try
            {
                // 中望 / 浩辰：把自己登记进"随 CAD 启动自动加载"的 HKCU 注册表。
                // 用户手动加载一次后，以后重启 CAD 不再需要 netload / APPLOAD。
                // AutoCAD / BricsCAD 上是空方法（前者走 bundle 自动加载）。
                //
                // v1.51.12：丢到线程池做。
                // EnsureRegistered 内部是"产品 × 版本 × 语言"三层嵌套枚举 + 每个组合
                // 一次可写 OpenSubKey / CreateSubKey + 最多 8 次读写。装过几个版本、
                // 几个语言包时就是上百次注册表事务，而 HKCU 在漫游 / 网络 profile 上
                // 一次访问可达数十毫秒 —— Initialize 是 AutoCAD **同步**调用的启动钩子，
                // 在这里做完整句话等同于"CAD 启动雪崩一段"。
                // 登记结果只影响"下一次启动"，晚几百毫秒毫无副作用。
                System.Threading.ThreadPool.QueueUserWorkItem(delegate (object state)
                {
                    try { AutoLoadRegister.EnsureRegistered(); }
                    catch { }
                });
            }
            catch
            {
                // 连线程池都排不上（极端情况）就直接同步来一次，保证功能不丢
                try { AutoLoadRegister.EnsureRegistered(); } catch { }
            }

            try
            {
                Autodesk.AutoCAD.ApplicationServices.Document doc =
                    Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;

                if (doc != null && doc.Editor != null)
                    doc.Editor.WriteMessage("\n[图库管理器] 已加载。输入 BLOCKLIB 打开图库面板。\n");
            }
            catch
            {
                // 只是打印提示信息，失败无所谓
            }
        }

        public void Terminate()
        {
            try
            {
                // v1.51.12：把挂在 DocumentCollection / Document 上的事件摘掉。
                // 以前只 Shutdown 面板，delegate 还挂在文档上：
                // netload 二次加载 / 插件重载后，旧委托会再触发一次 Toggle，
                // 去重建一个已经 Dispose 的窗体。
                Commands.UnhookCustomLaunchCommand();
            }
            catch
            {
                // 退出阶段不能再抛异常
            }

            try
            {
                LibraryPalette.Shutdown();
            }
            catch
            {
                // 退出阶段不能再抛异常
            }
        }
    }
}
