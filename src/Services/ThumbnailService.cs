using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

// 别名：AutoCAD 类型与 System.Drawing.Image 同名冲突，不能直接 using 命名空间
using AcadDb = Autodesk.AutoCAD.DatabaseServices.Database;
using AcadTr = Autodesk.AutoCAD.DatabaseServices.Transaction;
using AcadEntity = Autodesk.AutoCAD.DatabaseServices.Entity;
using AcadBtr = Autodesk.AutoCAD.DatabaseServices.BlockTableRecord;
using AcadCurve = Autodesk.AutoCAD.DatabaseServices.Curve;
using AcadOpenMode = Autodesk.AutoCAD.DatabaseServices.OpenMode;
using AcadXline = Autodesk.AutoCAD.DatabaseServices.Xline;
using AcadRay = Autodesk.AutoCAD.DatabaseServices.Ray;
using AcadMatrix3d = Autodesk.AutoCAD.Geometry.Matrix3d;
using AcadPoint3d = Autodesk.AutoCAD.Geometry.Point3d;
using AcadPts = Autodesk.AutoCAD.Geometry.Point3dCollection;
using AcadExtents3d = Autodesk.AutoCAD.DatabaseServices.Extents3d;

namespace BlockLibraryManager
{
    /// <summary>
    /// 缩略图服务。
    ///
    /// 两条重要约束：
    /// 1. AutoCAD 的 Database 只能在应用主线程操作，不能丢到 ThreadPool，
    ///    所以这里采用"后台队列 + Application.Idle 时分批处理"的模式。
    /// 2. 每个 DWG 的预览图会缓存成 PNG 存到 %LOCALAPPDATA%，
    ///    第二次打开同一个图库基本是秒开。
    /// </summary>
    public sealed class ThumbnailService : IDisposable
    {
        private readonly LibraryConfig _config;

        private readonly Queue<BlockItem> _pending = new Queue<BlockItem>();
        private readonly HashSet<string> _pendingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 缺少磁盘缓存、需要真正打开 DWG 生成的条目。
        /// ProcessQueue 先把所有"已有缓存"的条目快速加载完（纯 PNG 解码），
        /// 再回来逐个处理这里慢速的生成任务，保证列表先把画面填满、流畅不卡。
        /// </summary>
        private readonly Queue<BlockItem> _generate = new Queue<BlockItem>();
        private readonly Dictionary<string, Image> _memoryCache =
            new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 内存缓存的 LRU 访问顺序（末尾 = 最近使用）。
        ///
        /// 为什么需要上限：图库动辄上千个 DWG，每张缩略图是一个 Bitmap，
        /// 无上限缓存会让内存随浏览量一直涨。超过上限时淘汰最久未用的一批。
        ///
        /// 为什么淘汰时**不 Dispose**：缓存里的图会被 CreateDisplayImage 直接
        /// 交给 ImageList 使用（ImageList 内部持有原对象引用），一旦在这里
        /// Dispose，界面重绘时就会抛"参数无效"——这正是历史崩溃的根源。
        /// 只解除引用、交给 GC 回收，是最安全的做法。
        /// </summary>
        private readonly List<string> _cacheOrder = new List<string>();

        /// <summary>内存缓存最多保留的缩略图张数。</summary>
        private const int MaxMemoryCache = 600;

        private Image _placeholderWhole;
        private Image _placeholderInner;
        private bool _disposed;

        /// <summary>
        /// 动态块标记缓存：key = 图块条目 Key，value = 是否含动态块定义。
        /// 生成缩略图时顺带检测，检测结果同时落盘为 ".dyn" 标记文件持久化。
        /// </summary>
        private readonly Dictionary<string, bool> _dynamicFlags =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 可见性动态块的状态名缓存：key = 图块条目 Key。
        /// 空列表 = 不是可见性动态块；≥2 项 = 缩略图底部显示"切换"按钮。
        /// 生成缩略图时检测并落盘为 ".vis" 标记文件（每行一个状态名）。
        /// </summary>
        private readonly Dictionary<string, List<string>> _visStates =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>每个图块条目当前切换到的可见性状态下标（仅会话内记忆）。</summary>
        private readonly Dictionary<string, int> _visIndex =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>旧版（可能带黑边）缓存是否已清理。static 保证整个进程只做一次。</summary>
        private static bool _legacyCacheCleaned;

        /// <summary>当前缓存命名版本前缀。升版本会让旧图（含带黑边的、旧动态块判定）自动失效、重新生成。</summary>
        private const string CacheVersionPrefix = "v3_";

        /// <summary>某张缩略图生成完成时触发（参数为图块条目）。</summary>
        public event Action<BlockItem> ThumbnailReady;

        /// <summary>
        /// 缩略图底色模式：true = 白底（黑底预览图的黑色背景替换为白色），
        /// false = 黑底（白底预览图的白色背景替换为黑色）。
        /// 切换后需 ClearAllCaches 并重新生成。
        /// </summary>
        public bool WhiteBackground { get; set; }

        public ThumbnailService(LibraryConfig config)
        {
            if (config == null) throw new ArgumentNullException("config");
            _config = config;
        }

        public string CacheFolder
        {
            get
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BlockLibraryManager", "Thumbnails");
                return path;
            }
        }

        /// <summary>
        /// 把一批图块排进生成队列，返回本次新排入的数量。
        ///
        /// 关键：这里**不再同步读磁盘缓存**。以前每显示一页图块都要在主线程上
        /// 把上百个 PNG 读进来解码，是打开面板卡顿的头号原因；
        /// 现在只登记，真正的读缓存 / 生成统统推迟到 ProcessQueue（空闲时小批量做）。
        /// </summary>
        public int RequestThumbnails(IEnumerable<BlockItem> items)
        {
            if (items == null) return 0;

            int added = 0;

            foreach (BlockItem item in items)
            {
                if (item == null) continue;
                if (_memoryCache.ContainsKey(item.Key)) continue;
                if (_pendingKeys.Contains(item.Key)) continue;
                if (!item.IsWholeDrawing) continue;      // 内部块没有独立预览图

                _pendingKeys.Add(item.Key);
                _pending.Enqueue(item);
                added++;
            }

            return added;
        }

