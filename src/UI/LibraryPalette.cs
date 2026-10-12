using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;

namespace BlockLibraryManager
{
    /// <summary>
    /// 主窗口宿主。用无边框自绘窗口（LibraryForm）承载 LibraryControl。
    ///
    /// 与性能有关的细节：
    /// 1. 窗口不预先创建，直到第一次调用命令时才实例化，避免拖慢 AutoCAD 启动；
    /// 2. 缩略图生成挂在 Application.Idle 上分批处理，
    ///    这样打开一个上千张图的图库时界面也不会卡住。
    /// </summary>
    public static class LibraryPalette
    {
        private static LibraryForm _form;
        private static LibraryControl _control;
        private static LibraryConfig _config;
        private static bool _idleHooked;
        private static bool _displayEventsHooked;

        /// <summary>当前是否处于最大化（以操作系统原生 WindowState 为准）。
        /// 标题栏拖动 / 双击等据此判断，与按钮图标保持一致。</summary>
        internal static bool IsManualMaximized
        {
            get
            {
                try { return _form != null && _form.WindowState == FormWindowState.Maximized; }
                catch { return false; }
            }
        }

        /// <summary>程序化改变窗口尺寸时置 true，避免 BoundsChangedByUser 把过程尺寸写进配置。</summary>
        private static bool _suppressCapture;

        // ---- 拖拽插入（缩略图拖到 CAD 图形窗口）期间的自动最小化 / 还原 ----

        private static Autodesk.AutoCAD.DatabaseServices.UnitsValue _savedInsunits;
        private static bool _insunitsChanged;
        private static bool _dragRestoreHooked;

        /// <summary>命令结束事件挂在哪张图上。还原时必须用它反挂接，
        /// 不能用"当前活动图"——中途换图会摘错对象，旧图上的订阅会残留。</summary>
        private static Autodesk.AutoCAD.ApplicationServices.Document _dragHookDoc;

        private static Timer _dragFallbackTimer;
#if !BLM_ACAD
        /// <summary>中望 / 浩辰：反射挂上的 CommandEnded / CommandCancelled 委托（卸载时要原样传回）。</summary>
        private static object _dragEndedBound;
        private static object _dragCancelledBound;
        /// <summary>命令结束事件是否真的挂上了。挂不上时兜底定时器会缩短等待。</summary>
        private static bool _dragEventAvailable;
#endif

        public static bool IsCreated
        {
            get { return _form != null; }
        }

        /// <summary>
        /// 用户是否真的看得见窗口。
        ///
        /// 三重判定，缺一个都会出现"命令唤不回窗口"：
        ///   1. Form.Visible —— 最小化时它仍为 true，只看它会把"最小化"
        ///      当成"显示中"，BLOCKLIB 开关方向反掉（老 BUG）；
        ///   2. WindowState != Minimized —— 补上第 1 条漏掉的最小化；
        ///   3. **实际在屏幕几何范围内** —— 双屏下窗口被摆到屏幕外时
        ///      前两条都成立，于是 BLOCKLIB 又去执行 Hide，用户再怎么输命令
        ///      窗口都不出现（本次修的就是这条）。
        /// </summary>
        internal static bool IsEffectivelyVisible
        {
            get
            {
                try
                {
                    if (_form == null || !_form.Visible) return false;
                    if (_form.WindowState == FormWindowState.Minimized) return false;
                    return IsOnScreen();
                }
                catch { return false; }
            }
        }

        /// <summary>窗口在屏幕几何上是否找得着。取不到句柄时按"找得着"处理，避免误判。</summary>
        private static bool IsOnScreen()
        {
            try
            {
                if (_form == null || _form.IsDisposed || !_form.IsHandleCreated) return true;

                // Win32 物理口径：与 Per-Monitor V2 窗口同一坐标系，
                // 副屏缩放比不同也不会误判
                return NativeWindow.IsOnScreen(_form.Handle, 200, 120);
            }
            catch { return true; }
        }

        /// <summary>
        /// 程序化改变 WindowState。
        /// 期间置 LibraryForm.SuppressStateToggle，避免随之而来的
        /// WM_SYSCOMMAND 被再路由一次把状态改错。
        /// </summary>
        private static void SetWindowState(FormWindowState state)
        {
            if (_form == null) return;

            _suppressCapture = true;
            try
            {
                _form.SuppressStateToggle = true;
                try { _form.WindowState = state; }
                finally { _form.SuppressStateToggle = false; }
            }
            finally { _suppressCapture = false; }
        }

        public static void Show()
        {
            EnsureCreated();
            if (_form == null) return;

            // 窗口被摆到屏幕外（双屏最大化落点跑偏）时，光 Show 是唤不回来的：
            // 它"显示着"，只是不在任何显示器的可视范围内。先把位置救回来。
            if (!IsOnScreen()) RecoverLostWindow();

            ShowWindow();
            _form.Activate();
        }

