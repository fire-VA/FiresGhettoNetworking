using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// 1.6.38 ([fgn], Fire 11-05, group B): vanilla ZDOMan.SendZDOs builds two new ZPackages (a stream, a reader, a writer each) on every
    /// send to every peer. Both are finished with before SendZDOs returns: ZRpc.Invoke copies the outer one into its own package
    /// straight away and the nested one is only written into the outer one. So two kept packages, cleared on each use, do the same job.
    /// SendZDOs runs on the main thread and doesn't call itself. Not A/B proven yet.
    /// </summary>
    [HarmonyPatch]
    internal static class SendZdoPackagePool
    {
        internal static ConfigEntry<bool> ConfigEnabled;
        private static readonly ZPackage[] s_kept = { new ZPackage(), new ZPackage() };

        internal static void InitConfig(ConfigFile config)
        {
            ConfigEnabled = config.Bind("02 - Client Performance", "Reuse ZDO Send Packages", true,
                Cfg.Adv("Sending objects to other players reuses two buffers instead of creating new ones on every send, on clients and servers. Less garbage; the data sent is the same."));
        }

        private static ZPackage Outer() => Rent(0);
        private static ZPackage Nested() => Rent(1);

        private static ZPackage Rent(int i)
        {
            if (!ConfigEnabled.Value) return new ZPackage();
            s_kept[i].Clear();
            return s_kept[i];
        }

        [HarmonyPatch(typeof(ZDOMan), "SendZDOs"), HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            ConstructorInfo ctor = AccessTools.Constructor(typeof(ZPackage), new System.Type[0]);
            MethodInfo[] helpers = { AccessTools.Method(typeof(SendZdoPackagePool), nameof(Outer)), AccessTools.Method(typeof(SendZdoPackagePool), nameof(Nested)) };
            List<CodeInstruction> list = new List<CodeInstruction>(code);
            int found = 0;
            foreach (CodeInstruction i in list) if (i.opcode == OpCodes.Newobj && i.operand is ConstructorInfo c && c == ctor) found++;
            if (found != 2)
            {
                LoggerOptions.LogWarning($"[SendZdoPackagePool] ZDOMan.SendZDOs creates {found} packages where 2 were expected: game update? Reuse is off this session.");
                return list;
            }
            int n = 0;
            foreach (CodeInstruction i in list)
                if (i.opcode == OpCodes.Newobj && i.operand is ConstructorInfo c && c == ctor) { i.opcode = OpCodes.Call; i.operand = helpers[n++]; }
            return list;
        }
    }
}
