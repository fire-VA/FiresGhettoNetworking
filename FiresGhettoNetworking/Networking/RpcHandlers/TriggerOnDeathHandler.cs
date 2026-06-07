using System.Collections.Generic;
using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// AoI-filters RPC_TriggerOnDeath to nearby peers only.
    ///
    /// RPC_TriggerOnDeath fires when any creature or player dies, triggering
    /// death visual effects (ragdoll, particle FX, loot drop spawn animation).
    /// Vanilla broadcasts this to ALL connected clients regardless of distance.
    ///
    /// On a busy server with monster farms or PvP, every death event from anywhere
    /// on the map gets broadcast to all 100 players — including players on the other
    /// side of the world who will never render that character or see the effect.
    ///
    /// We return true (allow through) and let the RoutedRpcManager AoI system
    /// filter it to only peers within ConfigRpcAoIRadius. Players beyond that
    /// distance cannot see the character being rendered anyway.
    ///
    /// NOTE: Player deaths that need to be globally visible (e.g. Minimap death
    /// pin) are handled separately by game systems that don't use this RPC.
    /// The death pin is added in Player.OnDeath via Minimap.instance.AddPin,
    /// which does not go through ZRoutedRpc — it is safe to AoI filter this RPC.
    /// </summary>
    public sealed class TriggerOnDeathHandler : RpcMethodHandler
    {
        private static readonly TriggerOnDeathHandler _instance = new TriggerOnDeathHandler();

        private TriggerOnDeathHandler() { }

        public static void Register()
        {
            RoutedRpcManager.AddHandler("OnDeath", _instance);
        }

        public override bool Process(ZRoutedRpc.RoutedRPCData routedRpcData)
        {
            // Allow — AoI routing in RoutedRpcManager will filter by distance.
            // Death effects (ragdoll, particles) are only visible within render distance
            // anyway. Players far away do not need this RPC.
            return true;
        }
    }
}
