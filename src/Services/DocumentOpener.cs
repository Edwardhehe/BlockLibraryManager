using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;

namespace BlockLibraryManager
{
    /// <summary>
    /// 用当前 CAD 打开一个 DWG 文件（右键菜单"打开该图形文件"）。
    ///
    /// 背景：四家 CAD 的"打开文档"API 形态很不统一，而且同一家不同版本也可能
    /// 只实现其中一部分——中望 2026 专业版上就出现过调用
    /// DocumentCollectionExtension.Open 既不抛异常、也打不开文件的现象
    /// （用户看到的就是"点了没反应"）。因此这里改成**多层回退 + 结果校验**：
    /// 每一层都先试，再按"文档数是否增加 / 是否拿到了目标文档"判定真的成功了没，
    /// 不成功就换下一条通道。
    ///
    /// 通道顺序（越靠前越"官方"）：
    ///   1. DocumentCollectionExtension.Open（静态扩展）—— AutoCAD / 中望 / 浩辰
    ///      BricsCAD 没有扩展类，只有实例方法，故编译期分流
    ///   2. 实例方法 docs.Open(path, false) —— 中望 / BricsCAD 有，浩辰**没有**
    ///   3. 实例方法 docs.AppContextOpenDocument(path) —— 中望 / 浩辰 / BricsCAD 都有
    ///   4. COM：Application.AcadApplication.Documents.Open(path) —— 兜底，
    ///      四家都暴露了 AcadApplication（BricsCAD 若拿不到则自动跳过）
    ///
    /// 2/3/4 全部走反射：某家没这个方法时只是跳过，不会编译失败、不会抛。
    /// </summary>
    internal static class DocumentOpener
    {
        /// <summary>
        /// 尝试打开指定文件。
        /// </summary>
        /// <param name="path">DWG 全路径（调用方已确认文件存在）。</param>
        /// <param name="message">成功时是提示语；失败时是给用户看的错误汇总。</param>
        /// <returns>是否真的打开了。</returns>
        public static bool TryOpen(string path, out string message)
        {
            message = string.Empty;
            List<string> errors = new List<string>();

            DocumentCollection docs = null;
            try
            {
                docs = Application.DocumentManager;
            }
            catch (System.Exception ex)
            {
                message = "取不到文档管理器：" + OneLine(ex.Message);
                return false;
            }

            if (docs == null)
            {
                message = "文档管理器为空，无法打开图形。";
                return false;
            }

            int before = GetCount(docs);

            // ---- 通道 1：静态扩展 Open（四家都能编过，形态不同故分流） ----
            Document doc = null;
            try
            {
#if BLM_BRICSCAD
                doc = docs.Open(path, false);
#else
                doc = DocumentCollectionExtension.Open(docs, path, false);
#endif
            }
            catch (System.Exception ex)
            {
                errors.Add("扩展 Open：" + OneLine(ex.Message));
            }
            if (Confirm(docs, path, before, ref doc))
            {
                Activate(docs, doc);
                message = "已打开：" + path;
                BlmLog.Write("打开图形文件成功（通道1 扩展Open）：" + path);
                return true;
            }
            if (doc != null) errors.Add("扩展 Open：调用返回但文档未打开");

            // ---- 通道 2：实例方法 Open（浩辰没有，反射找不到就跳过） ----
            try
            {
                object result = InvokeInstance(docs, "Open", new object[] { path, false });
                doc = result as Document;
                if (Confirm(docs, path, before, ref doc))
                {
                    Activate(docs, doc);
                    message = "已打开：" + path;
                    BlmLog.Write("打开图形文件成功（通道2 实例Open）：" + path);
                    return true;
                }
                if (result != null) errors.Add("实例 Open：调用返回但文档未打开");
            }
            catch (System.Exception ex)
            {
                errors.Add("实例 Open：" + OneLine(ex.Message));
            }

            // ---- 通道 3：AppContextOpenDocument（在应用上下文里打开） ----
            try
            {
                object result = InvokeInstance(docs, "AppContextOpenDocument", new object[] { path });
                doc = result as Document;
                if (Confirm(docs, path, before, ref doc))
                {
                    Activate(docs, doc);
                    message = "已打开：" + path;
                    BlmLog.Write("打开图形文件成功（通道3 AppContextOpenDocument）：" + path);
                    return true;
                }
                if (result != null) errors.Add("AppContextOpenDocument：调用返回但文档未打开");
            }
            catch (System.Exception ex)
            {
                errors.Add("AppContextOpenDocument：" + OneLine(ex.Message));
            }

            // ---- 通道 4：COM（AcadApplication.Documents.Open） ----
            try
            {
                if (TryOpenByCom(path))
                {
                    Document found = FindByName(docs, path);
                    if (found != null || GetCount(docs) > before)
                    {
                        Activate(docs, found);
                        message = "已打开：" + path;
                        BlmLog.Write("打开图形文件成功（通道4 COM）：" + path);
                        return true;
                    }
                }
            }
            catch (System.Exception ex)
            {
                errors.Add("COM Open：" + OneLine(ex.Message));
            }

            // ---- 全部失败 ----
            string detail = errors.Count > 0
                ? string.Join("；", errors.ToArray())
                : "四条通道均静默返回，未抛出异常";
            message = detail;
            BlmLog.Write("打开图形文件失败：" + path + " —— " + detail);
            return false;
        }

