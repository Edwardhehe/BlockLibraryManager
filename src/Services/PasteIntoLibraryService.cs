using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;

namespace BlockLibraryManager
{
    /// <summary>
    /// "粘贴入库"实现：把用户在 AutoCAD 里 Ctrl+C 复制的图形（图块）
    /// 保存为一个 DWG 文件，放进图库的当前目录。
    ///
    /// 流程（命令队列接力，三步）：
    ///   1. BLOCKLIBPASTE（BeginPaste）：记录当前空间里已有的实体，
    ///      然后向命令队列追加 "_.PASTECLIP 0,0" 和 "BLOCKLIBPASTE2"；
    ///   2. PASTECLIP 作为顶层命令执行，命令行用 0,0 应答"指定插入点"提示。
    ///      （不能用 ed.Command 嵌套调用：PASTECLIP 会要插入点，
    ///       嵌套时不给点就抛异常，这就是之前保存不了的原因。）
    ///   3. BLOCKLIBPASTE2（CompletePaste）：PASTECLIP 一结束就被队列调起，
    ///      对比得到新粘进来的实体 → 块名（块参照用其块名，否则"粘贴图块"，
    ///      同名文件追加日期数字/时间/序号）→ WblockCloneObjects 克隆到新
    ///      Database → SaveAs 成 AutoCAD 2007（AC1021）格式 → 删掉临时粘贴的实体。
    /// </summary>
    public static class PasteIntoLibraryService
    {
        private static string _pendingDir;
        private static List<ObjectId> _beforeIds;

        /// <summary>清掉一次粘贴入库的中间状态（成功、失败、取消都要走到）。</summary>
        private static void Reset()
        {
            _pendingDir = null;
            _beforeIds = null;
        }

