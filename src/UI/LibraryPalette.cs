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

        public static void Show()
        {
            EnsureCreated();
            if (_form == null) return;

            ShowWindow();
            _form.Activate();
        }

        public static void Hide()
        {
            if (_form == null) return;
            try
            {
                CaptureBounds();
                _form.Hide();
                if (_config != null) _config.Save();
            }
            catch { }
        }

        public static void Toggle()
        {
            EnsureCreated();
            if (_form == null) return;

            if (_form.Visible) Hide();
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
            using (AboutForm form = new AboutForm())
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
                _form.WindowState = FormWindowState.Minimized;
            }
            catch (System.Exception ex)
            {
                BlmLog.Write("最小化窗口失败", ex);
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
            Minimize();

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
                    Document doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                    UnhookDragRestore(doc);
                }
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
                if (!_form.Visible) ShowWindow();
                if (_form.WindowState == FormWindowState.Minimized)
                    _form.WindowState = FormWindowState.Normal;
                _form.Activate();
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
                    _suppressCapture = true;
                    try { _form.WindowState = FormWindowState.Normal; }
                    finally { _suppressCapture = false; }
                }
                else
                {
                    if (_form.WindowState == FormWindowState.Minimized)
                        _form.WindowState = FormWindowState.Normal;

                    // 无边框窗口必须显式给出最大化范围，否则会铺满整屏盖住任务栏
                    _form.SetMaximizedBounds(Screen.FromControl(_form).WorkingArea);

                    _suppressCapture = true;
                    try { _form.WindowState = FormWindowState.Maximized; }
                    finally { _suppressCapture = false; }
                }
            }
            catch (System.Exception ex)
            {
                BlmLog.Write("切换最大化失败", ex);
            }

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
                if (!_form.Visible)
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
                _form.Activate();
            }
            catch (System.Exception ex)
            {
                BlmLog.Write("显示窗口失败", ex);
                try { _form.Show(); } catch { }
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
