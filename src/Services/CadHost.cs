using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace BlockLibraryManager
{
    /// <summary>
    /// 宿主 CAD 平台适配层（AutoCAD / 中望 CAD / 浩辰 CAD / BricsCAD 四合一）。
    ///
    /// 背景：中望 CAD（ZwSoft.ZwCAD.*）与浩辰 CAD（Gssoft.Gscad.*）的 .NET API
    /// 是 ObjectARX 的移植版，绝大多数类型与 AutoCAD 同名同签名，直接替换命名空间
    /// 即可编译。BricsCAD 也是移植版，但命名空间被拆成 Bricscad.*（BrxMgd.dll）
    /// 与 Teigha.*（TD_Mgd.dll）两个根，由 tools\gen-platform-src.ps1 的映射表处理。
    /// 有极少数成员各家实现情况不一致，例如：
    ///   Application.ShowModelessDialog / ShowModalDialog
    ///   Document.CommandEnded / CommandCancelled / UnknownCommand
    /// 如果在国产平台上像 AutoCAD 那样直接写这些成员，一旦对方版本没有实现，
    /// 就会在**编译期**报错（而不是运行期），导致整套源码无法出包。
    ///
    /// 解决办法：把这些"不确定成员"全部改用
    /// <see cref="CadHost"/> 的反射调用——有这个成员就走原生路径（行为与 AutoCAD
    /// 完全一致），没有就自动降级到等价实现，并且整个过程被 try/catch 兜住，
    /// 任何情况下都不会影响主功能（浏览图库 / 插入图块）。
    ///
    /// 因此本类保证：同一份源码在三家的任意版本上都能编译通过并运行，
    /// 只是少数锦上添花的细节（拖拽后精确时机还原等）可能在个别版本上退化为近似实现。
    /// </summary>
    internal static class CadHost
    {
        /// <summary>当前编译产物面向的是不是 AutoCAD（编译期常量，由 csproj 定义 BLM_ACAD）。</summary>
        public static bool IsAutoCAD
        {
            get
            {
#if BLM_ACAD
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>宿主平台显示名，用于日志与关于窗体。</summary>
        public static string PlatformName
        {
            get
            {
#if BLM_ZWCAD
                return "ZWCAD";
#elif BLM_GSTARCAD
                return "GstarCAD";
#elif BLM_BRICSCAD
                return "BricsCAD";
#else
                return "AutoCAD";
#endif
            }
        }

        // ------------------------------------------------------------------
        // 事件：反射挂接 + IL 适配器
        // ------------------------------------------------------------------

        private static readonly Dictionary<int, Action<object, object>> Sinks =
            new Dictionary<int, Action<object, object>>();
        private static int _nextToken = 1;

        /// <summary>
        /// 尝试给 <paramref name="target"/> 的指定事件挂一个处理器。
        /// 用 IL 动态生成目标事件的确切委托类型（参数是各不相同 EventArgs），
        /// 因此不需要在编译期知道 CommandEventArgs / UnknownCommandEventArgs 这些类型，
        /// 也就不会因为某平台缺少该类型而编译失败。
        /// </summary>
        /// <param name="target">事件所属对象（通常是 Document）。</param>
        /// <param name="eventName">事件名，如 "CommandEnded"。</param>
        /// <param name="sink">真正的处理逻辑（sender, eventArgs）。</param>
        /// <returns>成功则委托实例（卸载时要传回来），失败 / 不存在则 null。</returns>
        public static object TryHook(object target, string eventName, Action<object, object> sink)
        {
            if (target == null || string.IsNullOrEmpty(eventName) || sink == null) return null;

            try
            {
                EventInfo ev = target.GetType().GetEvent(eventName,
                    BindingFlags.Public | BindingFlags.Instance);
                if (ev == null) return null;

                Type handlerType = ev.EventHandlerType;
                MethodInfo add = ev.GetAddMethod(true);
                if (add == null) return null;

                Delegate bound = CreateAdapter(handlerType, sink);
                if (bound == null) return null;

                add.Invoke(target, new object[] { bound });
                return (object)bound;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>卸载 <see cref="TryHook"/> 挂上的事件。永远不抛异常。</summary>
        public static void TryUnhook(object target, string eventName, object bound)
        {
            if (target == null || string.IsNullOrEmpty(eventName) || bound == null) return;

            try
            {
                EventInfo ev = target.GetType().GetEvent(eventName,
                    BindingFlags.Public | BindingFlags.Instance);
                if (ev == null) return;

                MethodInfo remove = ev.GetRemoveMethod(true);
                if (remove == null) return;

                remove.Invoke(target, new object[] { (Delegate)bound });
            }
            catch { }
        }

        /// <summary>
        /// 生成一个与目标事件委托类型完全一致、内部转发到 <paramref name="sink"/> 的委托。
        /// 生成的 IL 相当于： (object s, TArgs e) => sink(s, (object)e)
        /// </summary>
        private static Delegate CreateAdapter(Type handlerType, Action<object, object> sink)
        {
            MethodInfo invoke = handlerType.GetMethod("Invoke");
            if (invoke == null) return null;
            if (invoke.ReturnType != typeof(void)) return null;

            ParameterInfo[] ps = invoke.GetParameters();
            if (ps.Length != 2) return null;

            try
            {
                int token;
                lock (Sinks)
                {
                    token = _nextToken++;
                    Sinks[token] = sink;
                }

                Type[] argTypes = { ps[0].ParameterType, ps[1].ParameterType };
                DynamicMethod dm = new DynamicMethod(
                    "BlmSinkAdapter" + token,
                    typeof(void),
                    argTypes,
                    typeof(CadHost).Module,
                    true);

                ILGenerator il = dm.GetILGenerator();
                MethodInfo raise = typeof(CadHost).GetMethod("Raise",
                    BindingFlags.Static | BindingFlags.NonPublic);

                il.Emit(OpCodes.Ldc_I4, token);
                il.Emit(OpCodes.Ldarg_0);                       // sender
                il.Emit(OpCodes.Ldarg_1);                       // event args
                if (ps[1].ParameterType.IsValueType)
                    il.Emit(OpCodes.Box, ps[1].ParameterType);
                il.Emit(OpCodes.Call, raise);
                il.Emit(OpCodes.Ret);

                return dm.CreateDelegate(handlerType);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>IL 适配器实际调用的目标方法（由 token 找到真正的 sink）。</summary>
        private static void Raise(int token, object sender, object args)
        {
            Action<object, object> sink;
            lock (Sinks)
            {
                if (!Sinks.TryGetValue(token, out sink) || sink == null) return;
            }

            try { sink(sender, args); }
            catch { /* 事件里的异常绝不能冒到宿主主循环 */ }
        }

        // ------------------------------------------------------------------
        // 属性 / 方法：反射读取与调用
        // ------------------------------------------------------------------

        /// <summary>反射读取实例属性值。不存在或出错返回 null。</summary>
        public static object TryGetProperty(object instance, string propertyName)
        {
            if (instance == null || string.IsNullOrEmpty(propertyName)) return null;

            try
            {
                PropertyInfo p = instance.GetType().GetProperty(propertyName,
                    BindingFlags.Public | BindingFlags.Instance);
                if (p == null || !p.CanRead) return null;
                return p.GetValue(instance, null);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 反射调用静态方法。典型用途：
        /// Application.ShowModelessDialog(Form) —— 国产 CAD 不一定有这个重载。
        /// </summary>
        public static bool TryInvokeStatic(Type type, string methodName, params object[] args)
        {
            if (type == null || string.IsNullOrEmpty(methodName)) return false;

            try
            {
                foreach (MethodInfo m in type.GetMethods(
                             BindingFlags.Public | BindingFlags.Static))
                {
                    if (!string.Equals(m.Name, methodName, StringComparison.Ordinal)) continue;

                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length != (args == null ? 0 : args.Length)) continue;

                    bool matched = true;
                    for (int i = 0; i < ps.Length; i++)
                    {
                        object a = args[i];
                        if (a == null) continue;
                        if (!ps[i].ParameterType.IsInstanceOfType(a)) { matched = false; break; }
                    }
                    if (!matched) continue;

                    m.Invoke(null, args);
                    return true;
                }
            }
            catch
            {
                // 掉到下面返回 false
            }

            return false;
        }
    }
}
