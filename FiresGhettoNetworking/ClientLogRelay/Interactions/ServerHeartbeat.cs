using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

namespace VerdantsAscent.Modules.ClientLogRelay.Interactions
{
    /// <summary>
    /// Server crash detection via a live-updating Discord status message.
    /// Based on the proven VerdantsAscent DiscordCrashDetector implementation.
    /// </summary>
    public static class ServerHeartbeat
    {
        private const string DiscordApiBase = "https://discord.com/api/v10";
        private const float HeartbeatIntervalSeconds = 60f;
        private const string SentinelFileName = "server_heartbeat_ghetto.json";
        private const string EmojiRestart = "\uD83D\uDD04";
        private const string EmojiStop = "\u26D4";

        private static string _sentinelPath;
        private static string _statusMessageId;
        private static string _botToken;
        private static string _channelId;
        private static string _statusWebhookUrl;
        private static string _webhookName;
        private static string _brandLabel;
        private static string _botUserId;
        private static string _cachedServerName;
        private static Coroutine _heartbeatCoroutine;
        private static MonoBehaviour _host;
        private static DateTime _serverStartUtc;
        private static bool _cleanShutdown;
        private static readonly HashSet<string> _fulfilledReactions = new HashSet<string>(StringComparer.Ordinal);

        public static void OnServerStart(string botToken, string statusWebhookUrl,
            string channelIdOverride, string webhookName, string brandLabel)
        {
            _botToken = botToken;
            _statusWebhookUrl = statusWebhookUrl;
            _webhookName = webhookName;
            _brandLabel = brandLabel;
            _channelId = channelIdOverride;

            if (string.IsNullOrEmpty(_botToken) || string.IsNullOrEmpty(_channelId))
            {
                Debug.Log("[ServerHeartbeat] BotToken or StatusChannelId not set - heartbeat disabled.");
                InitSentinel();
                SpawnCrashNotifier();
                return;
            }

            _serverStartUtc = DateTime.UtcNow;
            _cleanShutdown = false;
            _statusMessageId = null;

            InitSentinel();
            EnsureHost();
            if (_host == null) return;

            Debug.Log($"[ServerHeartbeat] Starting - server start time: {_serverStartUtc:yyyy-MM-dd HH:mm:ss} UTC");

            // Delete old status message (with stale reactions) and post a fresh one
            _host.StartCoroutine(CleanupAndPostNewMessage());

            SpawnCrashNotifier();
            _host.StartCoroutine(ReactionPollLoop());
        }

        public static void OnServerStop()
        {
            _cleanShutdown = true;
            try { WriteSentinel("stopped"); } catch { }

            if (_heartbeatCoroutine != null && _host != null)
            {
                _host.StopCoroutine(_heartbeatCoroutine);
                _heartbeatCoroutine = null;
            }

            if (!string.IsNullOrEmpty(_statusMessageId) && !string.IsNullOrEmpty(_botToken) && !string.IsNullOrEmpty(_channelId))
            {
                try { EditStatusMessageSync(_statusMessageId, BuildOfflineEmbed()); }
                catch (Exception ex) { Debug.LogWarning($"[ServerHeartbeat] Failed to edit status on shutdown: {ex.Message}"); }
            }
        }

