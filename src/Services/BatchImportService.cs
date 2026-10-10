using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;

namespace BlockLibraryManager
{
    /// <summary>
    /// "批量入库"实现。
    ///
    /// 流程：
    ///   1. 用户在面板点「批量入库」→ 选目标文件夹 → 面板最小化 →
    ///      SendStringToExecute 触发 BLOCKLIBBATCH（目标目录经 BatchBridge 暂存）；
    ///   2. 本类 Run() 在命令上下文中提示用户框选图块（只过滤块参照 INSERT）；
    ///   3. 逐个把选中的块参照克隆进新 Database，按块名保存为
    ///      AutoCAD 2007（AC1021）格式 DWG 到目标目录；
    ///      重名文件自动追加日期数字 / 时间 / 序号；
    ///   4. 处理较多时显示进度条（主线程处理 + DoEvents 刷新）；
    ///   5. 结束后面板自动恢复显示并刷新图库。
    /// </summary>
    public static class BatchImportService
    {
        /// <summary>
        /// 命令入口（BLOCKLIBBATCH）。必须在命令上下文中调用。
        /// </summary>
        public static void Run(string targetDir)
        {
            Autodesk.AutoCAD.ApplicationServices.Document doc =
                Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;

            if (doc == null)
            {
                Report(false, "请先在 AutoCAD 中打开一个图形文件。");
                return;
            }

            if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir))
            {
                Report(false, "目标目录不存在：" + targetDir);
                return;
            }

            Editor ed = doc.Editor;