        /// <summary>
        /// 把跑到屏幕外的窗口救回来。
        ///
        /// 分屏 / 混合 DPI 场景下，最大化后的实际矩形可能整个落在工作区之外，
        /// 表现就是"点最大化窗口消失，再输命令也没反应"。这里先把状态拉回可控的
        /// Normal（最大化/最小化状态下改位置没有意义），再用 Win32 把窗口夹取回
        /// 它所在显示器的工作区——全程物理像素，不做任何 DPI 换算。
        /// </summary>
        private static void RecoverLostWindow()
        {
            if (_form == null || _form.IsDisposed) return;

            try
            {
                if (_form.WindowState != FormWindowState.Normal)
                    SetWindowState(FormWindowState.Normal);

                IntPtr hwnd = _form.Handle;
                NativeWindow.RestoreFromMinimized(hwnd);
                NativeWindow.ClampIntoWorkArea(hwnd);

                BlmLog.Write("窗口已从不屏幕外拉回可视区（原矩形：" +
                    _form.NativeBounds.ToString() + "）");
            }
            catch (System.Exception ex)
            {
                BlmLog.Write("恢复丢失窗口失败", ex);
            }
        }

        public static void Hide()
        {
            if (_form == null) return;
            try
            {
                CaptureBounds();
                _form.Hide();

                // 面板一关，后台缩略图立刻停工。
                // 缩略图是挂在 Application.Idle 上分批做的，每批要占用主线程
                // 打开 DWG / 解码 / 重采样；窗口都关了还在跑，就是用户感觉到的
                // "关掉图库管理器之后 CAD 一直卡"。
                if (_control != null) _control.SuspendThumbnailWork();

                if (_config != null) _config.Save();
            }
            catch { }
        }

        public static void Toggle()
        {
            EnsureCreated();
            if (_form == null) return;

            // 按"实际看得见"判断，而不是 Form.Visible。
            // 最小化时 Visible 仍是 true，若按老逻辑判断，用户按 BLOCKLIB
            // 会执行 Hide 而不是 Show，窗口就再也唤不回来了。
            if (IsEffectivelyVisible) Hide();
            else Show();
        }

        public static void Refresh()
        {
            if (!IsCreated) return;

            if (_control != null)
                _control.ReloadLibrary(true);
        }

        /// <summary>打开设置对话框（命令 BLOCKLIBS 使用）。</summary>
        public static void ShowSettings()
        {
            EnsureCreated();
            if (_config == null || _control == null || _form == null) return;

            using (SettingsForm form = new SettingsForm(_config))
            {
                if (form.ShowDialog(_form) != DialogResult.OK) return;
            }

            _config.Save();
            _control.Initialize(_config);
        }

        /// <summary>打开“关于”对话框（工具栏“关于”按钮使用）。</summary>
        public static void ShowAbout()
        {
            // 把面板当前主题传进"关于"窗口：内容区底色、正文、灰色副标题、
            // 确定按钮全部跟随同一套配色。以前它们是写死的白底 + 系统灰字，
            // 切到"深黑灰"之后关于窗口还是一块白，跟面板完全脱节。
            // 面板还没创建过（_config 为 null）时用默认主题，不会空引用。
            UiTheme theme = _config != null ? UiThemes.Get(_config.Theme) : UiThemes.Default;

            using (AboutForm form = new AboutForm(theme))
            {
                try
                {
#if BLM_ACAD
                    // ShowModalDialog(Form) 处理了 AutoCAD 的焦点/所有权
                    Autodesk.AutoCAD.ApplicationServices.Application.ShowModalDialog(form);
#else
                    // 中望 / 浩辰：Application.ShowModalDialog 不一定实现，
                    // 先反射调用，失败再退回普通 WinForms 模态框（差别只是
                    // 对话框不再强制归属 CAD 主窗口，功能完全一样）。
                    if (!CadHost.TryInvokeStatic(
                            typeof(Autodesk.AutoCAD.ApplicationServices.Application),
                            "ShowModalDialog", form))
                    {
                        if (_form != null && _form.Visible) form.ShowDialog(_form);
                        else form.ShowDialog();
                    }
#endif
                }
                catch
                {
                    if (_form != null && _form.Visible) form.ShowDialog(_form);
                    else form.ShowDialog();
                }
            }
        }

        /// <summary>把插入结果回传到面板状态栏（由命令 BLOCKLIBIN 调用）。</summary>
        public static void ReportInsert(IList<BlockItem> items, string message, bool succeeded)
        {
            try
            {
                if (_control == null) return;
                _control.ReportInsertResult(items, message, succeeded);
            }
            catch
            {
                // 面板已关闭时忽略
            }
        }

        /// <summary>把粘贴入库结果回传到面板（由命令 BLOCKLIBPASTE 调用）。</summary>
        public static void ReportPaste(bool succeeded, string message)
        {
            try
            {
                if (_control == null) return;
                _control.ReportPasteResult(succeeded, message);
            }
            catch
            {
                // 面板已关闭时忽略
            }
        }

        /// <summary>标题栏“最小化”。</summary>
        public static void Minimize()
        {
            if (_form == null) return;

            try
            {
                SetWindowState(FormWindowState.Minimized);

                // 最小化后列表一帧都不画，后台再生成缩略图纯属浪费主线程
                if (_control != null) _control.SuspendThumbnailWork();
            }
            catch (System.Exception ex)
            {
                BlmLog.Write("最小化窗口失败", ex);
            }
        }