        /// <summary>
        /// 第一步：记录基准实体并触发 PASTECLIP。
        /// 必须在命令上下文中调用。返回 false 时 message 带错误说明。
        /// </summary>
        public static bool BeginPaste(string targetDir, out string message)
        {
            message = null;

            if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir))
            {
                message = "目标目录不存在：" + targetDir;
                return false;
            }

            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                message = "请先在 AutoCAD 中打开一个图形文件。";
                return false;
            }

            _pendingDir = targetDir;
            _beforeIds = CollectSpaceEntityIds(doc.Database);

            // 本命令结束后，AutoCAD 按队列依次执行：
            //   PASTECLIP（"0,0" 应答插入点提示）→ BLOCKLIBPASTE2（收尾保存）
            doc.SendStringToExecute("_.PASTECLIP 0,0 BLOCKLIBPASTE2\n", true, false, false);
            return true;
        }

        /// <summary>
        /// 第三步：由命令 BLOCKLIBPASTE2 调用（PASTECLIP 结束后队列自动调起）。
        /// </summary>
        public static void CompletePaste()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;

            // 早退也必须给面板一个回话：否则状态栏会永远停在
            // "正在把剪贴板中的图块保存到…"，用户以为插件卡死了。
            if (doc == null)
            {
                Reset();
                LibraryPalette.ReportPaste(false, "没有活动的图形，粘贴入库已取消。");
                return;
            }

            if (string.IsNullOrEmpty(_pendingDir))
            {
                Reset();
                LibraryPalette.ReportPaste(false, "粘贴入库超时或已被取消，请重新执行一次。");
                return;
            }

            string savedPath;
            string message;
            bool ok = FinishPaste(doc, out savedPath, out message);

            LibraryPalette.ReportPaste(ok, message);

            if (!string.IsNullOrEmpty(message))
            {
                try
                {
                    Editor ed = doc.Editor;
                    if (ed != null) ed.WriteMessage("\n" + message);
                }
                catch { }
            }
        }

        /// <summary>对比、克隆、保存、清理。</summary>
        private static bool FinishPaste(Document doc, out string savedPath, out string message)
        {
            savedPath = null;
            string targetDir = _pendingDir;
            List<ObjectId> before = _beforeIds;
            Reset();

            if (string.IsNullOrEmpty(targetDir) || before == null)
            {
                message = "粘贴入库状态丢失，请重试。";
                return false;
            }

            try
            {
                using (DocumentLock lockDoc = doc.LockDocument())
                {
                    Database db = doc.Database;

                    // 1. 找出新粘进来的实体
                    List<ObjectId> pasted = new List<ObjectId>();
                    foreach (ObjectId id in CollectSpaceEntityIds(db))
                    {
                        if (!before.Contains(id)) pasted.Add(id);
                    }

                    if (pasted.Count == 0)
                    {
                        message = "剪贴板中没有可粘贴的 AutoCAD 图形数据（请先在 CAD 中复制图块或图形）。";
                        return false;
                    }

                    // 2. 定文件名：优先用块参照的块名
                    string baseName = ResolveBaseName(db, pasted);
                    string path = BuildUniquePath(targetDir, baseName);

                    // 3. 克隆到新库并保存为 2007 格式
                    try
                    {
                        using (Database tmp = new Database(true, true))
                        {
                            ObjectIdCollection ids = new ObjectIdCollection();
                            foreach (ObjectId id in pasted) ids.Add(id);

                            IdMapping mapping = new IdMapping();
                            db.WblockCloneObjects(ids, tmp.CurrentSpaceId, mapping,
                                DuplicateRecordCloning.Ignore, false);

                            tmp.SaveAs(path, DwgVersion.AC1021);
                        }
                    }
                    catch (System.Exception ex)
                    {
                        message = "保存 DWG 失败：" + ex.Message;
                        BlmLog.Write("PasteIntoLibrary SaveAs 失败: " + ex.Message);
                        return false;
                    }

                    // 4. 删掉临时粘进当前图形的实体
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        foreach (ObjectId id in pasted)
                        {
                            Entity entity = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                            if (entity != null) entity.Erase();
                        }
                        tr.Commit();
                    }

                    savedPath = path;
                    message = "粘贴入库成功：" + Path.GetFileName(path) +
                              "（已保存为 AutoCAD 2007 格式）";
                    return true;
                }
            }
            catch (System.Exception ex)
            {
                message = "粘贴入库失败：" + ex.Message;
                BlmLog.Write("PasteIntoLibrary 失败: " + ex.Message);
                return false;
            }
            finally
            {
                // 无论成功失败，状态只消费一次
                _pendingDir = null;
                _beforeIds = null;
            }
        }

        /// <summary>列出当前空间里的所有实体 Id。</summary>
        private static List<ObjectId> CollectSpaceEntityIds(Database db)
        {
            List<ObjectId> ids = new List<ObjectId>();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTableRecord space =
                    (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);

                foreach (ObjectId id in space) ids.Add(id);

                tr.Commit();
            }

            return ids;
        }

        /// <summary>
        /// 决定保存用的文件名：粘进来的是块参照 → 用它的块名；
        /// 不是块 → "粘贴图块"。
        /// </summary>
        private static string ResolveBaseName(Database db, List<ObjectId> pasted)
        {
            string name = null;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in pasted)
                {
                    BlockReference br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (br == null) continue;

                    name = br.Name;
                    break;
                }
                tr.Commit();
            }

            if (string.IsNullOrEmpty(name)) name = "粘贴图块";

            // 去掉不能出现在文件名里的字符
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');

            name = name.Trim();
            if (name.Length == 0) name = "粘贴图块";

            return name;
        }

        /// <summary>
        /// 目标目录里取一个不重名的路径：
        ///   块名.dwg → 块名_20261009.dwg → 块名_20261009_104612.dwg → …_2.dwg
        /// </summary>
        private static string BuildUniquePath(string dir, string baseName)
        {
            string candidate = Path.Combine(dir, baseName + ".dwg");
            if (!File.Exists(candidate)) return candidate;

            // 同名：追加日期数字
            string stamp = DateTime.Now.ToString("yyyyMMdd");
            candidate = Path.Combine(dir, baseName + "_" + stamp + ".dwg");
            if (!File.Exists(candidate)) return candidate;

            // 日期也撞了：再加时间
            stamp = stamp + "_" + DateTime.Now.ToString("HHmmss");
            candidate = Path.Combine(dir, baseName + "_" + stamp + ".dwg");
            if (!File.Exists(candidate)) return candidate;

            // 还撞：加序号兜底
            for (int i = 2; i < 1000; i++)
            {
                candidate = Path.Combine(dir, baseName + "_" + stamp + "_" + i + ".dwg");
                if (!File.Exists(candidate)) return candidate;
            }

            return Path.Combine(dir, Guid.NewGuid().ToString("N") + ".dwg");
        }
    }
}