        private static IEnumerator CleanupAndPostNewMessage()
        {
            Debug.Log("[ServerHeartbeat] Cleaning up old status messages...");

            // Fetch recent messages from the status channel
            string url = $"{DiscordApiBase}/channels/{_channelId}/messages?limit=50";

            using (var req = UnityWebRequest.Get(url))
            {
                req.SetRequestHeader("Authorization", $"Bot {_botToken}");
                req.timeout = 15;
                yield return req.SendWebRequest();

                if (req.responseCode == 200)
                {
                    JArray messages;
                    try
                    {
                        messages = JArray.Parse(req.downloadHandler.text);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[ServerHeartbeat] Failed to parse messages: {ex.Message}");
                        messages = null;
                    }

                    if (messages != null && messages.Count > 0)
                    {
                        int deletedCount = 0;

                        // Look for and delete old server status messages
                        foreach (var msg in messages)
                        {
                            var msgObj = msg as JObject;
                            if (msgObj == null) continue;

                            // Check if this is a bot message
                            var author = msgObj["author"] as JObject;
                            if (author == null) continue;

                            bool isBot = author["bot"]?.Value<bool>() ?? false;
                            if (!isBot) continue;

                            // Check if it has server status embeds
                            var embeds = msgObj["embeds"] as JArray;
                            if (embeds == null || embeds.Count == 0) continue;

                            bool isServerStatus = false;
                            foreach (var embedItem in embeds)
                            {
                                var embedObj = embedItem as JObject;
                                string title = embedObj?["title"]?.ToString() ?? "";

                                if (title.Contains("Server Online") || 
                                    title.Contains("Server Offline") || 
                                    title.Contains("Server Restarting") ||
                                    title.Contains("Server Stopped") ||
                                    title.Contains("Server Crash"))
                                {
                                    isServerStatus = true;
                                    break;
                                }
                            }

                            if (!isServerStatus) continue;

                            // Delete this old status message
                            string messageId = msgObj["id"]?.ToString();
                            if (string.IsNullOrEmpty(messageId)) continue;

                            string deleteUrl = $"{DiscordApiBase}/channels/{_channelId}/messages/{messageId}";
                            using (var deleteReq = UnityWebRequest.Delete(deleteUrl))
                            {
                                deleteReq.SetRequestHeader("Authorization", $"Bot {_botToken}");
                                deleteReq.timeout = 10;
                                yield return deleteReq.SendWebRequest();

                                if (deleteReq.responseCode == 204)
                                {
                                    deletedCount++;
                                    Debug.Log($"[ServerHeartbeat] Deleted old status message: {messageId}");
                                }
                            }

                            // Small delay to avoid rate limiting
                            if (deletedCount > 0 && deletedCount % 3 == 0)
                                yield return new WaitForSeconds(0.5f);
                        }

                        if (deletedCount > 0)
                            Debug.Log($"[ServerHeartbeat] Cleaned up {deletedCount} old status message(s)");
                    }
                }
                else
                {
                    Debug.LogWarning($"[ServerHeartbeat] Failed to fetch messages ({req.responseCode})");
                }
            }

            // Post fresh status message
            yield return PostStatusMessage();
        }

