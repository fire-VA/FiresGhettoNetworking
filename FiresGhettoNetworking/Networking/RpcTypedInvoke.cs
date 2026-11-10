using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// 1.6.39 ([fgn], Fire 11-05, group B, "ZRpc invoke"): vanilla runs every received RPC with a parameter through
    /// Delegate.DynamicInvoke (reflection: an argument array, a parameter lookup, a reflected call). Here the three places that hand a
    /// received RPC to its handler (ZRpc.HandlePackage, ZRoutedRpc.HandleRoutedRPC, ZNetView.HandleRoutedRPC) first offer it to Run:
    /// the arguments are still read by vanilla's own reader (ZRpc.Deserialize(paramInfo, pkg, ref list)), then the handler's own
    /// delegate type is called directly with them. Handlers of a shape it doesn't know (no parameter, more than 6 for routed or 4
    /// for direct, another mod's own method class) go to vanilla's Invoke as before.
    /// Differences from vanilla: a handler that throws now shows its own exception instead of it wrapped in TargetInvocationException;
    /// and a mod patching a RpcMethod or RoutedMethod Invoke itself is bypassed while this is on. OFF by default: every RPC passes
    /// here, so it gets proven on the rig before it's turned on.
    /// </summary>
    [HarmonyPatch]
    internal static class RpcTypedInvoke
    {
        internal static ConfigEntry<bool> ConfigEnabled;

        private delegate void Caller(Delegate action, List<object> args);
        private sealed class Shape { internal FieldInfo Action; internal Caller Call; internal Type[] Params; }
        private static readonly HashSet<MethodInfo> s_refused = new HashSet<MethodInfo>();

        private static readonly Dictionary<Type, Shape> s_shapes = new Dictionary<Type, Shape>();
        private static readonly Dictionary<MethodInfo, ParameterInfo[]> s_params = new Dictionary<MethodInfo, ParameterInfo[]>();
        private static readonly List<object> s_args = new List<object>(8);
        private static bool s_busy;
        private static int s_fast, s_vanilla;

        internal static void InitConfig(ConfigFile config)
        {
            ConfigEnabled = config.Bind("02 - Client Performance", "Call RPC Handlers Directly", false,
                Cfg.Adv("Network messages are handed to their handler by a direct call instead of the slower reflection call. The values passed are the same. Off until proven on a test server."));
        }

        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ZRpc), "HandlePackage");
            yield return AccessTools.Method(typeof(ZRoutedRpc), "HandleRoutedRPC");
            yield return AccessTools.Method(typeof(ZNetView), nameof(ZNetView.HandleRoutedRPC));
        }

        // Each "callvirt <base>.Invoke(first, pkg)" becomes: keep the three values, try Run, and only when it declines call vanilla's.
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code, ILGenerator il, MethodBase original)
        {
            MethodInfo run = AccessTools.Method(typeof(RpcTypedInvoke), nameof(Run));
            var list = new List<CodeInstruction>(code);
            int done = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].opcode != OpCodes.Callvirt || !(list[i].operand is MethodInfo m) || m.Name != "Invoke") continue;
                Type owner = m.DeclaringType;
                if (owner == null || !owner.IsInterface || (owner.Name != "RpcMethodBase" && owner.Name != "RoutedMethodBase")) continue;
                ParameterInfo[] p = m.GetParameters();
                if (p.Length != 2 || p[1].ParameterType != typeof(ZPackage)) continue;
                LocalBuilder lm = il.DeclareLocal(owner), lf = il.DeclareLocal(p[0].ParameterType), lp = il.DeclareLocal(typeof(ZPackage));
                Label skip = il.DefineLabel();
                var call = list[i];
                var repl = new List<CodeInstruction>
                {
                    new CodeInstruction(OpCodes.Stloc, lp), new CodeInstruction(OpCodes.Stloc, lf), new CodeInstruction(OpCodes.Stloc, lm),
                    new CodeInstruction(OpCodes.Ldloc, lm), new CodeInstruction(OpCodes.Ldloc, lf),
                };
                if (p[0].ParameterType.IsValueType) repl.Add(new CodeInstruction(OpCodes.Box, p[0].ParameterType));
                repl.Add(new CodeInstruction(OpCodes.Ldloc, lp));
                repl.Add(new CodeInstruction(OpCodes.Call, run));
                repl.Add(new CodeInstruction(OpCodes.Brtrue, skip));
                repl.Add(new CodeInstruction(OpCodes.Ldloc, lm));
                repl.Add(new CodeInstruction(OpCodes.Ldloc, lf));
                repl.Add(new CodeInstruction(OpCodes.Ldloc, lp));
                repl.Add(new CodeInstruction(OpCodes.Callvirt, m));
                var after = new CodeInstruction(OpCodes.Nop); after.labels.Add(skip);
                repl.Add(after);
                repl[0].labels.AddRange(call.labels); repl[0].blocks.AddRange(call.blocks);
                list.RemoveAt(i);
                list.InsertRange(i, repl);
                i += repl.Count - 1;
                done++;
            }
            if (done == 0) LoggerOptions.LogWarning($"[RpcTypedInvoke] {original.DeclaringType.Name}.{original.Name}: no handler call found; game update? Direct calls are off there.");
            return list;
        }

        // true = the handler ran here; false = vanilla's own Invoke runs it.
        private static bool Run(object method, object first, ZPackage pkg)
        {
            if (!ConfigEnabled.Value || s_busy || method == null) { s_vanilla++; return false; }
            Shape shape = ShapeOf(method.GetType());
            if (shape == null) { s_vanilla++; return false; }
            var action = shape.Action.GetValue(method) as Delegate;
            if (action == null) { s_vanilla++; return false; }
            MethodInfo target = action.Method;
            if (s_refused.Contains(target)) { s_vanilla++; return false; }
            if (!s_params.TryGetValue(target, out ParameterInfo[] pars)) s_params[target] = pars = target.GetParameters();
            List<object> args = s_args;
            int pos = pkg.GetPos();
            s_busy = true;
            try
            {
                args.Clear();
                args.Add(first);
                ZRpc.Deserialize(pars, pkg, ref args);   // vanilla's reader: every value read as before
            }
            finally { s_busy = false; }
            // 1.6.43: every value must fit the delegate before the call; a value that doesn't (a shape this code got wrong) rewinds the
            // package and hands this RPC to vanilla's Invoke from now on, with one warning naming it. Checked before the call, so a
            // handler's own exception is never caught here and a handler never runs twice.
            if (!Fits(shape.Params, args, out string why))
            {
                pkg.SetPos(pos);
                s_refused.Add(target);
                LoggerOptions.LogWarning($"[RpcTypedInvoke] {target.DeclaringType?.Name}.{target.Name}: {why}; vanilla's call runs this RPC from now on.");
                s_vanilla++;
                return false;
            }
            s_fast++;
            shape.Call(action, args);   // the arguments are taken out of the list before the handler runs, so a nested RPC may reuse it
            return true;
        }

        private static Shape ShapeOf(Type t)
        {
            if (s_shapes.TryGetValue(t, out Shape s)) return s;
            s = null;
            try
            {
                if (t.IsGenericType && t.Assembly == typeof(ZNet).Assembly)
                {
                    Type[] g = t.GetGenericArguments();
                    string def = t.GetGenericTypeDefinition().Name;
                    bool direct = t.DeclaringType == typeof(ZRpc) && def.StartsWith("RpcMethod`") && g.Length <= 4;
                    bool routed = t.DeclaringType == null && def.StartsWith("RoutedMethod`") && g.Length <= 6;
                    FieldInfo f = AccessTools.Field(t, "m_action");
                    if ((direct || routed) && f != null)
                    {
                        MethodInfo open = AccessTools.Method(typeof(RpcTypedInvoke), (direct ? "D" : "R") + g.Length);
                        var call = (Caller)Delegate.CreateDelegate(typeof(Caller), open.MakeGenericMethod(g));
                        // 1.6.43: the field's delegate parameters are what every value is checked against before a call (see Fits).
                        MethodInfo invoke = f.FieldType.GetMethod("Invoke");
                        ParameterInfo[] ip = invoke?.GetParameters();
                        if (ip == null || ip.Length != g.Length + 1) throw new InvalidOperationException($"delegate {f.FieldType.Name} has {ip?.Length ?? -1} parameters");
                        var types = new Type[ip.Length];
                        for (int k = 0; k < ip.Length; k++) types[k] = ip[k].ParameterType;
                        s = new Shape { Action = f, Call = call, Params = types };
                    }
                }
            }
            catch (Exception e) { LoggerOptions.LogWarning($"[RpcTypedInvoke] {t.Name}: no direct call ({e.GetType().Name}); vanilla runs it."); s = null; }
            s_shapes[t] = s;
            return s;
        }

        private static void D1<A>(Delegate d, List<object> a) => ((Action<ZRpc, A>)d)((ZRpc)a[0], (A)a[1]);
        // 1.6.43 ([fgn], trial 11-05): vanilla's 4-, 5- and 6-value RPCs hold their own nested delegate type (ZRpc.RpcMethod<T,U,V,B>.Method,
        // RoutedMethod<...>.Method), not Action<...>; 1.6.39 cast them to Action and the ChatMessage RPC threw InvalidCastException.
        private static void D2<A, B>(Delegate d, List<object> a) => ((Action<ZRpc, A, B>)d)((ZRpc)a[0], (A)a[1], (B)a[2]);
        private static void D3<A, B, C>(Delegate d, List<object> a) => ((Action<ZRpc, A, B, C>)d)((ZRpc)a[0], (A)a[1], (B)a[2], (C)a[3]);
        private static void D4<A, B, C, E>(Delegate d, List<object> a) => ((ZRpc.RpcMethod<A, B, C, E>.Method)d)((ZRpc)a[0], (A)a[1], (B)a[2], (C)a[3], (E)a[4]);
        private static void R1<A>(Delegate d, List<object> a) => ((Action<long, A>)d)((long)a[0], (A)a[1]);
        private static void R2<A, B>(Delegate d, List<object> a) => ((Action<long, A, B>)d)((long)a[0], (A)a[1], (B)a[2]);
        private static void R3<A, B, C>(Delegate d, List<object> a) => ((Action<long, A, B, C>)d)((long)a[0], (A)a[1], (B)a[2], (C)a[3]);
        private static void R4<A, B, C, E>(Delegate d, List<object> a) => ((RoutedMethod<A, B, C, E>.Method)d)((long)a[0], (A)a[1], (B)a[2], (C)a[3], (E)a[4]);
        private static void R5<A, B, C, E, F>(Delegate d, List<object> a) => ((RoutedMethod<A, B, C, E, F>.Method)d)((long)a[0], (A)a[1], (B)a[2], (C)a[3], (E)a[4], (F)a[5]);
        private static void R6<A, B, C, E, F, G>(Delegate d, List<object> a) => ((RoutedMethod<A, B, C, E, F, G>.Method)d)((long)a[0], (A)a[1], (B)a[2], (C)a[3], (E)a[4], (F)a[5], (G)a[6]);

        private static bool Fits(Type[] want, List<object> args, out string why)
        {
            why = null;
            if (args.Count != want.Length) { why = $"{args.Count} values for {want.Length} parameters"; return false; }
            for (int i = 0; i < want.Length; i++)
            {
                object v = args[i];
                if (v == null ? want[i].IsValueType : !want[i].IsInstanceOfType(v))
                { why = $"value {i} is {(v == null ? "null" : v.GetType().Name)}, parameter is {want[i].Name}"; return false; }
            }
            return true;
        }

        internal static string Census() => $"RPC direct calls {s_fast}, vanilla {s_vanilla}";
    }
}
