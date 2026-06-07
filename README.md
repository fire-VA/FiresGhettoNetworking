# FiresGhettoNetworkMod

A comprehensive networking and server-authority mod for Valheim dedicated servers.
 Combines compression, send-rate tuning, queue management, server-side simulation, RPC filtering,
 and a per-client auto-tuner into a single drop-in plugin.

## What this mod actually does

Valheim's vanilla networking is built around small, friend-group sessions. It works fine for 4 people and starts visibly straining around 8+. This mod replaces the bottlenecks:

- **Server pushes harder and smarter, and with more headroom** — bigger Steam send/recv buffers, higher max send rates, larger send queues, etc
- **Server runs the world authoritatively** — server creates/destroys objects for all peers, runs AI, drives spawns, manages zones. Less work on each client.
- **Server sends less garbage to clients** — filtering on broadcast RPCs, distance-based ZDO update throttling, AI LOD, WearNTear short-circuits for infinite health pieces.
- **Client configs are tuned automatically** — built-in auto-tuner sends test on first login, picks a performance tier, and applies the right values per machine. No more having to read and understand config values

You don't need to be a networking engineer to run a heavily-modded server with this. That's the point... 
I'd rather you not touch your configs at all if you dont understand what they do. 

## Auto-Tune (the new feature in 1.2.1)

The mod now ships with an auto-tuner that handles config adjustments. Most users should never need to touch a config setting again.
(only settings that a client should ever touch will be player sync settings per prefernce)

### How the client probe works

When a client connects to a server for the first time, the mod runs a brief network/hardware probe:

1. **30-second delay** after spawn — lets Valheim's initial-sync flood drain 
2. **Hardware fingerprint** — CPU cores, RAM, GPU. Scored into a CPU tier.
3. **Latency probe** — 10 ping RPCs, 200ms apart. performance computed over the trimmed samples (worst outlier dropped). Scores into a network tier.
4. **Frame-time sample** — 5 seconds of testing afer the world settles. Scores into an FPS tier.
5. **Bandwidth probe** — 3 × 32KB echo samples, peak wins. Only runs if latency tier is MED or HIGH (this is to keep clients on lower end machines from being effected by the tests).

The result is one of three tiers — **LOW**, **MED**, or **HIGH** — combining hardware capability and link quality. 
The tier is cached per-server for 7 days; reconnecting picks up where you left off.

### How the rolling monitor works

After the initial probe, the mod periodically re-probes latency every 5 minutes. Each result goes into a rolling buffer. This catches:

- **Initial probe got unlucky** (server happened to be busy) — re-probes will see the truth and promote the tier within 15-20 minutes.
- **Mid-session weather changes** (ISP route flap, server load) — tier auto-adjusts down or back up as conditions change.

The cache is updated whenever consensus changes, so the next reconnect starts with the corrected tier.

### How the server self-tune works

When the mod runs on a dedicated server with `Enable Server Auto-Tune = true` (the default), the server scores its own CPU/RAM at startup and applies a server-tier preset to:

- ZDO update throttle distance
- AI LOD near/far distances
- RPC Area-of-Interest radius
- Extended zone pre-load radius
- Send queue size
- Steam send buffer size (per-connection outbound during initial sync)

The send-rate **Min** stays at a safe baseline regardless of server tier so Steam can always back off 

The server also collects tier reports from connected clients and periodically logs the median client tier with suggestions, 
so an admin can see at a glance whether the server's settings actually match the audience joining.

## Server-side networking improvements

These run on the dedicated server (the mod auto-detects). Effects are visible to all connected clients regardless of whether they have the mod installed.

| Feature | What it does |
|---------|--------------|
| **ZSTD compression** | Compresses outbound traffic with a tuned dictionary. Significant bandwidth saving on busy servers, especially with large modded worlds. |
| **Higher Steam send rates** | Vanilla caps at ~150 KB/s; we open Min/Max up to 1024 KB/s. Less waiting during big fights or zone bursts. |
| **Larger send queue** | Default 80KB instead of vanilla ~10KB. Fewer dropped updates under burst load. |
| **Steam SendBufferSize tuning** | Per-connection outbound buffer up to 2MB. Directly addresses errors during initial-sync floods with heavy modpacks. |
| **ZDO delta compression** | On resyncs, only the fields that changed are sent — not the whole ZDO. Big savings on creatures/players where 1-2 fields change per tick. |
| **Distance-based ZDO throttling** | Distant objects (creatures/structures beyond ~500m) update at a lower rate. Combat-range objects stay full speed. |
| **AI LOD throttling** | Distant AI runs FixedUpdate at half speed. Server CPU saving with possible visible effect for nearby players depending on throttle distance. |
| **WearNTear server optimization** | Skips structural support recalculation for full-health/Inf health or fully intact pieces. Big CPU win on servers with large built bases. |

