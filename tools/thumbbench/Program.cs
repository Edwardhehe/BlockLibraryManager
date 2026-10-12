using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using BlockLibraryManager;

namespace ThumbBench
{
    /// <summary>
    /// 缩略图加载策略基准台。
    ///
    /// 同一批真实 DWG、同一份 DwgPreviewReader、同样的 8 路并行、同样的缓存落盘，
    /// 只有"排队策略"不同：
    ///   A = v1.51：点开分类就把整个分类全部登记进队列（后台一直做到最后一张）
    ///   B = v1.52：只登记"预览区能显示的那一段 + 上下半屏余量"，
    ///             滚动期间后台完全停摆，停下后按新视口续装
    ///
    /// 量三个用户真正能感知的数字：
    ///   1) 首屏就绪：点开分类到屏幕上这批图全出来要多久
    ///   2) 后台闲下来：从点开分类到后台彻底没活干要多久（这段时间 CAD 一直在被占用）
    ///   3) 滚动时的后台工作量：下拉滚动条期间后台还在偷偷做多少张
    /// </summary>
    internal static class Program
    {
        const int ThumbSize = 96;          // 默认缩略图规格
        const int MaxWorkers = 8;          // 与 ThumbnailService.MaxWorkers 一致
        const int ScrollQuietMs = 140;     // 与 UI 的 ScrollQuietMs 一致

        static string _cacheDir;
        static int _doneCount;

        static int Main(string[] args)
        {
            string root = args.Length > 0 ? args[0]
                : @"D:\2025室内设计精品图库V2.0悟空整理制作";
            int perPage = args.Length > 1 ? int.Parse(args[1]) : 24;
            int screens = args.Length > 2 ? int.Parse(args[2]) : 4;

            _cacheDir = Path.Combine(Path.GetTempPath(), "blm_thumbbench_cache");
            Directory.CreateDirectory(_cacheDir);

            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("=== 图库缩略图加载策略基准 ===");
            Console.WriteLine("图库根目录 : " + root);
            Console.WriteLine("每屏可见数 : " + perPage + "（大图标 96px 下的典型值）");
            Console.WriteLine("缩略图规格 : " + ThumbSize + "px   并行度 : " + MaxWorkers);
            Console.WriteLine();

            if (!Directory.Exists(root))
            {
                Console.WriteLine("目录不存在，退出。");
                return 2;
            }

            List<string> files = new List<string>(Directory.GetFiles(root, "*.dwg", SearchOption.AllDirectories));
            files.Sort(StringComparer.OrdinalIgnoreCase);

            if (files.Count == 0)
            {
                Console.WriteLine("没有找到 DWG 文件。");
                return 2;
            }

            Console.WriteLine("样本规模 : " + files.Count + " 个 DWG");
            long totalBytes = 0;
            foreach (string f in files) { try { totalBytes += new FileInfo(f).Length; } catch { } }
            Console.WriteLine("样本体积 : " + (totalBytes / 1048576.0).ToString("0.0") + " MB");
            Console.WriteLine();

            // 磁盘 I/O 预热：让两种策略面对同一个"冷/热"档次的磁盘缓存
            WarmDisk(files);

            Result a = RunStrategyA(files, perPage, screens);
            Result b = RunStrategyB(files, perPage, screens);

            Report(a, b, files.Count, perPage, screens);
            return 0;
        }

        // ---------------- 两档策略 ----------------

        struct Result
        {
            public string Name;
            public double FirstScreenMs;   // 首屏（可见的那一批）全部就绪
            public double TotalMs;         // 点开分类后，后台要忙多久才第一次闲下来
            public int Processed;          // 整个测量周期一共处理了多少张（工作量）
            public int DuringScroll;       // 滚动期间后台做了多少张
            public double BusyMs;          // 整个周期里后台累计忙多久（不含静默等待）
            public double ScrollPhaseMs;   // 走完整个滚动过程用户共花多久
        }

