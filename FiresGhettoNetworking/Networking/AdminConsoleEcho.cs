namespace FiresGhettoNetworkMod
{
    /// <summary>
    /// Output channel for the admin test commands: prints into the local console, or the log when no
    /// console is open. Sending never throws, so a failed echo cannot abort a running test.
    /// </summary>
    internal static class AdminConsoleEcho
    {
        public static void Print(string message)
        {
            if (Console.instance != null) Console.instance.AddString(message);
            else LoggerOptions.LogMessage(message);
        }

        public static void Send(string rpcName, long target, string message)
        {
            try
            {
                if (ZRoutedRpc.instance != null) ZRoutedRpc.instance.InvokeRoutedRPC(target, rpcName, message);
            }
            catch { }
        }
    }
}
