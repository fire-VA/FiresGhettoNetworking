# Client Log Relay

A drop-in, mod-agnostic pipeline for receiving BepInEx logs + mod lists from clients on
login, parsing out errors/warnings, persisting per-client artifact folders on disk, and
optionally forwarding a snapshot to a Discord webhook.

This folder is **copy-pasteable**. Drop it into any BepInEx mod and perform one namespace
rename to use it.

---

## How to drop this module into another mod

1. Copy the entire `Modules/ClientLogRelay/` folder into your other mod's source tree.
2. Find/replace the root namespace in every `.cs` file under the folder:
   - From: `VerdantsAscent.Modules.ClientLogRelay`
   - To:   `YourMod.ClientLogRelay` (or wherever)
3. From your plugin's `Awake()` (server role only), register one or more consumers:

   ```csharp
   using YourMod.ClientLogRelay;
   using YourMod.ClientLogRelay.Consumers;

   // 1. Write to disk (uses the conventional {ConfigPath}/{ModId}/ClientLogs layout)
   ClientLogRelay.RegisterConsumer(new DiskConsumer(
       consumerId: "YourMod.Disk",
       rootDirResolver: () => ClientLogRelayPaths.GetDefaultClientLogsDir("YourMod"),
       enabledGate: () => MyConfig.EnableClientLogCapture.Value));

   // 2. Claim the Discord login-snapshot channel. Use a higher priority than sibling
   //    mods you want to override. Priority 0 is the neutral baseline.
   const string OwnerId = "YourMod.Login";
   ClientLogRelay.TryClaimLoginSnapshotOwnership(OwnerId, priority: 100);

   // 3. Post to Discord (self-suppresses when another mod owns the channel)
   var discord = new DiscordWebhookConsumer("YourMod.Discord")
   {
       EnabledGate            = () => MyConfig.DiscordEnabled.Value
                                     && ClientLogRelay.IsLoginSnapshotOwner(OwnerId),
       WebhookUrl             = () => MyConfig.WebhookUrl.Value,
       WebhookName            = () => "Valheim Server",
       AttachFullLog          = () => MyConfig.AttachFullLog.Value,
       AttachModList          = () => true,
       AttachErrorsWarnings   = () => true,
       OnlyIfErrorsOrWarnings = () => MyConfig.OnlyIfBad.Value,
       ServerName             = () => ZNet.instance?.GetWorldName() ?? "Unknown",
   };
   ClientLogRelay.RegisterConsumer(discord);
   ```

   **Cross-mod coordination.** Ownership is kept in an `AppDomain` slot keyed by a
   constant string literal (`FiresMods.ClientLogRelay.LoginSnapshotOwner`), so two
   mods that each embed a renamed copy of this folder still see the same claim.
   Highest priority wins; ties go to first-come. When no mod calls
   `TryClaimLoginSnapshotOwnership`, `IsLoginSnapshotOwner` returns `true` for
   every caller ? the backwards-compatible default.

4. **Client side** — read the local BepInEx log + mod list via the stock collector and
   forward them to the server using whatever RPC you prefer:

   ```csharp
   using YourMod.ClientLogRelay.Transport;

   // Usually wired from a challenge/response handler or on PeerInfo completion.
   if (ClientLogCollector.TryCollect(out byte[] logBytes,
                                     out Dictionary<string,string> modList))
   {
       var pkg = new ZPackage();
       pkg.Write(logBytes ?? new byte[0]);
       pkg.Write(modList.Count);
       foreach (var kv in modList) { pkg.Write(kv.Key); pkg.Write(kv.Value); }
       ZRoutedRpc.instance.InvokeRoutedRPC(
           ZRoutedRpc.instance.GetServerPeerID(), "YourMod_ClientLog", pkg);
   }
   ```

5. **Server side** — receive the bytes + mod list, build a `ClientLogArtifacts`, and hand
   it to the relay:

   ```csharp
   // Register once, e.g. inside your plugin's Awake() server branch:
   ZRoutedRpc.instance.Register<ZPackage>("YourMod_ClientLog", (long sender, ZPackage pkg) =>
   {
       byte[] logBytes = pkg.ReadByteArray();
       int count       = pkg.ReadInt();
       var modList     = new Dictionary<string,string>(count, StringComparer.OrdinalIgnoreCase);
       for (int i = 0; i < count; i++)
       {
           string guid = pkg.ReadString();
           string ver  = pkg.ReadString();
           modList[guid] = ver;
       }

       var peer = ZNet.instance?.GetPeer(sender);
       var artifacts = new ClientLogArtifacts(
           platformId: peer?.m_socket?.GetHostName() ?? sender.ToString(),
           playerName: peer?.m_playerName ?? "unknown",
           logBytes:   logBytes,
           modList:    modList);

       ClientLogRelay.ReportArtifacts(artifacts);
   });
   ```