        /// <summary>
        /// 临时让出画面（插入图块 / 拖拽插入 / 批量入库前调用）。
        ///
        /// 以前这里用的是"最小化"，问题很典型：
        ///   1. 最小化后的窗口在 CAD 主窗口后面 / 任务栏里，用户根本找不到；
        ///   2. 万一收尾的回调没走到（用户 Esc、命令被打断），窗口就永远停在
        ///      最小化状态，再按 BLOCKLIB 也唤不回来。
        /// 现在改成"只隐藏、不动 WindowState"：最大化状态原样保留，
        /// 还原时一定回到用户离开前的样子，且不存在找不到的中间态。
        /// </summary>
        public static void PushAside()
        {
            if (_form == null) return;

            try
            {
                CaptureBounds();
                _form.Hide();

                // 窗口都让开了，后台缩略图立刻停工
                if (_control != null) _control.SuspendThumbnailWork();
            }
            catch (System.Exception ex)
            {
                BlmLog.Write("临时让出窗口失败", ex);
            }
        }

        // ---------------- 拖拽插入：最小化 → 插入 → 还原 ----------------

        /// <summary>
        /// 拖拽插入开始（缩略图刚被拖出面板）：面板最小化，同时把当前图形的
        /// 图形单位临时设为"无单位"，这样 AutoCAD 原生拖入 DWG 时不做单位换算，
        /// 保证按 1:1 插入。原单位值记下来，插入结束后恢复。
        /// </summary>
        public static void BeginDragInsert()
        {
            // 同插入命令：只临时隐藏，不用最小化（最小化后窗口容易找不到）
            PushAside();

            try
            {
                Document doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return;

                using (doc.LockDocument())
                {
                    // 关键：上一次的单位若还没恢复（_insunitsChanged 仍为 true），
                    // 说明上回的还原没走到。此时数据库里存的是我们改过的 Undefined，
                    // 绝不能再拿它当"原值"保存，否则图形单位会被永久改丢。
                    if (!_insunitsChanged)
                        _savedInsunits = doc.Database.Insunits;

                    if (doc.Database.Insunits != Autodesk.AutoCAD.DatabaseServices.UnitsValue.Undefined)
                    {
                        doc.Database.Insunits = Autodesk.AutoCAD.DatabaseServices.UnitsValue.Undefined;
                        _insunitsChanged = true;
                    }
                }
            }
            catch (System.Exception ex)
            {
                BlmLog.Write("拖拽插入前设置图形单位失败", ex);
            }
        }

        /// <summary>
        /// 拖拽插入结束。
        /// dropped=false：用户取消了拖拽（没拖进图形窗口），立即还原；
        /// dropped=true ：AutoCAD 接管并开始原生插入命令，挂 CommandEnded /
        /// CommandCanceled，插入真正完成后还原面板与图形单位；
        /// 90 秒兜底定时器防止事件万一不触发导致面板永远收着。
        /// </summary>
        public static void EndDragInsert(bool dropped)
        {
            if (!dropped)
            {
                RestoreDragInsertState();
                return;
            }

            try
            {
                Document doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                if (doc == null)
                {
                    RestoreDragInsertState();
                    return;
                }

                UnhookDragRestore(doc);
#if BLM_ACAD
                doc.CommandEnded += new CommandEventHandler(OnDragInsertCommandFinished);
                doc.CommandCancelled += new CommandEventHandler(OnDragInsertCommandFinished);
#else
                // 中望 / 浩辰：CommandEnded / CommandCancelled 不一定实现，
                // 反射挂接；挂不上就只剩下面的兜底定时器到点还原。
                object ended = CadHost.TryHook(doc, "CommandEnded", OnDragCommandSink);
                object cancelled = CadHost.TryHook(doc, "CommandCancelled", OnDragCommandSink);
                _dragEndedBound = ended;
                _dragCancelledBound = cancelled;
                // 两个事件都挂不上时，下面的兜底定时器会改用更短的等待时间，
                // 免得用户拖完还得干等几十秒才看到面板回来。
                _dragEventAvailable = (ended != null || cancelled != null);
#endif
                _dragRestoreHooked = true;

                // 记下"事件挂在哪张图上"：还原时如果取当前活动图去摘，
                // 中途换过图就会摘错对象——旧图上的订阅残留，之后它里面随便
                // 跑完一条命令都会把面板拉回来。
                _dragHookDoc = doc;

                StartDragFallbackTimer();
            }
            catch
            {
                RestoreDragInsertState();
            }
        }

#if BLM_ACAD
        private static void OnDragInsertCommandFinished(object sender, CommandEventArgs e)
        {
            RestoreDragInsertState();
        }
#else
        /// <summary>中望 / 浩辰：CommandEnded / CommandCancelled 的反射处理器。</summary>
        private static void OnDragCommandSink(object sender, object args)
        {
            RestoreDragInsertState();
        }
#endif