            try
            {
                // 1. 提示用户框选图块（只选块参照）
                PromptSelectionOptions pso = new PromptSelectionOptions();
                pso.MessageForAdding = "\n框选需要批量入库的图块（仅块参照），回车或右键结束选择: ";
                pso.AllowDuplicates = false;
                pso.SingleOnly = false;

                SelectionFilter filter = new SelectionFilter(new TypedValue[]
                {
                    new TypedValue((int)DxfCode.Start, "INSERT")
                });

                PromptSelectionResult sel = ed.GetSelection(pso, filter);
                if (sel.Status != PromptStatus.OK || sel.Value == null || sel.Value.Count == 0)
                {
                    Report(false, "未选择任何图块，批量入库已取消。");
                    return;
                }

                // 2. 逐个保存（主线程处理；用小进度窗 + DoEvents 刷新界面）
                int total = sel.Value.Count;
                int saved = 0;
                int failed = 0;
                string lastSaved = null;
                List<string> failures = new List<string>();

                using (Autodesk.AutoCAD.ApplicationServices.DocumentLock lockDoc = doc.LockDocument())
                {
                    Database db = doc.Database;
                    ObjectId[] ids = sel.Value.GetObjectIds();

                    using (BatchProgressForm progress = new BatchProgressForm())
                    {
                        progress.Show();

                        for (int i = 0; i < ids.Length; i++)
                        {
                            progress.SetProgress(i, total, "正在入库 " + (i + 1) + " / " + total + " …");
                            System.Windows.Forms.Application.DoEvents();

                            string savedPath;
                            string err;
                            if (SaveOne(db, ids[i], targetDir, out savedPath, out err))
                            {
                                saved++;
                                lastSaved = savedPath;
                            }
                            else
                            {
                                failed++;
                                if (failures.Count < 8) failures.Add(err);
                            }

                            progress.SetProgress(i + 1, total, "正在入库 " + (i + 1) + " / " + total + " …");
                            System.Windows.Forms.Application.DoEvents();
                        }

                        progress.SetProgress(total, total, "完成");
                    }
                }

                // 3. 汇总
                string message;
                if (saved == 0)
                {
                    message = "批量入库失败：没有成功保存任何图块。"
                            + (failures.Count > 0 ? "\n原因：" + failures[0] : string.Empty);
                }
                else
                {
                    message = "批量入库完成：成功 " + saved + " 个" +
                              (failed > 0 ? "，失败 " + failed + " 个" : string.Empty) +
                              " → " + targetDir;
                    if (failed > 0)
                    {
                        for (int i = 0; i < failures.Count; i++)
                            message += "\n失败原因" + (i + 1) + "：" + failures[i];
                    }
                }

                Report(saved > 0, message);

                try
                {
                    if (!string.IsNullOrEmpty(lastSaved))
                        ed.WriteMessage("\n[图库管理器] 最后保存：" + lastSaved);
                    ed.WriteMessage("\n" + message);
                }
                catch { }
            }
            catch (System.Exception ex)
            {
                BlmLog.Write("批量入库异常: " + ex.Message);
                Report(false, "批量入库异常：" + ex.Message);
            }
        }

        /// <summary>把一个块参照保存为独立的 2007 格式 DWG。</summary>
        private static bool SaveOne(Database db, ObjectId brId, string targetDir, out string savedPath, out string error)
        {
            savedPath = null;
            error = null;

            try
            {
                string baseName;
                BlockTableRecord def;

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockReference br = tr.GetObject(brId, OpenMode.ForRead) as BlockReference;
                    if (br == null)
                    {
                        error = "所选对象不是块参照";
                        tr.Commit();
                        return false;
                    }

                    def = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);

                    if (def.IsFromExternalReference)
                    {
                        error = "外部参照不能入库：" + def.Name;
                        tr.Commit();
                        return false;
                    }
                    if (def.IsLayout)
                    {
                        error = "布局块不能入库";
                        tr.Commit();
                        return false;
                    }

                    baseName = GetBlockFileName(br, def, tr);
                    tr.Commit();
                }

                string path = BuildUniquePath(targetDir, baseName);

                // 克隆块参照（连带块定义与嵌套依赖）到新库，保存为 2007 格式
                using (Database newDb = new Database(true, true))
                {
                    ObjectIdCollection ids = new ObjectIdCollection();
                    ids.Add(brId);

                    IdMapping mapping = new IdMapping();
                    db.WblockCloneObjects(ids, newDb.CurrentSpaceId, mapping,
                        DuplicateRecordCloning.Ignore, false);

                    newDb.SaveAs(path, DwgVersion.AC1021);
                }

                savedPath = path;
                return true;
            }
            catch (System.Exception ex)
            {
                error = ex.Message;
                BlmLog.Write("批量入库单项失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 取块名作为文件名。动态块的 Name 是匿名名（*U…），
        /// 优先从 DynamicBlockTableRecord（原始具名定义）取真实名，失败退回 Name。
        /// </summary>
        private static string GetBlockFileName(BlockReference br, BlockTableRecord def, Transaction tr)
        {
            string name = null;

            try
            {
                if (br.IsDynamicBlock && !br.DynamicBlockTableRecord.IsNull)
                {
                    BlockTableRecord dynBtr =
                        (BlockTableRecord)tr.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead);
                    if (dynBtr != null) name = dynBtr.Name;
                }
            }
            catch { }

            if (string.IsNullOrEmpty(name))
            {
                try { name = def.Name; } catch { }
            }

            if (string.IsNullOrEmpty(name)) name = "图块";

            // 去掉不能出现在文件名里的字符
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');

            name = name.Trim();
            if (name.Length == 0) name = "图块";

            return name;
        }

        /// <summary>
        /// 目标目录里取一个不重名的路径：
        ///   块名.dwg → 块名_20261009.dwg → 块名_20261009_114231.dwg → …_2.dwg
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

        /// <summary>结束后回传面板（恢复显示 + 刷新）。</summary>
        private static void Report(bool succeeded, string message)
        {
            LibraryPalette.ReportBatch(succeeded, message);
        }
    }

    /// <summary>
    /// 批量入库的简易进度窗。
    /// 处理必须发生在 CAD 主线程（AutoCAD API 不允许跨线程），
    /// 所以这里用"显示窗体 + DoEvents 刷新"而不是后台线程。
    /// </summary>
    internal sealed class BatchProgressForm : Form
    {
        private readonly Label _lblTitle;
        private readonly Label _lblDetail;
        private readonly ProgressBar _bar;

        public BatchProgressForm()
        {
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ControlBox = false;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            ClientSize = new Size(360, 108);
            Text = "批量入库";

            _lblTitle = new Label();
            _lblTitle.Text = "正在批量入库…";
            _lblTitle.Location = new Point(16, 12);
            _lblTitle.Size = new Size(328, 18);

            _bar = new ProgressBar();
            _bar.Location = new Point(16, 40);
            _bar.Size = new Size(328, 22);
            _bar.Minimum = 0;
            _bar.Maximum = 100;

            _lblDetail = new Label();
            _lblDetail.Text = string.Empty;
            _lblDetail.ForeColor = SystemColors.GrayText;
            _lblDetail.Location = new Point(16, 72);
            _lblDetail.Size = new Size(328, 18);

            Controls.Add(_lblTitle);
            Controls.Add(_bar);
            Controls.Add(_lblDetail);
        }

        /// <summary>更新进度（done/total），detail 为说明文字。</summary>
        public void SetProgress(int done, int total, string detail)
        {
            if (total <= 0) total = 1;

            int percent = done * 100 / total;
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;

            _bar.Value = percent;
            _lblDetail.Text = detail;
        }
    }
}