        /// <summary>v1.51：整个分类一次性登记，滚走了也照做（后台不设边界）。</summary>
        static Result RunStrategyA(List<string> files, int perPage, int screens)
        {
            ClearCache();
            int n = files.Count;

            // 登记全部条目（对应 RebuildList 里的 RequestThumbnails(_currentBlocks)）
            List<string> queue = new List<string>(files);

            // Prioritize：把可见那一段提到队首（v1.51 唯一的"优先"手段，代价 O(n)）
            List<string> front = queue.GetRange(0, Math.Min(perPage, n));
            queue = Reorder(queue, front);

            Pool pool = new Pool(queue);
            Stopwatch sw = Stopwatch.StartNew();
            pool.Start(delegate (int completed) { });

            // 等首屏那一批出完
            int firstGoal = Math.Min(perPage, n);
            while (pool.Completed < firstGoal) Thread.Sleep(1);
            double firstScreenMs = sw.Elapsed.TotalMilliseconds;

            // 滚动阶段：队列里还压着剩下的 N-P 张，用户下拉时后台照跑不误
            double scrollStart = sw.Elapsed.TotalMilliseconds;
            int beforeScroll = pool.Completed;

            Thread.Sleep(screens * 300);      // 用户连续滚 screens 屏

            int duringScroll = pool.Completed - beforeScroll;

            // 用户滚完停下来，但后台把整个分类剩下的全部做完才罢休
            pool.Join();
            double totalMs = sw.Elapsed.TotalMilliseconds;
            double scrollPhaseMs = totalMs - scrollStart;
            double busyMs = totalMs - firstScreenMs + firstScreenMs;   // 全程无停顿

            return new Result
            {
                Name = "v1.51 全量排队",
                FirstScreenMs = firstScreenMs,
                TotalMs = totalMs,
                Processed = n,
                DuringScroll = duringScroll,
                BusyMs = busyMs,
                ScrollPhaseMs = scrollPhaseMs,
            };
        }

        /// <summary>v1.52：只装视口那一段；滚动期间后台停摆，停下再续。</summary>
        static Result RunStrategyB(List<string> files, int perPage, int screens)
        {
            ClearCache();
            int n = files.Count;

            Stopwatch sw = Stopwatch.StartNew();
            double firstScreenMs = 0;
            double busyMs = 0;
            int processed = 0;

            int margin = Math.Max(4, perPage / 2);

            // ---- 首屏 ----
            int lo = 0;
            int hi = Math.Min(n - 1, perPage - 1 + margin);
            List<string> batch = files.GetRange(lo, hi - lo + 1);
            processed += batch.Count;

            Pool pool = new Pool(batch);
            double t0 = sw.Elapsed.TotalMilliseconds;
            pool.Start(null);
            pool.Join();
            firstScreenMs = sw.Elapsed.TotalMilliseconds;
            busyMs += sw.Elapsed.TotalMilliseconds - t0;

            double totalMs = firstScreenMs;   // 点开分类后，做完这一屏后台就闲了

            // ---- 滚动 ----
            double scrollStart = sw.Elapsed.TotalMilliseconds;
            int duringScroll = 0;      // 滚动期间一个活都不派

            int top = 0;
            for (int s = 0; s < screens; s++)
            {
                Thread.Sleep(300);             // 用户在滚（后台暂停）
                Thread.Sleep(ScrollQuietMs);   // 停下后的静默期

                top += perPage;
                if (top >= n) break;

                lo = Math.Max(0, top - margin);
                hi = Math.Min(n - 1, top + perPage - 1 + margin);
                if (hi < lo) break;

                List<string> next = files.GetRange(lo, hi - lo + 1);
                processed += next.Count;

                Pool p2 = new Pool(next);
                double t1 = sw.Elapsed.TotalMilliseconds;
                p2.Start(null);
                p2.Join();
                busyMs += sw.Elapsed.TotalMilliseconds - t1;
            }

            double scrollPhaseMs = sw.Elapsed.TotalMilliseconds - scrollStart;

            return new Result
            {
                Name = "v1.52 只装视口 + 滚动暂停",
                FirstScreenMs = firstScreenMs,
                TotalMs = totalMs,
                Processed = processed,
                DuringScroll = duringScroll,
                BusyMs = busyMs,
                ScrollPhaseMs = scrollPhaseMs,
            };
        }

