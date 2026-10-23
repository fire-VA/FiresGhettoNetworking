using BepInEx.Configuration;
using HarmonyLib;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Stops a drop being claimed over and over and never picked up. Vanilla
    /// Player.AutoPickup asks for OWNERSHIP of every drop in reach (ItemDrop.RequestOwn) BEFORE it checks whether the player can
    /// carry it (Inventory.CanAddItem and the weight limit come only once it owns the drop). A player with a full bag or at the weight
    /// limit therefore takes ownership of drops it will never pick up and keeps it; everyone else's pickup has to win the drop back,
    /// and RequestOwn's back-off doubles each try (0.2 s .. 30 s), so two players near one pile pass it back and forth and the
    /// pickup stalls. This skips that request when the local player could not take the item anyway (the same two checks vanilla
    /// makes right after), so a full player never claims the drop; nothing else about pickup changes. Client side, no wire change.
    /// </summary>
    [HarmonyPatch]
    internal static class PickupClaimGuard
    {
        public static ConfigEntry<bool> ConfigEnabled;
        private static Player s_picking;
        internal static long Skipped;

        public static void InitConfig(ConfigFile config)
        {
            ConfigEnabled = config.Bind("10 - Server Authority", "Auto-Pickup Claims Only What You Can Carry", true,
                Cfg.Adv("A player with a full bag no longer blocks others from picking up items."));
        }

        [HarmonyPatch(typeof(Player), "AutoPickup"), HarmonyPrefix]
        private static void AutoPickup_Prefix(Player __instance) => s_picking = __instance;

        [HarmonyPatch(typeof(Player), "AutoPickup"), HarmonyFinalizer]
        private static void AutoPickup_Finalizer() => s_picking = null;

        [HarmonyPatch(typeof(ItemDrop), nameof(ItemDrop.RequestOwn)), HarmonyPrefix]
        private static bool RequestOwn_Prefix(ItemDrop __instance)
        {
            Player p = s_picking;
            if (p == null || ConfigEnabled == null || !ConfigEnabled.Value || __instance == null) return true;
            ItemDrop.ItemData item = __instance.m_itemData;
            Inventory inv = p.GetInventory();
            if (item == null || inv == null) return true;
            if (inv.CanAddItem(item) && item.GetWeight() + inv.GetTotalWeight() <= p.GetMaxCarryWeight()) return true;
            Skipped++;
            return false;   // could not take it: leave the drop with its owner
        }
    }
}