That's it. The relay will:
1. Run the error/warning extractor (with benign-pattern filtering + dedup).
2. Populate the artifact's `ErrorsWarningsReport`, `ErrorCount`, `WarningCount`.
3. Fan out to every registered consumer on the main thread.

---

## Reaction-as-button: request the full log on demand

The default `AttachFullLog` toggle on `DiscordWebhookConsumer` can be left off so every
login posts a compact embed (mod list + errors/warnings only). An authorised Discord
user can then react with a configured emoji on the snapshot message to fetch the full
`LogOutput.log` to the same webhook.

Why reactions and not real buttons: interactive Discord message components require
either a gateway websocket connection owned by a bot application or a public
HTTPS Interactions Endpoint ? both outside the "drop the folder in and it works"
baseline this module targets. Reactions are the closest equivalent that plain REST
polling can observe.

Everything needed lives inside `ClientLogRelay/`:

| File | Role |
|---|---|
| `Interactions/LogRequestContext.cs` | Message-id ? player bookkeeping DTO. |
| `Interactions/LogRequestRegistry.cs` | Thread-safe, TTL'd map populated by the Discord consumer after each successful POST. |
| `Interactions/ILogRequestHandler.cs` | Contract the host mod implements to actually fetch + post the log. |
| `Webhook/MinimalWebhookPoster.Post(...)` with `onPosted` callback | Posts with `?wait=true`, surfaces the Discord message id back. |

### Wire-up on the Discord consumer

```csharp
var discord = new DiscordWebhookConsumer("YourMod.Discord")
{
    EnabledGate              = () => MyCfg.DiscordEnabled.Value,
    WebhookUrl               = () => MyCfg.WebhookUrl.Value,
    AttachFullLog            = () => false,                       // compact by default
    AttachModList            = () => true,
    AttachErrorsWarnings     = () => true,
    EnableLogRequestReaction = () => MyCfg.EnableLogRequestReaction.Value,
    LogRequestEmoji          = () => MyCfg.LogRequestReactionEmoji.Value, // default ??
    ServerName               = () => ZNet.instance?.GetWorldName() ?? "Unknown",
};
ClientLogRelay.RegisterConsumer(discord);
```

### Implement the handler once, plug it in

```csharp
internal sealed class YourLogRequestHandler : ILogRequestHandler
{
    public bool IsAuthorized(string discordUserId)
    {
        var csv = MyCfg.BotAdminDiscordIds.Value ?? "";
        foreach (var s in csv.Split(','))
            if (s.Trim() == discordUserId) return true;
        return false;
    }

    public void HandleRequest(LogRequestContext ctx, string discordUserId, string emoji)
    {
        // Locate the log bytes you cached when the artifacts originally came in,
        // then POST them to the same webhook. See VAngardeLogRequestHandler.cs
        // in FiresRPGmaker for a complete reference implementation.
    }
}

ClientLogRelay.RegisterLogRequestHandler(new YourLogRequestHandler());
```

### Hook the bot's message poll to the relay

Wherever your existing Discord bot polls channel messages, add a reaction poll that:

1. Calls `LogRequestRegistry.Snapshot()` to get live message ids.
2. For each id, `GET /channels/{ch}/messages/{id}/reactions/{url-encoded-emoji}?limit=100`.
3. For each user in the response, call
   `ClientLogRelay.TryDispatchLogRequest(messageId, userId, emoji)`.
4. De-dup `(messageId, userId)` pairs in a `HashSet<string>` so a persisted
   reaction only fires once.

`FiresRPGmaker/Modules/Discord/DiscordBotListener.cs :: PollLogRequestReactions`
is a complete, copyable reference.

---

## Architecture