        private static IEnumerator PostStatusMessage()
        {
            var embed = BuildOnlineEmbed();
            string json = BuildEmbedPayload(embed);

            // Use webhook API instead of bot API to allow custom username/avatar
            string url = !string.IsNullOrEmpty(_statusWebhookUrl) 
                ? $"{_statusWebhookUrl}?wait=true"  // Webhook with wait=true returns message object
                : $"{DiscordApiBase}/channels/{_channelId}/messages";  // Fallback to bot API

            using (var req = new UnityWebRequest(url, "POST"))
            {
                byte[] body = Encoding.UTF8.GetBytes(json);
                req.uploadHandler = new UploadHandlerRaw(body);
                req.downloadHandler = new DownloadHandlerBuffer();

                // Only set Authorization header if using bot API (not webhook)
                if (string.IsNullOrEmpty(_statusWebhookUrl))
                {
                    req.SetRequestHeader("Authorization", $"Bot {_botToken}");
                }

                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = 15;

                yield return req.SendWebRequest();

                if (req.responseCode == 200 || req.responseCode == 201)
                {
                    try
                    {
                        string responseText = req.downloadHandler?.text;
                        if (!string.IsNullOrEmpty(responseText))
                        {
                            var resp = JObject.Parse(responseText);
                            _statusMessageId = resp.Value<string>("id");
                            Debug.Log($"[ServerHeartbeat] Status message posted to channel {_channelId} (msg ID: {_statusMessageId})");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[ServerHeartbeat] Posted but failed to parse message ID: {ex.Message}");
                    }
                }
                else
                {
                    string errBody = req.downloadHandler?.text ?? req.error;
                    Debug.LogWarning($"[ServerHeartbeat] Failed to post status message ({req.responseCode}): {errBody}");
                }
            }

            // Start heartbeat loop
            _heartbeatCoroutine = _host.StartCoroutine(HeartbeatLoop());

            // Seed reactions immediately (don't wait for poll loop)
            _host.StartCoroutine(SeedReactions());
        }

        private static IEnumerator SeedReactions()
        {
            // Wait a moment for message ID to be set
            yield return new WaitForSeconds(2f);

            if (string.IsNullOrEmpty(_statusMessageId) || string.IsNullOrEmpty(_channelId))
            {
                Debug.LogWarning("[ServerHeartbeat] Cannot seed reactions - message ID or channel ID missing");
                yield break;
            }

            Debug.Log($"[ServerHeartbeat] Seeding control reactions on message {_statusMessageId}");
            yield return AddReaction(_channelId, _statusMessageId, EmojiRestart);
            yield return AddReaction(_channelId, _statusMessageId, EmojiStop);
            Debug.Log("[ServerHeartbeat] Control reactions seeded successfully");
        }

        private static IEnumerator HeartbeatLoop()
        {
            while (!_cleanShutdown)
            {
                yield return new WaitForSecondsRealtime(HeartbeatIntervalSeconds);
                if (_cleanShutdown) yield break;

                try { WriteSentinel("running"); } catch { }

                if (string.IsNullOrEmpty(_statusMessageId)) continue;

                var embed = BuildOnlineEmbed();
                string json = BuildEmbedPayload(embed);

                // If using webhook API, we need to edit via webhook endpoint
                // Webhook URL format: https://discord.com/api/webhooks/{webhook.id}/{webhook.token}
                string url;
                bool useWebhook = !string.IsNullOrEmpty(_statusWebhookUrl);

                if (useWebhook)
                {
                    // Extract webhook ID and token from URL
                    // Format: https://discord.com/api/webhooks/123456789/abcdefg...
                    var parts = _statusWebhookUrl.Split(new[] { "/webhooks/" }, StringSplitOptions.None);
                    if (parts.Length == 2)
                    {
                        url = $"{_statusWebhookUrl}/messages/{_statusMessageId}";
                    }
                    else
                    {
                        // Fallback to bot API if webhook URL is malformed
                        useWebhook = false;
                        url = $"{DiscordApiBase}/channels/{_channelId}/messages/{_statusMessageId}";
                    }
                }
                else
                {
                    // Use bot API
                    url = $"{DiscordApiBase}/channels/{_channelId}/messages/{_statusMessageId}";
                }

                using (var req = new UnityWebRequest(url, "PATCH"))
                {
                    byte[] body = Encoding.UTF8.GetBytes(json);
                    req.uploadHandler = new UploadHandlerRaw(body);
                    req.downloadHandler = new DownloadHandlerBuffer();

                    // Only set Authorization header if using bot API (not webhook)
                    if (!useWebhook)
                    {
                        req.SetRequestHeader("Authorization", $"Bot {_botToken}");
                    }

                    req.SetRequestHeader("Content-Type", "application/json");
                    req.timeout = 10;
                    yield return req.SendWebRequest();

                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        Debug.LogWarning($"[ServerHeartbeat] Heartbeat edit failed ({req.responseCode}): {req.downloadHandler?.text ?? req.error}");
                    }
                }
            }
        }

        private static IEnumerator ReactionPollLoop()
        {
            // Wait for message to be posted and reactions to be seeded
            yield return new WaitForSeconds(10f);
            yield return ResolveBotUserId();

            string[] emojis = { EmojiRestart, EmojiStop };

            while (!_cleanShutdown)
            {
                yield return new WaitForSecondsRealtime(30f); // Increased from 15s to reduce rate limits
                if (_cleanShutdown) yield break;
                if (string.IsNullOrEmpty(_statusMessageId) || string.IsNullOrEmpty(_channelId))
                    continue;

                foreach (string emoji in emojis)
                {
                    string encoded = Uri.EscapeDataString(emoji);
                    string url = $"{DiscordApiBase}/channels/{_channelId}/messages/{_statusMessageId}/reactions/{encoded}?limit=100";

                    using (var req = UnityWebRequest.Get(url))
                    {
                        req.SetRequestHeader("Authorization", $"Bot {_botToken}");
                        req.timeout = 10;
                        yield return req.SendWebRequest();
                        if (req.responseCode != 200) continue;

                        string body = req.downloadHandler?.text;
                        if (string.IsNullOrEmpty(body)) continue;

                        JArray users;
                        try { users = JArray.Parse(body); }
                        catch { continue; }
                        if (users == null) continue;

                        foreach (var userToken in users)
                        {
                            string userId = (userToken as JObject)?.Value<string>("id");
                            if (string.IsNullOrEmpty(userId)) continue;
                            if (!string.IsNullOrEmpty(_botUserId) && userId == _botUserId) continue;

                            string key = $"{_statusMessageId}:{userId}:{emoji}";
                            if (_fulfilledReactions.Contains(key)) continue;
                            _fulfilledReactions.Add(key);

                            string userName = (userToken as JObject)?["username"]?.ToString() ?? userId;
                            Debug.Log($"[ServerHeartbeat] '{userName}' clicked {emoji}");

                            if (emoji == EmojiRestart)
                            {
                                yield return DoRestart(userName);
                                yield break;
                            }
                            else if (emoji == EmojiStop)
                            {
                                yield return DoStop(userName);
                                yield break;
                            }
                        }
                    }
                }
            }
        }

