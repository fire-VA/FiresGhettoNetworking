using UnityEngine;

namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Relays damage numbers only to players near them. The owner of whatever was hit raises RPC_DamageText to everybody,
    /// and each receiver throws away any number more than 30 m from its camera.
    /// </summary>
    public sealed class DamageTextHandler : RpcMethodHandler
    {
        private static readonly DamageTextHandler s_instance = new DamageTextHandler();

        private DamageTextHandler() { }

        public static void Register() => RoutedRpcManager.AddHandler("RPC_DamageText", s_instance);

        public override bool Process(ZRoutedRpc.RoutedRPCData routedRpcData)
        {
            var parameters = routedRpcData.m_parameters;
            if (parameters == null) return true;
            int saved = parameters.GetPos();
            try
            {
                parameters.SetPos(0);
                var text = parameters.ReadPackage();
                text.ReadInt();
                Vector3 position = text.ReadVector3();
                RoutedRpcManager.SetPositionHint(position, RoutedRpcManager.PositionRadius());
            }
            catch
            {
            }
            finally
            {
                parameters.SetPos(saved);
            }
            return true;
        }
    }
}
