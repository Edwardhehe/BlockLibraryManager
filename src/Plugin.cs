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
                AutoLoadRegister.EnsureRegistered();
            }
            catch
            {
                // 登记失败只影响"下次自动加载"，不影响本次使用
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
                LibraryPalette.Shutdown();
            }
            catch
            {
                // 退出阶段不能再抛异常
            }
        }
    }
}