        /// <summary>恢复图形单位 + 还原面板窗口，并清掉所有挂接 / 定时器。</summary>
        private static void RestoreDragInsertState()
        {
            StopDragFallbackTimer();

            try
            {
                if (_dragRestoreHooked)
                {
                    // 必须从"当初挂上去的那张图"上摘，不能用当前活动图
                    UnhookDragRestore(_dragHookDoc);
                }
                _dragHookDoc = null;
            }
            catch { }

            try
            {
                if (_insunitsChanged)
                {
                    Document doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                    if (doc != null)
                    {
                        using (doc.LockDocument())
                        {
                            doc.Database.Insunits = _savedInsunits;
                        }
                    }
                    _insunitsChanged = false;
                }
            }
            catch (System.Exception ex)
            {
                BlmLog.Write("恢复图形单位失败", ex);
            }

            Restore();
        }

        private static void UnhookDragRestore(Document doc)
        {
            if (doc == null) return;
#if BLM_ACAD
            try { doc.CommandEnded -= new CommandEventHandler(OnDragInsertCommandFinished); } catch { }
            try { doc.CommandCancelled -= new CommandEventHandler(OnDragInsertCommandFinished); } catch { }
#else
            CadHost.TryUnhook(doc, "CommandEnded", _dragEndedBound);
            CadHost.TryUnhook(doc, "CommandCancelled", _dragCancelledBound);
            _dragEndedBound = null;
            _dragCancelledBound = null;
#endif
            _dragRestoreHooked = false;
        }

        private static void StartDragFallbackTimer()
        {
            StopDragFallbackTimer();
            _dragFallbackTimer = new Timer();

            // 兜底时间：拖进 CAD 后正常会由 CommandEnded 立刻还原。
            // 这里只是防"事件万一没来"的保险；DoDragDrop 的返回值无法区分
            // 用户是拖进了 CAD 画布还是拖到了桌面，所以时间不宜过长，
            // 否则误判时面板要很久才回来。
            // 国产平台上如果 CommandEnded / CommandCancelled 都没挂上，
            // 定时器就是唯一的还原手段，这时再缩短一点避免长时间干等。
#if BLM_ACAD
            _dragFallbackTimer.Interval = 25000;
#else
            _dragFallbackTimer.Interval = _dragEventAvailable ? 25000 : 12000;
#endif
            _dragFallbackTimer.Tick += new EventHandler(delegate(object s, EventArgs e)
            {
                RestoreDragInsertState();
            });
            _dragFallbackTimer.Start();
        }

        private static void StopDragFallbackTimer()
        {
            if (_dragFallbackTimer != null)
            {
                try
                {
                    _dragFallbackTimer.Stop();
                    _dragFallbackTimer.Dispose();
                }
                catch { }
                _dragFallbackTimer = null;
            }
        }

        /// <summary>
        /// 恢复显示：批量入库等操作结束后，把最小化 / 隐藏的面板带回来并激活。
        /// </summary>
        public static void Restore()
        {
            if (_form == null) return;

            try
            {
                // 先脱离最小化（若处于该状态），再决定是否要重新显示，
                // 最后强制回到屏幕上可见的位置并激活 —— 三步都做，
                // 任何一种"窗口找不到了"的中间态都能救回来。
                if (_form.WindowState == FormWindowState.Minimized)
                    SetWindowState(FormWindowState.Normal);

                if (!_form.Visible) ShowWindow();
                else _form.Activate();

                EnsureOnScreen();
                try { _form.BringToFront(); } catch { }
                _form.Activate();

                SyncThumbnailWork();
                SyncMaximizeButton();
            }
            catch (System.Exception ex)
            {
                BlmLog.Write("恢复窗口失败", ex);
            }
        }

        /// <summary>
        /// 把批量入库结果回传到面板：先恢复窗口，再刷新图库显示新入库的图块。
        /// 由命令 BLOCKLIBBATCH 调用。
        /// </summary>
        public static void ReportBatch(bool succeeded, string message)
        {
            Restore();

            try
            {
                if (_control != null)
                    _control.ReportBatchResult(succeeded, message);
            }
            catch
            {
                // 面板已关闭时忽略
            }
        }

        /// <summary>
        /// 标题栏“最大化 / 还原”。
        /// 用操作系统原生路径：MaximizedBounds + WindowState。
        /// 之前手工 SetBounds 铺满屏幕的方式在 AutoCAD 宿主窗口上会被
        /// 宿主/系统还原（表现为闪一下又弹回原大小），改用原生状态后
        /// 由 Windows 负责保存与恢复原边界，状态机不可能失步。
        /// </summary>
        public static void ToggleMaximize()
        {
            if (_form == null) return;

            try
            {
                if (_form.WindowState == FormWindowState.Maximized)
                {
                    // 还原：WinForms 自动回到最大化之前的原边界
                    SetWindowState(FormWindowState.Normal);
                }
                else if (_form.WindowState == FormWindowState.Minimized)
                {
                    // 最小化状态下收到"最大化 / 还原"指令：先正常还原成 Normal。
                    // 绝不能一路切到最大化 —— 否则从任务栏点一下窗口就铺满全屏。
                    SetWindowState(FormWindowState.Normal);
                }
                else
                {
                    // 最大化范围不再用 MaximizedBounds 指定。
                    // MaximizedBounds 是 WinForms 逻辑坐标，与 Per-Monitor V2 窗口的
                    // 物理像素不是同一口径，混合 DPI 副屏上会把窗口摆到屏幕外
                    //（"点最大化后消失"）。现在由 LibraryForm 在 WM_GETMINMAXINFO 里
                    // 用 Win32 现场回答"窗口当前所在显示器的工作区"。
                    SetWindowState(FormWindowState.Maximized);
                }

                // 个别宿主不按 MaximizedBounds 摆窗口（分屏 / 混合 DPI 下
                // "最大化后窗口消失 / 显示不全"）——落点不对就强制校正
                EnsureMaximizedOnScreen();
            }
            catch (System.Exception ex)
            {
                BlmLog.Write("切换最大化失败", ex);
            }

            // 从最小化直接最大化时，这里顺带把后台缩略图工作恢复起来
            SyncThumbnailWork();
            SyncMaximizeButton();
        }