        /// <summary>
        /// 处理队列中的若干条目。由 Application.Idle 事件驱动。
        /// 每批最多占用 ~40ms，宁可多跑几次也不让界面卡住。
        ///
        /// 两阶段策略（保证"已有缩略图跳过、加载丝滑"）：
        ///   阶段一：把有磁盘缓存的条目全部直接读进来（纯 PNG 解码，一张不到 1ms），
        ///           没有缓存的先挪进 _generate 暂不处理——列表因此最先被填满；
        ///   阶段二：缓存全读完后，再逐个打开 DWG 生成缺失的缩略图。
        /// 返回 true 表示队列仍未处理完，需要继续。
        /// </summary>
        public bool ProcessQueue(int maxItems)
        {
            if (maxItems <= 0) maxItems = 1;

            int count = 0;
            int startTick = Environment.TickCount;

            // ---- 阶段一：快速读磁盘缓存 ----
            while (count < maxItems && _pending.Count > 0)
            {
                // 时间预算：到点就收工，剩下的等下一次 Idle，界面永远能响应
                if (count > 0 && Environment.TickCount - startTick > 40) break;

                BlockItem item = _pending.Dequeue();
                _pendingKeys.Remove(item.Key);

                if (_memoryCache.ContainsKey(item.Key)) continue;

                if (HasDiskCache(item))
                {
                    Image cached = TryLoadCached(item);
                    if (cached != null)
                    {
                        PutCache(item.Key, cached);
                        RaiseReady(item);
                        count++;
                        continue;
                    }
                }

                // 没有缓存 → 进入慢速生成队列
                _generate.Enqueue(item);
            }

            // ---- 阶段二：生成缺失的缩略图（打开 DWG，较慢） ----
            if (_pending.Count == 0)
            {
                while (count < maxItems && _generate.Count > 0)
                {
                    // 生成单条耗时不稳定，同样受总时间预算约束
                    if (count > 0 && Environment.TickCount - startTick > 40) break;

                    BlockItem item = _generate.Dequeue();

                    if (_memoryCache.ContainsKey(item.Key)) continue;

                    // 双保险：阶段一之后缓存可能又被别的路径补上了，先查一次（一次 File.Exists，代价极小）
                    Image image = TryLoadCached(item);
                    if (image == null) image = CreateThumbnail(item);   // 优先用 DWG 内嵌预览位图

                    // 生成失败时缓存 null，而不是共享的占位图单例——
                    // 占位图是全局唯一实例，一旦进了缓存就可能被 Dispose，
                    // 之后再拿它去绘制就会抛"参数无效"。GetThumbnail 遇到 null 会自动回退到占位图。
                    PutCache(item.Key, image);
                    RaiseReady(item);

                    count++;
                }
            }

            return _pending.Count > 0 || _generate.Count > 0;
        }