```
???????????????????????????????????????????????????????????
? Your wire transport                                     ?
?   - receives client log bytes + mod list                ?
?   - builds ClientLogArtifacts                           ?
?   - calls ClientLogRelay.ReportArtifacts(...)           ?
???????????????????????????????????????????????????????????
                        ?
                        ?
???????????????????????????????????????????????????????????
? ClientLogRelay (this module)                            ?
?                                                         ?
?   1. LogErrorWarningExtractor.Extract(...)              ?
?        • benign-pattern filter                          ?
?        • dedup                                          ?
?        • stack-trace aggregation                        ?
?                                                         ?
?   2. populate artifact.ErrorsWarningsReport             ?
?                                                         ?
?   3. fan out to registered IClientLogConsumer[]         ?
???????????????????????????????????????????????????????????
                        ?
          ??????????????????????????????
          ?             ?              ?
   ????????????? ??????????????? ???????????????
   ?Disk       ? ?Discord      ? ?Your custom  ?
   ?Consumer   ? ?Webhook      ? ?consumer     ?
   ?           ? ?Consumer     ? ?             ?
   ? writes 3  ? ? posts embed ? ? anything    ?
   ? files per ? ? + 3 files   ? ?             ?
   ? client    ? ?             ? ?             ?
   ????????????? ??????????????? ???????????????
```

---

## Files

| File | Role |
|---|---|
| `ClientLogRelay.cs` | Entry point. `ReportArtifacts`, `RegisterConsumer`, `UnregisterConsumer`, `HasConsumers`. |
| `ClientLogArtifacts.cs` | Immutable DTO: platformId, playerName, logBytes, modList, captured-UTC + post-parse fields. |
| `ClientLogRelayPaths.cs` | `GetDefaultClientLogsDir(modId)` ? `{ConfigPath}/{ModId}/ClientLogs` (creates on first use). |
| `IClientLogConsumer.cs` | Interface implemented by anything that wants artifacts. |
| `LogErrorWarningExtractor.cs` | Pure parser: benign filter, dedup, stack-trace aggregation. |
| `ClientLogArtifactWriter.cs` | Pure file writer: 3 files per client into a target dir. |
| `Consumers/DiskConsumer.cs` | Stock consumer that delegates to the writer. |
| `Consumers/DiscordWebhookConsumer.cs` | Stock consumer that builds an embed + multipart POST. |
| `Transport/ClientLogCollector.cs` | Client-side helpers: `ReadLocalBepInExLog()`, `BuildLocalModList()`, `TryCollect(...)`. |
| `Webhook/MinimalWebhookPoster.cs` | Self-contained HTTP multipart POST via UnityWebRequest. |

---

## Dependencies

- `UnityEngine` + `UnityEngine.Networking` (shipped with every BepInEx mod)
- `Newtonsoft.Json` (shipped with Valheim; if your mod targets a different host, any JSON lib
  would do — only used inside `MinimalWebhookPoster.BuildMultipart`).

**No dependencies on**:
- BepInEx configuration APIs
- ConfigSync
- ZNet / ZNetPeer / ZRpc / ZPackage (the relay only deals in `byte[]` and `IDictionary`)
- Any Valheim-specific types

---

## Writing a custom consumer

```csharp
public sealed class MyAnalyticsConsumer : IClientLogConsumer
{
    public string ConsumerId => "YourMod.Analytics";

    public void OnClientArtifacts(ClientLogArtifacts artifacts)
    {
        // Fire your own REST call, write to InfluxDB, post to Sentry — anything.
        // Called on the main thread, so dispatch any long-running work yourself.
    }
}

ClientLogRelay.RegisterConsumer(new MyAnalyticsConsumer());
```

---

## Notes

- **Thread safety:** `RegisterConsumer` / `UnregisterConsumer` / `ReportArtifacts` are
  guarded by a single lock. Consumer invocations happen on the main thread in registration
  order. Consumer exceptions are caught and logged — one failing consumer does not affect
  the others.

- **No RPC:** this module does not touch the network. That is deliberately the calling
  mod's concern because wire-protocol security (replay protection, tampering, rate limits,
  admin bypass) is too mod-specific to generalise.

- **Zero bleed-through:** registered consumers stay registered until the process exits or
  the owning mod calls `UnregisterConsumer`. Hot-reloading BepInEx plugins is not supported
  upstream, so this is the right lifecycle.

- **Clean module:** no `using VerdantsAscent.*` outside of this folder's own namespace. You
  can grep the folder for `using Fires` / `using Verdants` and find zero matches apart from
  `using VerdantsAscent.Modules.ClientLogRelay.*`, which are self-references.