        private static void SyncMaximizeButton()
        {
            try
            {
                if (_form != null && _form.Bar != null)
                    _form.Bar.SetMaximized(IsManualMaximized);
            }
            catch { }
        }

        // ---------------- 多联屏 / 显示设置变化 ----------------

        /// <summary>窗口跨屏后：最大化范围要以新屏为准，系统可能仍按旧屏摆 → 重摆一次。</summary>
        private static void OnFormScreenSwitched()
        {
            try
            {
                if (_form == null || _form.IsDisposed) return;

                if (_form.WindowState == FormWindowState.Maximized)
                    ReapplyMaximize();

                SyncMaximizeButton();
            }
            catch { }
        }

        /// <summary>
        /// 拔插显示器 / 改分辨率 / 改缩放后校正窗口：
        /// 最大化态按当前屏重摆；普通态把不在屏幕内的窗口拉回可视区；
        /// 最后整窗强制重绘一次（跨 DPI 屏内容不重画是白块的根源）。
        /// </summary>
        private static void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            try
            {
                if (_form == null || _form.IsDisposed) return;

                if (_form.WindowState == FormWindowState.Maximized)
                    ReapplyMaximize();
                else
                    EnsureOnScreen();

                try { _form.Invalidate(true); } catch { }
                SyncMaximizeButton();
            }
            catch (System.Exception ex)
            {
                BlmLog.Write("显示设置变化校正失败", ex);
            }
        }

        /// <summary>
        /// 最大化落点自愈。
        ///
        /// 无边框窗口的"最大化"靠 MaximizedBounds + WindowState 实现，最终由系统
        /// 按这个范围摆放。但个别宿主 / 多屏 / 混合 DPI 场景下，系统实际摆出来的
        /// 位置和范围对不上——窗口跑到屏幕外（用户看到"最大化后窗口消失"）
        /// 或没铺满 / 超出屏幕（浩辰全屏下的"显示不全"）。
        /// 这里校验窗口实际 Bounds 与期望范围（MaximizedBounds）的重合度，
        /// 低于 85% 就强制把窗口摆到期望范围上。
        /// </summary>
        private static void EnsureMaximizedOnScreen()
        {
            if (_form == null || _form.IsDisposed) return;
            if (_form.WindowState != FormWindowState.Maximized) return;
            if (!_form.IsHandleCreated) return;

            try
            {
                IntPtr hwnd = _form.Handle;

                // 期望范围取 Win32 物理工作区（与窗口同一口径），
                // 不再用 MaximizedBounds（WinForms 逻辑坐标）
                if (NativeWindow.VisiblePercent(hwnd) >= 85) return;   // 落点正常

                // 先按状态机走一遍 Normal → Maximized，让系统重新问一次
                // WM_GETMINMAXINFO（它会按当前屏给出正确的落点）
                ReapplyMaximizeCore();

                // 个别宿主会强行改回错误位置：直接用 Win32 摆到工作区兜底
                if (NativeWindow.VisiblePercent(hwnd) < 85)
                {
                    NativeWindow.RECT work;
                    if (NativeWindow.TryGetWorkArea(hwnd, out work))
                    {
                        NativeWindow.RECT before;
                        NativeWindow.TryGetWindowRect(hwnd, out before);
                        NativeWindow.Place(hwnd, work);

                        BlmLog.Write("最大化落点异常已校正（实际：" + before.ToRectangle().ToString() +
                            "，期望：" + work.ToRectangle().ToString() + "）");
                    }
                }
            }
            catch
            {
                // 校正失败不影响窗口本身
            }
        }

        /// <summary>
        /// 重新应用一次最大化（Normal → Maximized）。
        /// 只改 MaximizedBounds 的值系统不会重摆已最大化的窗口，
        /// 必须走一遍状态切换才会重新询问落点。
        /// </summary>
        private static void ReapplyMaximize()
        {
            if (_form == null || _form.IsDisposed) return;
            if (_form.WindowState != FormWindowState.Maximized) return;

            ReapplyMaximizeCore();
            EnsureMaximizedOnScreen();
        }

