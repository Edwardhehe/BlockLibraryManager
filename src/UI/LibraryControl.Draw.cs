using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BlockLibraryManager
{
    /// <summary>
    /// 图块列表的"自绘 + 按需取图"层。
    ///
    /// 为什么改成自绘（OwnerDraw）：
    ///   原来每一项都要往 ImageList 里塞一张图，打开一个有 2000 个图块的分类，
    ///   就得同步做出 2000 张缩略图塞进 ImageList —— 这是刷新列表最慢的一步，
    ///   而且 ImageList 没有"原地替换"的方法，缩略图陆续到位时只能
    ///   RemoveByKey + Add，一删一加会让后面所有项的索引整体错位。
    ///   改成自绘之后：
    ///     · ImageList 只留 1 张透明占位图，纯粹用来给列表撑出图标尺寸；
    ///     · 每一项的缩略图在**要被画出来的那一刻**才从缓存里取；
    ///     · 屏幕上没显示的项，永远不会被取图、不会被解码 ——
    ///       这就是 WinForms 版的"UI 虚拟化 + 只为可见项加载图片"。
    ///   角标（动态块闪电 / 可见性 ◀ ▶）也从"烤进位图"改成"画的时候叠上去"，
    ///   省掉每个动态块一次全尺寸 Bitmap 拷贝。
    ///
    /// 再配合 PrioritizeVisibleThumbnails()：后台队列会跟着视口走，
    /// 用户滚到哪儿，哪一段就优先出图（数据虚拟化）。
    /// </summary>
    public partial class LibraryControl : UserControl
    {
        // ---------------- Win32 消息：算可见区间 ----------------

        private const int LVM_FIRST = 0x1000;
        private const int LVM_GETTOPINDEX = LVM_FIRST + 39;      // 0x1027
        private const int LVM_GETCOUNTPERPAGE = LVM_FIRST + 40;  // 0x1028

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        // ---------------- 目录树节点自绘 ----------------

        /// <summary>
        /// 目录树节点文字自绘（v1.51.13）。
        ///
        /// 为什么要自绘：模态对话框（设置 / 关于）打开时，WinForms 会把面板窗口
        /// **禁用**；系统的 TreeView 通用控件在窗口被禁用后重绘时，会用默认的
        /// 系统窗口色（白）画节点文字的底 —— 深色主题下就是用户截图里那一片片
        /// 白斑。TVM_SETBKCOLOR 设的深色底对这段"禁用态重绘"不生效，无解。
        ///
        /// 把文字抢过来自己画：底色 / 文字色 / 选中高亮 / 悬停全部走主题，
        /// 无论窗口是否被禁用、是否失活，画出来的永远是主题色。
        /// 展开 +/- 与连接线仍由系统画（颜色走 ApplyTheme 里的 LineColor）。
        /// </summary>
        private void OnTreeDrawNode(object sender, DrawTreeNodeEventArgs e)
        {
            if (e.Node == null) return;

            UiTheme t = CurrentTheme;

            bool selected = (e.State & TreeNodeStates.Selected) != 0;
            bool hot = (e.State & TreeNodeStates.Hot) != 0;

            // 底色：选中 > 悬停 > 常规；文字同理。全部取自主题，深浅主题都成立。
            Color back = t.ListBack;
            Color fore = t.BodyText;
            if (hot) { back = t.HeaderBack; fore = t.BodyText; }
            if (selected) { back = t.ChromeBack; fore = t.HeaderText; }

            // 面板被模态窗口禁用时把文字稍微调暗一点，让"不可交互"有视觉反馈，
            // 同时也避免与"正常态"看起来完全一样。
            if (!_tree.Enabled) fore = t.HintText;

            if (e.Bounds.Width > 0 && e.Bounds.Height > 0)
            {
                using (SolidBrush b = new SolidBrush(back))
                    e.Graphics.FillRectangle(b, e.Bounds);
            }

            if (!string.IsNullOrEmpty(e.Node.Text) && e.Bounds.Width > 3)
            {
                Font font = e.Node.NodeFont ?? _tree.Font;
                Rectangle r = new Rectangle(
                    e.Bounds.X + 1, e.Bounds.Y, e.Bounds.Width - 2, e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, e.Node.Text, font, r, fore, back,
                    TextFormatFlags.VerticalCenter |
                    TextFormatFlags.SingleLine |
                    TextFormatFlags.NoPrefix |
                    TextFormatFlags.EndEllipsis);
            }

            // 注意：这里绝不设 e.DrawDefault = true —— 那会把节点交回系统绘制，
            // 白底问题就会回来。
        }

        /// <summary>ImageList 里那张透明占位图的键名（只为撑尺寸，永远不会被画出来）。</summary>
        private const string PlaceholderImageKey = "__blm_placeholder__";

        /// <summary>上一次调整队列优先级时的"可见区间"。区间没变就不重排队列。-1 = 还没算过。</summary>
        private int _lastTopIndex = -1;

        /// <summary>上一次算出来的"一屏能放几项"，同上用于判断是否要重排队列。</summary>
        private int _lastPerPage = -1;

        /// <summary>有缩略图到位、需要重绘列表。攒着，一帧只 Invalidate 一次。</summary>
        private bool _listDirty;

        // ---------------- 初始化 ----------------

        /// <summary>
        /// 打开 ListView 的双缓冲。自绘时如果不开，滚动会明显闪。
        /// DoubleBuffered 是 protected 属性，只能反射设（这是 WinForms 的标准做法）。
        /// </summary>
        private static void SetDoubleBuffered(Control control, bool value)
        {
            try
            {
                System.Reflection.PropertyInfo pi = typeof(Control).GetProperty(
                    "DoubleBuffered",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic);

                if (pi != null) pi.SetValue(control, value, null);
            }
            catch
            {
                // 设不上双缓冲只是会闪一点，不影响功能
            }
        }

        /// <summary>
        /// 确保 ImageList 里有一张占位图。它唯一的作用是让 ListView 按
        /// ThumbSize 给每项留出图标空间（ItemBoundsPortion.Icon 才能算出正确矩形），
        /// 真正的图由自绘画上去。
        /// </summary>
        private void EnsurePlaceholderImage()
        {
            AddPlaceholderTo(_imageList);
            AddPlaceholderTo(_smallImageList);      // 详情 / 列表视图那份（v1.51.12）
        }

        private static void AddPlaceholderTo(ImageList list)
        {
            if (list == null) return;

            try
            {
                if (list.Images.ContainsKey(PlaceholderImageKey)) return;

                int size = list.ImageSize.Width;
                if (size < 8) size = 8;

                // 注意：这里**不能** Dispose 这张图。ImageList 会持有原对象引用，
                // 句柄延后创建时还要用它来生成原生位图（早释放会抛"参数无效"）。
                Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
                try { list.Images.Add(PlaceholderImageKey, bmp); }
                catch { try { bmp.Dispose(); } catch { } throw; }
            }
            catch
            {
                // 占位图加不进去最多是图标尺寸不对，不影响功能
            }
        }

        // ---------------- 自绘入口 ----------------

        private void OnListDrawColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            // 表头交给系统画，自己画既费事又容易跟主题对不上
            e.DrawDefault = true;
        }

        private void OnListDrawItem(object sender, DrawListViewItemEventArgs e)
        {
            // 详情视图下 DrawItem 与 DrawSubItem 会同时触发（底层 Win32 的已知行为）：
            // 鼠标划过某一行时，DrawItem 会在没有 DrawSubItem 陪同的情况下再画一次，
            // 把 DrawSubItem 画的内容盖掉。微软官方建议的做法就是——
            // 详情视图一律只在 DrawSubItem 里画，DrawItem 直接不参与。
            if (_list != null && _list.View == View.Details) return;

            try
            {
                DrawGalleryItem(e.Graphics, e.Item, e.Bounds);
            }
            catch
            {
                // 单项画失败绝不能冒出去打断整个列表的重绘
            }
        }

        private void OnListDrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            try
            {
                // 第 0 列负责把整行的背景铺满（含其它列），
                // 其余列只画自己那一格 —— 这样即使系统只重绘个别格子也不会露白。
                Rectangle fill = (e.ColumnIndex == 0) ? e.Item.Bounds : e.Bounds;
                DrawDetailsRow(e.Graphics, e.Item, e.SubItem, fill, e.Bounds, e.ColumnIndex);
            }
            catch
            {
                // 同上
            }
        }

        // ---------------- 图标视图 / 列表视图 ----------------

        /// <summary>画一个大图标视图（View.LargeIcon）或列表视图（View.List）的项。</summary>
        private void DrawGalleryItem(Graphics g, ListViewItem lvi, Rectangle itemBounds)
        {
            if (_list == null || lvi == null) return;
            if (itemBounds.Width <= 0 || itemBounds.Height <= 0) return;

            bool selected = lvi.Selected;
            Color back = selected ? SystemColors.Highlight : _list.BackColor;
            Color fore = selected ? SystemColors.HighlightText : _list.ForeColor;

            using (SolidBrush brush = new SolidBrush(back))
                g.FillRectangle(brush, itemBounds);

            Rectangle iconRect = GetIconRect(lvi, itemBounds);

            // 只有真正要被画出来的项才会走到这一步 —— 屏幕外的项永远取不到图
            Image img = GetItemImage(lvi);

            if (img != null) DrawImageFitted(g, img, iconRect);

            // 角标一律贴"实际画出来的缩略图矩形"定位（与点击判定共用 GetDrawnImageRect）
            Rectangle drawnRect = GetDrawnImageRect(lvi, itemBounds, iconRect);
            DrawItemOverlays(g, lvi.Tag as BlockItem, iconRect, drawnRect);

            // 文字：大图标视图在图下方居中；列表视图（View.List）在图右侧
            Rectangle textRect;
            TextFormatFlags flags;

            if (_list.View == View.List)
            {
                textRect = new Rectangle(
                    iconRect.Right + 3, itemBounds.Y,
                    Math.Max(0, itemBounds.Right - iconRect.Right - 3), itemBounds.Height);
                flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                        TextFormatFlags.SingleLine;
            }
            else
            {
                textRect = new Rectangle(
                    itemBounds.X, iconRect.Bottom + 2,
                    itemBounds.Width, Math.Max(0, itemBounds.Bottom - iconRect.Bottom - 2));
                flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.Top |
                        TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis |
                        TextFormatFlags.NoPrefix;
            }

            if (textRect.Width > 0 && textRect.Height > 0)
                TextRenderer.DrawText(g, lvi.Text, _list.Font, textRect, fore, back, flags);

            if (selected && lvi.Focused)
                ControlPaint.DrawFocusRectangle(g, itemBounds);
        }

        // ---------------- 详情视图 ----------------

        private void DrawDetailsRow(Graphics g, ListViewItem lvi,
            ListViewItem.ListViewSubItem sub, Rectangle fill, Rectangle cell, int columnIndex)
        {
            if (_list == null || lvi == null) return;

            bool selected = lvi.Selected;
            Color back = selected ? SystemColors.Highlight : _list.BackColor;
            Color fore = selected ? SystemColors.HighlightText : _list.ForeColor;

            using (SolidBrush brush = new SolidBrush(back))
                g.FillRectangle(brush, fill);

            Rectangle textRect = cell;

            // 第 0 列先画小图标，文字从图标右边开始
            if (columnIndex == 0)
            {
                Rectangle iconRect = GetIconRect(lvi, lvi.Bounds);
                if (iconRect.Width >= 6 && iconRect.Height >= 6)
                {
                    Image img = GetItemImage(lvi);
                    if (img != null) DrawImageFitted(g, img, iconRect);
                }

                int shift = iconRect.Width + 3;
                if (shift < cell.Width)
                    textRect = new Rectangle(cell.X + shift, cell.Y, cell.Width - shift, cell.Height);
            }

            if (textRect.Width > 0 && textRect.Height > 0)
            {
                TextRenderer.DrawText(g, sub == null ? string.Empty : sub.Text,
                    _list.Font, textRect, fore, back,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                    TextFormatFlags.SingleLine);
            }

            if (selected && lvi.Focused && columnIndex == 0)
                ControlPaint.DrawFocusRectangle(g, lvi.Bounds);
        }

        // ---------------- 取图与叠加层 ----------------

        /// <summary>
        /// 取该项要显示的缩略图。这是整套优化的落点：
        /// 系统只为**可见项**发绘制事件，所以这里每秒最多被调用几十次，
        /// 而不是以前那样一进分类就被调用几千次。
        /// </summary>
        private Image GetItemImage(ListViewItem lvi)
        {
            BlockItem item = lvi == null ? null : lvi.Tag as BlockItem;
            if (item == null || _thumbnails == null) return null;

            // GetThumbnail 只查内存缓存，不碰磁盘；没就绪时返回占位图
            return _thumbnails.GetThumbnail(item);
        }

        /// <summary>该项的图标矩形。用原生 Icon 部分，保证与点击命中的判定完全一致。</summary>
        private static Rectangle GetIconRect(ListViewItem lvi, Rectangle itemBounds)
        {
            try
            {
                Rectangle r = lvi.GetBounds(ItemBoundsPortion.Icon);
                if (r.Width >= 8 && r.Height >= 8) return r;
            }
            catch
            {
                // 拿不到就走下面的兜底估算
            }

            int side = Math.Min(itemBounds.Width, itemBounds.Height * 3 / 4);
            if (side < 8) side = Math.Max(8, Math.Min(itemBounds.Width, itemBounds.Height));

            return new Rectangle(
                itemBounds.X + (itemBounds.Width - side) / 2,
                itemBounds.Y, side, side);
        }

        /// <summary>
        /// 该项缩略图"实际画出来"的矩形（等比缩放居中后的结果）。
        /// **绘制角标与点击判定必须共用这一个方法**——两边各算一次、基准不一致
        /// 就会出现"按钮看得见却点不中 / 点中的地方没按钮"。没图（含占位图取不到）
        /// 时退回 iconRect，保证角标位置不会跳。
        /// </summary>
        private Rectangle GetDrawnImageRect(ListViewItem lvi, Rectangle itemBounds, Rectangle iconRect)
        {
            try
            {
                if (iconRect.Width <= 0 || iconRect.Height <= 0)
                    iconRect = GetIconRect(lvi, itemBounds);

                Image img = GetItemImage(lvi);
                if (img == null) return iconRect;

                Rectangle fitted = GetFittedRect(img, iconRect);
                return (fitted.Width > 0 && fitted.Height > 0) ? fitted : iconRect;
            }
            catch
            {
                return iconRect;
            }
        }

        /// <summary>
        /// 计算把图等比缩放居中放进 rect 后"实际绘制"的矩形（不放大）。
        /// 角标定位与绘制共用这一个算法，保证画在哪、贴哪是一致的。
        /// </summary>
        private static Rectangle GetFittedRect(Image img, Rectangle rect)
        {
            if (img == null || img.Width <= 0 || img.Height <= 0) return Rectangle.Empty;
            if (rect.Width <= 0 || rect.Height <= 0) return Rectangle.Empty;

            double scale = Math.Min((double)rect.Width / img.Width, (double)rect.Height / img.Height);
            if (scale > 1.0) scale = 1.0;          // 小图不放大，避免糊

            int w = Math.Max(1, (int)Math.Round(img.Width * scale));
            int h = Math.Max(1, (int)Math.Round(img.Height * scale));

            return new Rectangle(rect.X + (rect.Width - w) / 2,
                                 rect.Y + (rect.Height - h) / 2, w, h);
        }

        /// <summary>把图等比画进矩形（居中、不放大）。</summary>
        private static void DrawImageFitted(Graphics g, Image img, Rectangle rect)
        {
            Rectangle target = GetFittedRect(img, rect);
            if (target.Width <= 0 || target.Height <= 0) return;

            InterpolationMode old = g.InterpolationMode;
            try
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(img, target);
            }
            finally
            {
                g.InterpolationMode = old;
            }
        }

        /// <summary>
        /// 叠加动态块闪电角标与可见性 ◀ ▶ 按钮。
        /// 以前这些是"烤"进一张复制出来的 Bitmap 里的（每个动态块一份拷贝），
        /// 现在改成绘制期叠加：零拷贝、且缩略图更新时不用重建位图，
        /// **缩略图缓存文件 / 缓存位图保持原样，不含任何角标**。
        /// imgRect 是图标格子（可见性 ◀ ▶ 的画与点击判定都用它）；
        /// imageRect 是缩略图等比缩放居中后"实际画出来"的矩形（闪电贴它定位）。
        /// </summary>
        private void DrawItemOverlays(Graphics g, BlockItem item, Rectangle imgRect, Rectangle imageRect)
        {
            if (item == null || _thumbnails == null) return;
            if (!_thumbnails.IsDynamicBlock(item)) return;

            try
            {
                // 闪电叠加在缩略图自身右上角（不是格子右上角——
                // 横向长图上下留白时按格子定位会飘到图外，v1.51.9）
                if (imageRect.Width > 0 && imageRect.Height > 0)
                    DrawDynamicBadge(g, imageRect);

                List<string> states = _thumbnails.GetVisibilityStates(item);
                if (states != null && states.Count >= 2)
                {
                    // 三角按钮也贴着"实际画出来的缩略图"底部（与闪电同一基准），
                    // 且用与点击判定完全相同的算法算位置，画和点才对得上（v1.51.9）。
                    // 之前按格子定位：横向长图上下留白 → 按钮画在图下方的空白里，
                    // 看起来像"没实现"。
                    Rectangle prev = GetPrevTriangleRect(imageRect.Width, imageRect.Height);
                    prev.Offset(imageRect.Location);
                    Rectangle next = GetNextTriangleRect(imageRect.Width, imageRect.Height);
                    next.Offset(imageRect.Location);

                    DrawTriangleButton(g, prev, true);
                    DrawTriangleButton(g, next, false);
                }
            }
            catch
            {
                // 角标画不出来不影响缩略图本身
            }
        }

        /// <summary>右上角的手绘矢量闪电（细长锐利造型，白色描边保证深色图上也醒目）。
        /// 坐标基于"实际画出来的缩略图矩形"（imageRect），叠加在上、不进缓存。</summary>
        private static void DrawDynamicBadge(Graphics g, Rectangle imageRect)
        {
            float s = Math.Max(8f, Math.Min(imageRect.Width, imageRect.Height) / 8.4f);

            // 缩略图太小就不叠了，免得闪电把图盖没
            if (imageRect.Width < s * 1.8f || imageRect.Height < s * 1.8f) return;

            // 离缩略图上边、右边各留 10px（用户指定，v1.51.9）
            const float margin = 10f;

            float boltW = s * 0.72f;
            float x0 = imageRect.Right - boltW - margin;
            float y0 = imageRect.Top + margin;

            PointF[] bolt = new PointF[]
            {
                new PointF(x0 + 0.90f * boltW, y0),                 // 顶端右上
                new PointF(x0 + 0.08f * boltW, y0 + 0.58f * s),     // 左下斜边
                new PointF(x0 + 0.52f * boltW, y0 + 0.58f * s),     // 中部右折
                new PointF(x0 + 0.34f * boltW, y0 + s),             // 底尖
                new PointF(x0 + 0.94f * boltW, y0 + 0.40f * s),     // 右上斜边
                new PointF(x0 + 0.50f * boltW, y0 + 0.40f * s),     // 中部左折
            };

            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddPolygon(bolt);

                using (Pen halo = new Pen(Color.White, Math.Max(1.2f, s / 12f)))
                {
                    halo.LineJoin = LineJoin.Round;
                    g.DrawPath(halo, path);
                }

                using (SolidBrush red = new SolidBrush(Color.Red))
                    g.FillPath(red, path);
            }
        }

        // ---------------- 数据虚拟化：队列跟着视口走 ----------------

        /// <summary>
        /// 把"当前屏幕上可见的这一段"提到缩略图队列最前面，让看得见的先出图。
        /// 由空闲处理每轮调一次；只有可见区间真的变了才会重排队列，所以开销可以忽略。
        /// </summary>
        private void PrioritizeVisibleThumbnails()
        {
            if (_list == null || _thumbnails == null) return;
            if (_currentBlocks == null || _currentBlocks.Count == 0) return;
            if (!_list.IsHandleCreated) return;

            try
            {
                // 用 long 接再判断：64 位进程里 LRESULT 是 8 字节，
                // 控件为空时返回 -1（0xFFFF...），直接 (int) 强转会抛 OverflowException。
                long topRaw = SendMessage(_list.Handle, LVM_GETTOPINDEX, IntPtr.Zero, IntPtr.Zero).ToInt64();
                long perPageRaw = SendMessage(_list.Handle, LVM_GETCOUNTPERPAGE, IntPtr.Zero, IntPtr.Zero).ToInt64();
                if (topRaw < 0 || perPageRaw <= 0) return;

                int count = _currentBlocks.Count;
                int firstVisible = (int)Math.Min(topRaw, count - 1);
                int perPage = (int)Math.Min(perPageRaw, count);
                if (firstVisible < 0) firstVisible = 0;

                int now = Environment.TickCount;
                bool sweepDue = _thumbnails.PendingCount <= 0 && (now - _lastThumbSweepTick) >= 2000;
                bool rangeChanged = firstVisible != _lastTopIndex || perPage != _lastPerPage;

                // v1.51.12：Application.Idle 每秒能来几十次，而下面要建一个
                // "可见项"列表（几十个百分点成员的堆分配）。区间没变、又不到
                // 查漏时间的话直接早退 —— 以前是无条件每帧都分配这个列表，
                // 连续滚动时等于每一帧分配一次几十项的集合。
                if (!rangeChanged && !sweepDue) return;

                // 多带一屏：往下滚的时候下一屏已经在排队了
                int last = Math.Min(count - 1, firstVisible + perPage);
                if (last < firstVisible) return;

                List<BlockItem> visible = new List<BlockItem>(last - firstVisible + 1);
                for (int i = firstVisible; i <= last; i++) visible.Add(_currentBlocks[i]);

                // ---- 查漏补缺 ----
                // 可见项里若有还没出图的（此前生成失败、缓存存了 null、画面上是"DWG"占位），
                // 自动重新排队重试。只在队列空闲时每 2 秒扫一次，开销可忽略；
                // 服务端有失败计数上限，真坏文件不会陷入无限重试。
                if (sweepDue)
                {
                    _lastThumbSweepTick = now;

                    int requeue = _thumbnails.RequestThumbnails(visible);
                    if (requeue > 0)
                    {
                        // 熔断（v1.49.1）：连续多轮查漏都在重排同一批出不了图的条目时，
                        // 右下角进度条就会每 2 秒重置一轮、看起来永远在闪。
                        // 给自动重排一个轮数上限，到顶不再自动重排；
                        // 真想再试可以右键"刷新"，或等切换目录后自动恢复。
                        _sweepRetryRounds++;
                        if (_sweepRetryRounds <= MaxSweepRetryRounds)
                            BeginThumbRound(requeue);
                    }
                    else
                    {
                        _sweepRetryRounds = 0;   // 这一轮没有待重排的条目，恢复查漏资格
                    }
                }

                // 区间没变就不重排队列
                if (!rangeChanged) return;
                _lastTopIndex = firstVisible;
                _lastPerPage = perPage;

                _thumbnails.Prioritize(visible);
            }
            catch
            {
                // 排不了队不影响出图，只是顺序不理想
            }
        }

        /// <summary>上次"查漏补缺"扫描的时间（Environment.TickCount），用于限频。</summary>
        private int _lastThumbSweepTick = Environment.TickCount - 100000;

        /// <summary>查漏补缺连续重排的轮数（重排的条目一张都没出图时逐轮累加）。</summary>
        private int _sweepRetryRounds;

        /// <summary>自动查漏重排的轮数上限：防止对出不了图的条目反复重排、进度条反复闪。</summary>
        private const int MaxSweepRetryRounds = 3;

        /// <summary>换了分类 / 重建列表后要把上一次的可见区间作废，下次重新算。</summary>
        private void ResetVisibleStamp()
        {
            _lastTopIndex = -1;
            _lastPerPage = -1;
        }

        /// <summary>
        /// 把攒下的重绘请求一次性刷出去。
        /// 缩略图是一张张陆续到位的，如果每张都 Invalidate，
        /// 生成 2000 张就会排队 2000 次重绘，界面会被自己的重绘拖住。
        /// </summary>
        private void FlushListPaint()
        {
            if (!_listDirty) return;
            _listDirty = false;

            if (_list == null || _list.IsDisposed) return;

            try { _list.Invalidate(); }
            catch { }
        }
    }
}