## Server-authority patches

Vanilla Valheim relies on whichever client is "near" an object to simulate it. This mod makes the server authoritative instead:

- **Server creates/destroys objects for all peers** — client CPU stays free, world state is consistent across all players.
- **Zone management runs server-side** — server pre-loads zones around every connected player, including a configurable extended radius (default +1 layer). Smoother zone crossings.
- **Server runs all AI** — monsters update even when no player is "owning" the zone. Recently adjusted to fix raids not firing with server authority on.
- **RPC Router with area based filtering** — broadcast RPCs targeting a specific position only forward to peers within range. Massive bandwidth saving on busy servers (DamageText, HealthChanged, SetTarget, etc. don't get broadcast worldwide).

Server authority is **server-only** behavior. 
Clients receive the results but don't run the simulation themselves.

## Client-side settings (the only ones you should touch you heathens)

If you're a player joining someone else's server, your config has dozens of settings...
 but **the ONLY section relevant to you is `Player Sync`**. 
 Everything else is either server-driven or auto-tuned.
 If you believe you know best, feel free to adjust, but adjusting settings incorrectly 
 can easily make the mod have the opposite effect from intended...

The Player Sync section has four options:

- **Enable Client-Side Interpolation** *(default: off)*
  Smooths other players' movement on your screen by interpolating between received network positions, eliminating the small snapping that comes from discrete network ticks. Most players don't need this — the rest of the mod's improvements deliver positions smoothly enough on their own, and interpolation adds a small render-lag. Turn ON if you still see other players snap/teleport on a healthy connection.

- **Enable Client-Side Prediction** *(default: off)*
  Extrapolates other players forward using their last known velocity. Helps on high-latency (>100ms) connections where interpolation alone can't cover the gap. Auto-tune may flip this on automatically if your measured ping warrants it. Leave it off otherwise — it can overshoot at low ping.

- **Smoothing Min Interval** *(default: 0.0s)*
  Below this inter-packet arrival rate, smoothing is disabled and vanilla movement renders directly. 0 means "always smooth"; raise it only on LAN/very low-latency servers if you want fast packets to bypass smoothing.

- **Smoothing Max Interval** *(default: 0.20s)*
  Above this inter-packet arrival rate, smoothing runs at full strength. Between Min and Max the strength fades in linearly. Raise for a more aggressive fade (less smoothing on borderline connections); lower for a snappier ramp to full smoothing.

**Everything else in the config is either server-only or auto-tuned.** 
The mod automatically disables server-only features on clients regardless of what you set.
so that you can't manually toggle `Enable Server-Side Simulation` on your client,
but im not smart enough to know how to hide the config to only clients while allowing admins and servers to see it. 

## Installation

1. Install BepInEx (a recent BepInExPack_Valheim from Thunderstore is recommended).
2. Drop `VAGhettoNetworking.dll` into `BepInEx/plugins/` on every machine that should have it.
3. Start Valheim or your dedicated server. The mod auto-detects which side it's running on and enables the appropriate features.

## Compatibility

- Designed to coexist with many mods, I use it on my own server with 60 other mods, and have tested it with many others
- Works alongside any content mods that don't touch ZNetScene/ZDOMan internals. (doesn't work with most other networking mods, besides better z log and timeout limits, both highly recommended to go with this)
- If you also run BetterNetworking, Serverside Simulations, or another networking mod — disable one. Running two networking mods at once will produce conflicting patches. This mod covers what those two do.


## Credits

Built on the shoulders of BetterNetworking and Serverside Simulations. 
The general approach to ZDO throttling, RPC routing/AoI filtering, and ZSTD wire compression took inspiration from those mods and from the Comfy Valheim 
BetterZeeRouter pattern. The auto-tuner, rolling monitor, server self-tune, and Steam send-buffer tuning are original to this mod as far as I know...
but theres only so many different ways to do networking mods in valheim 

License: see `LICENSE` in the repo.

## Issues / questions


https://discord.gg/zQRgHbqms4