        /// <summary>该图块是否已有磁盘缓存 PNG。有则直接跳过生成。</summary>
        private bool HasDiskCache(BlockItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.FilePath)) return false;
            try { return File.Exists(GetCachePath(item.FilePath)); }
            catch { return false; }
        }

        public int PendingCount
        {
            get { return _pending.Count + _generate.Count; }
        }

        /// <summary>
        /// 取缩略图；未就绪时返回占位图。
        /// 这里只读内存缓存——一旦在这里碰磁盘，显示一页图块就得同步解码上百张 PNG，界面必卡。
        /// </summary>
        public Image GetThumbnail(BlockItem item)
        {
            if (item == null) return GetPlaceholder(true);

            Image img;
            if (_memoryCache.TryGetValue(item.Key, out img))
            {
                TouchCache(item.Key);      // 记录最近使用，供 LRU 淘汰
                return img ?? GetPlaceholder(item.IsWholeDrawing);
            }

            return GetPlaceholder(item.IsWholeDrawing);
        }

        /// <summary>把 key 标记为最近使用（移到 LRU 队尾）。</summary>
        private void TouchCache(string key)
        {
            if (string.IsNullOrEmpty(key)) return;

            int last = _cacheOrder.Count - 1;
            if (last >= 0 && string.Equals(_cacheOrder[last], key, StringComparison.OrdinalIgnoreCase))
                return;                     // 已经在队尾，最常见的情况，直接返回

            _cacheOrder.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            _cacheOrder.Add(key);
        }

        /// <summary>
        /// 写入内存缓存 + 更新 LRU，必要时淘汰最久未用的条目。
        /// 淘汰只解除引用、不 Dispose（原因见 _cacheOrder 注释）。
        /// </summary>
        private void PutCache(string key, Image image)
        {
            _memoryCache[key] = image;
            TouchCache(key);

            while (_cacheOrder.Count > MaxMemoryCache)
            {
                string oldest = _cacheOrder[0];
                _cacheOrder.RemoveAt(0);
                _memoryCache.Remove(oldest);
            }
        }

        /// <summary>读磁盘上的缓存 PNG（已经在 ProcessQueue 里，属于可控的空闲时间）。</summary>
        private Image TryLoadCached(BlockItem item)
        {
            if (item == null) return null;
            if (!File.Exists(GetCachePath(item.FilePath))) return null;
            return TryLoadPng(GetCachePath(item.FilePath));
        }

        // ---------- 动态块标记 ----------

        /// <summary>
        /// 该图块对应的 DWG 是否包含动态块定义（用于在缩略图右上角标"动"）。
        /// 检测在生成缩略图时顺带完成（数据库已打开），结果缓存进内存并落盘；
        /// 内存未命中时直接查磁盘标记文件，开销仅一次 File.Exists。
        /// </summary>
        public bool IsDynamicBlock(BlockItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.Key)) return false;

            bool flag;
            if (_dynamicFlags.TryGetValue(item.Key, out flag)) return flag;

            try
            {
                flag = File.Exists(GetCachePath(item.FilePath) + ".dyn");
            }
            catch
            {
                flag = false;
            }

            _dynamicFlags[item.Key] = flag;
            return flag;
        }

        /// <summary>写入动态块标记文件（与缩略图 PNG 同目录，同前缀同键名）。</summary>
        private void WriteDynamicMarker(string dwgPath)
        {
            try
            {
                string marker = GetCachePath(dwgPath) + ".dyn";
                if (!File.Exists(marker))
                    File.WriteAllBytes(marker, new byte[0]);
            }
            catch
            {
                // 标记写不进去只影响角标显示，不影响功能
            }
        }

        // ---------- 可见性动态块 ----------

        /// <summary>一个可见性参数解析结果：状态名 → 该状态下"明确可见"的定义实体句柄集合。</summary>
        private sealed class VisParamData
        {
            public readonly Dictionary<string, HashSet<long>> StateHandles =
                new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);

            public readonly HashSet<long> AllControlled = new HashSet<long>();
        }

        /// <summary>按可见性状态渲染时的实体过滤器。</summary>
        private sealed class VisFilter
        {
            public HashSet<long> Visible;
            public HashSet<long> Controlled;

            public bool IsVisible(long handle)
            {
                return !Controlled.Contains(handle) || Visible.Contains(handle);
            }
        }

        /// <summary>可见性参数解析结果缓存：key = 图块条目 Key（会话内，避免每次切换都重新导 DXF）。</summary>
        private readonly Dictionary<string, List<VisParamData>> _visData =
            new Dictionary<string, List<VisParamData>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 取该图块 DWG 里可见性动态块的可见性状态名列表（≥2 个才有切换意义）。
        /// 优先读内存缓存，未命中时读 ".vis" 标记文件。非可见性动态块返回空列表。
        /// </summary>
        public List<string> GetVisibilityStates(BlockItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.Key)) return new List<string>();

            List<string> states;
            if (_visStates.TryGetValue(item.Key, out states)) return states ?? new List<string>();

            try
            {
                string marker = GetCachePath(item.FilePath) + ".vis";
                states = new List<string>();
                if (File.Exists(marker))
                {
                    foreach (string line in File.ReadAllLines(marker, System.Text.Encoding.UTF8))
                    {
                        string name = (line ?? string.Empty).Trim();
                        if (name.Length > 0 && !states.Contains(name)) states.Add(name);
                    }
                }
            }
            catch
            {
                states = new List<string>();
            }

            _visStates[item.Key] = states;
            return states;
        }

        /// <summary>
        /// 切换可见性动态块缩略图：按状态顺序切换到上一个（direction=-1）或
        /// 下一个（direction=1）可见性状态，同步重新渲染缩略图并覆盖内存缓存
        /// 与磁盘缓存，随后触发 ThumbnailReady 刷新界面。
        /// </summary>
        public void CycleVisibilityThumbnail(BlockItem item, int direction)
        {
            if (item == null || _disposed) return;

            List<string> states = GetVisibilityStates(item);
            if (states == null || states.Count < 2) return;

            int count = states.Count;
            int index;
            _visIndex.TryGetValue(item.Key, out index);
            index = ((index + direction) % count + count) % count;   // 支持负方向循环
            _visIndex[item.Key] = index;

            Image img = RenderVisibilityState(item, states[index]);
            if (img == null) return;

            // 注意：旧图**不能**在这里 Dispose。它可能正被 ImageList / 列表项引用着
            // （CreateDisplayImage 对非动态块直接返回缓存里的同一实例），
            // 释放后界面重绘就会抛"参数无效"。只替换引用，旧的交给 GC 回收。
            PutCache(item.Key, img);

            SavePng(img, GetCachePath(item.FilePath));
            RaiseReady(item);
        }

        /// <summary>
        /// 取（或解析并缓存）该图块 DWG 的可见性参数数据。
        /// 可见性数据没有托管 API，通过把侧数据库导出成 DXF 再解析
        /// BLOCKVISIBILITYPARAMETER 对象（303 状态名 / 94 数量 / 331 可见实体句柄）获得。
        /// </summary>
        private List<VisParamData> GetVisData(string itemKey, AcadDb db)
        {
            if (string.IsNullOrEmpty(itemKey)) return null;

            List<VisParamData> data;
            if (_visData.TryGetValue(itemKey, out data)) return data;

            data = new List<VisParamData>();
            string dxfPath = Path.Combine(Path.GetTempPath(), "blm_vis_" + Guid.NewGuid().ToString("N") + ".dxf");

            try
            {
                db.DxfOut(dxfPath, 0, Autodesk.AutoCAD.DatabaseServices.DwgVersion.Current);
                data = ParseDxfVisibility(dxfPath);
            }
            catch
            {
                data = new List<VisParamData>();
            }
            finally
            {
                try { if (File.Exists(dxfPath)) File.Delete(dxfPath); } catch { }
            }

            _visData[itemKey] = data;
            return data;
        }

        /// <summary>解析 DXF 文本里全部 BLOCKVISIBILITYPARAMETER 对象的状态与可见实体句柄。</summary>
        private static List<VisParamData> ParseDxfVisibility(string dxfPath)
        {
            List<VisParamData> result = new List<VisParamData>();

            try
            {
                // 流式解析：DXF 动辄几十 MB，一次性 ReadAllLines 会把整个文件读进内存，
                // 解析一个图块就顶一次内存峰值。改成边读边解析，内存占用恒定。
                // （detectEncodingFromByteOrderMarks=true，自动识别 UTF-8 BOM）
                using (StreamReader reader = new StreamReader(
                    dxfPath, System.Text.Encoding.UTF8, true))
                {
                    VisParamData param = null;
                    HashSet<long> current = null;
                    int remain = 0;

                    // DXF 文本严格"组码行 + 值行"成对出现，所以两行两行地读
                    while (true)
                    {
                        string codeLine = reader.ReadLine();
                        if (codeLine == null) break;

                        string valueLine = reader.ReadLine();
                        if (valueLine == null) break;

                        string code = codeLine.Trim();

                        if (code == "0")
                        {
                            // 新对象开始：先把正在解析的可见性参数收尾
                            if (param != null)
                            {
                                AddIfUseful(result, param);
                                param = null;
                                current = null;
                                remain = 0;
                            }

                            if (string.Equals(valueLine.Trim(), "BLOCKVISIBILITYPARAMETER",
                                    StringComparison.OrdinalIgnoreCase))
                                param = new VisParamData();

                            continue;
                        }

                        if (param == null) continue;   // 不在可见性参数对象里，跳过

                        if (code == "303")
                        {
                            string name = DecodeDxfText(valueLine).Trim();
                            if (name.Length == 0) name = "状态" + (param.StateHandles.Count + 1);
                            if (!param.StateHandles.ContainsKey(name))
                                param.StateHandles[name] = new HashSet<long>();
                            current = param.StateHandles[name];
                            remain = 0;
                        }
                        else if (code == "94")
                        {
                            int.TryParse(valueLine.Trim(), out remain);
                        }
                        else if (code == "331")
                        {
                            long handle;
                            if (current != null && remain > 0 &&
                                long.TryParse(valueLine.Trim(),
                                    System.Globalization.NumberStyles.HexNumber,
                                    System.Globalization.CultureInfo.InvariantCulture, out handle))
                            {
                                current.Add(handle);
                                param.AllControlled.Add(handle);
                                remain--;
                            }
                        }
                    }

                    // 文件结束时收尾最后一个参数
                    if (param != null) AddIfUseful(result, param);
                }
            }
            catch
            {
                result.Clear();
            }

            return result;
        }

        /// <summary>
        /// 只收"至少两个状态、且确实解析到可见实体"的可见性参数，
        /// 过滤掉解析残缺的坏块（否则切换按钮会出现但点了没反应）。
        /// </summary>
        private static void AddIfUseful(List<VisParamData> result, VisParamData param)
        {
            if (param == null || param.StateHandles.Count < 2) return;

            foreach (HashSet<long> set in param.StateHandles.Values)
            {
                if (set.Count > 0)
                {
                    result.Add(param);
                    return;
                }
            }
        }

        /// <summary>解码 DXF 文本中的 \U+XXXX Unicode 转义（中文状态名常见）。</summary>
        private static string DecodeDxfText(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf("\\U+", StringComparison.OrdinalIgnoreCase) < 0)
                return text;

            System.Text.StringBuilder sb = new System.Text.StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (i + 5 < text.Length + 1 && i + 6 <= text.Length &&
                    text[i] == '\\' && text[i + 1] == 'U' && text[i + 2] == '+' )
                {
                    int code;
                    string hex = text.Substring(i + 3, 4);
                    if (hex.Length == 4 &&
                        int.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out code))
                    {
                        sb.Append((char)code);
                        i += 6;
                        continue;
                    }
                }
                sb.Append(text[i]);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 按指定可见性状态重新渲染整张预览图（只动内存，不写回 DWG 文件）。
        /// 动态块参照改从"块定义"展开（定义里才带每个实体的句柄，匿名块只是当前状态的克隆），
        /// 并按该状态的可见实体集合过滤。
        /// </summary>
        private Image RenderVisibilityState(BlockItem item, string stateName)
        {
            if (item == null || string.IsNullOrEmpty(item.FilePath) || !File.Exists(item.FilePath))
                return null;

            AcadDb db = null;
            try
            {
                db = new AcadDb(false, true);
                db.ReadDwgFile(item.FilePath,
                    Autodesk.AutoCAD.DatabaseServices.FileOpenMode.OpenForReadAndReadShare,
                    false, string.Empty);

                List<VisParamData> data = GetVisData(item.Key, db);
                if (data == null || data.Count == 0) return null;

                VisParamData chosen = null;
                foreach (VisParamData p in data)
                {
                    if (p.StateHandles.ContainsKey(stateName)) { chosen = p; break; }
                }
                if (chosen == null) return null;

                HashSet<long> visible;
                if (!chosen.StateHandles.TryGetValue(stateName, out visible) || visible.Count == 0)
                    return null;

                VisFilter filter = new VisFilter { Visible = visible, Controlled = chosen.AllControlled };
                return RenderPreview(db, filter);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (db != null)
                {
                    try { db.CloseInput(true); } catch { }
                    try { db.Dispose(); } catch { }
                }
            }
        }

        /// <summary>写入可见性状态标记文件（每行一个状态名，UTF-8）。</summary>
        private void WriteVisibilityMarker(string dwgPath, List<string> states)
        {
            try
            {
                if (states == null || states.Count < 2) return;
                File.WriteAllLines(GetCachePath(dwgPath) + ".vis",
                    states.ToArray(), System.Text.Encoding.UTF8);
            }
            catch
            {
                // 标记写不进去只影响切换按钮显示，不影响功能
            }
        }

        /// <summary>删除可见性状态标记文件（检测不到可见性状态时清理旧标记）。</summary>
        private void RemoveVisibilityMarker(string dwgPath)
        {
            try
            {
                string marker = GetCachePath(dwgPath) + ".vis";
                if (File.Exists(marker)) File.Delete(marker);
            }
            catch
            {
                // 清不掉就算了
            }
        }

        /// <summary>
        /// 检测 DWG 数据库里是否包含"实际在用"的动态块。
        /// 判定标准（两者其一）：
        ///   1. 任一布局空间里存在动态块参照（BlockReference.IsDynamic）；
        ///   2. 存在被块参照实际引用的动态块定义（BlockTableRecord.IsDynamicBlock）。
        /// 之前只检查"库里存在动态块定义"， drawings 里残留的未使用动态块定义
        /// 会被误标为动态块，导致角标不准确；现在必须真正被引用才算。
        /// </summary>
        private static bool DetectDynamicBlock(AcadDb db)
        {
            try
            {
                using (AcadTr tr = (AcadTr)db.TransactionManager.StartTransaction())
                {
                    Autodesk.AutoCAD.DatabaseServices.BlockTable bt =
                        (Autodesk.AutoCAD.DatabaseServices.BlockTable)tr.GetObject(
                            db.BlockTableId, AcadOpenMode.ForRead);

                    HashSet<Autodesk.AutoCAD.DatabaseServices.ObjectId> referenced =
                        new HashSet<Autodesk.AutoCAD.DatabaseServices.ObjectId>();

                    // 第一遍：遍历所有布局空间（模型 + 图纸）中的块参照
                    foreach (Autodesk.AutoCAD.DatabaseServices.ObjectId btrId in bt)
                    {
                        AcadBtr btr = tr.GetObject(btrId, AcadOpenMode.ForRead) as AcadBtr;
                        if (btr == null || !btr.IsLayout) continue;

                        foreach (Autodesk.AutoCAD.DatabaseServices.ObjectId entId in btr)
                        {
                            Autodesk.AutoCAD.DatabaseServices.BlockReference br =
                                tr.GetObject(entId, AcadOpenMode.ForRead) as
                                    Autodesk.AutoCAD.DatabaseServices.BlockReference;
                            if (br == null) continue;

                            // 动态块实例：参照指向的动态定义与其原始定义不一致
                            //（2020 程序集的 BlockReference 没有 IsDynamic 属性，用这个等价判断）
                            bool isDynamicRef;
                            try { isDynamicRef = br.BlockTableRecord != br.DynamicBlockTableRecord; }
                            catch { isDynamicRef = false; }

                            if (isDynamicRef)
                            {
                                tr.Commit();
                                return true;    // 动态块实例，直接判定
                            }

                            referenced.Add(br.BlockTableRecord);
                        }
                    }

                    // 第二遍：被引用的块定义中存在动态块定义
                    foreach (Autodesk.AutoCAD.DatabaseServices.ObjectId btrId in bt)
                    {
                        AcadBtr btr = tr.GetObject(btrId, AcadOpenMode.ForRead) as AcadBtr;
                        if (btr != null && btr.IsDynamicBlock && referenced.Contains(btrId))
                        {
                            tr.Commit();
                            return true;
                        }
                    }

                    tr.Commit();
                }
            }
            catch
            {
                // 检测失败按非动态块处理
            }

            return false;
        }

        /// <summary>汇总解析到的可见性状态名（跨全部可见性参数去重，保持出现顺序）。</summary>
        private List<string> DetectVisibilityStateNames(string itemKey, AcadDb db)
        {
            List<string> states = new List<string>();
            try
            {
                List<VisParamData> data = GetVisData(itemKey, db);
                if (data == null) return states;

                foreach (VisParamData param in data)
                {
                    foreach (string name in param.StateHandles.Keys)
                        if (!states.Contains(name)) states.Add(name);
                }
            }
            catch
            {
                // 解析失败按没有可见性状态处理
            }
            return states;
        }

        /// <summary>
        /// 按当前底色模式处理预览位图：
        /// 白底模式遇到黑底图 → 连通黑色背景填充为白色；
        /// 黑底模式遇到白底图 → 连通白色背景填充为黑色。
        /// 图块自身的线条颜色保持不变。失败时原样返回（调用方需 using）。
        /// </summary>
        private Bitmap ApplyBackgroundMode(Bitmap source)
        {
            try
            {
                bool dark = IsDarkEdgeBackground(source);
                if (WhiteBackground && dark)
                    return ReplaceEdgeBackground(source, true);
                if (!WhiteBackground && !dark)
                    return ReplaceEdgeBackground(source, false);
            }
            catch
            {
                // 处理失败保持原图
            }
            return source;
        }

        /// <summary>
        /// 判断预览图是不是"深色背景"：统计四条边一圈采样像素，
        /// 亮度低于 64 的占比 ≥ 60% 就认定是黑底。
        /// </summary>
        private static bool IsDarkEdgeBackground(Bitmap bmp)
        {
            try
            {
                int width = bmp.Width, height = bmp.Height;
                if (width < 4 || height < 4) return false;

                Rectangle full = new Rectangle(0, 0, width, height);
                BitmapData data = bmp.LockBits(full, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int stride = data.Stride;
                    byte[] buffer = new byte[stride * height];
                    Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

                    int dark = 0, total = 0;
                    Action<int, int> sample = delegate (int x, int y)
                    {
                        int i = y * stride + x * 4;      // BGRA
                        int lum = (buffer[i] * 299 + buffer[i + 1] * 587 + buffer[i + 2] * 114) / 1000;
                        if (lum < 64) dark++;
                        total++;
                    };

                    for (int x = 0; x < width; x += 2) { sample(x, 0); sample(x, height - 1); }
                    for (int y = 0; y < height; y += 2) { sample(0, y); sample(width - 1, y); }

                    return total > 0 && dark * 10 >= total * 6;
                }
                finally
                {
                    bmp.UnlockBits(data);
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 从四条边开始泛洪填充，把与边缘背景色连通的像素整体替换成目标底色。
        /// darkEdge=true 表示当前背景是黑色、替换为白；false 表示背景是白色、替换为黑。
        /// 只动与边缘连通的背景区域，图块内部的同色线条不受影响。
        /// </summary>
        private static Bitmap ReplaceEdgeBackground(Bitmap source, bool darkEdge)
        {
            int width = source.Width, height = source.Height;
            if (width < 4 || height < 4) return source;

            Rectangle full = new Rectangle(0, 0, width, height);
            BitmapData data = source.LockBits(full, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);

            try
            {
                int stride = data.Stride;
                byte[] buffer = new byte[stride * height];
                Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

                // 背景判定阈值：黑底亮度 < 80，白底亮度 > 208
                Func<int, bool> isBg = delegate (int i)
                {
                    int lum = (buffer[i] * 299 + buffer[i + 1] * 587 + buffer[i + 2] * 114) / 1000;
                    return darkEdge ? lum < 80 : lum > 208;
                };

                byte[] visited = new byte[width * height];
                Stack<int> stack = new Stack<int>();

                Action<int, int> tryPush = delegate (int x, int y)
                {
                    int p = y * width + x;
                    if (visited[p] != 0) return;
                    int i = y * stride + x * 4;
                    if (!isBg(i)) return;
                    visited[p] = 1;
                    stack.Push(p);
                };

                for (int x = 0; x < width; x++) { tryPush(x, 0); tryPush(x, height - 1); }
                for (int y = 0; y < height; y++) { tryPush(0, y); tryPush(width - 1, y); }

                byte fillB = darkEdge ? (byte)255 : (byte)0;
                byte fillG = darkEdge ? (byte)255 : (byte)0;
                byte fillR = darkEdge ? (byte)255 : (byte)0;

                while (stack.Count > 0)
                {
                    int p = stack.Pop();
                    int x = p % width;
                    int y = p / width;
                    int i = y * stride + x * 4;
                    buffer[i] = fillB;
                    buffer[i + 1] = fillG;
                    buffer[i + 2] = fillR;
                    buffer[i + 3] = 255;

                    if (x > 0) tryPush(x - 1, y);
                    if (x < width - 1) tryPush(x + 1, y);
                    if (y > 0) tryPush(x, y - 1);
                    if (y < height - 1) tryPush(x, y + 1);
                }

                Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);

                Bitmap result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                BitmapData dst = result.LockBits(full, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    Marshal.Copy(buffer, 0, dst.Scan0, Math.Min(buffer.Length, dst.Stride * height));
                }
                finally
                {
                    result.UnlockBits(dst);
                }
                return result;
            }
            finally
            {
                source.UnlockBits(data);
            }
        }

        /// <summary>
        /// 清空全部缩略图缓存（内存 + 磁盘 PNG），用于切换底色模式后强制重新生成。
        /// 注意：这里只解除引用、不主动 Dispose —— 部分图像此刻还在 ListView 的
        /// ImageList / 屏幕重绘路径里被引用，立即释放会在 UI 重绘时抛
        /// "System.ArgumentException: 参数无效"（Image.get_Width 访问已释放对象）。
        /// 释放交给 GC 终结器稍后处理；动态块 / 可见性检测结果标记保留。
        /// </summary>
        public void ClearAllCaches()
        {
            _memoryCache.Clear();
            _cacheOrder.Clear();
            _pendingKeys.Clear();
            _pending.Clear();
            _generate.Clear();
            _visIndex.Clear();

            try
            {
                string folder = CacheFolder;
                if (Directory.Exists(folder))
                {
                    foreach (string file in Directory.GetFiles(folder, CacheVersionPrefix + "*.png"))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            catch
            {
                // 磁盘清不掉时，靠内存缓存清空 + 重新生成覆盖同名文件
            }
        }

        private void RaiseReady(BlockItem item)
        {
            Action<BlockItem> handler = ThumbnailReady;
            if (handler != null)
            {
                try { handler(item); }
                catch { /* 回调异常不能中断队列处理 */ }
            }
        }

        // ---------- 生成 ----------

        private Image CreateThumbnail(BlockItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.FilePath)) return null;
            if (!File.Exists(item.FilePath)) return null;

            Image result = null;
            Autodesk.AutoCAD.DatabaseServices.Database db = null;

            try
            {
                db = new Autodesk.AutoCAD.DatabaseServices.Database(false, true);
                db.ReadDwgFile(item.FilePath,
                    Autodesk.AutoCAD.DatabaseServices.FileOpenMode.OpenForReadAndReadShare,
                    false, string.Empty);

                // 顺带检测该 DWG 是否含动态块定义（数据库已打开，代价极小）
                bool dyn = DetectDynamicBlock(db);
                _dynamicFlags[item.Key] = dyn;
                if (dyn) WriteDynamicMarker(item.FilePath);

                // 可见性状态检测需要把整个 DWF 导出成 DXF 文本再解析（DxfOut，开销很大），
                // 只对真正含动态块的文件做——普通图块（绝大多数）直接跳过，大幅缩短生成时间。
                List<string> visStates;
                if (dyn)
                {
                    visStates = DetectVisibilityStateNames(item.Key, db);
                }
                else
                {
                    visStates = new List<string>();
                    _visData[item.Key] = new List<VisParamData>();   // 非动态块，无需解析
                }
                _visStates[item.Key] = visStates;
                if (visStates != null && visStates.Count >= 2)
                    WriteVisibilityMarker(item.FilePath, visStates);
                else
                    RemoveVisibilityMarker(item.FilePath);

                using (Bitmap preview = db.ThumbnailBitmap)
                {
                    if (preview != null)
                    {
                        // 不少 DWG 的预览位图右缘带一条与图块内容无关的纯黑竖带，
                        // 直接缩放就会出现"缩略图右侧黑边"。先裁掉再适配画布。
                        using (Bitmap trimmed = TrimRightBlackBand(preview))
                        // 按当前底色模式把黑底图转白底（或白底图转黑底），只替换连通背景色
                        using (Bitmap modeApplied = ApplyBackgroundMode(trimmed))
                        {
                            int size = _config.ThumbSize;
                            Image fitted = FitToCanvas(modeApplied, size, size,
                                WhiteBackground ? Color.White : Color.Black);
                            SavePng(fitted, GetCachePath(item.FilePath));
                            result = fitted;
                        }
                    }
                }

                // 兜底：很多 DWG 保存时没写预览位图（ThumbnailBitmap 为空），
                // 之前会一直显示"DWG"占位图。现在退而求其次——把模型空间的
                // 几何（含块参照逐层展开）自绘成线框预览。
                if (result == null)
                {
                    Image fallback = RenderPreview(db, null);
                    if (fallback != null)
                    {
                        SavePng(fallback, GetCachePath(item.FilePath));
                        result = fallback;
                    }
                }
            }
            catch
            {
                result = null;
            }
            finally
            {
                if (db != null)
                {
                    try { db.CloseInput(true); } catch { }
                    try { db.Dispose(); } catch { }
                }
            }

            return result;
        }

        // ---------- 无预览位图时的线框兜底渲染 ----------

        /// <summary>几何累计范围（简易可变结构）。</summary>
        private sealed class BoundsAcc
        {
            public bool Has;
            public double MinX, MinY, MaxX, MaxY;

            public void Add(double x, double y)
            {
                if (!Has)
                {
                    MinX = MaxX = x;
                    MinY = MaxY = y;
                    Has = true;
                    return;
                }
                if (x < MinX) MinX = x;
                if (x > MaxX) MaxX = x;
                if (y < MinY) MinY = y;
                if (y > MaxY) MaxY = y;
            }

            public void AddPoint(AcadPoint3d p) { Add(p.X, p.Y); }

            public void AddExtents(AcadExtents3d ext)
            {
                Add(ext.MinPoint.X, ext.MinPoint.Y);
                Add(ext.MaxPoint.X, ext.MaxPoint.Y);
            }
        }

        /// <summary>
        /// 把 DWG 模型空间的几何自绘成一张线框预览图。
        /// 用于保存时没写预览位图的文件（之前只能显示"DWG"占位图），
        /// 或按可见性状态切换重画（filter 不为空时）。
        /// 任何失败都返回 null（走占位图），绝不抛异常。
        /// </summary>
        private Image RenderPreview(AcadDb db, VisFilter filter)
        {
            try
            {
                List<AcadPts> polylines = new List<AcadPts>();
                BoundsAcc bounds = new BoundsAcc();

                using (AcadTr tr = db.TransactionManager.StartTransaction())
                {
                    AcadBtr space =
                        (AcadBtr)tr.GetObject(db.CurrentSpaceId, AcadOpenMode.ForRead);

                    foreach (Autodesk.AutoCAD.DatabaseServices.ObjectId id in space)
                    {
                        AcadEntity ent = tr.GetObject(id, AcadOpenMode.ForRead) as AcadEntity;
                        if (ent != null)
                            AppendEntityGeometry(ent, AcadMatrix3d.Identity, 0, polylines, bounds, tr, filter);
                    }

                    tr.Commit();
                }

                if (!bounds.Has || polylines.Count == 0) return null;

                double w = bounds.MaxX - bounds.MinX;
                double h = bounds.MaxY - bounds.MinY;
                if (w <= 0 && h <= 0) return null;
                if (w <= 0) w = Math.Max(1e-6, h * 0.01);
                if (h <= 0) h = Math.Max(1e-6, w * 0.01);

                int size = _config.ThumbSize;
                if (size < 48) size = 48;

                int margin = 8;
                double scale = Math.Min((size - margin * 2) / w, (size - margin * 2) / h);
                if (scale <= 0 || double.IsInfinity(scale) || double.IsNaN(scale)) return null;

                // 居中 + Y 轴翻转（CAD 的 Y 向上，屏幕的 Y 向下）
                double offsetX = (size - w * scale) / 2.0 - bounds.MinX * scale;
                double offsetY = (size + h * scale) / 2.0 + bounds.MinY * scale;

                Bitmap canvas = new Bitmap(size, size, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(canvas))
                {
                    // 线框兜底渲染同样跟随底色模式：白底黑线 / 黑底白线
                    Color back = WhiteBackground ? Color.White : Color.Black;
                    Color line = WhiteBackground ? Color.FromArgb(40, 40, 40) : Color.FromArgb(225, 225, 225);

                    g.Clear(back);
                    g.SmoothingMode = SmoothingMode.AntiAlias;

                using (Pen pen = new Pen(line, 1.2f))
                {
                    foreach (AcadPts pts in polylines)
                    {
                        for (int i = 0; i < pts.Count - 1; i++)
                        {
                            AcadPoint3d a = pts[i];
                            AcadPoint3d b = pts[i + 1];

                                g.DrawLine(pen,
                                    (float)(a.X * scale + offsetX),
                                    (float)(offsetY - a.Y * scale),
                                    (float)(b.X * scale + offsetX),
                                    (float)(offsetY - b.Y * scale));
                            }
                        }
                    }
                }
                return canvas;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>把一条折线加进结果并累计范围。</summary>
        private static void AddPolyline(AcadPts pts, List<AcadPts> polylines, BoundsAcc bounds)
        {
            if (pts == null || pts.Count < 2) return;
            for (int i = 0; i < pts.Count; i++) bounds.AddPoint(pts[i]);
            polylines.Add(pts);
        }

        /// <summary>
        /// 收集单个实体的几何：曲线按参数采样；块参照逐层展开（限 6 层）；
        /// 其它实体画外接框。所有点统一变换到世界坐标。
        /// </summary>
        private static void AppendEntityGeometry(
            AcadEntity ent, AcadMatrix3d transform, int depth,
            List<AcadPts> polylines, BoundsAcc bounds, AcadTr tr, VisFilter filter)
        {
            if (ent == null || depth > 6) return;

            try
            {
                // 当前可见性状态下被隐藏的实体不参与渲染
                //（可见性动态块的匿名块里，非当前状态的实体 Visible = false）
                if (!ent.Visible) return;

                // 可见性状态过滤：不在当前状态可见集合里的定义实体直接跳过
                if (filter != null && !filter.IsVisible(ent.Handle.Value)) return;

                // 动态块定义里的参数 / 动作实体是不可见的辅助图元，画出来只是乱线
                if (filter != null)
                {
                    try
                    {
                        string className = ent.GetRXClass().Name;
                        if (className.IndexOf("BlockParameter", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            className.IndexOf("BlockAction", StringComparison.OrdinalIgnoreCase) >= 0)
                            return;
                    }
                    catch { }
                }

                // 1. 块参照：进入它的块定义递归收集
                Autodesk.AutoCAD.DatabaseServices.BlockReference br = ent as Autodesk.AutoCAD.DatabaseServices.BlockReference;
                if (br != null)
                {
                    // 嵌套矩阵：先做块参照的变换（块空间 → 父空间），再叠加外层变换
                    AcadMatrix3d inner = transform.PostMultiplyBy(br.BlockTransform);

                    AcadBtr btr =
                        (AcadBtr)tr.GetObject(br.BlockTableRecord, AcadOpenMode.ForRead);

                    // 可见性切换渲染：动态参照改从"块定义"展开
                    //（定义里才带每个实体的句柄，匿名块只是当前状态烤出来的克隆）
                    if (filter != null)
                    {
                        try
                        {
                            if (br.BlockTableRecord != br.DynamicBlockTableRecord)
                            {
                                AcadBtr dynDef = (AcadBtr)tr.GetObject(
                                    br.DynamicBlockTableRecord, AcadOpenMode.ForRead);
                                if (dynDef != null) btr = dynDef;
                            }
                        }
                        catch { }
                    }

                    foreach (Autodesk.AutoCAD.DatabaseServices.ObjectId id in btr)
                    {
                        AcadEntity child = tr.GetObject(id, AcadOpenMode.ForRead) as AcadEntity;
                        if (child != null)
                            AppendEntityGeometry(child, inner, depth + 1, polylines, bounds, tr, filter);
                    }
                    return;
                }

                // 无限长的构造线没有有意义的范围，跳过
                if (ent is AcadXline || ent is AcadRay) return;

                // 2. 曲线（直线/多段线/圆/弧/椭圆/样条）：按参数均匀采样成折线
                AcadCurve curve = ent as AcadCurve;
                if (curve != null)
                {
                    double start = curve.StartParam;
                    double end = curve.EndParam;
                    if (end > start && !double.IsInfinity(start) && !double.IsInfinity(end))
                    {
                        const int samples = 32;
                        AcadPts pts = new AcadPts();
                        for (int i = 0; i <= samples; i++)
                        {
                            double p = start + (end - start) * i / samples;
                            pts.Add(curve.GetPointAtParameter(p).TransformBy(transform));
                        }
                        AddPolyline(pts, polylines, bounds);
                    }
                    return;
                }

                // 3. 其它实体（文字/填充/实体面等）：画外接框示意
                AcadExtents3d ext = ent.GeometricExtents;
                AcadPts rect = new AcadPts();
                rect.Add(new AcadPoint3d(ext.MinPoint.X, ext.MinPoint.Y, 0).TransformBy(transform));
                rect.Add(new AcadPoint3d(ext.MaxPoint.X, ext.MinPoint.Y, 0).TransformBy(transform));
                rect.Add(new AcadPoint3d(ext.MaxPoint.X, ext.MaxPoint.Y, 0).TransformBy(transform));
                rect.Add(new AcadPoint3d(ext.MinPoint.X, ext.MaxPoint.Y, 0).TransformBy(transform));
                rect.Add(new AcadPoint3d(ext.MinPoint.X, ext.MinPoint.Y, 0).TransformBy(transform));
                AddPolyline(rect, polylines, bounds);
            }
            catch
            {
                // 单个实体画不出来就跳过，不影响整体
            }
        }

        /// <summary>等比缩放并居中绘制到方形画布。</summary>
        public static Image FitToCanvas(Image source, int width, int height)
        {
            return FitToCanvas(source, width, height, Color.White);
        }

        public static Image FitToCanvas(Image source, int width, int height, Color canvasColor)
        {
            Bitmap canvas = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(canvas))
            {
                g.Clear(canvasColor);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                if (source == null || source.Width <= 0 || source.Height <= 0)
                    return canvas;

                double scaleX = (double)(width - 2) / source.Width;
                double scaleY = (double)(height - 2) / source.Height;
                double scale = Math.Min(scaleX, scaleY);
                if (scale > 1.0) scale = 1.0; // 小图不放大，避免糊

                int w = Math.Max(1, (int)(source.Width * scale));
                int h = Math.Max(1, (int)(source.Height * scale));
                int x = (width - w) / 2;
                int y = (height - h) / 2;

                g.DrawImage(source, new Rectangle(x, y, w, h));
            }
            return canvas;
        }

        /// <summary>
        /// 裁掉 DWG 预览位图贴右边缘的黑带。
        ///
        /// 判定：从最右列向左，连续"几乎全黑"的列（列内 ≥90% 采样像素 RGB 均 &lt;32）
        /// 达到 2 列以上时全部裁掉；黑区超过图宽 1/3 就放弃（防止把深色图纸误裁）。
        /// 图块内容里正常的黑色线条两侧都是白底，不会被误伤。
        /// 失败或不满足条件时原样返回（可能返回 source 本身，调用方需用 using）。
        /// </summary>
        private static Bitmap TrimRightBlackBand(Bitmap source)
        {
            try
            {
                int width = source.Width;
                int height = source.Height;
                if (width < 10 || height < 10) return source;

                Rectangle full = new Rectangle(0, 0, width, height);
                BitmapData data = source.LockBits(full, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

                int lastNonBlack;
                try
                {
                    int stride = data.Stride;
                    byte[] buffer = new byte[stride * height];
                    Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

                    int maxBand = width / 3;                     // 黑带超过 1/3 宽就放弃
                    int col = width - 1;
                    lastNonBlack = width - 1;

                    while (col >= 0 && (width - 1 - col) < maxBand &&
                           IsMostlyBlackColumn(buffer, stride, height, col))
                    {
                        lastNonBlack = col - 1;
                        col--;
                    }
                }
                finally
                {
                    source.UnlockBits(data);
                }

                // 没有黑带（最右列本来就是内容）或整张全黑，都不动
                if (lastNonBlack >= width - 1 || lastNonBlack < 0) return source;

                int newWidth = lastNonBlack + 1;
                if (newWidth >= width) return source;

                return source.Clone(new Rectangle(0, 0, newWidth, height), source.PixelFormat);
            }
            catch
            {
                return source;
            }
        }

        /// <summary>判断一列是否"几乎全黑"（≥90% 采样像素 RGB 都低于 32）。</summary>
        private static bool IsMostlyBlackColumn(byte[] buffer, int stride, int height, int col)
        {
            int step = Math.Max(1, height / 60);      // 最多采样 60 行，足够判定
            int black = 0, total = 0;

            for (int y = 0; y < height; y += step)
            {
                int idx = y * stride + col * 4;
                byte b = buffer[idx];
                byte grn = buffer[idx + 1];
                byte r = buffer[idx + 2];

                total++;
                if (r < 32 && grn < 32 && b < 32) black++;
            }

            return total > 0 && black * 10 >= total * 9;
        }

        // ---------- 缓存 ----------

        private string GetCachePath(string dwgPath)
        {
            string key;
            if (string.IsNullOrEmpty(dwgPath)) key = "unknown";
            else key = Md5(dwgPath.ToLowerInvariant());

            int size = _config.ThumbSize;
            string folder = CacheFolder;

            CleanLegacyCacheOnce(folder);
            PruneDiskCacheOnce(folder);

            return Path.Combine(folder, CacheVersionPrefix + key + "_" + size + ".png");
        }

        /// <summary>
        /// 清理旧版本缓存（文件名不带 v2_ 前缀的 PNG）。
        /// 旧图是带着右缘黑边生成的，留着也没用；删掉让它们按新逻辑重新生成。
        /// 只在第一次用到缓存目录时执行一次。
        /// </summary>
        private static void CleanLegacyCacheOnce(string folder)
        {
            if (_legacyCacheCleaned) return;
            _legacyCacheCleaned = true;

            try
            {
                if (!Directory.Exists(folder)) return;

                foreach (string file in Directory.GetFiles(folder, "*.png"))
                {
                    string name = Path.GetFileName(file);
                    if (name.StartsWith(CacheVersionPrefix, StringComparison.OrdinalIgnoreCase)) continue;

                    try { File.Delete(file); } catch { }
                }

                // 旧版本的动态块标记文件（.png.dyn）与可见性状态标记（.png.vis）一并清理
                foreach (string file in Directory.GetFiles(folder, "*.png.dyn"))
                {
                    string name = Path.GetFileName(file);
                    if (name.StartsWith(CacheVersionPrefix, StringComparison.OrdinalIgnoreCase)) continue;

                    try { File.Delete(file); } catch { }
                }

                foreach (string file in Directory.GetFiles(folder, "*.png.vis"))
                {
                    string name = Path.GetFileName(file);
                    if (name.StartsWith(CacheVersionPrefix, StringComparison.OrdinalIgnoreCase)) continue;

                    try { File.Delete(file); } catch { }
                }
            }
            catch
            {
                // 清理失败不影响使用，只是旧文件多占点磁盘
            }
        }

        /// <summary>磁盘缓存 PNG 数量上限，超出后按最后访问时间淘汰最旧的。</summary>
        private const int MaxDiskCacheFiles = 4000;

        private static bool _diskCachePruned;

        /// <summary>
        /// 磁盘缓存容量控制。图库越用越大、换过缩略图尺寸或底色后，
        /// 旧 PNG 会一直堆在 %LOCALAPPDATA% 里；这里在每次进程启动时检查一次，
        /// 超过上限就删掉最久没被用到的一批（连带它的 .dyn / .vis 标记文件），
        /// 避免缓存目录无限膨胀。被删的图块下次打开时会重新生成，不影响使用。
        /// </summary>
        private static void PruneDiskCacheOnce(string folder)
        {
            if (_diskCachePruned) return;
            _diskCachePruned = true;

            try
            {
                if (!Directory.Exists(folder)) return;

                FileInfo[] files = new DirectoryInfo(folder).GetFiles(CacheVersionPrefix + "*.png");
                if (files.Length <= MaxDiskCacheFiles) return;

                // 按最后访问时间升序：最久没用过的排在前面，优先删
                Array.Sort(files, delegate(FileInfo a, FileInfo b)
                {
                    try { return a.LastAccessTimeUtc.CompareTo(b.LastAccessTimeUtc); }
                    catch { return 0; }
                });

                // 一次多留 5% 余量，避免之后每加一张就又要排序删一次
                int keep = (int)(MaxDiskCacheFiles * 0.95);

                for (int i = 0; i < files.Length - keep; i++)
                {
                    string path = files[i].FullName;
                    try { files[i].Delete(); } catch { }

                    try
                    {
                        if (File.Exists(path + ".dyn")) File.Delete(path + ".dyn");
                        if (File.Exists(path + ".vis")) File.Delete(path + ".vis");
                    }
                    catch { }
                }
            }
            catch
            {
                // 清理失败不影响使用，只是缓存多占点磁盘
            }
        }

        private static string Md5(string text)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
            using (System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] hash = md5.ComputeHash(bytes);
                System.Text.StringBuilder sb = new System.Text.StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private void SavePng(Image image, string path)
        {
            try
            {
                string folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                    Directory.CreateDirectory(folder);

                // 先写临时文件再替换，避免半张图被读进缓存
                string temp = path + ".tmp";
                image.Save(temp, ImageFormat.Png);
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch
            {
                // 缓存失败可以忽略：下次重新生成
            }
        }

        private static Image TryLoadPng(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

                // 不能直接 Image.FromFile，那会锁住文件
                byte[] bytes = File.ReadAllBytes(path);
                MemoryStream stream = new MemoryStream(bytes);
                Image img = Image.FromStream(stream);
                return img;
            }
            catch
            {
                return null;
            }
        }

        public Image GetPlaceholder(bool wholeDrawing)
        {
            if (wholeDrawing)
            {
                if (_placeholderWhole == null) _placeholderWhole = DrawPlaceholder("DWG", Color.SteelBlue);
                return _placeholderWhole;
            }

            if (_placeholderInner == null) _placeholderInner = DrawPlaceholder("块", Color.DarkSeaGreen);
            return _placeholderInner;
        }

        private Image DrawPlaceholder(string text, Color color)
        {
            int size = _config.ThumbSize;
            if (size < 48) size = 48;

            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.WhiteSmoke);
                using (Pen pen = new Pen(color, 2f))
                {
                    g.DrawRectangle(pen, 2, 2, size - 5, size - 5);
                }
                using (Brush brush = new SolidBrush(color))
                using (Font font = new Font("Microsoft YaHei", 14f, FontStyle.Bold))
                {
                    StringFormat sf = new StringFormat();
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(text, font, brush, new RectangleF(0, 0, size, size), sf);
                }
            }
            return bmp;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 缓存里的图**不**逐个 Dispose：它们可能仍被 ImageList / 列表项引用，
            // 释放后任何一次重绘都会抛"参数无效"。只解除引用，由 GC 统一回收。
            _memoryCache.Clear();
            _cacheOrder.Clear();
            _pendingKeys.Clear();
            _pending.Clear();
            _generate.Clear();

            // 占位图是本服务自己创建、且从不外泄所有权的，可以安全释放
            if (_placeholderWhole != null) { try { _placeholderWhole.Dispose(); } catch { } }
            if (_placeholderInner != null) { try { _placeholderInner.Dispose(); } catch { } }
            _placeholderWhole = null;
            _placeholderInner = null;
        }
    }
}
