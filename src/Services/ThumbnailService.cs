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
using AcadDictionary = Autodesk.AutoCAD.DatabaseServices.DBDictionary;
using AcadLayout = Autodesk.AutoCAD.DatabaseServices.Layout;
using AcadBlockRef = Autodesk.AutoCAD.DatabaseServices.BlockReference;
using AcadObjectId = Autodesk.AutoCAD.DatabaseServices.ObjectId;

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

        // 这个队列不能是 readonly —— Prioritize() 会整体重排它
        //（数据虚拟化：把屏幕上看得见的条目挪到队首优先出图）。
        private Queue<BlockItem> _pending = new Queue<BlockItem>();
        private readonly HashSet<string> _pendingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, Image> _memoryCache =
            new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 内存缓存的 LRU 访问顺序（末尾 = 最近使用）。
        ///
        /// 为什么需要上限：图库动辄上千个 DWG，每张缩略图是一个 Bitmap，
        /// 无上限缓存会让内存随浏览量一直涨。超过上限时淘汰最久未用的一批。
        ///
        /// 为什么淘汰时**不 Dispose**：缓存里的图会被列表的自绘代码直接拿去
        /// DrawImage（自绘之后不再经过 ImageList），如果在这里 Dispose，
        /// 一次还没画完的重绘就会抛"参数无效"——这正是历史崩溃的根源。
        /// 只解除引用、交给 GC 回收，是最安全的做法。
        /// </summary>
        /// <summary>
        /// LRU 顺序链（最近使用的在链尾）+ key → 链表节点的索引。
        ///
        /// 以前是 List&lt;string&gt;：TouchCache 要 RemoveAll 全扫一遍再 Add，
        /// 而自绘是"逐项取图"，每取一项就 touch 一次、key 每次都变，
        /// 于是每一帧都在做 O(600) 的字符串比较 + 数组搬移。
        /// 改成双链表 + 字典后 Touch 是 O(1)（v1.51.12）。
        /// </summary>
        private readonly LinkedList<string> _cacheOrder = new LinkedList<string>();
        private readonly Dictionary<string, LinkedListNode<string>> _cacheNodes =
            new Dictionary<string, LinkedListNode<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>内存缓存最多保留的缩略图张数。</summary>
        private const int MaxMemoryCache = 600;

        private Image _placeholderWhole;
        private Image _placeholderInner;
        private bool _disposed;

        /// <summary>
        /// 是否已暂停。面板关闭（隐藏）或最小化时置位：
        /// 此时队列会立刻清空、后续登记与处理全部停止，
        /// 后台不再占用 AutoCAD 主线程去开 DWG——这正是"关掉面板后 CAD 变卡"的根源。
        /// </summary>
        private volatile bool _suspended;

        /// <summary>
        /// 生成失败记账：key → 本会话内已失败的次数。
        /// 失败的条目在内存缓存里存的是 null（见 PutCache），
        /// 重新排队时靠这个账本防止对真坏文件无限重试。
        /// Resume（面板重新可见）时清零，给失败过的条目新一轮机会。
        /// </summary>
        private readonly Dictionary<string, int> _failCounts = new Dictionary<string, int>();

        /// <summary>元数据（动态块/可见性）检测失败记账：key = 条目 Key。</summary>
        private readonly Dictionary<string, int> _metaFails = new Dictionary<string, int>();

        /// <summary>同一会话内元数据检测的最大自动重试次数。
        /// 检测失败的文件不会落盘任何标记，若不记账，每次可见区间变化都会
        /// 重新排队 → 再次 ReadDwgFile 打开 Database → 再次失败，无限循环。</summary>
        private const int MaxMetaRetry = 3;

        /// <summary>
        /// 同一会话内元数据检测（动态块 / 可见性）的总配额（v1.51.12）。
        /// 一次检测 = ReadDwgFile 打开 Database + 导出 DXF + 流式解析，
        /// 主线程上一次几十到几百毫秒。配额覆盖**全部**需要开图纸的情形：
        ///   · 从没检测过（新图库 / 清过缓存 / 升过版本）—— 数量可达几千；
        ///   · 旧版写坏的 0 字节 .vis 空壳需要重测 —— 本机实测遗留 1032 个。
        /// 不限量的话一启动就给它们全排上队，主线程被一轮轮 DxfOut 拖到假死。
        /// 配额外的留到下次会话：测成功一次就落盘非空标记，永不复发。
        /// </summary>
        private const int MaxMetaPerSession = 300;

        /// <summary>本会话已用掉的元数据检测配额。</summary>
        private int _metaUsed;

        /// <summary>同一会话内允许自动重试的最大次数，超过就不再排队（等下次 Resume 清零）。</summary>
        private const int MaxAutoRetry = 3;

        // ---- 并行解码层 ----
        //
        // 以前所有出图工作（读磁盘缓存、打开 DWG 取预览）都压在 AutoCAD 主线程上，
        // 只能靠 Application.Idle 一批批挤 40ms。现在只有真正需要 Database 的活
        // 才留在主线程，其余全部丢给线程池并行跑。

        /// <summary>后台线程的产出（线程间交接，取用前必须加锁）。</summary>
        private readonly List<KeyValuePair<BlockItem, Image>> _done =
            new List<KeyValuePair<BlockItem, Image>>();

        private readonly object _doneLock = new object();

        /// <summary>正在后台跑的任务数（Interlocked 维护）。</summary>
        private int _workers;

        /// <summary>并行上限。IO + 解码为主，CPU 不是瓶颈，给到 8 已经能吃满磁盘。</summary>
        private const int MaxWorkers = 8;

        /// <summary>
        /// 后台取不到内嵌预览、必须打开 Database 才能出图的条目（主线程做，慢）。
        /// 典型：THUMBSAVE=0 存的图、R13 以前的图、第三方工具生成的图。
        /// </summary>
        private Queue<BlockItem> _fallback = new Queue<BlockItem>();
        private readonly HashSet<string> _fallbackKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 只差"动态块 / 可见性状态"元数据的条目（主线程做，低优先级）。
        /// 缩略图本身已经从内嵌预览拿到了，只有屏幕上看得见的那些才需要补这个角标，
        /// 所以这一路只对可见项排队，避免为上千个用不上的文件白开 Database。
        /// </summary>
        private Queue<BlockItem> _meta = new Queue<BlockItem>();
        private readonly HashSet<string> _metaKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

        /// <summary>
        /// 缓存目录的一次性初始化（建目录 + 清旧版缓存 + 容量裁剪）是否已完成。
        /// volatile：后台预热线程写、主线程读，要保证读到的立刻可见。
        /// static 保证整个进程只做一次。
        /// </summary>
        private static volatile bool _cacheFolderReady;

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
        /// 后台预热缓存目录（v1.51.11）。
        /// 一次性清理（旧版缓存）+ 容量裁剪本来是在第一次 GetCachePath 时同步做的，
        /// 而首次调用常常发生在主线程上（Prioritize → HasDynamicMeta → GetCachePath），
        /// 缓存目录上万个小文件时就是一段白白的启动卡顿。这里在线程池上先跑一遍，
        /// 之后主线程再走到 EnsureCacheFolderReady 时标志位已置好，直接返回。
        /// </summary>
        public void WarmUpCacheFolder()
        {
            try
            {
                string folder = CacheFolder;
                System.Threading.ThreadPool.QueueUserWorkItem(delegate (object state)
                {
                    try { EnsureCacheFolderReady(folder); }
                    catch { }
                });
            }
            catch
            {
                // 预热失败不影响使用，最多是首次取缓存时同步做一次
            }
        }

        /// <summary>
        /// 把一批图块排进生成队列，返回本次新排入的数量。
        ///
        /// 关键：这里**不再同步读磁盘缓存**。以前每显示一页图块都要在主线程上
        /// 把上百个 PNG 读进来解码，是打开面板卡顿的头号原因；
        /// 现在只登记，真正的读缓存 / 生成统统推迟到 ProcessQueue（空闲时小批量做）。
        /// </summary>
        /// <summary>
        /// 暂停后台工作：清空待处理队列，之后不再登记、不再处理。
        /// 已生成好的内存缓存保留，下次打开面板立刻能用。
        /// </summary>
        public void Suspend()
        {
            _suspended = true;
            CancelPending();
        }

        /// <summary>恢复后台工作（面板重新可见时调用）。</summary>
        public void Resume()
        {
            _suspended = false;

            // 给上一轮生成失败的条目（缓存 null、显示占位图的那批）新一轮机会：
            // 清空失败账本，Resume 后调用方会重新 RequestThumbnails，它们会再次排队。
            // 不清零的话，一次偶发失败（CAD 忙 / 文件被占）就永远停留在"DWG"占位图。
            _failCounts.Clear();
            _metaFails.Clear();
        }

        /// <summary>内存缓存里是否有**可用的**缩略图（存在且非 null）。
        /// 缓存值为 null 表示生成失败，不算数 —— 允许重新排队重试。</summary>
        private bool HasUsableCache(string key)
        {
            Image img;
            return key != null && _memoryCache.TryGetValue(key, out img) && img != null;
        }

        /// <summary>当前是否处于暂停状态。</summary>
        public bool IsSuspended
        {
            get { return _suspended; }
        }

        /// <summary>
        /// 丢弃全部待处理条目（各个队列 + 去重集合）。
        /// 只丢"任务"，不丢已经生成好的缩略图缓存。
        ///
        /// 已经在后台跑着的任务拦不住（ThreadPool 上的工作项撤不回来），
        /// 但它们最多再跑几毫秒，且产出会在下一次 Resume 前一直躺在 _done 里。
        /// </summary>
        public void CancelPending()
        {
            _pending.Clear();
            _pendingKeys.Clear();
            _fallback.Clear();
            _fallbackKeys.Clear();
            _meta.Clear();
            _metaKeys.Clear();

            lock (_doneLock)
            {
                _done.Clear();
            }
        }

        public int RequestThumbnails(IEnumerable<BlockItem> items)
        {
            if (items == null) return 0;

            // 面板不可见时一律不登记：看不见的东西没必要在后台生成，
            // 否则队列会在隐藏期间越攒越多，白白拖慢 AutoCAD。
            if (_suspended) return 0;

            int added = 0;

            foreach (BlockItem item in items)
            {
                if (item == null) continue;
                // 只跳过"缓存里真有一张图"的；缓存为 null（上次生成失败）的重新排队重试
                if (HasUsableCache(item.Key)) continue;
                if (_pendingKeys.Contains(item.Key)) continue;

                // 已经在"开 Database 兜底"队列里的不能再进并行队列：
                // 两条队列同时为同一个文件出图，会各自 SavePng 到同一个路径
                // （共用 path.tmp），File.Move 互相踩；而且完成时 RaiseReady 会被
                // 触发两次，界面计数双算、进度条提前收尾。
                if (_fallbackKeys.Contains(item.Key)) continue;

                if (!item.IsWholeDrawing) continue;      // 内部块没有独立预览图

                // 失败太多次的本会话不再自动重试，防止对真坏文件无限开 Database
                int fails;
                if (_failCounts.TryGetValue(item.Key, out fails) && fails >= MaxAutoRetry) continue;

                _pendingKeys.Add(item.Key);
                _pending.Enqueue(item);
                added++;
            }

            // 一次登记太多（切到大分类 / 首屏）就启动软启动窗口，避免瞬间打满并发
            if (added > 200)
                _softStartUntilTick = Environment.TickCount + SoftStartMs;

            return added;
        }

        /// <summary>软启动时长（毫秒）：这段时间内只放 SoftStartWorkers 路并发。</summary>
        private const int SoftStartMs = 3000;

        /// <summary>软启动期间的并发路数。</summary>
        private const int SoftStartWorkers = 2;

        /// <summary>软启动窗口结束的时间戳（Environment.TickCount）。</summary>
        private int _softStartUntilTick;

        /// <summary>
        /// 主线程每轮 ProcessQueue 的时间预算（毫秒）。
        /// 碰到 Database 的任务（兜底出图、元数据检测）单次就是几十到几百毫秒，
        /// 超预算必须立刻停工，把剩下的留到下一次 Application.Idle。
        /// </summary>
        private const int MainThreadBudgetMs = 40;

        /// <summary>
        /// 数据虚拟化：把一批条目（调用方传的是"当前屏幕上看得见的那些"）挪到队列最前面，
        /// 让看得见的先出图。用户滚动后调用方会重算可见区间再调一次，于是队列始终
        /// 跟着视口走——这才是"只为可见项加载图片"在后台队列侧的那一半。
        ///
        /// 代价 O(n)（n = 队列长度），所以调用方只在可见区间真的变化时才调用，
        /// 不会每帧都重排。
        /// </summary>
        public void Prioritize(System.Collections.Generic.ICollection<BlockItem> first)
        {
            if (_suspended) return;
            if (first == null || first.Count == 0) return;

            DropMetaOutOfView(first);

            // 缩略图可以从内嵌预览直接取，但"是否动态块 / 有没有可见性状态"必须打开
            // 图纸才知道。只为屏幕上看得见的这几项排一次队——角标本来就只在可见项上画，
            // 看不见的那些根本用不上，没必要为它们开 Database。
            //
            // 注意这段必须放在下面的"队列为空就返回"之前：所有缩略图都出完之后
            // _pending / _fallback 都是空的，但用户滚动到新的可见项时仍然要补测元数据。
            foreach (BlockItem item in first)
            {
                if (item == null || string.IsNullOrEmpty(item.FilePath)) continue;
                if (!item.IsWholeDrawing) continue;              // 内部块没有独立 DWG
                if (_metaKeys.Contains(item.Key)) continue;       // 已在队列里

                // ---- 下面三项磁盘判定只算一次 basePath 然后复用（v1.51.12）----
                // 以前每个条件都各自调一次 HasDynamicMeta / NeedsVisibilityRetest，
                // 而它们内部各自 GetCachePath（含 MD5）+ 多次 File.Exists：
                // 单个可见项 = 2 次 MD5 + 5 次磁盘 stat，50 个可见项 × 每次视口变化
                // 就是几百次无谓 IO，全部落在主线程上。
                string basePath = GetCachePath(item.FilePath);
                bool hasMeta = HasDynamicMetaCore(basePath);
                bool retest = HasRetestMarker(basePath);

                if (hasMeta && !retest) continue;   // 已有结论且不是空壳

                // 失败账本要先判：被熔断的条目不该再吃掉下面的重测配额
                int metaFails;
                if (_metaFails.TryGetValue(item.Key, out metaFails) && metaFails >= MaxMetaRetry)
                    continue;

                // 从未检测（没有 .dyn/.nodyn）或空壳重测，都要开 Database + 导出 DXF，
                // 主线程上一次几十到几百毫秒。这里统一按会话配额限流：
                // 老图库里"从没检测过"的条目是几千量级（新图库/清过缓存/升过版本），
                // 历史空壳也有上千个，不限量的话一启动就给它们全排上队
                // = 主线程被 DxfOut 一轮轮拖死（打开面板明显卡顿 / 假死的原因）。
                // 配额外的留到下次会话慢慢自愈，反正测成功一次就落盘、永不复发。
                if (_metaUsed >= MaxMetaPerSession) continue;
                _metaUsed++;

                _metaKeys.Add(item.Key);
                _meta.Enqueue(item);
            }

            if (_pending.Count == 0 && _fallback.Count == 0) return;

            HashSet<string> front = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (BlockItem item in first)
            {
                if (item != null && !string.IsNullOrEmpty(item.Key))
                    front.Add(item.Key);
            }
            if (front.Count == 0) return;

            _pending = Reorder(_pending, front);
            _fallback = Reorder(_fallback, front);
        }

        /// <summary>
        /// 把已经滚出视口、却还排在 _meta 队列里的条目丢掉（v1.51.12）。
        /// _meta 队列以前只进不出：注释写着"只为屏幕上看得见的排队"，实际上条目
        /// 一旦入队就再不会因为滚出视口被移除。用户在一个两千项的分类里滚一遍，
        /// _meta 就攒下两千项，之后主线程每轮 Idle 处理一个、每个都要
        /// ReadDwgFile + DxfOut（几十~几百毫秒），界面会被连续拖住十几分钟。
        /// 元数据角标本来就只在看得见时才画，这些排队早已失去意义。
        /// </summary>
        private void DropMetaOutOfView(ICollection<BlockItem> visible)
        {
            if (_meta.Count == 0) return;

            HashSet<string> alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (BlockItem item in visible)
            {
                if (item != null && !string.IsNullOrEmpty(item.Key)) alive.Add(item.Key);
            }

            Queue<BlockItem> kept = new Queue<BlockItem>(_meta.Count);
            while (_meta.Count > 0)
            {
                BlockItem item = _meta.Dequeue();
                if (item == null) continue;

                if (alive.Contains(item.Key)) { kept.Enqueue(item); continue; }

                // 滚出视口：放弃这次检测机会，把配额还回去，并从去重集合里摘掉，
                // 这样用户滚回来时可以重新入队。
                _metaKeys.Remove(item.Key);
                if (_metaUsed > 0) _metaUsed--;
            }

            _meta = kept;
        }

        /// <summary>把队列中命中 front 的条目提到前面，其余保持原相对顺序。</summary>
        private static Queue<BlockItem> Reorder(Queue<BlockItem> source, HashSet<string> front)
        {
            if (source == null || source.Count == 0) return source;

            List<BlockItem> head = new List<BlockItem>(front.Count);
            List<BlockItem> tail = new List<BlockItem>(source.Count);

            foreach (BlockItem item in source)
            {
                if (item != null && front.Contains(item.Key)) head.Add(item);
                else tail.Add(item);
            }

            if (head.Count == 0) return source;     // 没有交集，原样返回避免无谓分配

            Queue<BlockItem> result = new Queue<BlockItem>(source.Count);
            foreach (BlockItem item in head) result.Enqueue(item);
            foreach (BlockItem item in tail) result.Enqueue(item);
            return result;
        }

        /// <summary>
        /// 处理队列中的若干条目。由 Application.Idle 事件驱动。
        ///
        /// 【调度策略】把活按"要不要碰 CAD Database"分成两类：
        ///   · 不碰 Database 的（读磁盘缓存 PNG、直读 DWG 内嵌预览、缩放、裁黑边、落盘）
        ///     → 全部丢给线程池并行跑，最多 MaxWorkers 路。主线程一个字节都不读。
        ///   · 必须碰 Database 的（没有内嵌预览时的线框兜底、动态块 / 可见性状态检测）
        ///     → 留在主线程，并且受 40ms 时间预算约束。
        ///
        /// 于是主线程在 ProcessQueue 里只干两件极便宜的事：
        /// 派活、收活。真正耗时的解码全线并行，AutoCAD 界面不再被出图拖住。
        /// 返回 true 表示还有活没干完，需要继续。
        /// </summary>
        public bool ProcessQueue(int maxItems)
        {
            // 暂停期间一滴活都不干：直接清空队列并返回 false，
            // 让空闲循环彻底空转，AutoCAD 主线程不被占。
            if (_suspended)
            {
                CancelPending();
                return false;
            }

            if (maxItems <= 0) maxItems = 1;

            int startTick = Environment.TickCount;

            // ---- 收活：把后台已经做完的缩略图搬进缓存（极便宜，纯字典操作）----
            DrainDone();

            // ---- 派活：把等待中的条目投进线程池，直到占满并行额度 ----
            //
            // 软启动（v1.51.11）：刚切到一个大分类时队列里可能一下堆进几千张图，
            // 8 路并发同时读盘 + 编码，磁盘 / CPU 立刻被吃满，用户感觉 CAD 变卡。
            // 前 SoftStartMs 毫秒只放 2 路，让界面和 CAD 自己的活先跑起来，之后恢复满并发。
            // 注意用**差值**比较而不是 TickCount < _softStartUntilTick：
            // Environment.TickCount 开机 24.8 天后会绕回负数，那时直接比较会
            // 永远判定"还在软启动窗口内"，把并发死死限制在 2 路。
            // 差值写法在环绕时依然给出正确结果。
            bool softStart = (Environment.TickCount - _softStartUntilTick) < 0;
            int maxWorkers = softStart ? SoftStartWorkers : MaxWorkers;

            while (_pending.Count > 0 && _workers < maxWorkers)
            {
                BlockItem item = _pending.Dequeue();
                _pendingKeys.Remove(item.Key);

                // 排队后缓存被别的路径补上了：跳过派活，但同样要通知一次
                //（本轮 total 把它算进去了，静默跳过会让进度永远差一条）。
                if (HasUsableCache(item.Key))
                {
                    RaiseReady(item);
                    continue;
                }

                DispatchQuick(item);
            }

            // ---- 主线程：只有必须打开 DWG 的活才在这里做 ----
            int count = 0;

            // 兜底出图：内嵌预览取不到，只能打开 Database 自绘线框
            while (count < maxItems && _fallback.Count > 0)
            {
                // 时间预算：到点就收工，剩下的等下一次 Idle，界面永远能响应。
                // v1.51.12：去掉了原来的 `count > 0 &&`——那个条件让**第一个任务
                // 完全不受预算约束**，而兜底单次包含 ReadDwgFile + 渲染 + 可能的
                // DxfOut，最坏几百毫秒；一轮 Idle 上来就把主线程占掉几百毫秒，
                // 用户感知就是"点一下卡半天"。现在从第一个起就受 40ms 预算管着。
                if (Environment.TickCount - startTick > MainThreadBudgetMs) break;

                BlockItem item = _fallback.Dequeue();
                _fallbackKeys.Remove(item.Key);

                // 同理：已有真图就通知一次再跳过，保证本轮计数闭合
                if (HasUsableCache(item.Key))
                {
                    RaiseReady(item);
                    continue;
                }

                // 兜底出图整段包住：万一 CAD API 抛出未预期异常，
                // 也必须写失败缓存（记账）+ 通知（闭合计数），否则这个条目
                // 不在任何账本里，查漏补缺会每 2 秒把它重新排队一遍——
                // 表现就是右下角"缩略图 x/y"的进度条永远在闪（v1.49.1 修）。
                try
                {
                    Image image = GenerateFromDatabase(item);

                    // 生成失败时缓存 null，而不是共享的占位图单例——
                    // 占位图是全局唯一实例，一旦进了缓存就可能被 Dispose，
                    // 之后再拿它去绘制就会抛"参数无效"。GetThumbnail 遇到 null 会自动回退到占位图。
                    PutCache(item.Key, image);
                }
                catch (System.Exception)
                {
                    PutCache(item.Key, null);
                }
                RaiseReady(item);

                count++;
            }

            // 元数据补测：只为屏幕上看得见的条目做（⚡ 角标 / ◀▶ 切换要用）
            //
            // 每轮最多做 1 个（v1.51.11）：一次检测 = ReadDwgFile + DxfOut，
            // 实测单个就要几十到几百毫秒，而且时间预算只在"做完一个之后"才检查——
            // 一轮连做 3 个就是持续几百毫秒的卡顿。限成 1 个，把大块时间摊到
            // 多次 Idle 之间，界面始终有喘息的机会。
            int metaThisRound = 0;
            while (count < maxItems && _meta.Count > 0 && metaThisRound < 1)
            {
                if (Environment.TickCount - startTick > MainThreadBudgetMs) break;
                metaThisRound++;

                BlockItem item = _meta.Dequeue();
                if (item == null) continue;
                _metaKeys.Remove(item.Key);

                // 同兜底出图：检测全程包住，任何异常都不能让本轮的通知丢失，
                // 否则进度计数差一条、查漏还会把它一遍遍重排（v1.49.1 修）。
                try
                {
                    bool visKnown;
                    DetectMetadata(item, out visKnown);

                    // 记账标准（v1.51.12）：三个条件都满足才算"检测成功"，缺一odem：
                    //   · visKnown   —— 可见性这一路真的跑完了（DXF 导出成功）。
                    //                  v1.51.11 的 bug：DxfOut 失败时 GetVisData 返回
                    //                  null、磁盘上根本没有 .vis，而 HasDynamicMeta 只看
                    //                  .dyn 是否存在 → 判成成功 → 把 _metaFails 销账。
                    //                  后果是该块在本会话内再也不排队、也永不自愈，
                    //                  用户看到的就是"有闪电、永远没有 ◀ ▶"。
                    //   · HasDynamicMeta —— 落了 .dyn / .nodyn
                    //   · !需重测      —— 不是仍未解析出来的 0 字节空壳
                    string metaBase = GetCachePath(item.FilePath);
                    bool metaOk = visKnown
                        && HasDynamicMetaCore(metaBase)
                        && !HasRetestMarker(metaBase);

                    if (!metaOk)
                    {
                        int metaFails;
                        _metaFails.TryGetValue(item.Key, out metaFails);
                        _metaFails[item.Key] = metaFails + 1;
                    }
                    else
                    {
                        _metaFails.Remove(item.Key);   // 成功落盘就销账
                    }
                }
                catch (System.Exception)
                {
                    int metaFails;
                    _metaFails.TryGetValue(item.Key, out metaFails);
                    _metaFails[item.Key] = metaFails + 1;
                }

                // 不计入缩略图进度：_meta 是 Prioritize 排进来的，
                // 从来没被 BeginThumbRound 登记进 _thumbTotal。
                // 仍然要通知，否则新测出来的 ⚡ / ◀ ▶ 角标不会立刻画出来。
                RaiseReady(item, false);

                count++;
            }

            return PendingCount > 0;
        }

        // ---------------- 并行出图 ----------------

        /// <summary>
        /// 把一个条目投进线程池。后台线程里只做纯文件 IO + GDI+ 图像处理，
        /// 绝不涉及 AutoCAD 的任何 API（Database 只能在主线程调用）。
        /// </summary>
        private void DispatchQuick(BlockItem item)
        {
            System.Threading.Interlocked.Increment(ref _workers);

            bool queued = true;
            try
            {
                System.Threading.ThreadPool.QueueUserWorkItem(delegate (object state)
                {
                    BlockItem it = (BlockItem)state;
                    Image image = null;

                    try
                    {
                        // 服务已 Dispose（面板销毁 / 插件卸载）时不再出图：
                        // 结果塞进 _done 也不会再有人来 DrainDone，那张 Bitmap 就泄漏了。
                        if (!_suspended && !_disposed) image = LoadOrExtract(it);
                    }
                    catch
                    {
                        image = null;
                    }
                    finally
                    {
                        if ((_disposed || _suspended) && image != null)
                        {
                            try { image.Dispose(); } catch { }
                            image = null;
                        }

                        // v1.51.12：已经 Dispose / Suspend 的不再往 _done 里塞。
                        // 以前无条件 Add，服务销毁后 _done 会一直挂着 BlockItem 和
                        // Bitmap 引用直到被 GC，白白占内存。
                        if (!_disposed && !_suspended)
                        {
                            lock (_doneLock)
                            {
                                _done.Add(new KeyValuePair<BlockItem, Image>(it, image));
                            }
                        }
                        else if (image != null)
                        {
                            try { image.Dispose(); } catch { }
                        }

                        System.Threading.Interlocked.Decrement(ref _workers);
                    }
                }, item);
            }
            catch
            {
                // QueueUserWorkItem 失败（线程池异常 / OOM）——必须把并发槽还回去，
                // 否则 _workers 永远回不到 0，达到 maxWorkers 后整条并行管道永久停摆，
                // _pending 再也不出队，用户看到的就是"缩略图突然全部停止更新"。
                queued = false;
            }

            if (!queued)
            {
                System.Threading.Interlocked.Decrement(ref _workers);

                // 放回队列头部还是队尾都行：这里直接放回 _pending 等下一次调度。
                // 注意别让它立刻又被无限重试——_failCounts 在 PutCache 时才涨，
                // 放回队列本身不记失败账，所以不会触发 MaxAutoRetry 熔断。
                if (_pendingKeys.Add(item.Key))
                    _pending.Enqueue(item);
            }
        }

        /// <summary>
        /// 后台线程上的出图主流程：先找磁盘缓存，再直读 DWG 内嵌预览。
        /// 返回 null 表示这条路走不通，需要主线程打开 Database 兜底。
        /// </summary>
        private Image LoadOrExtract(BlockItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.FilePath)) return null;
            if (!File.Exists(item.FilePath)) return null;

            // 1) 上次已经生成过 → 磁盘缓存（纯 PNG 解码）
            Image cached = TryLoadCached(item);
            if (cached != null) return cached;

            // 2) 直读 DWG 里内嵌的预览图：不打开 Database，只读预览段那几十 KB
            using (Bitmap raw = DwgPreviewReader.TryRead(item.FilePath))
            {
                if (raw == null) return null;
                return BuildThumbnail(raw, item.FilePath);
            }
        }

        /// <summary>
        /// 把内嵌预览图加工成最终缩略图：降采样 → 裁右侧黑边 → 换底色 → 适配画布 → 落盘。
        /// 全是 GDI+ 位图运算，与 CAD 无关，所以可以在后台线程跑。
        /// </summary>
        private Image BuildThumbnail(Bitmap raw, string dwgPath)
        {
            int size = _config.ThumbSize;
            if (size < 48) size = 48;

            // 【降采样前置】内嵌预览动辄 512×512 甚至更大，而裁黑边和换底色都是
            // 逐像素处理，按原尺寸跑是整条链路上最贵的一段。先缩到 2 倍缩略图尺寸。
            using (Bitmap small = ResampleWithin(raw, size * 2))
            {
                if (small == null) return null;

                using (Bitmap trimmed = TrimRightBlackBand(small))
                using (Bitmap modeApplied = ApplyBackgroundMode(trimmed))
                {
                    Image fitted = FitToCanvas(modeApplied, size, size,
                        WhiteBackground ? Color.White : Color.Black);

                    SavePng(fitted, GetCachePath(dwgPath));
                    return fitted;
                }
            }
        }

        /// <summary>
        /// 把后台做好的缩略图收进内存缓存并通知界面。
        /// 在主线程执行，所以不必加锁访问 _memoryCache。
        /// 取不到图的条目转进 _fallback，等主线程打开 Database 兜底。
        /// </summary>
        private void DrainDone()
        {
            KeyValuePair<BlockItem, Image>[] batch;

            lock (_doneLock)
            {
                if (_done.Count == 0) return;
                batch = _done.ToArray();
                _done.Clear();
            }

            foreach (KeyValuePair<BlockItem, Image> pair in batch)
            {
                BlockItem item = pair.Key;
                if (item == null) continue;

                if (pair.Value != null)
                {
                    // 只跳过"已有一张真图"的；缓存为 null（上次失败）的重试成果必须收下。
                    // 即便跳过也要 RaiseReady：调用方按 RaiseReady 计数闭合本轮进度，
                    // 静默 continue 会让进度条永远停在差一点的位置（例：568/599）。
                    if (HasUsableCache(item.Key))
                    {
                        // 已经有一张更好的图了，这张新产出的必须释放：
                        // 每张 24bpp 缩略图是几十到几百 KB 的**非托管** GDI+ 内存，
                        // 靠 GC 终结器回收不及时，滚几屏就攒出几十 MB。
                        try { pair.Value.Dispose(); } catch { }
                        RaiseReady(item);
                        continue;
                    }
                    PutCache(item.Key, pair.Value);
                    RaiseReady(item);
                    continue;
                }

                // 内嵌预览这条路没走通 → 交给主线程兜底。
                if (HasUsableCache(item.Key))
                {
                    RaiseReady(item);
                    continue;
                }
                if (_fallbackKeys.Contains(item.Key))
                {
                    // 已经排在兜底队列里等主线程处理，处理完自会通知。
                    // 但本轮 total 把它算进去了，这里也得先通知一次，否则
                    // RaiseReady 计数差一条、进度条收不了尾（v1.48.1 同类问题）。
                    RaiseReady(item);
                    continue;
                }

                int fails;
                if (_failCounts.TryGetValue(item.Key, out fails) && fails >= MaxAutoRetry)
                {
                    // 重试次数用尽：不再排兜底队列，但必须通知一次——
                    // 否则本轮的"已生成计数"永远差这一条，进度条收不了尾。
                    RaiseReady(item);
                    continue;
                }

                _fallbackKeys.Add(item.Key);
                _fallback.Enqueue(item);
            }
        }

        /// <summary>
        /// 补测动态块 / 可见性状态元数据。需要打开 Database，只在主线程调用。
        /// 结果会落盘成标记文件，下次启动直接读标记，不用再开一次图纸。
        /// </summary>
        private void DetectMetadata(BlockItem item, out bool visKnown)
        {
            visKnown = false;

            if (item == null || string.IsNullOrEmpty(item.FilePath)) return;
            if (!File.Exists(item.FilePath)) return;
            if (_disposed) return;

            Autodesk.AutoCAD.DatabaseServices.Database db = null;

            try
            {
                db = new Autodesk.AutoCAD.DatabaseServices.Database(false, true);
                db.ReadDwgFile(item.FilePath,
                    Autodesk.AutoCAD.DatabaseServices.FileOpenMode.OpenForReadAndReadShare,
                    false, string.Empty);

                bool dyn = DetectDynamicBlock(db);
                _dynamicFlags[item.Key] = dyn;

                if (dyn)
                {
                    WriteDynamicMarker(item.FilePath);
                    RemovePlainMarker(item.FilePath);

                    // 可见性状态要靠导出 DXF 文本再解析，很贵，只对真动态块做。
                    // 返回值三态（v1.51.11）：
                    //   null       = DXF 导出/解析本身失败 → **不落标记**，下次会话可重试
                    //               （内存账本 _metaFails 负责限次，不会无限重开 Database）
                    //   空列表     = 导出成功、确实没有可见性状态 → 落非空哨兵，永不重测
                    //   ≥2 个状态  = 正常可见性块 → 落状态名
                    List<string> visStates = DetectVisibilityStateNames(item.Key, db);
                    _visStates[item.Key] = visStates ?? new List<string>();

                    if (visStates == null)
                    {
                        // 检测没跑完，磁盘上不留结论
                    }
                    else if (visStates.Count >= 2)
                    {
                        WriteVisibilityMarker(item.FilePath, visStates);
                        visKnown = true;
                    }
                    else
                    {
                        WriteNoVisibilityMarker(item.FilePath);   // 落盘"已检测、无可见性"
                        visKnown = true;                          // "确实没有"也是一种确定结论
                    }
                }
                else
                {
                    // 落盘"已检测、非动态块"，下次直接读标记，省掉一次 ReadDwgFile
                    WritePlainMarker(item.FilePath);
                    _visStates[item.Key] = new List<string>();
                    visKnown = true;      // 非动态块不需要可见性，结论本身就是确定的
                }
            }
            catch
            {
                // 元数据检测失败只影响角标，不影响缩略图。
                // visKnown 保持 false：这次没跑完，必须记账而不是销账。
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

        /// <summary>该图块是否已有磁盘缓存 PNG。有则直接跳过生成。</summary>
        private bool HasDiskCache(BlockItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.FilePath)) return false;
            try { return File.Exists(GetCachePath(item.FilePath)); }
            catch { return false; }
        }

        /// <summary>
        /// 还有多少活没干完（含后台在跑的、做完待收的、以及元数据补测）。
        /// 用于判断"空闲循环要不要继续调 ProcessQueue"。
        /// </summary>
        public int PendingCount
        {
            get
            {
                int done;
                lock (_doneLock) { done = _done.Count; }

                return _pending.Count + _workers + done + _fallback.Count + _meta.Count;
            }
        }

        /// <summary>
        /// 是否还有**缩略图本身**没出完（不含元数据补测）。
        /// 进度条以此为准：元数据只是角标，不该让进度条一直挂着。
        /// </summary>
        public bool HasPendingThumbnails
        {
            get
            {
                int done;
                lock (_doneLock) { done = _done.Count; }

                return _pending.Count > 0 || _workers > 0 || done > 0 || _fallback.Count > 0;
            }
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

        /// <summary>
        /// 把 key 标记为最近使用（移到 LRU 链尾）。
        /// O(1)：直接从字典拿到节点，摘下来挂到尾部即可。
        /// </summary>
        private void TouchCache(string key)
        {
            if (string.IsNullOrEmpty(key)) return;

            LinkedListNode<string> node;
            if (_cacheNodes.TryGetValue(key, out node))
            {
                if (ReferenceEquals(node, _cacheOrder.Last)) return;   // 已在队尾，最常见

                _cacheOrder.Remove(node);
                _cacheOrder.AddLast(node);
                return;
            }

            node = _cacheOrder.AddLast(key);
            _cacheNodes[key] = node;
        }

        /// <summary>把 key 从 LRU 里彻底移除（条目被 Forget / 缓存清空时调用）。</summary>
        private void DropCacheOrder(string key)
        {
            if (string.IsNullOrEmpty(key)) return;

            LinkedListNode<string> node;
            if (!_cacheNodes.TryGetValue(key, out node)) return;

            _cacheOrder.Remove(node);
            _cacheNodes.Remove(key);
        }

        /// <summary>
        /// 写入内存缓存 + 更新 LRU，必要时淘汰最久未用的条目。
        /// 淘汰只解除引用、不 Dispose（原因见 _cacheOrder 注释）。
        ///
        /// image = null 表示这一轮生成失败：除了照常写入（GetThumbnail 遇 null
        /// 会回退占位图）之外，还必须在 _failCounts 记一笔账 —— 否则失败条目
        /// 会被反复排队，对真坏文件无限开 Database。
        /// </summary>
        private void PutCache(string key, Image image)
        {
            _memoryCache[key] = image;
            TouchCache(key);

            if (image == null)
            {
                int fails;
                _failCounts.TryGetValue(key, out fails);
                _failCounts[key] = fails + 1;
            }
            else
            {
                _failCounts.Remove(key);   // 成功出图就销账
            }

            while (_cacheOrder.Count > MaxMemoryCache)
            {
                LinkedListNode<string> oldest = _cacheOrder.First;
                _cacheOrder.RemoveFirst();
                _cacheNodes.Remove(oldest.Value);
                _memoryCache.Remove(oldest.Value);
            }
        }

        /// <summary>读磁盘上的缓存 PNG（已经在 ProcessQueue 里，属于可控的空闲时间）。</summary>
        private Image TryLoadCached(BlockItem item)
        {
            if (item == null) return null;
            string path = GetCachePath(item.FilePath);
            if (!File.Exists(path)) return null;
            return TryLoadPng(path, _config.ThumbSize);
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

        /// <summary>
        /// 写入"已检测、确认不是动态块"的标记。
        /// 没有这个标记就无法区分"还没检测过"和"检测过但不是动态块"，
        /// 于是每次启动都要为同一批文件重开一次 Database。落盘之后第二次起全是零成本。
        /// </summary>
        private void WritePlainMarker(string dwgPath)
        {
            try
            {
                string marker = GetCachePath(dwgPath) + ".nodyn";
                if (!File.Exists(marker))
                    File.WriteAllBytes(marker, new byte[0]);
            }
            catch
            {
                // 同上，只影响角标
            }
        }

        /// <summary>文件被判定为动态块时，要把可能存在的"非动态块"标记删掉。</summary>
        private void RemovePlainMarker(string dwgPath)
        {
            try
            {
                string marker = GetCachePath(dwgPath) + ".nodyn";
                if (File.Exists(marker)) File.Delete(marker);
            }
            catch { }
        }

        /// <summary>
        /// 该文件的动态块属性是否已经检测过（.dyn / .nodyn 有一个就算）。
        /// 没检测过才需要排进 _meta 队列去开一次图纸。
        /// </summary>
        private bool HasDynamicMeta(string dwgPath)
        {
            try
            {
                return HasDynamicMetaCore(GetCachePath(dwgPath));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 重载：直接吃已经算好的缓存路径。
        /// 调用方往往在同一轮里既要判 hasMeta 又要判 retest，复用已经算出的
        /// basePath 可避免重复调用 GetCachePath（含 MD5 与路径查表）。
        /// 名字加 Core 是为了与单参版本区分——同名重载会让 GetCachePath(dwgPath)
        /// 的字符串结果匹配到单参版本，形成无限递归。
        /// </summary>
        private static bool HasDynamicMetaCore(string basePath)
        {
            try
            {
                return File.Exists(basePath + ".dyn") || File.Exists(basePath + ".nodyn");
            }
            catch
            {
                return false;
            }
        }

        private bool NeedsVisibilityRetest(string dwgPath)
        {
            try
            {
                return HasRetestMarker(GetCachePath(dwgPath));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 该块的 .vis 是不是"0 字节空壳"（重载：复用已算好的缓存路径）。
        /// 空壳 = 旧版 DXF 解析失败时写的假"已检测"（v1.51.9），必须允许重测，
        /// 否则这些块永远没有 ◀ ▶ 切换按钮。
        /// 注意 v1.51.11 起正常分支写的是非空哨兵 "-"，所以这里判定的是
        /// **历史遗留**空壳，新写的结论永远不会再被判成需要重测。
        /// </summary>
        private static bool HasRetestMarker(string basePath)
        {
            try
            {
                if (!File.Exists(basePath + ".dyn")) return false;   // 非动态块不用测
                string vis = basePath + ".vis";
                if (!File.Exists(vis)) return false;                 // 没写过可见性标记 → 不是空壳
                return new FileInfo(vis).Length == 0;                // 0 字节 = 假"已检测"
            }
            catch
            {
                return false;
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
                        // "-" 是"已检测、无可见性状态"的哨兵（v1.51.11），不是状态名
                        if (name.Length > 0 && name != "-" && !states.Contains(name)) states.Add(name);
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

            // 注意：旧图**不能**在这里 Dispose。它可能正被列表的自绘代码 / 屏幕重绘
            // 路径引用着，释放后界面重绘就会抛"参数无效"。只替换引用，旧的交给 GC 回收。
            PutCache(item.Key, img);

            SavePng(img, GetCachePath(item.FilePath));
            RaiseReady(item);
        }

        /// <summary>
        /// 取（或解析并缓存）该图块 DWG 的可见性参数数据。
        /// 可见性数据没有托管 API，通过把侧数据库导出成 DXF 再解析
        /// BLOCKVISIBILITYPARAMETER 对象（303 状态名 / 94 数量 / 331 可见实体句柄）获得。
        /// </summary>
        /// <summary>本会话已记录的"解析为空"条数（日志限流，见 GetVisData）。</summary>
        private static int _emptyVisLogged;

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

                // "导出成功但没有可见性状态"是正常的（大部分动态块没有可见性参数），
                // 一条一条写日志会把 error.log 刷爆（实测一天 1100+ 行）。本会话限 20 条。
                if (data.Count == 0 && _emptyVisLogged < 20)
                {
                    _emptyVisLogged++;
                    BlmLog.Write("可见性状态解析为空（该动态块没有可见性参数，属正常）：" + itemKey
                        + (_emptyVisLogged >= 20 ? "  （本会话后续同类消息不再记录）" : string.Empty));
                }
            }
            catch (System.Exception ex)
            {
                BlmLog.Write("DXF 导出失败（可见性检测）：item=" + itemKey + "，" +
                    (ex.Message ?? string.Empty).Replace("\r", " ").Replace("\n", " "));

                // 返回 null = "这次没跑成"，与"跑成了但没找到状态"（空列表）区分开。
                // 不缓存 null：下次还能再试一次（v1.51.11）
                return null;
            }
            finally
            {
                try { if (File.Exists(dxfPath)) File.Delete(dxfPath); } catch { }
            }

            _visData[itemKey] = data;
            return data;
        }

        /// <summary>
        /// 解析 DXF 对象段里的可见性状态（v1.51.9 第三版，按真实 DXF 样本核对过）。
        ///
        /// 真实结构（AutoCAD 2020 accoreconsole 导出的 DXF 实测）：**没有**
        /// BLOCKVISIBILITYSTATE 对象，全部数据都在 BLOCKVISIBILITYPARAMETER
        /// 对象体的 AcDbBlockVisibilityParameter 段内：
        /// <code>
        ///   0   BLOCKVISIBILITYPARAMETER
        ///   100 AcDbEvalExpr / AcDbBlockElement（300 = 参数名，不要动）
        ///   100 AcDbBlockParameter / AcDbBlock1PtParameter（这里有 93！必须忽略）
        ///   100 AcDbBlockVisibilityParameter     ← 从这里才开始解析
        ///   301 &lt;参数名&gt;   302 &lt;空&gt;   91 &lt;0&gt;
        ///    93 &lt;N&gt;  → 331 × N                    ← 受控实体全集
        ///    92 &lt;状态数&gt;
        ///    循环 × 状态数：
        ///     303 &lt;状态名&gt;
        ///      94 &lt;N&gt; → 332 × N                  ← 该状态**可见**实体
        ///      95 &lt;M&gt; → 333 × M                  ← 该状态**隐藏**实体
        /// </code>
        /// 332 是可见实体：实测"单开 4 → 双开 5 → 三开 6"递增累加，语义吻合。
        /// AllControlled = 331 全集 ∪ 各状态 333（333 里的实体在所有状态都隐藏，
        /// 不并入 Controlled 的话渲染时会被当成"不受控"永远显示出来）。
        /// </summary>
        private static List<VisParamData> ParseDxfVisibility(string dxfPath)
        {
            List<VisParamData> result = new List<VisParamData>();

            try
            {
                // 流式解析：DXF 动辄几十 MB，一次性 ReadAllLines 会把整个文件读进内存。
                // （detectEncodingFromByteOrderMarks=true，自动识别 UTF-8 BOM）
                using (StreamReader reader = new StreamReader(
                    dxfPath, System.Text.Encoding.UTF8, true))
                {
                    VisParamData param = null;      // 当前 BLOCKVISIBILITYPARAMETER
                    bool inBody = false;            // 已进入 AcDbBlockVisibilityParameter 段
                    string stateName = null;        // 当前状态名
                    HashSet<long> visible = null;   // 当前状态可见实体（挂在 param.StateHandles）
                    int pending = 0;                // 等待收集的句柄组：331/332/333
                    int remain = 0;

                    // DXF 文本严格"组码行 + 值行"成对出现，所以两行两行地读
                    while (true)
                    {
                        string codeLine = reader.ReadLine();
                        if (codeLine == null) break;

                        string valueLine = reader.ReadLine();
                        if (valueLine == null) break;

                        string code = codeLine.Trim();

                        if (code == "102")
                        {
                            // reactor 块（102 {ACAD_REACTORS ... 102 }）里的 330 是
                            // owner 句柄，混进实体句柄会污染过滤集合，整块跳过
                            while (true)
                            {
                                string skipCode = reader.ReadLine();
                                if (skipCode == null) break;
                                string skipValue = reader.ReadLine();
                                if (skipValue == null) break;
                                if (skipCode.Trim() == "102" && skipValue.Trim().StartsWith("}"))
                                    break;
                            }
                            continue;
                        }

                        if (code == "0")
                        {
                            // 对象切换：先收尾当前参数（333 并入受控集、判定是否可用）
                            if (param != null)
                            {
                                FinishVisParam(result, param);
                                param = null; inBody = false;
                                stateName = null; visible = null; pending = 0;
                            }

                            if (string.Equals(valueLine.Trim(), "BLOCKVISIBILITYPARAMETER",
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                param = new VisParamData();
                            }
                            continue;
                        }

                        if (param == null) continue;

                        if (code == "100")
                        {
                            // 段名切换：只有真正进入可见性段才开始收数据
                            // （AcDbBlock1PtParameter 段里也有 93，不能让它触发收集）
                            inBody = string.Equals(valueLine.Trim(),
                                "AcDbBlockVisibilityParameter", StringComparison.OrdinalIgnoreCase);
                            pending = 0;
                            continue;
                        }

                        if (!inBody) continue;

                        if (code == "93")                       // 受控实体全集数量 → 331 × N
                        {
                            pending = 331;
                            remain = ParseInt(valueLine);
                        }
                        else if (code == "331" && pending == 331 && remain > 0)
                        {
                            long h;
                            if (TryParseHandle(valueLine, out h)) param.AllControlled.Add(h);
                            remain--;
                        }
                        else if (code == "92")                  // 状态总数（提示性，忽略）
                        {
                            pending = 0;
                        }
                        else if (code == "303")                 // 状态名 → 开新状态
                        {
                            // 解码失败就当原始文本，别把一个状态名的转义异常放大成
                            // "整份 DXF 解析失败"（见 PaintDxfText 与外层 catch 的说明）
                            string n = valueLine.Trim();
                            try
                            {
                                n = DecodeDxfText(valueLine).Trim();
                            }
                            catch { }

                            if (n.Length == 0) n = "状态" + (param.StateHandles.Count + 1);
                            // 重名兜底（理论上一参数内不重名）
                            while (param.StateHandles.ContainsKey(n))
                                n = n + "_";
                            stateName = n;
                            visible = new HashSet<long>();
                            param.StateHandles[n] = visible;
                            pending = 0;
                        }
                        else if (code == "94")                  // 可见实体数量 → 332 × N
                        {
                            pending = 332;
                            remain = ParseInt(valueLine);
                        }
                        else if (code == "332" && pending == 332 && remain > 0 && visible != null)
                        {
                            long h;
                            if (TryParseHandle(valueLine, out h)) visible.Add(h);
                            remain--;
                        }
                        else if (code == "95")                  // 隐藏实体数量 → 333 × N
                        {
                            pending = 333;
                            remain = ParseInt(valueLine);
                        }
                        else if (code == "333" && pending == 333 && remain > 0)
                        {
                            // 隐藏实体直接并入受控全集：IsVisible 逻辑是
                            // "受控且不在当前状态可见集 → 隐藏"，333 不并入就永远显示
                            long h;
                            if (TryParseHandle(valueLine, out h)) param.AllControlled.Add(h);
                            remain--;
                        }
                    }

                    // 文件收尾
                    if (param != null)
                        FinishVisParam(result, param);
                }
            }
            catch
            {
                // v1.51.12：以前这里是 `result.Clear()` —— 把**整份文件**已经解析出来的
                // 可见性参数全部作废。配合下面 DecodeDxfText 的越界异常，一个状态名
                // 转义不合法就让这个块彻底失去 ◀ ▶，而且因为落了哨兵还会永不重试。
                // 部分解析结果不完美，也比全丢要好得多；这里保留已解析到的部分。
            }

            return result;
        }

        /// <summary>
        /// 收尾一个可见性参数：至少两个"解析到了可见实体"的状态才收进结果，
        /// 过滤解析残缺的坏块（否则切换按钮会出现但点了没反应）。
        /// </summary>
        private static void FinishVisParam(List<VisParamData> result, VisParamData param)
        {
            if (param == null || param.AllControlled.Count == 0) return;

            int usable = 0;
            foreach (HashSet<long> set in param.StateHandles.Values)
                if (set.Count > 0) usable++;

            if (usable >= 2) result.Add(param);
        }

        /// <summary>解析组码数值行。</summary>
        private static int ParseInt(string text)
        {
            int v;
            int.TryParse((text ?? string.Empty).Trim(), out v);
            return v;
        }

        /// <summary>解析 DXF 句柄（十六进制文本）。</summary>
        private static bool TryParseHandle(string text, out long handle)
        {
            return long.TryParse((text ?? string.Empty).Trim(),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out handle);
        }

        /// <summary>解码 DXF 文本中的 \U+XXXX Unicode 转义（中文状态名常见）。</summary>
        private static string DecodeDxfText(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf("\\U+", StringComparison.OrdinalIgnoreCase) < 0)
                return text;

            System.Text.StringBuilder sb = new System.Text.StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                // v1.51.12 修：老条件的有效部分是 `i + 6 <= text.Length`，
                // 但下面要的是 Substring(i + 3, 4)，实际需要 `i + 7 <= length`。
                // 文件末尾正好截在转义中途时（例如状态名 @"\U+123" 丢了最后一位），
                // Substring 会抛 ArgumentOutOfRangeException，一路冒泡到解析器的
                // 最外层 catch —— 以前那里是 result.Clear()，整份 DXF 的可见性
                // 结果全丢。改成正确的边界，缺尾就当普通文本处理。
                if (i + 7 <= text.Length &&
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

        /// <summary>
        /// 写入一个空的可见性标记，表示"这个文件已经检测过，没有可见性状态"。
        /// 与 .nodyn 同理：空文件本身不影响 GetVisibilityStates（读出来就是空列表），
        /// 但能避免同一个动态块每次启动都被重复导出一次 DXF 去解析。
        /// </summary>
        /// <summary>
        /// 落盘"已检测、该动态块没有可见性状态"（v1.51.11）。
        ///
        /// 这里**必须写非空哨兵**，不能像旧版那样写 0 字节文件：
        /// NeedsVisibilityRetest 把"0 字节 .vis"当成旧版解析失败留下的空壳来重测，
        /// 而"动态块但没有可见性参数"是正常结论、永远解析不出状态 —— 写空文件
        /// 等于给这 1000 多个块判了无期徒刑：每个会话都要重开 Database + 导出 DXF，
        /// 主线程被一轮轮 DxfOut 拖住，表现就是打开图库时明显卡顿 / 假死。
        /// 哨兵内容 "-" 不是状态名，GetVisibilityStates 读标记时会跳过。
        /// </summary>
        private void WriteNoVisibilityMarker(string dwgPath)
        {
            try
            {
                string marker = GetCachePath(dwgPath) + ".vis";
                // 已经是"有状态"的标记就别覆盖
                if (File.Exists(marker) && new FileInfo(marker).Length > 1) return;

                File.WriteAllText(marker, "-", System.Text.Encoding.UTF8);
            }
            catch
            {
                // 同上，只影响切换按钮
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

                    HashSet<AcadObjectId> referenced = new HashSet<AcadObjectId>();

                    // v1.51.12：第一遍原本是"遍历块表、逐个 GetObject 看 IsLayout"——
                    // 一张含 3000 个块定义的图纸就是 3000 次无谓打开，只为找那几个布局。
                    // 直接走 LayoutDictionary，只打开布局对象的 BlockTableRecordId。
                    AcadDictionary layoutDict = tr.GetObject(db.LayoutDictionaryId, AcadOpenMode.ForRead)
                        as AcadDictionary;

                    if (layoutDict != null)
                    {
                        foreach (System.Collections.DictionaryEntry entry in layoutDict)
                        {
                            AcadLayout layout =
                                tr.GetObject((AcadObjectId)entry.Value, AcadOpenMode.ForRead) as AcadLayout;
                            if (layout == null) continue;

                            AcadBtr btr = tr.GetObject(layout.BlockTableRecordId, AcadOpenMode.ForRead)
                                as AcadBtr;
                            if (btr == null) continue;

                            if (CollectReferences(tr, btr, referenced)) { tr.Commit(); return true; }
                        }
                    }

                    // 第二遍原本又全表扫一遍 bt（再 3000 次 GetObject），
                    // 实际只需要看**被引用到的**那几十个块定义。
                    foreach (AcadObjectId refId in referenced)
                    {
                        AcadBtr btr = tr.GetObject(refId, AcadOpenMode.ForRead) as AcadBtr;
                        if (btr != null && btr.IsDynamicBlock)
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

        /// <summary>
        /// 遍历一个布局里的块参照：把被引用的块定义记进 referenced。
        /// 返回 true 表示已经找到动态块实例，调用方可直接收工。
        /// </summary>
        private static bool CollectReferences(AcadTr tr, AcadBtr btr, HashSet<AcadObjectId> referenced)
        {
            try
            {
                foreach (AcadObjectId entId in btr)
                {
                    AcadBlockRef br = null;
                    try { br = tr.GetObject(entId, AcadOpenMode.ForRead) as AcadBlockRef; }
                    catch { br = null; }

                    if (br == null) continue;

                    // 动态块实例：参照指向的动态定义与其原始定义不一致
                    // （2020 程序集的 BlockReference 没有 IsDynamic 属性，用这个等价判断）
                    bool isDynamicRef;
                    try { isDynamicRef = br.BlockTableRecord != br.DynamicBlockTableRecord; }
                    catch { isDynamicRef = false; }

                    if (isDynamicRef) return true;

                    referenced.Add(br.BlockTableRecord);
                }
            }
            catch
            {
                // 某个实体打不开不影响其余部分
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
                if (data == null) return null;          // 导出失败：让调用方知道"没跑成"

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
                        int i = y * stride + x * 4;      // BGRA：i=B, i+1=G, i+2=R
                        // 亮度 = 299*R + 587*G + 114*B（写成 B、G、R 的顺序会把红蓝算反）
                        int lum = (buffer[i + 2] * 299 + buffer[i + 1] * 587 + buffer[i] * 114) / 1000;
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
                    // 同为 BGRA：R 在 i+2、B 在 i
                    int lum = (buffer[i + 2] * 299 + buffer[i + 1] * 587 + buffer[i] * 114) / 1000;
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
        /// 注意：这里只解除引用、不主动 Dispose —— 部分图像此刻还在列表的自绘
        /// 绘制路径里被引用，立即释放会在 UI 重绘时抛
        /// "System.ArgumentException: 参数无效"（Image.get_Width 访问已释放对象）。
        /// 释放交给 GC 终结器稍后处理；动态块 / 可见性检测结果标记保留。
        /// </summary>
        public void ClearAllCaches()
        {
            _memoryCache.Clear();
            _cacheOrder.Clear();
            _cacheNodes.Clear();        // 与 _cacheOrder 必须同步清，否则节点索引会指向已摘下的节点
            _cachePathCache.Clear();    // ThumbSize 可能已变，缓存路径要重算
            _pendingKeys.Clear();
            _pending.Clear();
            _fallback.Clear();
            _fallbackKeys.Clear();
            _meta.Clear();
            _metaKeys.Clear();
            _visIndex.Clear();
            _visData.Clear();           // 随磁盘 PNG 一起作废的可见性解析结果也必须丢
            _visStates.Clear();
            _dynamicFlags.Clear();

            // 失败账本也必须一起清：切底色 / 改规格后所有图都要重出，
            // 而 _failCounts 记到上限的条目会被 RequestThumbnails 无条件跳过，
            // 不清就永远停在占位图上，只能等下一次 Suspend/Resume 才恢复。
            _failCounts.Clear();
            _metaFails.Clear();

            lock (_doneLock) { _done.Clear(); }

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

            // 缩略图规格（ThumbSize）可能已经变了，占位图必须按新规格重画，
            // 否则会一直留着按旧尺寸画的那张（小规格下 "DWG" 会换行、被切掉）。
            DropPlaceholders();
        }

        /// <summary>丢掉两张占位图，下次取用时按当前 ThumbSize 重画。
        /// 缩略图规格（ThumbSize）改过之后必须调用，否则会一直用按旧尺寸画的那张。</summary>
        public void DropPlaceholders()
        {
            if (_placeholderWhole != null) { try { _placeholderWhole.Dispose(); } catch { } }
            if (_placeholderInner != null) { try { _placeholderInner.Dispose(); } catch { } }
            _placeholderWhole = null;
            _placeholderInner = null;
        }

        /// <summary>
        /// 文件被重命名 / 移动后调用：丢弃旧路径的全部缓存痕迹——
        /// 内存缩略图、磁盘 PNG、动态块 / 可见性标记、失败账本。
        /// 新路径首次出图时按新文件重新生成，不会残留旧图。
        /// </summary>
        public void ForgetItem(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;

            try
            {
                // 与 BlockItem.FromFile 的 key 规则保持一致（"file:" + 小写全路径）
                string key = "file:" + filePath.ToLowerInvariant();

                _memoryCache.Remove(key);
                DropCacheOrder(key);
                _dynamicFlags.Remove(key);
                _visStates.Remove(key);
                _visData.Remove(key);
                _visIndex.Remove(key);
                _failCounts.Remove(key);
                _metaFails.Remove(key);

                // 磁盘侧：缩略图 PNG 与三个检测标记都挂在"路径的 MD5"上
                string basePath = GetCachePath(filePath);
                string[] files = new string[]
                {
                    basePath,
                    basePath + ".dyn",
                    basePath + ".nodyn",
                    basePath + ".vis"
                };

                foreach (string path in files)
                {
                    try { if (File.Exists(path)) File.Delete(path); }
                    catch { }
                }
            }
            catch
            {
                // 清理失败不影响重命名本身，最多是多生成一次缩略图
            }
        }

        /// <summary>
        /// 目录树右键"刷新"：只清指定文件夹（含子目录）下所有 DWG 的缩略图痕迹——
        /// 内存缓存、磁盘 PNG、动态块 / 可见性标记、失败账本，其它文件夹一概不动。
        /// 返回清掉的 DWG 文件数（0 = 该文件夹下没有 DWG）。
        /// </summary>
        public int InvalidateFolder(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath)) return 0;

            List<string> dwgs = new List<string>();

            try
            {
                // 顶层 + 子目录（图库默认按目录树展示，块可能藏在子目录里）
                dwgs.AddRange(Directory.GetFiles(folderPath, "*.dwg", SearchOption.TopDirectoryOnly));
                foreach (string sub in Directory.GetDirectories(folderPath))
                {
                    try
                    {
                        dwgs.AddRange(Directory.GetFiles(sub, "*.dwg", SearchOption.AllDirectories));
                    }
                    catch
                    {
                        // 无权限的子目录跳过
                    }
                }
            }
            catch
            {
                return 0;
            }

            foreach (string dwg in dwgs) ForgetItem(dwg);
            return dwgs.Count;
        }

        private void RaiseReady(BlockItem item)
        {
            RaiseReady(item, true);
        }

        /// <summary>
        /// 通知界面"这个条目有变化了"（v1.51.12 增加 counted 参数）。
        ///
        /// counted = true  表示这条通知要计入本轮缩略图进度（调用方靠
        ///                 ThumbnailReady 的次数闭合 BeginThumbRound 登记的 _thumbTotal）。
        /// counted = false 用于**元数据补测**这类从不计入 _thumbTotal 的完成通知：
        ///                 它仍然要让界面重绘（角标出现/消失），但不能动进度计数
        ///                 —— 否则 _thumbDone 会被不属于本轮的条目推着走，
        ///                 状态栏会出现"已全部生成"但数字对不上的现象。
        /// </summary>
        private void RaiseReady(BlockItem item, bool counted)
        {
            Action<BlockItem, bool> typed = ThumbnailReadyCounted;
            if (typed != null)
            {
                try { typed(item, counted); }
                catch { /* 回调异常不能中断队列处理 */ }
                return;
            }

            Action<BlockItem> handler = ThumbnailReady;
            if (handler != null)
            {
                try { handler(item); }
                catch { /* 回调异常不能中断队列处理 */ }
            }
        }

        /// <summary>带"是否计入进度"标记的完成事件（订阅它就能拿到精确的计数语义）。</summary>
        public event Action<BlockItem, bool> ThumbnailReadyCounted;

        // ---------- 生成 ----------

        /// <summary>
        /// 兜底出图：内嵌预览取不到时（THUMBSAVE=0 存的图、R13 以前、第三方工具生成），
        /// 只好打开 Database。既然图纸已经打开了，元数据（动态块 / 可见性）顺手一起测掉，
        /// 避免同一个文件被打开两次。
        ///
        /// 注意：这条路必须在 AutoCAD 主线程调用，且很慢，所以只给"直读失败"的条目走。
        /// </summary>
        private Image GenerateFromDatabase(BlockItem item)
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

                if (dyn)
                {
                    WriteDynamicMarker(item.FilePath);
                    RemovePlainMarker(item.FilePath);

                    // 可见性状态检测需要把整个 DWG 导出成 DXF 文本再解析（DxfOut，开销很大），
                    // 只对真正含动态块的文件做——普通图块（绝大多数）直接跳过。
                    List<string> visStates = DetectVisibilityStateNames(item.Key, db);
                    _visStates[item.Key] = visStates ?? new List<string>();

                    // 同 DetectMetadata 的三态约定（v1.51.11）：
                    // null = 没跑成（不落标记，下次还能重试）；空 = 确实没有可见性状态（落哨兵）
                    if (visStates == null)
                    {
                        // 检测没跑完，磁盘上不留结论
                    }
                    else if (visStates.Count >= 2)
                    {
                        WriteVisibilityMarker(item.FilePath, visStates);
                    }
                    else
                    {
                        WriteNoVisibilityMarker(item.FilePath);
                    }
                }
                else
                {
                    _visStates[item.Key] = new List<string>();
                    _visData[item.Key] = new List<VisParamData>();   // 非动态块，无需解析
                    WritePlainMarker(item.FilePath);
                }

                int size = _config.ThumbSize;
                if (size < 48) size = 48;

                // 预览段直读已经失败过一次，但 Database 里可能还有一张
                //（例如文件里的预览段损坏、而 CAD 自己能解出来）
                using (Bitmap preview = db.ThumbnailBitmap)
                {
                    if (preview != null)
                        result = BuildThumbnail(preview, item.FilePath);
                }

                // 再兜一层：连 Database 里都没有预览位图，就把模型空间的几何
                //（含块参照逐层展开）自绘成线框预览。
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
            // 必须在 try 外面声明：try 块里声明的变量在 catch 里是看不见的，
            // 而异常路径要靠它把半成品位图释放掉（GDI 句柄泄漏）。
            Bitmap canvas = null;

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

                canvas = new Bitmap(size, size, PixelFormat.Format24bppRgb);
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
                // 画到一半出错时 canvas 已经建出来了，不释放就是一次 GDI 句柄泄漏
                if (canvas != null)
                {
                    try { canvas.Dispose(); } catch { }
                    canvas = null;
                }
                return null;
            }
        }

        /// <summary>
        /// 单张兜底渲染的几何量上限（v1.51.12 新增熔断）。
        ///
        /// 以前这条路上没有任何上限：一张 10 万实体的图纸会展开出十几万个
        /// Point3d，AppendEntityGeometry 里每条曲线固定采样 33 点，最后
        /// RenderPreview 用 DrawLine 逐段画 —— 几百万条线段在主线程跑好几秒，
        /// **完全不受 ProcessQueue 的 40ms 预算约束**（预算只在"做完一个任务之后"才检查）。
        /// 用户遇到这种图就是彻底卡死。6000 条折线足够把一张图的外形画清楚。
        /// </summary>
        private const int MaxRenderPolylines = 6000;

        /// <summary>把一条折线加进结果并累计范围（超过熔断上限后静默丢弃）。</summary>
        private static void AddPolyline(AcadPts pts, List<AcadPts> polylines, BoundsAcc bounds)
        {
            if (pts == null || pts.Count < 2) return;
            if (polylines.Count >= MaxRenderPolylines) return;

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

                    // 注意：这里**不能**按"块定义"去重。几何是带着各自的
                    // BlockTransform 变换到世界坐标收集的，同一个定义放在 200 个
                    // 不同位置会产生 200 份不同的世界坐标几何 —— 一旦按定义去重，
                    // 画面上就只剩第一个实例。控制开销只能靠 AddPolyline 的熔断。
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

                    int maxBand = width / 6;                     // 黑带超过 1/6 宽就放弃

                    // 整张图本来就偏黑（黑底预览 / 深色图纸）时一律不裁：
                    // 这种图右侧的每一列都满足"几乎全黑"，循环会一路跑到 maxBand，
                    // 结果就是把图切掉一大块——以前 maxBand 取 1/3，实测会切掉三分之一。
                    if (IsMostlyDarkImage(buffer, stride, width, height)) return source;

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

        /// <summary>
        /// 整张图是否"几乎全黑"（≥85% 采样点 RGB 都低于 32）。
        /// 用于裁右缘黑带前的兜底：这种图本来就是黑底，裁了等于把内容切掉。
        /// </summary>
        private static bool IsMostlyDarkImage(byte[] buffer, int stride, int width, int height)
        {
            int stepX = Math.Max(1, width / 60);
            int stepY = Math.Max(1, height / 60);
            int black = 0, total = 0;

            for (int y = 0; y < height; y += stepY)
            {
                for (int x = 0; x < width; x += stepX)
                {
                    int idx = y * stride + x * 4;
                    if (buffer[idx] < 32 && buffer[idx + 1] < 32 && buffer[idx + 2] < 32) black++;
                    total++;
                }
            }

            return total > 0 && black * 100 >= total * 85;
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

        /// <summary>
        /// 文件路径 → 缓存 PNG 路径的映射表（v1.51.12）。
        /// GetCachePath 是全局调用最密集的方法之一：自绘每一项、每次排队、
        /// 每次查标记文件都会走到。以前它每次都现算一次 MD5，现在算一次就记住。
        /// 字典本身随条目数增长（几万张图也不过几 MB），超过上限直接整体丢弃重建。
        /// </summary>
        private readonly Dictionary<string, string> _cachePathCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private const int MaxCachePathEntries = 20000;

        private string GetCachePath(string dwgPath)
        {
            string pathKey;
            if (string.IsNullOrEmpty(dwgPath)) pathKey = "unknown";
            else pathKey = dwgPath.ToLowerInvariant();

            string result;
            if (_cachePathCache.TryGetValue(pathKey, out result)) return result;

            string hash = string.Equals(pathKey, "unknown", StringComparison.Ordinal)
                ? "unknown" : Md5(pathKey);

            result = Path.Combine(CacheFolder,
                CacheVersionPrefix + hash + "_" + _config.ThumbSize + ".png");

            if (_cachePathCache.Count >= MaxCachePathEntries) _cachePathCache.Clear();
            _cachePathCache[pathKey] = result;
            return result;
        }

        /// <summary>缓存目录的一次性初始化（清旧版缓存 + 容量裁剪）。</summary>
        private static readonly object _cacheInitLock = new object();

        /// <summary>
        /// 这两件一次性清理工作现在会被**后台线程**并发触发（出图已经并行化），
        /// 而它们内部是"看一眼标志位就开始删文件"，两个线程同时进来就会一边删一边读。
        /// 这里统一串行化：既保证只做一次，也保证不会和正在读缓存的线程打架。
        /// </summary>
        private static void EnsureCacheFolderReady(string folder)
        {
            if (_cacheFolderReady) return;

            // 注意：这个方法**不再**由 GetCachePath 调用（v1.51.12）。
            // 以前 GetCachePath 每次都跑一遍这里，而 PruneDiskCacheOnce 在
            // "缓存没超上限"的正常路径上是 `return` 且**不置位**标志的，
            // 于是标志永远为 false —— 每次取缓存路径都要：
            //     ① new DirectoryInfo(folder).GetFiles("v3_*.png")  全目录枚举
            //     ② 抢一把 static 全局锁 _cacheInitLock
            // 缓存目录上万个小文件时，这是"打开面板就假死"的头号元凶：
            // 自绘每一项都要 IsDynamicBlock → GetCachePath，8 路后台 worker
            // 还会在这把全局锁上全部串行化，所谓并行退化成串行。
            // 现在它只由 WarmUpCacheFolder 在后台线程跑一次，与关键路径彻底解耦。
            lock (_cacheInitLock)
            {
                if (_cacheFolderReady) return;

                try
                {
                    if (!Directory.Exists(folder))
                        Directory.CreateDirectory(folder);

                    CleanAndPruneCacheOnce(folder);
                }
                catch
                {
                    // 清理失败不影响使用，只是旧文件多占点磁盘
                }
                finally
                {
                    // 无论走哪个分支都必须置位，否则下次还会重来一遍（这正是老 bug）
                    _cacheFolderReady = true;
                }
            }
        }

        /// <summary>
        /// 清旧版缓存 + 容量裁剪，合并成**一次**目录枚举（v1.51.12）。
        /// 以前是先 GetFiles("*") 删旧版、再 GetFiles("v3_*.png") 裁剪，
        /// 缓存目录上万个小文件时等于扫两遍。现在一次枚举就把三件事做完：
        /// 删除非当前版本前缀的遗留文件、统计当前版本缓存、记录访问时间用于淘汰。
        /// </summary>
        private static void CleanAndPruneCacheOnce(string folder)
        {
            List<FileInfo> keep = new List<FileInfo>();
            int deletedLegacy = 0;

            foreach (string file in Directory.GetFiles(folder))
            {
                string name = Path.GetFileName(file);

                bool isCache = name.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
                bool isMarker = name.EndsWith(".png.dyn", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".png.vis", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".png.nodyn", StringComparison.OrdinalIgnoreCase);

                if (!isCache && !isMarker) continue;

                // 旧版本留下的文件（前面版本算法不同，留着只会误导），直接删
                if (!name.StartsWith(CacheVersionPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(file); deletedLegacy++; } catch { }
                    continue;
                }

                // 当前版本的 PNG 留着参与容量裁剪；标记文件不占配额
                if (isCache)
                {
                    try { keep.Add(new FileInfo(file)); } catch { }
                }
            }

            if (deletedLegacy > 0)
                BlmLog.Write("已清理旧版本缩略图缓存 " + deletedLegacy + " 个文件");

            if (keep.Count <= MaxDiskCacheFiles) return;

            // 超过上限：按最后访问时间升序淘汰最久没用的一批
            try
            {
                keep.Sort(delegate (FileInfo a, FileInfo b)
                {
                    try { return a.LastAccessTimeUtc.CompareTo(b.LastAccessTimeUtc); }
                    catch { return 0; }
                });
            }
            catch
            {
                return;      // 排序失败就跳过裁剪，不影响使用
            }

            // 一次多留 5% 余量，避免之后每加一张就又要排序删一次
            int keepCount = (int)(MaxDiskCacheFiles * 0.95);
            int remove = keep.Count - keepCount;
            if (remove <= 0) return;

            for (int i = 0; i < remove; i++)
            {
                string path = keep[i].FullName;
                try { keep[i].Delete(); } catch { }

                try
                {
                    // 三种标记文件都要跟着删，漏掉 .nodyn 会让缓存目录只增不减——
                    // 而且容量统计是按 *.png 算的，永远统计不到它们。
                    DeleteSilently(path + ".dyn");
                    DeleteSilently(path + ".nodyn");
                    DeleteSilently(path + ".vis");
                }
                catch { }
            }

            BlmLog.Write("磁盘缩略图缓存已裁剪 " + remove + " 个旧文件（上限 " + MaxDiskCacheFiles + "）");
        }

        private static void DeleteSilently(string path)
        {
            try { File.Delete(path); } catch { }
        }

        /// <summary>磁盘缓存 PNG 数量上限，超出后按最后访问时间淘汰最旧的。</summary>
        private const int MaxDiskCacheFiles = 4000;

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

        /// <summary>
        /// 允许的"过采样倍率"。缓存图比显示尺寸略大一点点（1.5 倍），
        /// 高分屏下拉伸时不会发虚；超过这个倍率就一律降采样。
        /// </summary>
        private const double DecodeOversample = 1.5;

        /// <summary>
        /// 解码时降采样 —— 本项目的 GDI+ 版 DecodePixelWidth。
        ///
        /// WPF 里设 BitmapImage.DecodePixelWidth 能让解码器直接产出小图，
        /// 省掉"先解码整张大图、再在内存里缩小"的那一遍；GDI+ 没有对应的
        /// 解码参数，只能在解码后**立刻**重采样、然后马上释放大图与流，
        /// 达到同样的效果：留在缓存里的是目标尺寸的小图，而不是原始大图。
        ///
        /// 顺带解决另一个隐患：Image.FromStream 要求底层流在 Image 的整个
        /// 生命周期里保持打开，原来的写法把 MemoryStream 一直挂在那儿，
        /// 每张缩略图都额外占着一份完整的文件字节。现在统一复制/重采样后
        /// 立即 using 释放。
        /// </summary>
        private static Image TryLoadPng(string path, int targetSize)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

                // 不能直接 Image.FromFile，那会锁住文件
                byte[] bytes = File.ReadAllBytes(path);
                using (MemoryStream stream = new MemoryStream(bytes, false))
                using (Image raw = Image.FromStream(stream))
                {
                    if (targetSize <= 0) return new Bitmap(raw);

                    int limit = Math.Max(16, (int)(targetSize * DecodeOversample));

                    // 已经够小：复制一份断开与流的绑定即可（复制本身很便宜）
                    if (raw.Width <= limit && raw.Height <= limit)
                        return new Bitmap(raw);

                    // 大图：立刻按目标尺寸降采样，大图本体出 using 即释放
                    return ResampleWithin(raw, limit);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 等比重采样，使最长边不超过 maxSide（小图不放大）。
        /// 返回的 Bitmap 由调用方负责释放；失败返回 null。
        /// </summary>
        private static Bitmap ResampleWithin(Image source, int maxSide)
        {
            if (source == null || source.Width <= 0 || source.Height <= 0) return null;

            int w = source.Width;
            int h = source.Height;
            double scale = Math.Min((double)maxSide / w, (double)maxSide / h);

            if (scale >= 1.0) return new Bitmap(source);

            int nw = Math.Max(1, (int)Math.Round(w * scale));
            int nh = Math.Max(1, (int)Math.Round(h * scale));

            // 缓存图一律不透明（FitToCanvas / RenderPreview 画的都是不透明画布），
            // 用 24bpp 比 32bpp 省 25% 内存
            Bitmap result = new Bitmap(nw, nh, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(result))
            {
                // 大比例缩小时双三次会振铃出噪点，改用双线性；
                // 小比例（≥1/3）才用双三次保留细节。
                g.InterpolationMode = scale < 0.34
                    ? InterpolationMode.HighQualityBilinear
                    : InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.White);
                g.DrawImage(source, new Rectangle(0, 0, nw, nh));
            }
            return result;
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

                float penWidth = Math.Max(1f, size / 48f);   // 边框粗细也跟着规格走
                using (Pen pen = new Pen(color, penWidth))
                {
                    g.DrawRectangle(pen, 2, 2, size - 5, size - 5);
                }

                // 字号必须随缩略图规格缩放：以前固定 14pt，规格调小（48 / 64）时
                // "DWG" 会被挤得换行、撑出边框，画面上只剩被切掉的两个字母。
                float px = FitTextPixels(g, text, size);

                using (Brush brush = new SolidBrush(color))
                using (Font font = new Font(PlaceholderFontName, px, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    StringFormat sf = new StringFormat();
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    sf.FormatFlags = StringFormatFlags.NoWrap;      // 绝不换行，换行就是显示不全
                    sf.Trimming = StringTrimming.None;
                    g.DrawString(text, font, brush,
                        new RectangleF(0, 0, size, size), sf);
                }
            }
            return bmp;
        }

        /// <summary>占位图文字所用的字体族（各 Windows 版本都有，缺字体时 GDI+ 会自己回退）。</summary>
        private const string PlaceholderFontName = "Microsoft YaHei";

        /// <summary>
        /// 把文字压到"单行能放下"的字号（像素）。
        /// 目标：宽度不超过边长的 78%、高度不超过 62%，保证任何规格下都不换行、不出框。
        /// </summary>
        private static float FitTextPixels(Graphics g, string text, int size)
        {
            float maxWidth = size * 0.78f;
            float maxHeight = size * 0.62f;
            float px = size * 0.42f;              // 起点：约半格；多字符会被下面逐步压小
            if (px < 5f) px = 5f;

            for (int i = 0; i < 16; i++)
            {
                using (Font font = new Font(PlaceholderFontName, px, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    SizeF m = g.MeasureString(text, font);
                    if (m.Width <= maxWidth && m.Height <= maxHeight) break;
                }

                px *= 0.88f;
                if (px < 5f) { px = 5f; break; }
            }

            return px;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 缓存里的图**不**逐个 Dispose：它们可能仍被 ImageList / 列表项引用，
            // 释放后任何一次重绘都会抛"参数无效"。只解除引用，由 GC 统一回收。
            _memoryCache.Clear();
            _cacheOrder.Clear();
            _cacheNodes.Clear();
            _cachePathCache.Clear();
            _pendingKeys.Clear();
            _pending.Clear();
            _fallback.Clear();
            _fallbackKeys.Clear();
            _meta.Clear();
            _metaKeys.Clear();

            // 这几个字典里装着每个图块一份的元数据（可见性状态里可能有上千个句柄），
            // 面板销毁后服务对象若还被引用，这些内存就是纯泄漏。
            _dynamicFlags.Clear();
            _visStates.Clear();
            _visData.Clear();
            _visIndex.Clear();
            _failCounts.Clear();
            _metaFails.Clear();

            lock (_doneLock) { _done.Clear(); }

            // 占位图是本服务自己创建、且从不外泄所有权的，可以安全释放
            if (_placeholderWhole != null) { try { _placeholderWhole.Dispose(); } catch { } }
            if (_placeholderInner != null) { try { _placeholderInner.Dispose(); } catch { } }
            _placeholderWhole = null;
            _placeholderInner = null;
        }
    }
}
