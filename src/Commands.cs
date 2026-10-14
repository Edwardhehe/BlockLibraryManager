using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

namespace BlockLibraryManager
{
    /// <summary>
    /// 对外命令。命令名统一以 BLOCKLIB 开头：
    ///   BLOCKLIB    显示 / 隐藏图库面板（开关）
    ///   BLOCKLIBH   隐藏面板
    ///   BLOCKLIBR   重新扫描图库
    ///   BLOCKLIBS   打开设置
    ///
    /// 自定义启动命令：用户可在"设置"里把启动命令改成任意名字（如 TUKU）。
    /// 2020 版 .NET API 无法在运行时增删 CommandMethod，也没有带 Handled 的
    /// 应用级 UnknownCommand 事件；可用的是 Document.UnknownCommand（文档级）——
    /// 用户输入的命令 CAD 不认识时触发，命中配置的命令名就打开面板。
    /// 命令行仍会打印一条"未知命令"提示，属正常现象，面板会正常弹出。
    /// 配置只在插件启动（Initialize）时读取一次，所以修改后下次启动 CAD 生效。
    /// </summary>
    public static class Commands
    {
        /// <summary>用户配置的自定义启动命令名（大写）。空 = 未启用自定义命令。</summary>
        private static string _customLaunchCommand;

#if BLM_ACAD
        /// <summary>UnknownCommand 事件的统一处理器（静态保证重复挂接可去重）。</summary>
        private static readonly UnknownCommandEventHandler OnDocUnknownCommand =
            new UnknownCommandEventHandler(OnDocumentUnknownCommand);
#else
        /// <summary>
        /// 中望 / 浩辰：这两个平台有没有 UnknownCommand 事件随版本而定，
        /// 因此不走编译期绑定，改用 CadHost 的反射挂接（见 HookDocument）。
        /// </summary>
        private static object _unknownBound;
#endif

        [CommandMethod("BLOCKLIB", CommandFlags.Modal)]
        public static void BlockLib()
        {
            LibraryPalette.Toggle();
        }

        /// <summary>
        /// 插件启动时调用：读配置里的自定义启动命令并给当前及新建文档挂
        /// UnknownCommand 事件。命令名为空或就是 BLOCKLIB 时不挂
        /// （默认命令本身已注册）。
        /// </summary>
        public static void HookCustomLaunchCommand(string commandName)
        {
            try
            {
                string name = NormalizeLaunchCommand(commandName);
                if (string.IsNullOrEmpty(name) || name == "BLOCKLIB") return;

                _customLaunchCommand = name;

                Autodesk.AutoCAD.ApplicationServices.DocumentCollection dm =
                    Application.DocumentManager;
                if (dm == null) return;

                // 已打开 / 稍后新建的文档都挂上（先减后加，防止重复挂接）
                dm.DocumentCreated += new DocumentCollectionEventHandler(OnDocumentCreated);
                HookDocument(dm.MdiActiveDocument);
            }
            catch
            {
                // 挂接失败只影响自定义命令，默认 BLOCKLIB 仍然可用
            }
        }

        /// <summary>
        /// 摘掉自定义启动命令挂上去的全部事件（插件卸载 / Terminate 时调用）。
        /// v1.51.12：以前只在 Plugin.Terminate 里 Shutdown 面板，delegate 还挂着。
        /// netload 二次加载或插件重载后，旧委托随 UnknownCommand 再触发一次
        /// Toggle，去重建一个已经 Dispose 的窗体；而且每个文档一份、只增不减。
        /// </summary>
        public static void UnhookCustomLaunchCommand()
        {
            try
            {
                Autodesk.AutoCAD.ApplicationServices.DocumentCollection dm =
                    Application.DocumentManager;
                if (dm == null) return;

                dm.DocumentCreated -= new DocumentCollectionEventHandler(OnDocumentCreated);

                foreach (Autodesk.AutoCAD.ApplicationServices.Document doc in dm)
                    UnhookDocument(doc);
            }
            catch
            {
                // 卸载阶段忽略
            }

            _customLaunchCommand = null;
        }

        private static void UnhookDocument(Document doc)
        {
            if (doc == null) return;
#if BLM_ACAD
            try { doc.UnknownCommand -= OnDocUnknownCommand; } catch { }
#else
            try
            {
                CadHost.TryUnhook(doc, "UnknownCommand", _unknownBound);
                _unknownBound = null;
            }
            catch { }
#endif
        }

        private static void OnDocumentCreated(object sender, DocumentCollectionEventArgs e)
        {
            try { HookDocument(e != null ? e.Document : null); }
            catch { }
        }

        private static void HookDocument(Document doc)
        {
            if (doc == null) return;
#if BLM_ACAD
            try { doc.UnknownCommand -= OnDocUnknownCommand; } catch { }
            try { doc.UnknownCommand += OnDocUnknownCommand; } catch { }
#else
            try
            {
                CadHost.TryUnhook(doc, "UnknownCommand", _unknownBound);
                _unknownBound = null;
                _unknownBound = CadHost.TryHook(doc, "UnknownCommand", OnUnknownCommandSink);
            }
            catch { }
#endif
        }

#if BLM_ACAD
        private static void OnDocumentUnknownCommand(object sender, UnknownCommandEventArgs e)
        {
            HandleUnknownCommand(e != null ? e.GlobalCommandName : null);
        }
#else
        /// <summary>
        /// 国产平台：GlobalCommandName 属性同样用反射读取，
        /// 避免因为 EventArgs 类型不同而编译不过。
        /// </summary>
        private static void OnUnknownCommandSink(object sender, object args)
        {
            string name = CadHost.TryGetProperty(args, "GlobalCommandName") as string;
            if (name == null) name = CadHost.TryGetProperty(args, "CommandName") as string;
            HandleUnknownCommand(name);
        }
#endif

