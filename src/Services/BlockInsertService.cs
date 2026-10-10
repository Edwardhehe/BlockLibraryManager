using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace BlockLibraryManager
{
    /// <summary>插入参数。</summary>
    public sealed class InsertOptions
    {
        public double ScaleX { get; set; }
        public double ScaleY { get; set; }
        public double ScaleZ { get; set; }
        public bool UniformScale { get; set; }
        public double RotationDegrees { get; set; }
        public bool ExplodeAfterInsert { get; set; }

        /// <summary>图块定义已存在时是否更新为图库中的最新版本。</summary>
        public bool UpdateExistingDefinition { get; set; }

        /// <summary>
        /// 勾选后插入的图块归到 0 图层；
        /// 不勾选为使用图块所在图层（保持块定义内实体原有的图层）。
        /// </summary>
        public bool ForceLayerZero { get; set; }

        /// <summary>
        /// 勾选后插入的图块颜色跟随图层颜色（实体颜色改为 ByLayer）；
        /// 不勾选为使用图块原有颜色。
        /// </summary>
        public bool ColorByLayer { get; set; }

        public InsertOptions()
        {
            ScaleX = 1.0;
            ScaleY = 1.0;
            ScaleZ = 1.0;
            UniformScale = true;
            RotationDegrees = 0.0;
            ExplodeAfterInsert = false;
            UpdateExistingDefinition = true;
            ForceLayerZero = false;
            ColorByLayer = false;
        }

        public double GetScaleX()
        {
            if (UniformScale) return ScaleX;
            return ScaleX;
        }
    }

    /// <summary>插入结果。</summary>
    public sealed class InsertResult
    {
        public int PlacedCount { get; internal set; }
        public string Message { get; internal set; }
        public bool Succeeded
        {
            get { return PlacedCount > 0; }
        }
    }

    /// <summary>
    /// 图库 → 当前图形的插入实现。
    ///
    /// 兼容性要点：
    /// - 只用 2010~2027 全版本都存在的 API（Transaction / Database.Insert / EntityJig）；
    /// - 不使用 Editor.Command（2015 才有）；
    /// - "炸开"不走 Entity.Explode()，因为它的签名在各版本间变过，
    ///   改为自行遍历块定义内的实体做深拷贝，行为可控且不依赖版本。
    /// </summary>
    public static class BlockInsertService
    {
        public static InsertResult Insert(List<BlockItem> items, InsertOptions options)
        {
            InsertResult result = new InsertResult();
            if (items == null || items.Count == 0)
            {
                result.Message = "没有可插入的图块。";
                return result;
            }

            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                result.Message = "请先在 AutoCAD 中打开一个图形文件。";
                return result;
            }

            Editor ed = doc.Editor;
            Database destDb = doc.Database;

            int placed = 0;
            string error = null;

            try
            {
                using (DocumentLock lockDoc = doc.LockDocument())
                {
                    foreach (BlockItem item in items)
                    {
                        if (item == null) continue;

                        if (!File.Exists(item.FilePath))
                        {
                            error = "文件不存在：" + item.FilePath;
                            continue;
                        }

                        string blockName = ResolveBlockName(item);
                        if (string.IsNullOrEmpty(blockName))
                        {
                            error = "无法生成有效的块名。";
                            continue;
                        }

                        ObjectId defId;
                        string failReason;
                        double unitScale;

                        if (!TryPrepareDefinition(destDb, item, blockName, options,
                                out defId, out failReason, out unitScale))
                        {
                            error = failReason;
                            continue;
                        }

                        if (TryPlaceReference(doc, ed, destDb, defId, options, unitScale))
                            placed++;
                        else
                            break; // 用户取消后续插入
                    }
                }
            }
            catch (System.Exception ex)
            {
                error = ex.Message;
            }

            result.PlacedCount = placed;
            result.Message = ComposeMessage(items.Count, placed, error);
            return result;
        }

        private static string ComposeMessage(int requestCount, int placed, string error)
        {
            if (placed > 0)
            {
                string msg = "已插入 " + placed + " 个图块。";
                if (!string.IsNullOrEmpty(error)) msg += " 部分失败：" + error;
                return msg;
            }

            if (!string.IsNullOrEmpty(error)) return "插入失败：" + error;
            return "未插入任何图块。";
        }

        // ---------------- 块定义 ----------------

        private static string ResolveBlockName(BlockItem item)
        {
            string raw;
            if (item.IsWholeDrawing)
                raw = Path.GetFileNameWithoutExtension(item.FilePath);
            else
                raw = item.BlockName;

            return SanitizeBlockName(raw);
        }

        /// <summary>块名不允许包含空格与 AutoCAD 保留字符。</summary>
        public static string SanitizeBlockName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;

            char[] invalid = new char[] { '<', '>', '/', '\\', '"', ':', ';', '?', '*', '|', ',', '=', '`', '\'' };

            System.Text.StringBuilder sb = new System.Text.StringBuilder(raw.Length);
            foreach (char c in raw)
            {
                if (c == ' ') { sb.Append('_'); continue; }
                if (Array.IndexOf(invalid, c) >= 0) { sb.Append('_'); continue; }
                sb.Append(c);
            }

            string name = sb.ToString().Trim();
            if (name.Length == 0) name = "Block";
            if (name.Length > 250) name = name.Substring(0, 250);
            return name;
        }

        /// <summary>
        /// 确保目标数据库中存在该图块的定义，返回块表记录 Id。
        /// 整份 DWG：Database.Insert 整体导入为块；
        /// DWG 内部块：跨库 WblockCloneObjects 复制该定义。
        /// </summary>
        /// <remarks>
        /// 以前直接拿到 defId 就完事，结果经常出现"命令提示已插入，画面上什么都没有"。
        /// 三个真实原因在这里一并解决：
        ///   1. 单位不一致（源图毫米、目标图英寸）→ 尺寸差 25.4 倍，看起来像没插进来；
        ///   2. 源图的内容画在离原点很远的世界坐标上 → 插入点选在眼前，图形却在几万单位外；
        ///   3. Database.Insert 偶尔得到空定义 → 退回手写克隆。
        /// </remarks>
        private static bool TryPrepareDefinition(
            Database destDb, BlockItem item, string blockName,
            InsertOptions options, out ObjectId defId, out string reason, out double unitScale)
        {
            defId = ObjectId.Null;
            reason = null;
            unitScale = 1.0;

            // 目标图中已有同名定义，且用户不想更新 —— 直接复用
            if (!options.UpdateExistingDefinition)
            {
                ObjectId reuse = FindExistingDefinition(destDb, blockName);
                if (!reuse.IsNull)
                {
                    defId = reuse;
                    return true;
                }
            }

            Database srcDb = null;

            try
            {
                srcDb = new Database(false, true);
                srcDb.ReadDwgFile(item.FilePath,
                    FileOpenMode.OpenForReadAndReadShare, false, string.Empty);

                unitScale = ComputeUnitScale(srcDb.Insunits, destDb.Insunits);

                if (item.IsWholeDrawing)
                {
                    defId = destDb.Insert(blockName, srcDb, false);

                    // Insert 偶尔会得到空定义（例如源图的布局状态特殊），补一次手写克隆
                    if (!defId.IsNull && CountEntities(destDb, defId) == 0)
                        CloneModelSpaceIntoDefinition(destDb, srcDb, defId);
                }
                else
                {
                    defId = CloneSingleDefinition(destDb, srcDb, item.BlockName);
                }
            }
            catch (System.Exception ex)
            {
                reason = ex.Message;

                // 导入失败时，如果目标图里本来就有同名定义，退回复用它，
                // 总比整次插入失败要好。
                ObjectId existing = FindExistingDefinition(destDb, blockName);
                if (!existing.IsNull)
                {
                    defId = existing;
                    reason = null;
                    return true;
                }
                return false;
            }
            finally
            {
                if (srcDb != null)
                {
                    try { srcDb.CloseInput(true); } catch { }
                    try { srcDb.Dispose(); } catch { }
                }
            }

            if (defId.IsNull)
            {
                ObjectId existing = FindExistingDefinition(destDb, blockName);
                if (!existing.IsNull)
                {
                    defId = existing;
                    return true;
                }

                reason = "未能从文件中取得块定义。";
                return false;
            }

            if (CountEntities(destDb, defId) == 0)
            {
                reason = "该文件的模型空间里没有可插入的实体。";
                return false;
            }

            // 把内容挪到块自己的原点附近，避免插入点看得见图块跑没影
            try { NormalizeDefinitionGeometry(destDb, defId); }
            catch { }

            reason = null;
            return true;
        }

        // ---------------- 单位换算 ----------------

        /// <summary>源文件单位 → 目标文件单位 的换算系数。</summary>
        private static double ComputeUnitScale(UnitsValue sourceUnits, UnitsValue targetUnits)
        {
            double from = UnitsToMeters(sourceUnits);
            double to = UnitsToMeters(targetUnits);

            if (from <= 0 || to <= 0) return 1.0;

            double scale = from / to;
            if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0) return 1.0;
            return scale;
        }

        /// <summary>
        /// 各种单位对"米"的换算。
        /// 刻意用整数比较而不是 UnitsValue.XXX 成员名：数值在所有版本里都一样，
        /// 老版本万一没有某个枚举成员也不会崩。
        /// 0=无单位 1=英寸 2=英尺 3=英里 4=毫米 5=厘米 6=米 7=千米 8=微英寸 9=密尔
        /// 10=码 11=埃 12=纳米 13=微米 14=分米 15=十米 16=百米 17=吉米
        /// 18=天文单位 19=光年 20=秒差距
        /// </summary>
        private static double UnitsToMeters(UnitsValue units)
        {
            switch ((int)units)
            {
                case 1: return 0.0254;
                case 2: return 0.3048;
                case 3: return 1609.344;
                case 4: return 0.001;
                case 5: return 0.01;
                case 6: return 1.0;
                case 7: return 1000.0;
                case 8: return 2.54e-8;
                case 9: return 2.54e-5;
                case 10: return 0.9144;
                case 11: return 1e-10;
                case 12: return 1e-9;
                case 13: return 1e-6;
                case 14: return 0.1;
                case 15: return 10.0;
                case 16: return 100.0;
                case 17: return 1e9;
                case 18: return 1.495978707e11;
                case 19: return 9.46073047258e15;
                case 20: return 3.08567758149137e16;
                default: return 1.0;   // 无单位：不做换算
            }
        }

        // ---------------- 定义内容整理 ----------------

        private static int CountEntities(Database db, ObjectId defId)
        {
            if (defId.IsNull) return 0;

            int count = 0;
            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTableRecord btr = (BlockTableRecord)tr.GetObject(defId, OpenMode.ForRead);
                    foreach (ObjectId id in btr)
                    {
                        if (id.IsErased) continue;
                        count++;
                    }
                    tr.Commit();
                }
            }
            catch
            {
                return 0;
            }
            return count;
        }

        /// <summary>Database.Insert 拿到空定义时的兜底：手工把源图模型空间的实体克隆进该定义。</summary>
        private static bool CloneModelSpaceIntoDefinition(Database destDb, Database srcDb, ObjectId destDefId)
        {
            if (destDefId.IsNull) return false;

            try
            {
                using (Transaction srcTr = srcDb.TransactionManager.StartTransaction())
                using (Transaction tr = destDb.TransactionManager.StartTransaction())
                {
                    BlockTableRecord destBtr =
                        (BlockTableRecord)tr.GetObject(destDefId, OpenMode.ForWrite);

                    ObjectId srcMsId = SymbolUtilityServices.GetBlockModelSpaceId(srcDb);
                    BlockTableRecord srcMs =
                        (BlockTableRecord)srcTr.GetObject(srcMsId, OpenMode.ForRead);

                    ObjectIdCollection ids = new ObjectIdCollection();
                    foreach (ObjectId id in srcMs)
                    {
                        if (id.IsErased) continue;
                        ids.Add(id);
                    }

                    if (ids.Count == 0)
                    {
                        srcTr.Commit();
                        tr.Commit();
                        return false;
                    }

                    IdMapping mapping = new IdMapping();
                    destDb.WblockCloneObjects(ids, destDefId, mapping, DuplicateRecordCloning.Ignore, false);

                    srcTr.Commit();
                    tr.Commit();
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 把块定义内的实体整体平移，使内容的中心回到块的原点（坐标系原点）。
        /// 很多图库 DWG 是按真实世界坐标画的（例如 X=538000, Y=3412000），
        /// 直接插入虽然命令不报错，但图形会被丢到几百公里之外，肉眼一定看不到。
        /// 内容本来就包住原点时保持不动，不破坏正常的块。
        /// </summary>
        private static void NormalizeDefinitionGeometry(Database db, ObjectId defId)
        {
            if (defId.IsNull) return;

            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTableRecord btr = (BlockTableRecord)tr.GetObject(defId, OpenMode.ForWrite);

                    bool any = false;
                    double minX = 0, minY = 0, maxX = 0, maxY = 0;

                    foreach (ObjectId id in btr)
                    {
                        if (id.IsErased) continue;

                        Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;

                        try
                        {
                            Extents3d e = ent.GeometricExtents;
                            if (!any)
                            {
                                minX = e.MinPoint.X; minY = e.MinPoint.Y;
                                maxX = e.MaxPoint.X; maxY = e.MaxPoint.Y;
                                any = true;
                            }
                            else
                            {
                                if (e.MinPoint.X < minX) minX = e.MinPoint.X;
                                if (e.MinPoint.Y < minY) minY = e.MinPoint.Y;
                                if (e.MaxPoint.X > maxX) maxX = e.MaxPoint.X;
                                if (e.MaxPoint.Y > maxY) maxY = e.MaxPoint.Y;
                            }
                        }
                        catch
                        {
                            // 取不到包围盒的实体跳过
                        }
                    }

                    if (!any)
                    {
                        tr.Commit();
                        return;
                    }

                    double width = maxX - minX;
                    double height = maxY - minY;
                    double size = Math.Max(Math.Max(width, height), 1.0);

                    double centerX = (minX + maxX) / 2.0;
                    double centerY = (minY + maxY) / 2.0;

                    // 内容本来就绕着原点画：保持坐标关系不动
                    if (Math.Abs(centerX) <= size && Math.Abs(centerY) <= size)
                    {
                        tr.Commit();
                        return;
                    }

                    Matrix3d move = Matrix3d.Displacement(new Vector3d(-centerX, -centerY, 0.0));

                    foreach (ObjectId id in btr)
                    {
                        if (id.IsErased) continue;

                        Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;

                        try
                        {
                            if (!ent.IsWriteEnabled) ent.UpgradeOpen();
                            ent.TransformBy(move);
                            ent.DowngradeOpen();
                        }
                        catch
                        {
                            // 个别实体挪不动不影响整体
                        }
                    }

                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                BlmLog.Write("整理块定义几何失败", ex);
            }
        }

        /// <summary>在目标数据库中查找同名块定义，不存在返回 ObjectId.Null。</summary>
        private static ObjectId FindExistingDefinition(Database destDb, string blockName)
        {
            if (string.IsNullOrEmpty(blockName)) return ObjectId.Null;

            try
            {
                using (Transaction tr = destDb.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(destDb.BlockTableId, OpenMode.ForRead);
                    ObjectId id = bt.Has(blockName) ? bt[blockName] : ObjectId.Null;
                    tr.Commit();
                    return id;
                }
            }
            catch
            {
                return ObjectId.Null;
            }
        }

        /// <summary>跨数据库复制单个块定义。</summary>
        private static ObjectId CloneSingleDefinition(Database destDb, Database srcDb, string blockName)
        {
            ObjectId result = ObjectId.Null;

            using (Transaction srcTr = srcDb.TransactionManager.StartTransaction())
            {
                BlockTable srcBt = (BlockTable)srcTr.GetObject(srcDb.BlockTableId, OpenMode.ForRead);
                if (!srcBt.Has(blockName)) return ObjectId.Null;

                ObjectIdCollection ids = new ObjectIdCollection();
                ids.Add(srcBt[blockName]);

                IdMapping mapping = new IdMapping();
                srcDb.WblockCloneObjects(ids, destDb.BlockTableId, mapping,
                    DuplicateRecordCloning.Replace, false);

                ObjectId target;
                if (mapping.Contains(srcBt[blockName]))
                {
                    IdPair pair = mapping[srcBt[blockName]];
                    target = pair.Value;
                }
                else
                {
                    target = ObjectId.Null;
                }

                srcTr.Commit();
                result = target;
            }

            return result;
        }

        // ---------------- 放置 ----------------

        private static bool TryPlaceReference(
            Document doc, Editor ed, Database destDb, ObjectId defId,
            InsertOptions options, double unitScale)
        {
            Point3d picked = Point3d.Origin;

            using (Transaction tr = destDb.TransactionManager.StartTransaction())
            {
                BlockTableRecord space =
                    (BlockTableRecord)tr.GetObject(destDb.CurrentSpaceId, OpenMode.ForWrite);

                BlockReference br = new BlockReference(Point3d.Origin, defId);

                double sx = options.ScaleX * unitScale;
                double sy = (options.UniformScale ? options.ScaleX : options.ScaleY) * unitScale;
                double sz = (options.UniformScale ? options.ScaleX : options.ScaleZ) * unitScale;

                // 比例不能为 0，否则实体退化成一个点，看起来就是"什么都没插进来"
                if (Math.Abs(sx) < 1e-9) sx = unitScale;
                if (Math.Abs(sy) < 1e-9) sy = sx;
                if (Math.Abs(sz) < 1e-9) sz = sx;

                br.ScaleFactors = new Scale3d(sx, sy, sz);

                double radians = options.RotationDegrees * Math.PI / 180.0;
                br.Rotation = radians;

                // 按面板勾选调整块定义内实体的图层 / 颜色（放置之前先改定义）
                ApplyLayerColorPolicy(tr, destDb, defId, options);

                // 修复跨库克隆后动态块求值缺失 / 图层被冻结导致的"插入了但不显示"
                RepairDynamicReferences(tr, defId);
                EnsureDefinitionLayersVisible(tr, destDb, defId);

                string layerName = GetCurrentLayerName(destDb, tr);
                if (!string.IsNullOrEmpty(layerName))
                {
                    try { br.Layer = layerName; } catch { }
                }

                space.AppendEntity(br);
                tr.AddNewlyCreatedDBObject(br, true);

                PromptStatus status = PromptStatus.None;

                try
                {
                    BlockPlacementJig jig = new BlockPlacementJig(br, Point3d.Origin);
                    PromptResult drag = ed.Drag(jig);

                    status = drag == null ? PromptStatus.Cancel : drag.Status;
                    picked = jig.Position;
                }
                catch (System.Exception ex)
                {
                    // 拖拽在某些版本 / 焦点状态下会抛 eNotApplicable；
                    // 与其整次插入失败，不如退化成普通取点。
                    BlmLog.Write("拖拽定位失败，改用普通取点", ex);

                    PromptPointOptions ppo = new PromptPointOptions("\n指定插入点 <0,0,0>: ");
                    ppo.AllowNone = true;

                    PromptPointResult ppr = ed.GetPoint(ppo);
                    status = ppr.Status;
                    picked = ppr.Status == PromptStatus.OK ? ppr.Value : Point3d.Origin;
                }

                // 只用 OK 作为成功条件，其余（Esc 取消 / 出错 / 空响应）一律放弃本次放置
                if (status != PromptStatus.OK)
                {
                    tr.Abort();
                    return false;
                }

                // 不管 jig 有没有更新过位置，这里统一再落一次点，保证一定在用户取的位置上
                br.Position = picked;

                if (options.ExplodeAfterInsert)
                {
                    string fail = ExplodeManually(tr, destDb, defId, space, br);
                    if (fail != null)
                        ed.WriteMessage("\n[图库管理器] 炸开失败，已保留为块引用：" + fail);
                }

                tr.Commit();
            }

            // 提交后再刷新一次，老版本经常出现"数据库里有了、屏幕还没重画"的情况
            try { ed.Regen(); } catch { }
            try { ed.UpdateScreen(); } catch { }

            ReportPlacement(ed, picked, unitScale);

            return true;
        }

        /// <summary>
        /// 在命令行输出本次插入的关键信息。
        /// 万一用户还是看不到，至少能知道它到底跑到哪儿去了，并给出下一步提示。
        /// </summary>
        private static void ReportPlacement(Editor ed, Point3d position, double unitScale)
        {
            try
            {
                string tail = string.Empty;

                if (Math.Abs(unitScale - 1.0) > 1e-6)
                    tail = "（源图与目标图单位不同，已按 " + unitScale.ToString("0.####") + " 倍换算）";

                // 插入点不在当前视野里时给个明确提示
                try
                {
                    ViewTableRecord view = ed.GetCurrentView();
                    double halfW = view.Width / 2.0;
                    double halfH = view.Height / 2.0;
                    double cx = view.CenterPoint.X;      // ViewTableRecord.CenterPoint 是 Point2d
                    double cy = view.CenterPoint.Y;

                    bool inView =
                        position.X > cx - halfW && position.X < cx + halfW &&
                        position.Y > cy - halfH && position.Y < cy + halfH;

                    if (!inView)
                        tail = "（插入点不在当前视图范围内，可执行 ZOOM → 范围(E) 查看）";
                }
                catch
                {
                    // 判断失败不影响提示本身
                }

                ed.WriteMessage("\n[图库管理器] 已插入到 (" +
                    position.X.ToString("0.##") + ", " +
                    position.Y.ToString("0.##") + ")" +
                    tail + "\n");
            }
            catch
            {
                // 提示失败无所谓
            }
        }

        /// <summary>
        /// 修复跨库克隆后动态块的求值缺失。
        /// 动态块参照显示的几何存放在匿名块（*U）里；从其它 DWG 克隆过来的
        /// 嵌套动态块参照偶尔会丢失这个匿名块（或其内容为空），表现为
        /// "命令提示插入成功，画面上却什么都没有"。修复方式：发现参照指向的
        /// 显示块为空/缺失时，把参照重新绑定到命名动态块定义上，强制 AutoCAD
        /// 重新求值并重建匿名块。
        /// </summary>
        private static void RepairDynamicReferences(Transaction tr, ObjectId defId)
        {
            try
            {
                HashSet<ObjectId> visited = new HashSet<ObjectId>();
                Queue<ObjectId> pending = new Queue<ObjectId>();
                pending.Enqueue(defId);
                visited.Add(defId);

                while (pending.Count > 0)
                {
                    ObjectId current = pending.Dequeue();
                    if (current.IsNull || current.IsErased) continue;

                    BlockTableRecord btr =
                        (BlockTableRecord)tr.GetObject(current, OpenMode.ForRead);

                    // 先快照再遍历：修复过程会删旧参照、建新参照，不能边遍历边改
                    List<ObjectId> ids = new List<ObjectId>();
                    foreach (ObjectId eid0 in btr) ids.Add(eid0);

                    foreach (ObjectId id in ids)
                    {
                        if (id.IsErased) continue;

                        BlockReference br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                        if (br == null) continue;

                        ObjectId shown = br.BlockTableRecord;          // 实际显示的块（动态块为匿名块）
                        ObjectId named = br.DynamicBlockTableRecord;   // 命名动态块定义

                        bool broken = shown.IsNull || shown.IsErased;
                        if (!broken)
                        {
                            BlockTableRecord shownBtr =
                                (BlockTableRecord)tr.GetObject(shown, OpenMode.ForRead);
                            if (!shownBtr.IsLayout)
                            {
                                int count = 0;
                                foreach (ObjectId eid in shownBtr)
                                {
                                    if (!eid.IsErased) count++;
                                    if (count > 0) break;
                                }
                                broken = count == 0;
                            }
                        }

                        if (broken && !named.IsNull && !named.IsErased && named != shown)
                        {
                            try
                            {
                                // .NET API 的 DynamicBlockTableRecord 是只读的，
                                // 用"删除坏参照 + 按命名动态块定义重建参照"强制重新求值，
                                // AutoCAD 会为新建参照重建匿名块并恢复显示
                                BlockTableRecord namedBtr =
                                    (BlockTableRecord)tr.GetObject(named, OpenMode.ForRead);

                                int namedCount = 0;
                                foreach (ObjectId eid in namedBtr)
                                {
                                    if (!eid.IsErased) namedCount++;
                                    if (namedCount > 0) break;
                                }

                                if (namedCount > 0)
                                {
                                    BlockTableRecord ownerBtr =
                                        (BlockTableRecord)tr.GetObject(br.OwnerId, OpenMode.ForWrite);

                                    Point3d pos = br.Position;
                                    Scale3d scale = br.ScaleFactors;
                                    double rotation = br.Rotation;
                                    string layer = br.Layer;

                                    br.UpgradeOpen();
                                    br.Erase();

                                    BlockReference fresh = new BlockReference(pos, named);
                                    fresh.ScaleFactors = scale;
                                    fresh.Rotation = rotation;
                                    try { fresh.Layer = layer; } catch { }

                                    ownerBtr.AppendEntity(fresh);
                                    tr.AddNewlyCreatedDBObject(fresh, true);
                                }
                            }
                            catch
                            {
                                // 个别参照修不了就放过，不影响其它
                            }
                        }

                        // 继续往嵌套块里检查（深度由 visited 防环）
                        if (!shown.IsNull && !shown.IsErased && !visited.Contains(shown))
                        {
                            visited.Add(shown);
                            pending.Enqueue(shown);
                        }
                        if (!named.IsNull && !named.IsErased && !visited.Contains(named))
                        {
                            visited.Add(named);
                            pending.Enqueue(named);
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                BlmLog.Write("修复动态块参照失败", ex);
            }
        }

        /// <summary>
        /// 确保块定义内实体用到的图层处于可见状态（解冻 + 打开）。
        /// 图库 DWG 常把实体放在特定图层上；若目标图中同名图层被冻结或关闭，
        /// 插入后命令不报错但画面上看不到。只解冻/打开图块实际用到的图层，
        /// 不动其它图层。
        /// </summary>
        private static void EnsureDefinitionLayersVisible(Transaction tr, Database db, ObjectId defId)
        {
            try
            {
                HashSet<string> layers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectDefinitionLayers(tr, defId, layers, new HashSet<ObjectId>(), 0);
                if (layers.Count == 0) return;

                LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);

                foreach (string name in layers)
                {
                    if (string.IsNullOrEmpty(name) || !lt.Has(name)) continue;

                    LayerTableRecord ltr =
                        (LayerTableRecord)tr.GetObject(lt[name], OpenMode.ForWrite);

                    if (ltr.IsFrozen) ltr.IsFrozen = false;
                    if (ltr.IsOff) ltr.IsOff = false;
                }
            }
            catch (System.Exception ex)
            {
                BlmLog.Write("恢复块图层可见性失败", ex);
            }
        }

        private static void CollectDefinitionLayers(
            Transaction tr, ObjectId defId, HashSet<string> layers, HashSet<ObjectId> visited, int depth)
        {
            if (defId.IsNull || defId.IsErased || visited.Contains(defId) || depth > 8) return;
            visited.Add(defId);

            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(defId, OpenMode.ForRead);

            foreach (ObjectId id in btr)
            {
                if (id.IsErased) continue;

                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null) continue;

                try { if (!string.IsNullOrEmpty(ent.Layer)) layers.Add(ent.Layer); }
                catch { }

                BlockReference nested = ent as BlockReference;
                if (nested != null && !nested.BlockTableRecord.IsNull)
                    CollectDefinitionLayers(tr, nested.BlockTableRecord, layers, visited, depth + 1);
            }
        }

        private static string GetCurrentLayerName(Database db, Transaction tr)
        {
            try
            {
                LayerTableRecord rec = (LayerTableRecord)tr.GetObject(db.Clayer, OpenMode.ForRead);
                return rec.Name;
            }
            catch
            {
                return "0";
            }
        }

        /// <summary>
        /// 按插入参数调整块定义内实体的图层 / 颜色：
        ///   ForceLayerZero = true ：所有实体归到 "0" 图层；
        ///   ColorByLayer  = true ：所有实体颜色改为 ByLayer（跟随所在图层颜色）。
        /// 两项都不勾选时不动块定义（保持图块原有图层与颜色）。
        /// 修改是幂等的，重复插入同名块时再跑一遍也无副作用。
        /// </summary>
        private static void ApplyLayerColorPolicy(
            Transaction tr, Database db, ObjectId defId, InsertOptions options)
        {
            if (options == null) return;
            if (!options.ForceLayerZero && !options.ColorByLayer) return;

            try
            {
                BlockTableRecord def =
                    (BlockTableRecord)tr.GetObject(defId, OpenMode.ForWrite);

                foreach (ObjectId id in def)
                {
                    if (id.IsErased) continue;

                    Entity ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                    if (ent == null) continue;

                    if (options.ForceLayerZero)
                    {
                        try { ent.Layer = "0"; } catch { }
                    }

                    if (options.ColorByLayer)
                    {
                        try { ent.ColorIndex = 256; }   // 256 = ByLayer
                        catch { }
                    }
                }
            }
            catch (System.Exception ex)
            {
                BlmLog.Write("调整图块图层/颜色失败", ex);
            }
        }

        /// <summary>
        /// 手动"炸开"：把块定义内的实体逐个克隆进当前空间并套用缩放 / 旋转 / 位移。
        /// 有意不使用 Entity.Explode()，避免 2010~2027 之间该 API 签名不一致导致编译或运行出错。
        /// 已知局限：含属性定义的块不会转换为属性值。
        /// </summary>
        private static string ExplodeManually(
            Transaction tr, Database db, ObjectId defId, BlockTableRecord space, BlockReference br)
        {
            try
            {
                Matrix3d transform = BuildPlacementTransform(br);

                BlockTableRecord def =
                    (BlockTableRecord)tr.GetObject(defId, OpenMode.ForRead);

                foreach (ObjectId id in def)
                {
                    if (id.IsErased) continue;

                    DBObject dbObj = tr.GetObject(id, OpenMode.ForRead);
                    Entity src = dbObj as Entity;
                    if (src == null) continue;
                    if (src is AttributeDefinition) continue;

                    Entity copy = (Entity)src.Clone();
                    copy.TransformBy(transform);

                    space.AppendEntity(copy);
                    tr.AddNewlyCreatedDBObject(copy, true);
                }

                br.Erase();
            }
            catch (System.Exception ex)
            {
                return ex.Message;
            }

            return null;
        }

        private static Matrix3d BuildPlacementTransform(BlockReference br)
        {
            Matrix3d positioning = Matrix3d.Displacement(br.Position - Point3d.Origin);
            Matrix3d rotation = Matrix3d.Rotation(br.Rotation, Vector3d.ZAxis, Point3d.Origin);
            Matrix3d scaling = Matrix3d.Scaling(
                br.ScaleFactors.X != 0 ? br.ScaleFactors.X : 1.0, Point3d.Origin);

            return scaling.PreMultiplyBy(rotation).PreMultiplyBy(positioning);
        }

        /// <summary>读取 DWG 文件中的块定义名（用于"展开 DWG 内部块"功能）。</summary>
        public static IList<string> GetBlockNames(string dwgPath)
        {
            List<string> names = new List<string>();
            Database db = null;

            try
            {
                if (string.IsNullOrEmpty(dwgPath) || !File.Exists(dwgPath)) return names;

                db = new Database(false, true);
                db.ReadDwgFile(dwgPath, FileOpenMode.OpenForReadAndReadShare, false, string.Empty);

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

                    foreach (ObjectId id in bt)
                    {
                        BlockTableRecord btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                        if (btr == null) continue;
                        if (btr.IsLayout) continue;
                        if (btr.IsAnonymous) continue;
                        if (btr.IsAProxy) continue;
                        if (btr.IsFromExternalReference) continue;

                        if (string.IsNullOrEmpty(btr.Name)) continue;
                        if (btr.Name.StartsWith("*")) continue;

                        names.Add(btr.Name);
                    }

                    tr.Commit();
                }
            }
            catch
            {
                // 打不开就当作没有内部块
            }
            finally
            {
                if (db != null)
                {
                    try { db.CloseInput(true); } catch { }
                    try { db.Dispose(); } catch { }
                }
            }

            names.Sort(StringComparer.CurrentCultureIgnoreCase);
            return names;
        }
    }

    /// <summary>块参照拖拽定位。只处理位置，缩放 / 旋转在插入前由面板给定。</summary>
    internal sealed class BlockPlacementJig : EntityJig
    {
        private Point3d _position;
        private readonly Point3d _basePoint;

        public BlockPlacementJig(BlockReference entity, Point3d basePoint)
            : base(entity)
        {
            if (entity == null) throw new ArgumentNullException("entity");
            _position = basePoint;
            _basePoint = basePoint;
        }

        public BlockReference Reference
        {
            get { return (BlockReference)Entity; }
        }

        public Point3d Position
        {
            get { return _position; }
        }

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            JigPromptPointOptions opts = new JigPromptPointOptions("\n指定插入点 <0,0>: ");
            opts.UserInputControls = UserInputControls.NullResponseAccepted;
            opts.BasePoint = _basePoint;
            opts.UseBasePoint = true;

            PromptPointResult res = prompts.AcquirePoint(opts);

            if (res.Status == PromptStatus.Cancel)
                return SamplerStatus.Cancel;

            Point3d picked = res.Status == PromptStatus.None ? Point3d.Origin : res.Value;

            if (picked.IsEqualTo(_position))
                return SamplerStatus.NoChange;

            _position = picked;
            return SamplerStatus.OK;
        }

        protected override bool Update()
        {
            try
            {
                Reference.Position = _position;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
