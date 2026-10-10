using System;
using System.Collections.Generic;

namespace BlockLibraryManager
{
    /// <summary>
    /// 面板 → 命令 的插入请求桥。
    ///
    /// 为什么需要它：
    /// 图库面板是"非模态"的（AutoCAD PaletteSet），在面板按钮的 Click 事件里直接调用
    /// Editor.Drag / Editor.GetPoint 在很多版本上会失败（常见的报错是 eNotApplicable，
    /// 或者鼠标输入被对话框吃掉）。可靠的做法是把请求暂存起来，然后用
    /// Document.SendStringToExecute 触发一个真正的命令，让 AutoCAD 在命令上下文中
    /// 完成交互式取点与插入。
    ///
    /// 这是 Autodesk 官方示例（ModelessForm / Palette）推荐的模式，2010~2027 全版本通用。
    /// </summary>
    internal static class InsertBridge
    {
        private static readonly List<BlockItem> _items = new List<BlockItem>();

        private static InsertOptions _options;
        private static bool _continuous;
        private static bool _pending;

        /// <summary>暂存一次插入请求。</summary>
        public static void Request(IList<BlockItem> items, InsertOptions options, bool continuous)
        {
            _items.Clear();
            if (items != null) _items.AddRange(items);

            _options = options ?? new InsertOptions();
            _continuous = continuous && _items.Count == 1;
            _pending = _items.Count > 0;
        }

        public static bool HasPending
        {
            get { return _pending && _items.Count > 0; }
        }

        /// <summary>取出请求。取走后自动清空，避免重复插入。</summary>
        public static bool Take(out List<BlockItem> items, out InsertOptions options, out bool continuous)
        {
            items = new List<BlockItem>(_items);
            options = _options ?? new InsertOptions();
            continuous = _continuous;

            _items.Clear();
            _options = null;
            _continuous = false;
            _pending = false;

            return items.Count > 0;
        }

        public static void Clear()
        {
            _items.Clear();
            _options = null;
            _continuous = false;
            _pending = false;
        }
    }
}