        /// <summary>
        /// 判定"是否真的打开了"：拿到目标文档，或文档数确实增加了。
        /// 只检查这两项——中望上出现过"调用成功返回、实际什么都没发生"的情况，
        /// 单看返回值是不够的。
        /// </summary>
        private static bool Confirm(DocumentCollection docs, string path, int before, ref Document doc)
        {
            try
            {
                if (doc != null) return true;

                // 有的 CAD（中望）打开文档可能是异步的：调用刚返回时文档还没挂上。
                // 先查一次，查不到就等 150ms 再查一次——避免"其实会成功"却被判失败，
                // 导致下一层通道把同一个文件重复打开一份。
                for (int pass = 0; pass < 2; pass++)
                {
                    Document found = FindByName(docs, path);
                    if (found != null)
                    {
                        doc = found;
                        return true;
                    }

                    if (GetCount(docs) > before) return true;

                    if (pass == 0) System.Threading.Thread.Sleep(150);
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>按文件名在已打开文档里找（大小写不敏感）。</summary>
        private static Document FindByName(DocumentCollection docs, string path)
        {
            try
            {
                IEnumerable e = docs as IEnumerable;
                if (e == null) return null;

                foreach (object o in e)
                {
                    Document d = o as Document;
                    if (d == null) continue;

                    string name = null;
                    try { name = d.Name; } catch { }
                    if (string.IsNullOrEmpty(name)) continue;

                    if (string.Equals(name, path, StringComparison.OrdinalIgnoreCase)) return d;
                    if (string.Equals(System.IO.Path.GetFileName(name),
                                      System.IO.Path.GetFileName(path),
                                      StringComparison.OrdinalIgnoreCase)) return d;
                }
            }
            catch
            {
                // 遍历失败就当没找到
            }
            return null;
        }

        private static int GetCount(DocumentCollection docs)
        {
            try
            {
                PropertyInfo p = docs.GetType().GetProperty("Count",
                    BindingFlags.Public | BindingFlags.Instance);
                if (p == null) return -1;
                return Convert.ToInt32(p.GetValue(docs, null));
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>把刚打开的文档设为当前文档（有些 CAD 打开后不自动切换）。</summary>
        private static void Activate(DocumentCollection docs, Document doc)
        {
            if (doc == null) return;
            try
            {
                PropertyInfo p = docs.GetType().GetProperty("MdiActiveDocument",
                    BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.CanWrite) p.SetValue(docs, doc, null);
            }
            catch
            {
                // 切换失败不影响已经打开的文档
            }
        }

        /// <summary>反射调用实例方法（按参数个数与类型匹配，找不到返回 null）。</summary>
        private static object InvokeInstance(object target, string methodName, object[] args)
        {
            Type t = target.GetType();

            Type[] types = new Type[args.Length];
            for (int i = 0; i < args.Length; i++) types[i] = args[i].GetType();

            MethodInfo mi = t.GetMethod(methodName,
                BindingFlags.Public | BindingFlags.Instance, null, types, null);

            if (mi == null)
            {
                // 参数类型不完全一致时（如 COM / 变体）退而求其次：只比参数个数
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name != methodName) continue;
                    if (m.GetParameters().Length != args.Length) continue;
                    mi = m;
                    break;
                }
            }

            if (mi == null) return null;
            return mi.Invoke(target, args);
        }

        /// <summary>
        /// COM 兜底：Application.AcadApplication → Documents → Open(path)。
        /// 全程反射（拿不到 COM 入口就返回 false，不会抛）。
        /// </summary>
        private static bool TryOpenByCom(string path)
        {
            PropertyInfo appProp = typeof(Application).GetProperty("AcadApplication",
                BindingFlags.Public | BindingFlags.Static);
            if (appProp == null) return false;

            object app = appProp.GetValue(null, null);
            if (app == null) return false;

            Type appType = app.GetType();
            object documents = null;
            try
            {
                documents = appType.InvokeMember("Documents",
                    BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance,
                    null, app, null);
            }
            catch
            {
                return false;
            }
            if (documents == null) return false;

            // COM 的 Open 签名是 Open(Name, [ReadOnly], [Password])，
            // 可选参数按 IDispatch 规则可以少传，两种参数个数都试一遍。
            object[] argSets = new object[]
            {
                new object[] { path, false },
                new object[] { path }
            };

            foreach (object[] args in argSets)
            {
                try
                {
                    object r = documents.GetType().InvokeMember("Open",
                        BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
                        null, documents, args);
                    if (r != null) return true;
                }
                catch
                {
                    // 换下一种参数个数再试
                }
            }
            return false;
        }

        private static string OneLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            string s = text.Replace("\r", " ").Replace("\n", " ");
            return s.Length > 120 ? s.Substring(0, 120) + "…" : s;
        }
    }
}