        private static IEnumerator ResolveBotUserId()
        {
            string url = $"{DiscordApiBase}/users/@me";
            using (var req = UnityWebRequest.Get(url))
            {
                req.SetRequestHeader("Authorization", $"Bot {_botToken}");
                req.timeout = 10;
                yield return req.SendWebRequest();

                if (req.responseCode == 200)
                {
                    try
                    {
                        var json = JObject.Parse(req.downloadHandler.text);
                        _botUserId = json["id"]?.ToString();
                        Debug.Log($"[ServerHeartbeat] Bot user ID resolved: {_botUserId}");
                    }
                    catch { }
                }
            }
        }

        private static IEnumerator AddReaction(string channelId, string messageId, string emoji)
        {
            string encoded = Uri.EscapeDataString(emoji);
            string url = $"{DiscordApiBase}/channels/{channelId}/messages/{messageId}/reactions/{encoded}/@me";

            using (var req = UnityWebRequest.Put(url, Array.Empty<byte>()))
            {
                req.method = "PUT";
                req.SetRequestHeader("Authorization", $"Bot {_botToken}");
                // Content-Length is managed automatically by UnityWebRequest
                req.timeout = 10;
                yield return req.SendWebRequest();
            }
        }

        private static IEnumerator DoRestart(string requestedByName)
        {
            Debug.Log($"[ServerHeartbeat] Server RESTART requested by '{requestedByName}'");
            _cleanShutdown = true;
            try { WriteSentinel("restarting"); } catch { }

            var embed = new Dictionary<string, object>
            {
                { "title", "\uD83D\uDD04 Server Restarting..." },
                { "description", $"Restart requested by {requestedByName}" },
                { "color", 15105570 }
            };
            yield return EditStatusMessageCoroutine(_statusMessageId, embed);

            WriteFlag("restart_requested.flag", $"Restart requested by {requestedByName} at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");

            yield return new WaitForSeconds(3f);
            Application.Quit();
        }

        private static IEnumerator DoStop(string requestedByName)
        {
            Debug.Log($"[ServerHeartbeat] Server STOP requested by '{requestedByName}'");
            _cleanShutdown = true;
            try { WriteSentinel("stopped"); } catch { }

            var embed = new Dictionary<string, object>
            {
                { "title", "\u26D4 Server Stopped" },
                { "description", $"Stop requested by {requestedByName}" },
                { "color", 15548997 }
            };
            yield return EditStatusMessageCoroutine(_statusMessageId, embed);

            WriteFlag("stop_requested.flag", $"Stop requested by {requestedByName} at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");

            yield return new WaitForSeconds(3f);
            Application.Quit();
        }

