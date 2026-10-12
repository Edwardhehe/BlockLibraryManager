using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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

        /// <summary>
        /// 交互式旋转：指定插入点后，块绕插入点跟随鼠标转动，
        /// 再次单击固定角度（面板填写的角度作为默认值，回车直接采用）。
        /// </summary>
        public bool InteractiveRotation { get; set; }

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
            InteractiveRotation = true;
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

            // 导入前先记下这个块名是否已经存在：只有"全新导入"的定义才调整基点。
            // 复用 / 更新的定义一旦改了基点，图里已有的那些参照会整体位移。
            bool isNewDefinition = FindExistingDefinition(destDb, blockName).IsNull;

            Database srcDb = null;

            try
            {
                srcDb = new Database(false, true);
                srcDb.ReadDwgFile(item.FilePath,
                    FileOpenMode.OpenForReadAndReadShare, false, string.Empty);

                // 源文件里若有"引用自己"的块参照，Insert / WblockCloneObjects 会直接抛
                // eSelfReference（典型：文件里有个与文件同名的块，模型空间又插了一个）。
                // 在这份只读内存副本里把自引用参照删掉（不落盘），再导入。
                StripSelfReferences(srcDb, blockName);

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

            // 把块基点挪到内容中心附近，避免插入点看得见图块跑没影。
            // 注意：只改基点、不动实体，动态块的求值数据才不会被破坏。
            if (isNewDefinition)
            {
                try { NormalizeDefinitionOrigin(destDb, defId); }
                catch { }
            }

            reason = null;
            return true;
        }

        // ---------------- 单位换算 ----------------

        /// <summary>源文件单位 → 目标文件单位 的换算系数。</summary>
        private static double ComputeUnitScale(UnitsValue sourceUnits, UnitsValue targetUnits)
        {
            // 0 = 无单位。绝大多数 DWG 默认就是无单位，而 UnitsToMeters(0) 会落到
            // default 分支返回 1.0（被当成"米"）——源图无单位 + 目标图毫米时
            // scale 就成了 1/0.001 = 1000，图块放大一千倍，画面上等于"没插进来"。
            // 按 AutoCAD 的规则：任一侧无单位就不做换算。
            if ((int)sourceUnits == 0 || (int)targetUnits == 0) return 1.0;

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

        /// <summary>
        /// 删除源文件里的自引用块参照，防 eSelfReference。
        ///
        /// 两种真实场景都会让 Database.Insert / WblockCloneObjects 抛
        /// eSelfReference，整次插入失败：
        ///   1. 整图入库时，模型空间里放着一个与目标块同名的参照——
        ///      导入后"模型空间"变成块定义，定义里又引用同名块 = 自己引用自己；
        ///   2. 内部块的定义里直接放了对同名定义的参照（递归定义）。
        /// 这里只动 ReadDwgFile 出来的内存副本，源文件不受影响。
        /// 删的是"参照"本身：定义还能正常导入，只是不再套娃。
        /// </summary>
        private static void StripSelfReferences(Database srcDb, string blockName)
        {
            if (string.IsNullOrEmpty(blockName)) return;

            try
            {
                using (Transaction tr = srcDb.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(srcDb.BlockTableId, OpenMode.ForRead);
                    ObjectId selfDefId = bt.Has(blockName) ? bt[blockName] : ObjectId.Null;

                    int removed = 0;

                    // 1) 模型空间 / 图纸空间里指向同名块的参照
                    foreach (ObjectId spaceId in new ObjectId[] {
                        SymbolUtilityServices.GetBlockModelSpaceId(srcDb),
                        SymbolUtilityServices.GetBlockPaperSpaceId(srcDb) })
                    {
                        BlockTableRecord space =
                            (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead);

                        foreach (ObjectId id in space)
                        {
                            if (id.IsErased) continue;

                            BlockReference br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                            if (br == null) continue;

                            if (!ReferencesBlock(tr, br, selfDefId, blockName)) continue;

                            try
                            {
                                br.UpgradeOpen();
                                br.Erase();
                                removed++;
                            }
                            catch { }
                        }
                    }

                    // 2) 内部块定义内部对同名定义的直接引用（递归定义）
                    if (!selfDefId.IsNull)
                    {
                        BlockTableRecord def =
                            (BlockTableRecord)tr.GetObject(selfDefId, OpenMode.ForRead);

                        foreach (ObjectId id in def)
                        {
                            if (id.IsErased) continue;

                            BlockReference br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                            if (br == null) continue;

                            if (!ReferencesBlock(tr, br, selfDefId, blockName)) continue;

                            try
                            {
                                br.UpgradeOpen();
                                br.Erase();
                                removed++;
                            }
                            catch { }
                        }
                    }

                    tr.Commit();

                    if (removed > 0)
                        BlmLog.Write("已从源文件内存副本中移除 " + removed +
                            " 个自引用参照（块：" + blockName + "）");
                }
            }
            catch (System.Exception ex)
            {
                // 剥离失败不影响插入：最坏情况是回到原来的 eSelfReference 报错
                BlmLog.Write("剥离自引用参照失败（块：" + blockName + "）", ex);
            }
        }

        /// <summary>
        /// 判断一个块参照是否指向目标块（按 ObjectId 或按名字比，动态块的
        /// BlockTableRecord 是匿名显示块，所以要两条路都查）。
        /// </summary>
        private static bool ReferencesBlock(
            Transaction tr, BlockReference br, ObjectId defId, string blockName)
        {
            try
            {
                if (!defId.IsNull)
                {
                    if (br.BlockTableRecord == defId) return true;
                    try
                    {
                        if (br.IsDynamicBlock && br.DynamicBlockTableRecord == defId)
                            return true;
                    }
                    catch { }
                }

                BlockTableRecord def =
                    (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                if (string.Equals(def.Name, blockName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }

            return false;
        }

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
        /// 把块定义的基点（BlockTableRecord.Origin）移到内容的中心，
        /// 避免"插入点就在眼前、图块却落在几百公里之外"。
        /// 内容本来就包住原点时保持不动，不破坏正常的块。
        /// </summary>
        /// <remarks>
        /// 【这里是动态块不显示的头号元凶，务必不要再改回"平移实体"的写法】
        /// 早期版本是把块定义内的每个实体逐个 TransformBy 挪回原点。对动态块来说
        /// 这是破坏性的：动态块的几何由参数 / 动作 / 可见性状态驱动，挪动实体会让
        /// 约束与参数基准全部错位；更致命的是匿名显示块（*U）不会跟着更新，
        /// 参照依旧指向那份"原坐标 / 已失效"的几何，表现就是命令提示成功、
        /// 画面上却什么都看不见。
        /// 改为只设置 BlockTableRecord.Origin —— 一个实体坐标都不动，
        /// 参照放在 P 点时内容中心自然落在 P，动态块的求值数据完全不受影响。
        /// </remarks>
        private static void NormalizeDefinitionOrigin(Database db, ObjectId defId)
        {
            if (defId.IsNull) return;

            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTableRecord btr = (BlockTableRecord)tr.GetObject(defId, OpenMode.ForRead);

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

                    // 内容本来就绕着原点画：保持基点不动
                    if (Math.Abs(centerX) <= size && Math.Abs(centerY) <= size)
                    {
                        tr.Commit();
                        return;
                    }

                    // 只改基点，不动实体坐标 —— 动态块参数 / 匿名块一律不受影响
                    try
                    {
                        btr.UpgradeOpen();
                        btr.Origin = new Point3d(centerX, centerY, 0.0);
                    }
                    catch
                    {
                        // 基点写不进去就保持原样，最多是插在远处，总比不显示好
                    }

                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                BlmLog.Write("整理块定义基点失败", ex);
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
            ObjectId brId = ObjectId.Null;
            bool degraded = false;

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

                // 【动态块专用】跨库导入的动态块经常丢掉求值数据，匿名显示块（*U）是空的，
                // 表现就是"命令提示插入成功、画面上什么都没有"。
                // 入库之后立刻强制求值一次，实在求不出来就退化成静态块（至少看得见）。
                // 提交前只"强制求值"，不做静态降级：
                // 此时匿名显示块往往还没生成，判定成空就把完好的动态块永久改成静态块了。
                // 真正需要降级的情况由提交后那次检查负责（那时显示块已经建好）。
                try { EnsureReferenceEvaluated(tr, destDb, br, false); }
                catch { }

                PromptStatus status = PromptStatus.None;

                // 取点的整个过程都临时打开对象捕捉（F3 被关掉时也会临时打开），
                // 结束时 OsnapScope 自动把 OSMODE 还原成用户原来的值。
                using (new OsnapScope())
                {
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

                    // 回车（Null 响应）= 采用提示里的默认值 <0,0>。
                    // 提示语写了 <0,0> 且允许空响应，用户回车却被当成取消，等于插不进来。
                    if (status == PromptStatus.None)
                    {
                        picked = Point3d.Origin;
                        status = PromptStatus.OK;
                    }

                    // 除了上面补过的空响应，其余（Esc 取消 / 出错）一律放弃本次放置
                    if (status != PromptStatus.OK)
                    {
                        tr.Abort();
                        return false;
                    }

                    // 不管 jig 有没有更新过位置，这里统一再落一次点，保证一定在用户取的位置上
                    br.Position = picked;

                    // ---- 第二段：旋转跟随鼠标 ----
                    // 以插入点为圆心，鼠标移动控制角度，再次单击固定；
                    // Esc 放弃本次放置；回车采用面板填写的默认角度。
                    if (options.InteractiveRotation)
                    {
                        double defaultRadians = options.RotationDegrees * Math.PI / 180.0;
                        PromptStatus rotStatus = PromptStatus.None;

                        try
                        {
                            BlockRotationJig rotJig = new BlockRotationJig(br, picked, defaultRadians);
                            PromptResult rot = ed.Drag(rotJig);
                            rotStatus = rot == null ? PromptStatus.Cancel : rot.Status;

                            if (rotStatus == PromptStatus.OK)
                            {
                                br.Rotation = rotJig.Rotation;
                            }
                            else if (rotStatus == PromptStatus.None)
                            {
                                // 回车 = 采用面板填写的默认角度。
                                // 注意不能"什么都不做"：Jig 的 Update() 在鼠标移动时
                                // 已经把 br.Rotation 改成最后拖到的角度了，
                                // 不动就等于把面板里的角度静默丢掉。
                                br.Rotation = defaultRadians;
                            }
                        }
                        catch (System.Exception ex)
                        {
                            // 旋转拖拽不可用时退化为面板默认角度，不整次失败
                            BlmLog.Write("旋转拖拽失败，采用默认角度", ex);
                        }

                        if (rotStatus == PromptStatus.Cancel)
                        {
                            tr.Abort();
                            return false;
                        }
                    }
                }

                if (options.ExplodeAfterInsert)
                {
                    string fail = ExplodeManually(tr, destDb, defId, space, br);
                    if (fail != null)
                        ed.WriteMessage("\n[图库管理器] 炸开失败，已保留为块引用：" + fail);
                }

                brId = br.ObjectId;
                tr.Commit();
            }

            // 提交后再兜一次：拖拽 / 旋转 Jig 改过几何之后，匿名块有可能又变空了。
            // 这一步能救回"拖完才消失"的情况。
            try
            {
                using (Transaction tr2 = destDb.TransactionManager.StartTransaction())
                {
                    BlockReference check = tr2.GetObject(brId, OpenMode.ForRead) as BlockReference;
                    if (check != null && !check.IsErased && !IsDisplayBlockUsable(tr2, check))
                    {
                        // 提交后显示块已经建好，这里的"空"是真的空，可以降级
                        if (EnsureReferenceEvaluated(tr2, destDb, check, true)) degraded = true;
                    }
                    tr2.Commit();
                }
            }
            catch
            {
                // 兜底失败不影响已经插入的图块
            }

            // 提交后再刷新一次，老版本经常出现"数据库里有了、屏幕还没重画"的情况
            try { ed.Regen(); } catch { }
            try { ed.UpdateScreen(); } catch { }

            if (degraded)
            {
                ed.WriteMessage(
                    "\n[图库管理器] 该图块的动态参数在导入时丢失，已按静态图块插入" +
                    "（图形可见，动态夹点不可用）。\n");
            }

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
        /// 保证一个块参照在画面上真有东西可画。
        ///
        /// 动态块参照画的不是具名定义，而是 AutoCAD 求值后生成的匿名块（*U）。
        /// 图库 DWG 是跨库导入进来的，动态块的求值数据（参数 / 动作 / 求值图）
        /// 经常在导入过程中丢失，匿名块于是为空 —— 命令提示插入成功，画面上
        /// 却什么都没有。这就是"插入动态块不显示"的直接原因。
        ///
        /// 处理顺序：ResetBlock() 强制重新求值 → 再求值一次（有的版本首轮只建空壳）
        /// → 仍然为空则把内容固化成普通块（牺牲动态特性，保证看得见）。
        /// </summary>
        /// <returns>true 表示已降级为静态块。</returns>
        /// <param name="allowStaticFallback">
        /// 是否允许在求值失败时退化为静态块。提交前传 false（显示块还没建好，
        /// 此时的"空"是假象），提交后传 true。
        /// </param>
        private static bool EnsureReferenceEvaluated(Transaction tr, Database db, BlockReference br,
            bool allowStaticFallback)
        {
            if (br == null) return false;

            try
            {
                if (!br.IsDynamicBlock) return false;

                if (IsDisplayBlockUsable(tr, br)) return false;

                SafeReset(br);
                if (IsDisplayBlockUsable(tr, br)) return false;

                // 有的版本第一次只建了个空壳，再来一次
                SafeReset(br);
                if (IsDisplayBlockUsable(tr, br)) return false;

                if (!allowStaticFallback) return false;

                return ConvertToStaticSnapshot(tr, db, br);
            }
            catch (System.Exception ex)
            {
                BlmLog.Write("动态块求值失败", ex);
                return false;
            }
        }

        /// <summary>强制 AutoCAD 重新求值一个动态块参照（重建匿名显示块）。</summary>
        private static void SafeReset(BlockReference br)
        {
            try
            {
                if (!br.IsWriteEnabled) br.UpgradeOpen();
                br.ResetBlock();
            }
            catch
            {
                // 个别版本 / 参照状态下 ResetBlock 会抛，忽略后走后面的兜底
            }
        }

        /// <summary>块定义里没被删除的实体数量。</summary>
        private static int CountBlockEntities(BlockTableRecord btr)
        {
            if (btr == null) return 0;

            int count = 0;
            try
            {
                foreach (ObjectId id in btr)
                {
                    if (!id.IsErased) count++;
                }
            }
            catch
            {
                return count;
            }
            return count;
        }

        /// <summary>参照真正用来显示的那个块里，是否至少有一个实体。</summary>
        private static bool IsDisplayBlockUsable(Transaction tr, BlockReference br)
        {
            try
            {
                ObjectId shown = br.BlockTableRecord;
                if (shown.IsNull || shown.IsErased) return false;

                BlockTableRecord btr = tr.GetObject(shown, OpenMode.ForRead) as BlockTableRecord;
                if (btr == null) return false;

                foreach (ObjectId id in btr)
                {
                    if (!id.IsErased) return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 动态块求值彻底失败时的兜底：把具名定义里的几何克隆进一个新的普通块，
        /// 再让参照指向它。动态特性会丢失，但图形一定画得出来 ——
        /// 看得见的静态图块，远比一个看不见的动态块有用。
        /// </summary>
        private static bool ConvertToStaticSnapshot(Transaction tr, Database db, BlockReference br)
        {
            try
            {
                // 具名定义含全部几何，优先取它；拿不到才退回当前显示块
                ObjectId source = br.DynamicBlockTableRecord;
                if (source.IsNull || source.IsErased) source = br.BlockTableRecord;
                if (source.IsNull || source.IsErased) return false;

                BlockTableRecord src = tr.GetObject(source, OpenMode.ForRead) as BlockTableRecord;
                if (src == null) return false;

                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

                string baseName = SanitizeBlockName(src.Name);
                if (string.IsNullOrEmpty(baseName)) baseName = "Block";
                if (baseName.Length > 230) baseName = baseName.Substring(0, 230);

                string name = baseName + "_BLM";
                int suffix = 0;
                while (bt.Has(name))
                {
                    suffix++;
                    name = baseName + "_BLM" + suffix;
                    if (suffix > 200) return false;
                }

                BlockTableRecord copy = new BlockTableRecord();
                copy.Name = name;

                bt.UpgradeOpen();
                ObjectId newId = bt.Add(copy);
                tr.AddNewlyCreatedDBObject(copy, true);

                foreach (ObjectId id in src)
                {
                    if (id.IsErased) continue;

                    Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;

                    Entity clone = (Entity)ent.Clone();
                    copy.AppendEntity(clone);
                    tr.AddNewlyCreatedDBObject(clone, true);
                }

                // 继承基点，否则插入点与内容中心的相对关系会变
                try
                {
                    copy.UpgradeOpen();
                    copy.Origin = src.Origin;
                }
                catch { }

                if (!br.IsWriteEnabled) br.UpgradeOpen();
                br.BlockTableRecord = newId;

                BlmLog.Write("动态块求值失败，已降级为静态块：" + name);
                return true;
            }
            catch (System.Exception ex)
            {
                BlmLog.Write("动态块降级为静态块失败", ex);
                return false;
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

                        // 先让 AutoCAD 自己重求一次值，能救回来就不必动刀
                        if (broken && br.IsDynamicBlock && !named.IsNull && !named.IsErased)
                        {
                            SafeReset(br);
                            broken = !IsDisplayBlockUsable(tr, br);
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
                // 动态块必须炸"当前显示状态"（匿名块 *U）。直接炸具名定义会把
                // 全部可见性状态的几何一次性摊开，图面一团乱、看起来像插坏了。
                ObjectId sourceId = br.BlockTableRecord;
                if (sourceId.IsNull || sourceId.IsErased) sourceId = defId;

                BlockTableRecord def =
                    (BlockTableRecord)tr.GetObject(sourceId, OpenMode.ForRead);

                // 显示块是空的（求值失败）就退回具名定义，总比炸出个空要好
                if (CountBlockEntities(def) == 0 && sourceId != defId)
                    def = (BlockTableRecord)tr.GetObject(defId, OpenMode.ForRead);

                // 基点取**实际被炸的那个块记录**的 Origin：
                // 导入时 NormalizeDefinitionOrigin 会把基点挪到内容中心（可能是几十万单位），
                // 用原点代替基点的话，炸出来的实体会整体飞走。
                Point3d origin = Point3d.Origin;
                try { origin = def.Origin; } catch { }

                Matrix3d transform = BuildPlacementTransform(br, origin);

                // 分两步：先把实体全部克隆好，全部成功再统一 Append + 删原块。
                // 以前是"克隆一个就 Append 一个"，中途出错会留下"原块 + 半个炸开结果"
                // 两份几何叠在一起，而且事务照常提交，用户得自己收拾。
                List<Entity> copies = new List<Entity>();
                foreach (ObjectId id in def)
                {
                    if (id.IsErased) continue;

                    DBObject dbObj = tr.GetObject(id, OpenMode.ForRead);
                    Entity src = dbObj as Entity;
                    if (src == null) continue;
                    if (src is AttributeDefinition) continue;

                    Entity copy = (Entity)src.Clone();
                    copy.TransformBy(transform);
                    copies.Add(copy);
                }

                foreach (Entity copy in copies)
                {
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

        /// <summary>
        /// 手工炸开用的变换矩阵。定义坐标 → 世界坐标：
        ///   Displacement(Position) · Rotation · Scale · Displacement(-Origin)
        /// 中间那一项 "Displacement(-Origin)" 以前漏了（默认基点是原点），
        /// 于是基点被改过的块（内容中心对齐过的）炸开后整体位移。
        /// </summary>
        private static Matrix3d BuildPlacementTransform(BlockReference br, Point3d origin)
        {
            Matrix3d toOrigin = Matrix3d.Displacement(Point3d.Origin - origin);
            Matrix3d scaling = Matrix3d.Scaling(
                br.ScaleFactors.X != 0 ? br.ScaleFactors.X : 1.0, Point3d.Origin);
            Matrix3d rotation = Matrix3d.Rotation(br.Rotation, Vector3d.ZAxis, Point3d.Origin);
            Matrix3d positioning = Matrix3d.Displacement(br.Position - Point3d.Origin);

            // scaling · toOrigin → rotation · (…) → positioning · (…)
            Matrix3d m = scaling.PostMultiplyBy(toOrigin);
            m = m.PreMultiplyBy(rotation);
            return m.PreMultiplyBy(positioning);
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

    /// <summary>
    /// 插入定位期间临时打开 CAD 的"运行中的对象捕捉"（OSMODE）。
    ///
    /// 为什么要动系统变量：Jig 取点是否吸附完全取决于当前 OSMODE。
    /// 用户按过 F3（OSMODE 带 16384 关闭位）或者一个捕捉模式都没勾（低 14 位全 0）时，
    /// 拖拽定位就变成"凭眼睛估"，插入点经常偏几毫米到几十毫米。
    /// 这里只在拖拽那几秒里临时清掉关闭位、并补一组常用模式，结束后原样还原——
    /// 用户在状态栏上看到的设置不会被改掉。
    ///
    /// 读写系统变量一律走反射：国产 CAD / BricsCAD 上万一没有
    /// Application.GetSystemVariable / SetSystemVariable，最多是"不自动开捕捉"，
    /// 不会编译失败，也不会影响插入本身。
    /// </summary>
    internal sealed class OsnapScope : IDisposable
    {
        private const int SnapOffBit = 16384;      // F3：关闭对象捕捉
        private const int ModeMask = 0x3FFF;       // 低 14 位才是各种捕捉模式

        /// <summary>临时补上的常用模式：端点 / 中点 / 圆心 / 交点 / 垂足 / 插入点。</summary>
        private const int DefaultModes = 1 | 2 | 4 | 32 | 128 | 64;

        private readonly object _oldValue;
        private bool _changed;

        public OsnapScope()
        {
            object raw = TryGetOsmode();
            if (raw == null) return;                  // 拿不到就不动系统变量

            int current;
            try { current = Convert.ToInt32(raw); }
            catch { return; }

            int wanted = current;
            if ((wanted & SnapOffBit) != 0) wanted &= ~SnapOffBit;   // 打开 F3
            if ((wanted & ModeMask) == 0) wanted |= DefaultModes;    // 一个模式都没选就补常用的

            if (wanted == current) return;            // 本来就开着，什么都不用改

            _oldValue = raw;
            _changed = true;
            TrySetOsmode((short)wanted);
        }

        public void Dispose()
        {
            if (!_changed) return;
            _changed = false;
            TrySetOsmode(_oldValue);                  // 原样还原，连类型都还给宿主
        }

        private static object TryGetOsmode()
        {
            try
            {
                Type app = typeof(Application);
                MethodInfo m = app.GetMethod("GetSystemVariable",
                    BindingFlags.Public | BindingFlags.Static,
                    null, new Type[] { typeof(string) }, null);
                if (m == null) return null;
                return m.Invoke(null, new object[] { "OSMODE" });
            }
            catch
            {
                return null;
            }
        }

        private static void TrySetOsmode(object value)
        {
            try
            {
                Type app = typeof(Application);
                foreach (MethodInfo m in app.GetMethods(
                             BindingFlags.Public | BindingFlags.Static))
                {
                    if (!string.Equals(m.Name, "SetSystemVariable", StringComparison.Ordinal)) continue;

                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != 2) continue;
                    if (ps[0].ParameterType != typeof(string)) continue;
                    if (ps[1].ParameterType != typeof(object) &&
                        ps[1].ParameterType != typeof(short) &&
                        ps[1].ParameterType != typeof(int)) continue;

                    object arg = value;
                    if (ps[1].ParameterType == typeof(short)) arg = Convert.ToInt16(value);

                    m.Invoke(null, new object[] { "OSMODE", arg });
                    return;
                }
            }
            catch
            {
                // 改不动就算了：只是少了自动开捕捉，插入照常可用
            }
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
            // 提示里点明可以用对象捕捉：插入期间由 OsnapScope 保证 OSMODE 是开着的
            JigPromptPointOptions opts = new JigPromptPointOptions(
                "\n指定插入点 <0,0>（可用对象捕捉，Esc 取消）: ");
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

    /// <summary>
    /// 块参照旋转拖拽：以插入点为圆心，鼠标绕其转动控制旋转角，
    /// 单击固定角度。回车（None）采用构造时给的默认角度，Esc 取消。
    /// </summary>
    internal sealed class BlockRotationJig : EntityJig
    {
        private readonly Point3d _center;
        private double _rotation;

        public BlockRotationJig(BlockReference entity, Point3d center, double defaultRadians)
            : base(entity)
        {
            if (entity == null) throw new ArgumentNullException("entity");
            _center = center;
            _rotation = defaultRadians;
        }

        public BlockReference Reference
        {
            get { return (BlockReference)Entity; }
        }

        public double Rotation
        {
            get { return _rotation; }
        }

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            JigPromptAngleOptions opts = new JigPromptAngleOptions(
                "\n移动鼠标确定旋转角度，单击固定（回车=默认角度，Esc=取消）: ");
            opts.UseBasePoint = true;
            opts.BasePoint = _center;
            opts.DefaultValue = _rotation;
            opts.UserInputControls = UserInputControls.NullResponseAccepted;

            PromptDoubleResult res = prompts.AcquireAngle(opts);

            if (res.Status == PromptStatus.Cancel)
                return SamplerStatus.Cancel;

            double angle = res.Status == PromptStatus.None ? _rotation : res.Value;

            if (Math.Abs(angle - _rotation) < 1e-9)
                return SamplerStatus.NoChange;

            _rotation = angle;
            return SamplerStatus.OK;
        }

        protected override bool Update()
        {
            try
            {
                Reference.Rotation = _rotation;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
