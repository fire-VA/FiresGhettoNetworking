using System;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// 1.6.81 (SP split): the cape and cloth crash diagnostics moved to FiresFixes. FGN's own network steps (Steam settings, auto-tune,
    /// compressed packets, clock corrections) keep logging their breadcrumbs here when the key is on.
    /// </summary>
    public static class CapeCrashDiagnostics
    {
        public static bool Enabled => FiresGhettoNetworkMod.ConfigEnableCapeCrashDiagnostics?.Value ?? false;

        public static void Log(string message)
        {
            if (!Enabled) return;
            FiresGhettoNetworkMod.Log?.LogMessage($"[CapeDiag {DateTime.Now:HH:mm:ss.fff}] {message}");
        }
    }
}