        private static Dictionary<string, object> BuildOnlineEmbed()
        {
            string serverName = GetServerName();
            int playerCount = 0;
            string playerNames = "*No players online*";

            try
            {
                // Thread-safe: snapshot the peer list to avoid collection modification during iteration
                List<ZNetPeer> peerSnapshot = null;
                if (ZNet.instance != null)
                {
                    var peers = ZNet.instance.GetPeers();
                    if (peers != null && peers.Count > 0)
                    {
                        // Create a snapshot to avoid concurrent modification exceptions
                        peerSnapshot = new List<ZNetPeer>(peers);
                    }
                }

                if (peerSnapshot != null && peerSnapshot.Count > 0)
                {
                    playerCount = peerSnapshot.Count;
                    var names = new List<string>();

                    foreach (var peer in peerSnapshot)
                    {
                        // Extra safety: peer could become null between snapshot and iteration
                        if (peer != null && !string.IsNullOrEmpty(peer.m_playerName))
                        {
                            names.Add("\u2022 " + peer.m_playerName);
                        }
                    }

                    if (names.Count > 0)
                    {
                        playerNames = string.Join("\n", names);
                    }
                }
            }
            catch (Exception ex)
            {
                // Log the error so we can debug if this is the crash source
                Debug.LogWarning($"[ServerHeartbeat] BuildOnlineEmbed exception: {ex.Message}");
            }

            var uptime = DateTime.UtcNow - _serverStartUtc;
            string uptimeStr = uptime.TotalHours >= 1
                ? $"{(int)uptime.TotalHours}h {uptime.Minutes}m"
                : $"{(int)uptime.TotalMinutes}m";

            return new Dictionary<string, object>
            {
                { "title", "\uD83D\uDFE2 Server Online" },
                { "description", $"**{serverName}** is running." },
                { "color", 5763719 },
                { "fields", new List<object>
                    {
                        new Dictionary<string, object> { { "name", $"Players Online ({playerCount})" }, { "value", playerNames }, { "inline", false } },
                        new Dictionary<string, object> { { "name", "Uptime" }, { "value", uptimeStr }, { "inline", true } },
                        new Dictionary<string, object> { { "name", "Last Heartbeat" }, { "value", DateTime.UtcNow.ToString("HH:mm:ss") + " UTC" }, { "inline", true } },
                    }
                },
                { "footer", new Dictionary<string, object> { { "text", $"Updates every 60s \u00B7 {_brandLabel ?? "ServerHeartbeat"}" } } }
            };
        }

        private static Dictionary<string, object> BuildOfflineEmbed()
        {
            string serverName = GetServerName();
            var uptime = DateTime.UtcNow - _serverStartUtc;
            string uptimeStr = uptime.TotalHours >= 1
                ? $"{(int)uptime.TotalHours}h {uptime.Minutes}m"
                : $"{(int)uptime.TotalMinutes}m";

            return new Dictionary<string, object>
            {
                { "title", "\uD83D\uDD34 Server Offline" },
                { "description", $"**{serverName}** shut down cleanly." },
                { "color", 15548997 },
                { "fields", new List<object>
                    {
                        new Dictionary<string, object> { { "name", "Session Duration" }, { "value", uptimeStr }, { "inline", true } },
                        new Dictionary<string, object> { { "name", "Stopped At" }, { "value", DateTime.UtcNow.ToString("HH:mm:ss") + " UTC" }, { "inline", true } },
                    }
                },
                { "footer", new Dictionary<string, object> { { "text", _brandLabel ?? "ServerHeartbeat" } } }
            };
        }

        private static string BuildEmbedPayload(Dictionary<string, object> embed)
        {
            var payload = new Dictionary<string, object>
            {
                { "embeds", new List<object> { embed } },
                { "username", "Verdant's Ascent ChatBot" },
                { "avatar_url", "https://cdn.discordapp.com/avatars/1493374832941858966/2c0dfcb966e9aa4e8b66e8b0f4e1c7e8.png" }
            };
            return JsonConvert.SerializeObject(payload);
        }

        private static IEnumerator EditStatusMessageCoroutine(string messageId, Dictionary<string, object> embed)
        {
            if (string.IsNullOrEmpty(messageId) || string.IsNullOrEmpty(_channelId))
                yield break;

            string json = BuildEmbedPayload(embed);
            string url = $"{DiscordApiBase}/channels/{_channelId}/messages/{messageId}";

            using (var req = new UnityWebRequest(url, "PATCH"))
            {
                byte[] body = Encoding.UTF8.GetBytes(json);
                req.uploadHandler = new UploadHandlerRaw(body);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Authorization", $"Bot {_botToken}");
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = 10;
                yield return req.SendWebRequest();
            }
        }

        private static void EditStatusMessageSync(string messageId, Dictionary<string, object> embed)
        {
            string json = BuildEmbedPayload(embed);
            string url = $"{DiscordApiBase}/channels/{_channelId}/messages/{messageId}";

            try
            {
                using (var client = new System.Net.WebClient())
                {
                    client.Headers.Add("Authorization", $"Bot {_botToken}");
                    client.Headers.Add("Content-Type", "application/json");
                    client.UploadString(url, "PATCH", json);
                }
            }
            catch { }
        }

        private static void InitSentinel()
        {
            try
            {
                string dir = Path.Combine(BepInEx.Paths.ConfigPath, "VAGhettoNetworking");
                Directory.CreateDirectory(dir);
                _sentinelPath = Path.Combine(dir, SentinelFileName);
                WriteSentinel("running");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ServerHeartbeat] Sentinel init failed: {ex.Message}");
            }
        }