        /// <summary>Normal → Maximized 的状态切换本体（不做落点校验，供自愈递归调用）。</summary>
        private static void ReapplyMaximizeCore()
        {
            if (_form == null || _form.IsDisposed) return;

            _suppressCapture = true;
            try
            {
                _form.SuppressStateToggle = true;
                try
                {
                    _form.WindowState = FormWindowState.Normal;
                    _form.WindowState = FormWindowState.Maximized;
                }
                finally { _form.SuppressStateToggle = false; }
            }
            finally { _suppressCapture = false; }
        }

        // ---------------- 创建 / 释放 ----------------

        private static void EnsureCreated()
        {
            if (_form != null) return;

            try
            {
                EnsureCreatedCore();
            }
            catch (System.Exception ex)
            {
                // 兜底：窗口创建失败时给用户明确提示 + 记日志，
                // 绝不让异常以“未经处理的异常”弹窗形式拖垮 AutoCAD。
                _form = null;
                _control = null;
                BlmLog.Write("创建图库窗口失败", ex);
                MessageBox.Show(
                    "图库管理器启动失败：" + ex.Message +
                    "\n\n详细错误已记录到：" + BlmLog.LogFilePath,
                    "图库管理器", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static void EnsureCreatedCore()
        {
            _config = LibraryConfig.Load();

            _control = new LibraryControl();
            _control.Dock = DockStyle.Fill;

            _form = new LibraryForm(_control, "图库管理器");

            _form.Bar.MinimizeClicked += new EventHandler(delegate(object s, EventArgs e) { Minimize(); });
            _form.Bar.MaximizeClicked += new EventHandler(delegate(object s, EventArgs e) { ToggleMaximize(); });
            _form.Bar.CloseClicked += new EventHandler(delegate(object s, EventArgs e) { Hide(); });
            _form.BoundsChangedByUser += new EventHandler(delegate(object s, EventArgs e) { CaptureBounds(); });
            _form.CaptionMaximizeToggle += new EventHandler(delegate(object s, EventArgs e) { ToggleMaximize(); });
            // 系统还原（如从任务栏点回来）不经过插件状态机，这里补一次同步，
            // 保证标题栏按钮图标与实际 WindowState 始终一致
            _form.Resize += new EventHandler(delegate(object s, EventArgs e) { SyncMaximizeButton(); });

            // 多联屏：窗口跨屏后，系统可能还按旧屏的工作区摆着最大化窗口
            //（白块 / 错位就发生在这一刻）——重新应用一次最大化
            _form.ScreenSwitched += new EventHandler(delegate(object s, EventArgs e) { OnFormScreenSwitched(); });

            // 显示设置变化（拔插显示器 / 改分辨率 / 改缩放）同样要校正
            try
            {
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged +=
                    new EventHandler(OnDisplaySettingsChanged);
                _displayEventsHooked = true;
            }
            catch
            {
                // 挂不上就不处理显示变化，功能不受影响
            }

            RestoreWindowBounds();

            // 初始化数据（建树 / 读配置），失败只记日志，不拖垮窗口
            SafeInitializeControl();

            HookIdle();
        }

        /// <summary>控件初始化（读配置、建树、列图块）出错时兜底，记日志但不中断。</summary>
        internal static void SafeInitializeControl()
        {
            if (_control == null || _config == null) return;
            try
            {
                _control.Initialize(_config);
            }
            catch (System.Exception ex)
            {
                BlmLog.Write("面板初始化失败", ex);
            }
        }

        private static void ShowWindow()
        {
            try
            {
                // 最小化状态下 Visible 是 true，老逻辑会跳过显示这一步，
                // 于是窗口一直缩在最小化状态里。这里先无条件脱离最小化。
                if (_form.WindowState == FormWindowState.Minimized)
                    SetWindowState(FormWindowState.Normal);

                if (!_form.Visible)
                {
                    // 窗口创建瞬间把线程切到 Per-Monitor V2：混合 DPI 多联屏下，
                    // System-Aware 窗口会被系统位图拉伸（内容糊 + 溢出屏幕）。
                    // 只影响这一个窗口的创建，随后立刻恢复。
                    IntPtr oldDpiCtx = DpiAwareness.EnterPerMonitorV2();
                    try
                    {
#if BLM_ACAD
                        // ShowModelessDialog 让窗口归属于 AutoCAD 主窗口：
                        // CAD 最小化时跟着最小化，也不会被压到 CAD 窗口后面。
                        // 注意：方法名是 ShowModelessDialog（参数是 WinForms Form），
                        // ShowModelessWindow 的参数是 WPF 的 System.Uri，不要混淆。
                        // （写全名：本文件同时 using 了 System.Windows.Forms，Application 会有二义）
                        Autodesk.AutoCAD.ApplicationServices.Application.ShowModelessDialog(_form);
#else
                        // 中望 / 浩辰：Application.ShowModelessDialog 不一定实现，
                        // 先反射调用；没有就直接 Show —— 窗口照常显示，仅不再
                        // 自动跟随 CAD 主窗口一起最小化。
                        if (!CadHost.TryInvokeStatic(
                                typeof(Autodesk.AutoCAD.ApplicationServices.Application),
                                "ShowModelessDialog", _form))
                        {
                            _form.Show();
                        }
#endif
                    }
                    finally { DpiAwareness.Restore(oldDpiCtx); }
                }

                // 记下 CAD 主窗口：万一窗口落点跑偏，最大化时参照它所在的显示器。
                // 反射取值是因为各家 CAD 的 Application.MainWindow 返回类型不同
                // （AutoCAD 是 Autodesk.AutoCAD.Windows.Window），强类型引用会编译不过。
                if (_form.HostWindowHandle == IntPtr.Zero)
                    _form.HostWindowHandle = GetCadMainWindowHandle();

                // 无论 WinForms 认为"已经显示"与否，都在系统层面强制显示一次。
                // 宿主（ShowModelessDialog）可能把窗口藏起来而 Visible 没同步，
                // 于是走不到上面的分支，窗口就一直不出现。
                if (_form.IsHandleCreated) NativeWindow.ForceShow(_form.Handle);

                _form.Activate();

                // 防"窗口跑到屏幕外"（拔掉副屏 / 位置被改坏）导致找不到窗口
                EnsureOnScreen();
                try { _form.BringToFront(); } catch { }

                SyncThumbnailWork();
                SyncMaximizeButton();
            }
            catch (System.Exception ex)
            {
                BlmLog.Write("显示窗口失败", ex);
                try { _form.Show(); } catch { }
                EnsureOnScreen();
                SyncThumbnailWork();
            }
        }

        /// <summary>
        /// 取 CAD 主窗口句柄。取不到返回 Zero（落点逻辑会退回窗口自身所在的显示器）。
        /// </summary>
        private static IntPtr GetCadMainWindowHandle()
        {
            try
            {
                Type appType = typeof(Autodesk.AutoCAD.ApplicationServices.Application);

                System.Reflection.PropertyInfo pi = appType.GetProperty(
                    "MainWindow",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (pi == null) return IntPtr.Zero;

                object win = pi.GetValue(null, null);
                if (win == null) return IntPtr.Zero;

                System.Reflection.PropertyInfo hp = win.GetType().GetProperty(
                    "Handle",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (hp == null) return IntPtr.Zero;

                object h = hp.GetValue(win, null);
                if (h is IntPtr) return (IntPtr)h;
                if (h == null) return IntPtr.Zero;

                return new IntPtr(Convert.ToInt64(h));
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// 按窗口当前可见状态决定后台缩略图工作要不要继续：
        /// 看得见就恢复（并把缺图的条目重新排队），看不见 / 最小化了就立刻停。
        /// 所有会改变窗口可见性的入口（Show / Hide / Minimize / Restore / 最大化切换）
        /// 最后都调一次这里，保证状态不会失步。
        /// </summary>
        private static void SyncThumbnailWork()
        {
            if (_control == null) return;

            try
            {
                bool visible = _form != null &&
                               _form.Visible &&
                               _form.WindowState != FormWindowState.Minimized;

                if (visible) _control.ResumeThumbnailWork();
                else _control.SuspendThumbnailWork();
            }
            catch
            {
                // 状态同步失败不影响窗口本身
            }
        }

        /// <summary>把当前窗口位置写进配置（只在 Normal 状态且非程序化调整时调用才有效）。</summary>
        private static void CaptureBounds()
        {
            if (_form == null || _config == null) return;
            if (_suppressCapture || IsManualMaximized) return;
            try
            {
                if (_form.WindowState != FormWindowState.Normal) return;

                _config.WindowLeft = _form.Left;
                _config.WindowTop = _form.Top;
                _config.WindowWidth = _form.Width;
                _config.WindowHeight = _form.Height;
            }
            catch { }
        }

        /// <summary>
        /// 取指定矩形所在的屏幕工作区（多显示器友好）。
        /// 矩形落在哪块屏就用哪块屏；屏幕已被移除（如拔掉外接显示器）时回退主屏。
        /// </summary>
        private static Rectangle GetWorkAreaFor(int left, int top, int width, int height)
        {
            try
            {
                if (width < 1) width = 1;
                if (height < 1) height = 1;

                Screen screen = Screen.FromRectangle(new Rectangle(left, top, width, height));
                if (screen != null) return screen.WorkingArea;
            }
            catch
            {
                // 探测失败就用主屏兜底
            }

            return Screen.PrimaryScreen != null
                ? Screen.PrimaryScreen.WorkingArea
                : new Rectangle(0, 0, 1024, 768);
        }

        /// <summary>
        /// 保证窗口至少有一部分落在某块屏幕的可视区内。
        /// 拔掉外接显示器、或位置被写到奇怪的坐标后，窗口会整个跑到屏幕外，
        /// 表现同样是"窗口不见了"。这里检测可见比例不足就把它拉回工作区。
        /// </summary>
        private static void EnsureOnScreen()
        {
            if (_form == null) return;

            // 最大化状态同样要校验落点：个别宿主（分屏 / 混合 DPI）不按
            // MaximizedBounds 摆窗口，表现为最大化后窗口消失或没铺满屏幕
            if (_form.WindowState == FormWindowState.Maximized)
            {
                EnsureMaximizedOnScreen();
                return;
            }

            if (_form.WindowState != FormWindowState.Normal) return;

            try
            {
                if (!_form.IsHandleCreated) return;

                IntPtr hwnd = _form.Handle;

                // Win32 物理口径：与窗口同一坐标系，副屏缩放比不同也不会误判
                if (NativeWindow.IsOnScreen(hwnd, 200, 120)) return;

                _suppressCapture = true;
                try { NativeWindow.ClampIntoWorkArea(hwnd); }
                finally { _suppressCapture = false; }

                BlmLog.Write("窗口已移回可视区域（原位置不在屏幕内）");
            }
            catch
            {
                // 位置校正失败不影响窗口显示本身
            }
        }

        private static void RestoreWindowBounds()
        {
            if (_form == null || _config == null) return;
            try
            {
                if (_config.WindowWidth > 0 && _config.WindowHeight > 0 &&
                    _config.WindowLeft != -1 && _config.WindowTop != -1)
                {
                    // 用"上次窗口所在的那块屏幕"来钳制，而不是硬钉在主屏——
                    // 否则双屏用户把面板拖到副屏后，下次启动会被强行拉回主屏。
                    Rectangle area = GetWorkAreaFor(
                        _config.WindowLeft, _config.WindowTop,
                        _config.WindowWidth, _config.WindowHeight);

                    int width = Math.Max(_form.MinimumSize.Width, Math.Min(_config.WindowWidth, area.Width));
                    int height = Math.Max(_form.MinimumSize.Height, Math.Min(_config.WindowHeight, area.Height));
                    int left = Math.Max(area.Left, Math.Min(_config.WindowLeft, area.Right - width));
                    int top = Math.Max(area.Top, Math.Min(_config.WindowTop, area.Bottom - height));

                    _form.SetBounds(left, top, width, height);
                }
                else
                {
                    // 首次运行：默认停靠在屏幕右侧
                    Rectangle area = Screen.PrimaryScreen != null
                        ? Screen.PrimaryScreen.WorkingArea
                        : new Rectangle(0, 0, 1024, 768);

                    int width = 440;
                    int height = Math.Min(680, area.Height - 80);
                    _form.SetBounds(
                        area.Right - width - 24,
                        area.Top + 40,
                        width,
                        height);
                }
            }
            catch (System.Exception ex)
            {
                BlmLog.Write("恢复窗口位置失败", ex);
            }
        }

        private static void HookIdle()
        {
            if (_idleHooked) return;

            try
            {
                // 这里必须写全名：本文件同时 using 了 System.Windows.Forms，
                // 会有另一个 Application（WinForms 的），不写全名编译器无法分辨。
                Autodesk.AutoCAD.ApplicationServices.Application.Idle += new EventHandler(OnApplicationIdle);
                _idleHooked = true;
            }
            catch
            {
                // 极老的 AutoCAD 若没有 Idle 事件，缩略图会在列表刷新时同步生成
            }
        }

        private static void OnApplicationIdle(object sender, EventArgs e)
        {
            if (_control == null) return;

            try
            {
                // 窗口看不见（已关闭 / 已最小化）时一次都不进处理逻辑。
                // 这里的 SuspendThumbnailWork 是兜底：万一某个改变可见性的入口
                // 漏了同步，空闲循环也会在第一时间把后台工作停掉，
                // 不会让 AutoCAD 在用户看不见面板时还一直忙着生成缩略图。
                if (_form != null &&
                    (!_form.Visible || _form.WindowState == FormWindowState.Minimized))
                {
                    _control.SuspendThumbnailWork();
                    return;
                }

                // 用户从任务栏把最小化的面板点回来时，走的是系统自己的还原路径，
                // 不会经过 ShowWindow()（那里面才会 Resume），缩略图服务就一直停在
                // Suspend 状态 —— 列表永远停在"DWG"占位图，只有重开 BLOCKLIB 才救得回来。
                // 这里补一次"看得见就恢复"；只在真的被暂停时才调用，不会每帧重复。
                if (_control.ThumbnailWorkSuspended) _control.ResumeThumbnailWork();

                _control.ProcessPendingThumbnails();
            }
            catch
            {
                // 空闲处理里的异常绝不能冒到 AutoCAD 主循环
            }
        }

        /// <summary>插件卸载 / AutoCAD 退出时释放资源。</summary>
        public static void Shutdown()
        {
            if (_displayEventsHooked)
            {
                try
                {
                    Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                }
                catch { }
                _displayEventsHooked = false;
            }

            try
            {
                if (_idleHooked)
                {
                    Autodesk.AutoCAD.ApplicationServices.Application.Idle -= new EventHandler(OnApplicationIdle);
                    _idleHooked = false;
                }
            }
            catch { }

            if (_config != null)
            {
                CaptureBounds();
                try { _config.Save(); } catch { }
            }

            if (_form != null)
            {
                try { _form.Visible = false; } catch { }
                try { _form.Dispose(); } catch { }
                _form = null;
            }

            if (_control != null)
            {
                try { _control.Dispose(); } catch { }
                _control = null;
            }
        }
    }
}