        private static void HandleUnknownCommand(string commandName)
        {
            try
            {
                if (string.IsNullOrEmpty(commandName)) return;
                if (!string.Equals(commandName, _customLaunchCommand,
                        StringComparison.OrdinalIgnoreCase)) return;

                LibraryPalette.Toggle();
            }
            catch
            {
                // 事件里的异常绝不能冒到宿主 CAD 主循环
            }
        }

        /// <summary>
        /// 清洗用户输入的命令名：去空白、整体转大写。
        /// 为空、超长或含非法字符（空白 / \ / ; 等命令分隔符）时返回 null。
        /// </summary>
        public static string NormalizeLaunchCommand(string text)
        {
            if (text == null) return null;

            string name = text.Trim();
            if (name.Length == 0 || name.Length > 64) return null;

            foreach (char c in name)
            {
                if (char.IsWhiteSpace(c)) return null;
                if (c == '\\' || c == ';' || c == '\'' || c == '"' ||
                    c == '*' || c == '?' || c == ',' || c == '=') return null;
            }

            return name.ToUpperInvariant();
        }

        [CommandMethod("BLOCKLIBH", CommandFlags.Modal)]
        public static void BlockLibHide()
        {
            LibraryPalette.Hide();
        }

        [CommandMethod("BLOCKLIBR", CommandFlags.Modal)]
        public static void BlockLibRefresh()
        {
            if (!LibraryPalette.IsCreated)
            {
                LibraryPalette.Show();
                return;
            }

            LibraryPalette.Refresh();
            WriteLine("图库已重新扫描。");
        }

        [CommandMethod("BLOCKLIBS", CommandFlags.Modal)]
        public static void BlockLibSettings()
        {
            LibraryPalette.ShowSettings();
        }

        /// <summary>
        /// 内部命令：真正执行插入。
        /// 面板按钮不会直接调用，而是通过 SendStringToExecute 触发本命令，
        /// 目的是让交互式取点发生在有效的命令上下文中（详见 InsertBridge 注释）。
        /// </summary>
        [CommandMethod("BLOCKLIBIN", CommandFlags.Modal)]
        public static void BlockLibInsert()
        {
            List<BlockItem> items;
            InsertOptions options;
            bool continuous;

            if (!InsertBridge.Take(out items, out options, out continuous)) return;

            int placed = 0;
            string message = null;

            if (continuous)
            {
                while (true)
                {
                    InsertResult result = BlockInsertService.Insert(items, options);
                    if (!result.Succeeded)
                    {
                        if (placed == 0) message = result.Message;
                        break;
                    }

                    placed++;
                    message = "连续插入完成，共 " + placed + " 个。";
                }
            }
            else
            {
                InsertResult result = BlockInsertService.Insert(items, options);
                placed = result.PlacedCount;
                message = result.Message;
            }

            LibraryPalette.ReportInsert(items, message, placed > 0);

            if (!string.IsNullOrEmpty(message))
                WriteLine(message);
        }

        /// <summary>
        /// 内部命令：粘贴入库。把剪贴板里的 AutoCAD 图形（图块）保存为
        /// AutoCAD 2007 格式的 DWG，放进面板当前选中的分类目录。
        /// 面板通过 PasteBridge 暂存目标目录，再 SendStringToExecute 触发本命令；
        /// 本命令只记录基准实体并触发 PASTECLIP，真正的保存在其结束事件里完成
        /// （PASTECLIP 必须作为顶层命令运行，不能嵌套）。
        /// </summary>
        [CommandMethod("BLOCKLIBPASTE", CommandFlags.Modal)]
        public static void BlockLibPaste()
        {
            string targetDir;
            if (!PasteBridge.Take(out targetDir)) return;

            string message;
            if (!PasteIntoLibraryService.BeginPaste(targetDir, out message))
            {
                LibraryPalette.ReportPaste(false, message);
                if (!string.IsNullOrEmpty(message))
                    WriteLine(message);
            }
        }

        /// <summary>
        /// 内部命令：粘贴入库的收尾。BLOCKLIBPASTE 把
        /// "_.PASTECLIP 0,0 BLOCKLIBPASTE2" 排进命令队列，
        /// PASTECLIP 一结束本命令就被自动调起，完成对比、保存、清理。
        /// 用户不会手动运行它。
        /// </summary>
        [CommandMethod("BLOCKLIBPASTE2", CommandFlags.Modal)]
        public static void BlockLibPasteFinish()
        {
            PasteIntoLibraryService.CompletePaste();
        }

        /// <summary>
        /// 内部命令：批量入库。面板通过 BatchBridge 暂存目标目录后，
        /// SendStringToExecute 触发本命令；命令里提示用户框选图块，
        /// 逐个按块名保存为 AutoCAD 2007 格式 DWG 到目标目录。
        /// </summary>
        [CommandMethod("BLOCKLIBBATCH", CommandFlags.Modal)]
        public static void BlockLibBatch()
        {
            string targetDir;
            if (!BatchBridge.Take(out targetDir))
            {
                WriteLine("批量入库：请通过图库管理器的「批量入库」按钮使用此功能。");
                return;
            }

            BatchImportService.Run(targetDir);
        }

        private static void WriteLine(string message)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            Editor ed = doc.Editor;
            if (ed == null) return;

            ed.WriteMessage("\n" + message);
        }
    }
}