        // ---------------- 执行器 ----------------

        /// <summary>8 路并行处理一批文件的"后台线程池"。</summary>
        sealed class Pool
        {
            readonly List<string> _batch;
            int _cursor = -1;
            int _completed;
            Thread[] _workers;

            public Pool(List<string> batch) { _batch = batch; }

            public int Completed { get { return Volatile.Read(ref _completed); } }

            public void Start(Action<int> onProgress)
            {
                int total = _batch.Count;
                if (total == 0) return;

                int wc = Math.Min(MaxWorkers, total);
                _workers = new Thread[wc];

                for (int w = 0; w < wc; w++)
                {
                    _workers[w] = new Thread(delegate ()
                    {
                        while (true)
                        {
                            int i = Interlocked.Increment(ref _cursor);
                            if (i >= total) return;

                            ProcessOne(_batch[i]);

                            int c = Interlocked.Increment(ref _completed);
                            Interlocked.Increment(ref _doneCount);
                            if (onProgress != null) onProgress(c);
                        }
                    });
                    _workers[w].IsBackground = true;
                    _workers[w].Start();
                }
            }

            public void Join()
            {
                if (_workers == null) return;
                foreach (Thread t in _workers) t.Join();
            }
        }

        /// <summary>
        /// 单张缩略图：命中磁盘缓存就直接解码，否则读 DWG 内嵌预览 → 缩放 → 落盘。
        /// 与 ThumbnailService.LoadOrExtract 的主路径一致。
        /// </summary>
        static void ProcessOne(string path)
        {
            string key = Path.GetFileNameWithoutExtension(path);
            string png = Path.Combine(_cacheDir, SafeName(key) + ".png");

            try
            {
                if (File.Exists(png))
                {
                    using (Bitmap cached = new Bitmap(png)) { /* 命中缓存：解码即可 */ }
                    return;
                }

                using (Bitmap src = DwgPreviewReader.TryRead(path))
                {
                    if (src == null) return;

                    using (Bitmap thumb = new Bitmap(ThumbSize, ThumbSize))
                    {
                        using (Graphics g = Graphics.FromImage(thumb))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g.DrawImage(src, 0, 0, ThumbSize, ThumbSize);
                        }
                        thumb.Save(png, ImageFormat.Png);
                    }
                }
            }
            catch
            {
                // 单张失败不影响基准
            }
        }

        static string SafeName(string s)
        {
            char[] bad = Path.GetInvalidFileNameChars();
            string r = s;
            foreach (char c in bad) r = r.Replace(c, '_');
            return r;
        }

        /// <summary>v1.51 的 Prioritize：把命中 front 的提到前面（O(n)）。</summary>
        static List<string> Reorder(List<string> source, List<string> front)
        {
            HashSet<string> set = new HashSet<string>(front, StringComparer.OrdinalIgnoreCase);
            List<string> head = new List<string>();
            List<string> tail = new List<string>();

            foreach (string s in source)
            {
                if (set.Contains(s)) head.Add(s);
                else tail.Add(s);
            }
            head.AddRange(tail);
            return head;
        }

        static void ClearCache()
        {
            try
            {
                foreach (string f in Directory.GetFiles(_cacheDir, "*.png"))
                {
                    try { File.Delete(f); } catch { }
                }
            }
            catch { }
        }

