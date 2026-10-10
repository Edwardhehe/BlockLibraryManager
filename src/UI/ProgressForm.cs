using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace BlockLibraryManager
{
    /// <summary>
    /// 加载 / 扫描进度窗体。
    ///
    /// 用法：
    ///   ProgressForm.Run(this, "正在加载图库", "正在扫描图块文件…",
    ///       delegate (ProgressForm.ProgressReport report, ProgressForm.CancelQuery isCancelled)
    ///       {
    ///           // 这里跑在后台线程上：只能做纯文件 / 数据操作，绝不能碰 AutoCAD API
    ///           for (...) { if (isCancelled()) return; report(percent, text); }
    ///       });
    ///
    /// 设计要点：
    ///   1. 不用 BackgroundWorker —— net35(CAD2010) 到 net8(CAD2025+) 版本差异较多，
    ///      直接用 Thread + Control.BeginInvoke 最稳，也不依赖任何额外程序集；
    ///   2. 工作线程通过 BeginInvoke 更新界面，绝不直接跨线程改控件；
    ///   3. 工作线程结束后主动 Close()，因此 ShowDialog 会正常返回；
    ///   4. 窗体没有关闭按钮，只能通过“取消”，避免工作线程还在跑时窗体被销毁。
    /// </summary>
    internal sealed class ProgressForm : Form
    {
        /// <summary>汇报进度：percent = 0~100，text = 当前正在处理的目录（可为 null）。</summary>
        public delegate void ProgressReport(int percent, string text);

        /// <summary>工作线程询问用户是否点了“取消”。</summary>
        public delegate bool CancelQuery();

        private Label _lblInfo;
        private Label _lblDetail;
        private Label _lblPercent;
        private ProgressBar _bar;
        private Button _btnCancel;

        private Action<ProgressReport, CancelQuery> _work;

        private volatile bool _cancelled;
        private volatile bool _finished;
        private int _lastPercent = -1;
        private int _lastTick;

        /// <summary>延迟显示计时器：小图库几百毫秒就扫完，完全不该闪一下进度窗。</summary>
        private System.Windows.Forms.Timer _delayTimer;

        /// <summary>用户是否点了“取消”。</summary>
        public bool Cancelled
        {
            get { return _cancelled; }
        }

        /// <summary>工作线程抛出的异常（没有则为 null）。</summary>
        public Exception Error { get; private set; }

        private ProgressForm(string title, string info)
        {
            Text = title;
            Font = SystemFonts.MessageBoxFont;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ControlBox = false;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(480, 152);
            BackColor = SystemColors.Control;

            _lblInfo = new Label();
            _lblInfo.Text = info;
            _lblInfo.AutoSize = true;
            _lblInfo.Location = new Point(16, 16);

            _bar = new ProgressBar();
            _bar.Location = new Point(16, 42);
            _bar.Size = new Size(448, 22);
            _bar.Minimum = 0;
            _bar.Maximum = 100;
            _bar.Value = 0;
            _bar.Style = ProgressBarStyle.Continuous;

            _lblDetail = new Label();
            _lblDetail.Text = "准备中…";
            _lblDetail.Location = new Point(16, 74);
            _lblDetail.Size = new Size(370, 20);
            _lblDetail.AutoEllipsis = true;
            _lblDetail.TextAlign = ContentAlignment.MiddleLeft;

            _lblPercent = new Label();
            _lblPercent.Text = "0%";
            _lblPercent.Location = new Point(396, 74);
            _lblPercent.Size = new Size(68, 20);
            _lblPercent.TextAlign = ContentAlignment.MiddleRight;

            _btnCancel = new Button();
            _btnCancel.Text = "取消";
            _btnCancel.Size = new Size(92, 28);
            _btnCancel.Location = new Point(372, 108);
            _btnCancel.Click += new EventHandler(OnCancelClick);

            Controls.Add(_lblInfo);
            Controls.Add(_bar);
            Controls.Add(_lblDetail);
            Controls.Add(_lblPercent);
            Controls.Add(_btnCancel);
        }

        /// <summary>显示进度窗体并在后台线程执行 work，work 结束后自动关闭并返回。</summary>
        public static ProgressForm Run(IWin32Window owner, string title, string info,
            Action<ProgressReport, CancelQuery> work)
        {
            return Run(owner, title, info, 0, work);
        }

        /// <summary>
        /// 显示进度窗体并在后台线程执行 work。
        /// </summary>
        /// <param name="delayMs">
        /// 延迟多少毫秒才真正显示出来。扫描很快时窗体一直是透明的，用户感知不到；
        /// 只有在确实慢（超过这个时间还没干完）时才浮出来。默认 0 = 立即显示。
        /// </param>
        public static ProgressForm Run(IWin32Window owner, string title, string info,
            int delayMs, Action<ProgressReport, CancelQuery> work)
        {
            ProgressForm form = new ProgressForm(title, info);
            form._work = work;

            if (delayMs > 0)
            {
                form.Opacity = 0;                 // 先隐形，超时后再现身
                form._delayTimer = new System.Windows.Forms.Timer();
                form._delayTimer.Interval = delayMs;
                form._delayTimer.Tick += new EventHandler(form.OnDelayTick);
                form._delayTimer.Start();
            }

            // Shown 事件里再起线程：此刻窗体句柄已创建，BeginInvoke 一定可用
            form.Shown += new EventHandler(form.OnShown);

            try
            {
                form.ShowDialog(owner);
            }
            catch (System.Exception ex)
            {
                BlmLog.Write("ProgressForm.ShowDialog 失败: " + ex.Message);
            }

            return form;
        }

        private void OnDelayTick(object sender, EventArgs e)
        {
            try
            {
                if (_delayTimer != null)
                {
                    _delayTimer.Stop();
                    _delayTimer.Dispose();
                    _delayTimer = null;
                }

                // 已经干完就不用现身了
                if (!_finished) Opacity = 1;
            }
            catch
            {
                // 忽略
            }
        }

        private void OnShown(object sender, EventArgs e)
        {
            Thread thread = new Thread(new ThreadStart(WorkerMain));
            thread.IsBackground = true;
            thread.Name = "BlmScan";
            try { thread.Start(); }
            catch (System.Exception ex)
            {
                BlmLog.Write("启动扫描线程失败: " + ex.Message);
                _finished = true;
                Close();
            }
        }

        private void WorkerMain()
        {
            try
            {
                if (_work != null)
                    _work(new ProgressReport(Report), new CancelQuery(IsCancelled));
            }
            catch (System.Exception ex)
            {
                Error = ex;
                BlmLog.Write("图库扫描异常: " + ex.Message);
            }
            finally
            {
                _finished = true;
                CloseForm();
            }
        }

        private bool IsCancelled()
        {
            return _cancelled;
        }

        private void Report(int percent, string text)
        {
            if (_finished || _cancelled) return;

            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;

            // 节流：百分比没变且距离上次刷新不足 120ms 就跳过，避免消息洪水
            int tick = Environment.TickCount;
            if (percent == _lastPercent && _lastTick != 0 && (tick - _lastTick) < 120) return;

            _lastPercent = percent;
            _lastTick = tick;

            try
            {
                if (!IsHandleCreated || IsDisposed) return;
                BeginInvoke(new MethodInvoker(delegate { ApplyReport(percent, text); }));
            }
            catch
            {
                // 窗体正在关闭，忽略
            }
        }

        private void ApplyReport(int percent, string text)
        {
            try
            {
                if (IsDisposed) return;
                _bar.Value = percent;
                _lblPercent.Text = percent + "%";
                if (!string.IsNullOrEmpty(text)) _lblDetail.Text = text;
            }
            catch
            {
                // 忽略界面刷新异常
            }
        }

        /// <summary>停掉延迟显示计时器（只能在界面线程调用）。</summary>
        private void StopDelayTimer()
        {
            try
            {
                if (_delayTimer != null)
                {
                    _delayTimer.Stop();
                    _delayTimer.Dispose();
                    _delayTimer = null;
                }
            }
            catch
            {
                // 忽略
            }
        }

        private void OnCancelClick(object sender, EventArgs e)
        {
            Opacity = 1;                 // 点了取消就把窗体露出来，让"正在取消…"可见
            _cancelled = true;
            _btnCancel.Enabled = false;
            _btnCancel.Text = "正在取消…";
            _lblInfo.Text = "正在取消，请稍候…";
        }

        private void CloseForm()
        {
            try
            {
                if (!IsHandleCreated || IsDisposed) return;
                BeginInvoke(new MethodInvoker(delegate
                {
                    try
                    {
                        StopDelayTimer();
                        DialogResult = _cancelled ? DialogResult.Cancel : DialogResult.OK;
                        Close();
                    }
                    catch
                    {
                        // 已经关了就算了
                    }
                }));
            }
            catch
            {
                // 忽略
            }
        }
    }
}
