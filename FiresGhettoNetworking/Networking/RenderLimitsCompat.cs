using BepInEx.Bootstrap;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Compatibility shim for JereKuusela's Render Limits (GUID "render_limits") — the dedicated
    /// zone-load / active-area mod. Render Limits owns ZoneSystem.m_activeArea / m_activeDistantArea
    /// (its "Active / Loaded / Generated zones" config) and prefixes the active-area checks.
    ///
    /// FGN's Extended Zone Radius normally ADDS layers on top of m_activeArea, which would inflate
    /// whatever Render Limits configured. When Render Limits is present we DEFER: FGN's extended
    /// radius contribution drops to 0, so the server sizes its (still multi-peer) authority object
    /// creation to exactly the area Render Limits set. FGN keeps its multi-peer logic — only the
    /// SIZE decision yields to Render Limits.
    /// </summary>
    public static class RenderLimitsCompat
    {
        private const string RenderLimitsGuid = "render_limits";

        private static bool? _present;

        /// True when Render Limits is loaded in this process. The plugin list is fixed after load,
        /// so this is evaluated once and cached — safe to call from per-frame patch code.
        public static bool Present
        {
            get
            {
                if (!_present.HasValue)
                {
                    try { _present = Chainloader.PluginInfos != null && Chainloader.PluginInfos.ContainsKey(RenderLimitsGuid); }
                    catch { _present = false; }

                    if (_present.Value)
                        LoggerOptions.LogInfo("Render Limits detected — FGN defers zone-load sizing to it (Extended Zone Radius treated as 0; server authority stays multi-peer).");
                }
                return _present.Value;
            }
        }

        /// FGN's effective extended-zone-radius contribution: 0 when Render Limits owns zone loading,
        /// otherwise the caller's configured value.
        public static int DeferRadius(int configuredRadius) => Present ? 0 : configuredRadius;
    }
}