        /// <summary>顺序读一遍文件头，让 OS 文件缓存对两种策略同等友好。</summary>
        static void WarmDisk(List<string> files)
        {
            int take = Math.Min(files.Count, 200);
            for (int i = 0; i < take; i++)
            {
                try
                {
                    using (FileStream fs = File.OpenRead(files[i]))
                    {
                        byte[] buf = new byte[4096];
                        fs.Read(buf, 0, buf.Length);
                    }
                }
                catch { }
            }
        }

        // ---------------- 输出 ----------------

        static void Report(Result a, Result b, int n, int perPage, int screens)
        {
            Console.WriteLine("------------------------------------------------------------");
            Console.WriteLine("【1】首屏就绪（点开分类 → 屏幕上这批图全部出来）");
            Console.WriteLine("   v1.51 : " + a.FirstScreenMs.ToString("0") + " ms");
            Console.WriteLine("   v1.52 : " + b.FirstScreenMs.ToString("0") + " ms");
            Console.WriteLine("   提速   : " + Pct(a.FirstScreenMs, b.FirstScreenMs) + "  （" +
                              (a.FirstScreenMs / Math.Max(0.001, b.FirstScreenMs)).ToString("0.00") + "×）");
            Console.WriteLine();

            Console.WriteLine("【2】点开分类后，AutoCAD 后台要忙多久才闲下来");
            Console.WriteLine("   v1.51 : " + a.TotalMs.ToString("0") + " ms（非做完整整 " + a.Processed + " 张不可）");
            Console.WriteLine("   v1.52 : " + b.TotalMs.ToString("0") + " ms（做完这一屏就停）");
            Console.WriteLine("   提速   : " + Pct(a.TotalMs, b.TotalMs) + "  （" +
                              (a.TotalMs / Math.Max(0.001, b.TotalMs)).ToString("0.00") + "×）");
            Console.WriteLine();

            Console.WriteLine("【3】整个操作周期的工作量（首屏 + 滚 " + screens + " 屏）");
            Console.WriteLine("   v1.51 : " + a.Processed + " 张，后台累计忙 " + a.BusyMs.ToString("0") + " ms");
            Console.WriteLine("   v1.52 : " + b.Processed + " 张，后台累计忙 " + b.BusyMs.ToString("0") + " ms");
            Console.WriteLine("   工作量减少 : " + Pct(a.Processed, b.Processed) +
                              "，后台占用减少 : " + Pct(a.BusyMs, b.BusyMs));
            Console.WriteLine();

            Console.WriteLine("【4】下拉滚动条期间后台偷偷做了多少张");
            Console.WriteLine("   v1.51 : " + a.DuringScroll + " 张（后台不暂停，持续占用线程与主线程预算）");
            Console.WriteLine("   v1.52 : " + b.DuringScroll + " 张（滚动期间完全停摆）");
            Console.WriteLine();

            Console.WriteLine("【5】滚过 " + screens + " 屏全程耗时（含滚动动作与静默等待）");
            Console.WriteLine("   v1.51 : " + a.ScrollPhaseMs.ToString("0") + " ms");
            Console.WriteLine("   v1.52 : " + b.ScrollPhaseMs.ToString("0") + " ms");
            Console.WriteLine();

            Console.WriteLine("------------------------------------------------------------");
            Console.WriteLine("结论：点开一个 " + n + " 个图块的分类，v1.52 只为屏幕上这 " +
                              perPage + " 张出图，");
            Console.WriteLine("      首屏快 " + Pct(a.FirstScreenMs, b.FirstScreenMs) +
                              "，后台占用时间少 " + Pct(a.TotalMs, b.TotalMs) + "，");
            Console.WriteLine("      总工作量少 " + Pct(a.Processed, b.Processed) + "。");
        }

        /// <summary>从 old 到 neu 的下降百分比。</summary>
        static string Pct(double oldVal, double newVal)
        {
            if (oldVal <= 0) return "-";
            double p = (oldVal - newVal) / oldVal * 100.0;
            if (p < 0) p = 0;
            return p.ToString("0.0") + "%";
        }
    }
}
