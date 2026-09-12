using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Prevents mobs from targeting tamed creatures or players via RPC_SetTarget.
    /// 
    /// When a mob targets a tamed creature or player, this handler clears the target
    /// ZDOID to ZDOID.None before the RPC is forwarded. This prevents exploits where
    /// clients force mobs to attack specific players or tamed animals.
    /// 
    /// Based on BetterZeeRouter.SetTargetHandler.
    /// </summary>
    public sealed class SetTargetHandler : RpcMethodHandler
    {
        private static readonly SetTargetHandler _instance = new SetTargetHandler();
        public static readonly int PlayerHashCode = "Player".GetStableHashCode();

        private SetTargetHandler() { }

        public static void Register()
        {
            RoutedRpcManager.AddHandler("RPC_SetTarget", _instance);
        }

        public override bool Process(ZRoutedRpc.RoutedRPCData routedRpcData)
        {
            ZPackage parameters = routedRpcData.m_parameters;

            parameters.SetPos(0);
            ZDOID targetZDOID = parameters.ReadZDOID();
            parameters.SetPos(0);

            if (targetZDOID == ZDOID.None)
            {
                return true; // Clearing target - allow
            }

            ZDO targetZDO;
            if (!ZDOMan.instance.m_objectsByID.TryGetValue(targetZDOID, out targetZDO))
            {
                return true; // Target doesn't exist - allow (vanilla will handle)
            }

            // Distance sanity check - if target is >50km from origin, it's suspicious
            if (Utils.DistanceXZ(Vector3.zero, targetZDO.m_position) > 50000f)
            {
                LoggerOptions.LogWarning($"[RpcRouter] SetTarget suspicious - target ZDO at extreme distance ({targetZDO.m_position})");
                return true;
            }

            // Block targeting players or tamed creatures
            if (targetZDO.m_prefab == PlayerHashCode || targetZDO.GetBool(ZDOVars.s_tamed))
            {
                // Rewrite the target to ZDOID.None
                parameters.Clear();
                parameters.Write(ZDOID.None);
                parameters.m_writer.Flush();
                parameters.m_stream.Flush();
                parameters.SetPos(0);

                // Route back to sender so their client sees the cleared target
                routedRpcData.m_senderPeerID = ZRoutedRpc.instance.m_id;

                LoggerOptions.LogInfo("[RpcRouter] SetTarget cleared - target was player or tamed creature");
            }

            return true;
        }
    }
}