        private static void WriteSentinel(string status)
        {
            if (string.IsNullOrEmpty(_sentinelPath)) return;
            int pid = Process.GetCurrentProcess().Id;
            string messageId = _statusMessageId ?? "";
            string json = $"{{\"status\":\"{status}\",\"heartbeat\":\"{DateTime.UtcNow:O}\",\"pid\":{pid},\"messageId\":\"{messageId}\"}}";
            File.WriteAllText(_sentinelPath, json);
        }

        private static void SpawnCrashNotifier()
        {
            try
            {
                if (string.IsNullOrEmpty(_statusWebhookUrl) || string.IsNullOrEmpty(_sentinelPath))
                    return;

                int pid = Process.GetCurrentProcess().Id;
                string serverName = GetServerName();

                string escapedSentinelPath = _sentinelPath.Replace("'", "''");
                string escapedWebhookUrl = _statusWebhookUrl.Replace("'", "''");
                string escapedServerName = serverName.Replace("'", "''").Replace("\"", "\\\"");
                string escapedDisplayName = (_webhookName ?? "Valheim Server").Replace("'", "''");

                string watchdogScript =
                    "try{ (Get-Process -Id " + pid + " -EA SilentlyContinue).WaitForExit() }catch{}\n" +
                    "Start-Sleep 3\n" +
                    "$s=Get-Content '" + escapedSentinelPath + "' -Raw -EA SilentlyContinue\n" +
                    "if($s -notmatch '\"running\"'){exit}\n" +
                    "$t=(Get-Date).ToUniversalTime().ToString('HH:mm:ss')\n" +
                    "$emoji=[char]::ConvertFromUtf32(0x1F4A5)\n" +
                    "$b=@{username='" + escapedDisplayName + "';embeds=@(@{title=\"\"$emoji Server Crash Detected\"\";description=\"\"**" + escapedServerName + "** crashed or was killed.\"\";color=15105570;fields=@(@{name='Detected At';value=\"\"$t UTC\"\";inline=$true});footer=@{text='" + (_brandLabel ?? "ServerHeartbeat") + "'}})}|ConvertTo-Json -Depth 5 -Compress\n" +
                    "try{(New-Object Net.WebClient).UploadString('" + escapedWebhookUrl + "','POST',$b)|Out-Null}catch{}\n" +
                    "try{Set-Content '" + escapedSentinelPath + "' '{\"status\":\"crashed\"}' -EA SilentlyContinue}catch{}";

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -Command -",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    CreateNoWindow = true,
                };

                var proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.StandardInput.Write(watchdogScript);
                    proc.StandardInput.Close();
                    Debug.Log($"[ServerHeartbeat] Crash notifier spawned (watchdog PID {proc.Id}, monitoring server PID {pid})");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ServerHeartbeat] Failed to spawn crash notifier: {ex.Message}");
            }
        }

        private static void WriteFlag(string filename, string content)
        {
            try
            {
                // Write to BepInEx\plugins\ root, not the mod subfolder
                string pluginsDir = Path.Combine(BepInEx.Paths.BepInExRootPath, "plugins");
                string flagPath = Path.Combine(pluginsDir, filename);
                File.WriteAllText(flagPath, content);
                Debug.Log($"[ServerHeartbeat] Wrote flag: {flagPath}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ServerHeartbeat] Failed to write flag '{filename}': {ex.Message}");
            }
        }

        private static string GetServerName()
        {
            if (!string.IsNullOrEmpty(_cachedServerName))
                return _cachedServerName;

            try
            {
                string[] args = System.Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (args[i].Equals("-name", StringComparison.OrdinalIgnoreCase))
                    {
                        _cachedServerName = args[i + 1];
                        return _cachedServerName;
                    }
                }

                string worldName = ZNet.instance?.GetWorldName();
                if (!string.IsNullOrEmpty(worldName))
                {
                    _cachedServerName = worldName;
                    return _cachedServerName;
                }
            }
            catch { }

            _cachedServerName = "Unknown";
            return _cachedServerName;
        }

        private static void EnsureHost()
        {
            if (_host != null) return;
            var host = new GameObject("ServerHeartbeat_Host");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            _host = host.AddComponent<HeartbeatHost>();
        }

        private class HeartbeatHost : MonoBehaviour { }
    }
}
