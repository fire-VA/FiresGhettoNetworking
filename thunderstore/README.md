# FiresGhettoNetworkMod

**Updated for Valheim 1.0.**

A comprehensive networking and server-authority mod for Valheim dedicated servers.
 Combines compression, send-rate tuning, queue management, server-side simulation, RPC filtering,
 and a per-client auto-tuner into a single drop-in plugin.

## What this mod actually does

Valheim's vanilla networking is built around small, friend-group sessions. It works fine for 4 people and starts visibly straining around 8+. This mod replaces the bottlenecks:

- **Server pushes harder and smarter, and with more headroom** — bigger Steam send/recv buffers, higher max send rates, larger send queues, etc
- **Server can run the world** — with Server-Side Simulation on, the server loads the world around every player, drives spawns and raids, and manages zones
- **Server sends less garbage and does less busywork** — filtering on broadcast RPCs, closest-first ZDO sending when a connection backs up, AI LOD, WearNTear skips for unbreakable pieces.
- **Client configs are tuned automatically** — built-in auto-tuner sends test on first login, picks a performance tier, and applies the right values per machine. No more having to read and understand config values

You don't need to be a networking engineer to run a heavily-modded server with this. That's the point... 
I'd rather you not touch your configs at all if you dont understand what they do. 

## What runs in each configuration

The mod is layered. Installing it does not turn everything on — most of the heavy server-authority work sits behind one master switch, and mob simulation sits behind another. Here is exactly what you get.

### Server only (vanilla clients)

Everything here is server-side and needs nothing installed on your players' machines:

- Player limit + advertised player limit, crossplay backend selection
- Steam send rate / send buffer, send queue size, ZDO send rate — ⚠️ the Steam rate and buffer items reach **Steam peers only**
- Per-peer adaptive send rate (each client ramps toward its own real link capacity)
- **Bulk-transfer gate** — raises the 20 KB queue limit inside every loaded ServerSync / ServerCharacters copy, so large config syncs on join stop stalling and dropping peers. This is one of the biggest real-world wins and it is entirely server-side
- Server-side auto-tune, plus all diagnostics and admin commands (`fgn_headroom`, `fgn_flood`, `fgn_socketramp`, `fgn_comptest`, `fgn_zdoflood`, `fgn_links`)
- Everything under **Server-Side Simulation**, if you enable it (see below)

### Server + client

Installing on both adds the client-side half:

- **Deflate packet compression** — negotiated per peer, so it only engages when both ends have the mod. A vanilla client simply never negotiates and stays uncompressed
- Client-side interpolation and prediction — smooths other players' movement
- Client auto-tune: zone-load batching, instantiation budget, receive-buffer sizing, destroy throttling
- HyperBoost receive side (also needs FiresSteamworksPatcher)
- **Boat damage fix** — every 2 seconds the server corrects each player's clock, and vanilla jumps the waves to match, which a boat with players aboard takes as slamming into the water. The mod eases those corrections in so the water never jumps. A ship is damaged by the game of whoever owns it (normally someone aboard), so every player who sails needs the mod
- **The ship follows its helmsman** — whoever takes the helm gets the ship, so steering answers right away instead of going through another player's game (`Give The Ship To Its Helmsman`, on by default). Only the ship's current owner needs the mod

### Default settings

Out of the box you get compression, all the send-rate / queue / buffer tuning, adaptive send rate, the bulk-transfer gate, auto-tune, and player-position priority. A dedicated server also gets these traffic features, each behind its own toggle:

- **RPC router + area-of-interest filtering** — damage numbers, health bars and similar stop being broadcast to everyone and only go to players near the action. This is the big one for large fights
- ZDO delta compression and distance-based ZDO throttling
- AI LOD throttling and WearNTear server CPU skips. These only act on objects the server itself has loaded, so they have little to do unless Server-Side Simulation is on

**`Enable Server-Side Simulation` is OFF by default.** It only controls the server simulating the world. The traffic features above follow their own toggles whether it is on or off.

### Server-Side Simulation ON

Turning on the master switch activates:

- Server-driven spawning and raids / random events
- Extended zone radius and predictive zone streaming, zone create/destroy authority
- Ship steering fixes

Note that mobs are still simulated by the nearest **client** at this point. The server decides when and where they spawn, and filters the traffic, but it is not running their brains.

**With ValheimCommunityPatch on the server, Server-Side Simulation stays off** for that session and the server log says why. VCP replaces the server's object create/destroy pass with its own, built around the world origin, and removes everything the server creates for players, so the two would fight endlessly. The traffic features above keep working either way.

### ZDO ownership transfer — not recommended

This is the switch that moves creature simulation itself onto the server. Ownership is simulation: whoever owns a creature runs its AI, pathing, attacks, movement physics and death checks. There is a broad variant and a Selective one (creatures, plus ships only if you have also enabled server-side ship physics).

**We do not recommend enabling this.** It is the least-tested path in the mod, and handing rigidbody simulation to a headless server has knock-on effects — vehicles are the usual casualty. Every other server-authority feature above works without it. If you do want to experiment, use **Selective** rather than broad, and expect to keep an eye on your boats and carts.

## Auto-Tune (the new feature in 1.2.1)

The mod now ships with an auto-tuner that handles config adjustments. Most users should never need to touch a config setting again.
(only settings that a client should ever touch will be player sync settings per prefernce)

### How the client probe works

When a client connects to a server for the first time, the mod runs a brief network/hardware probe:

1. **Waits for the connection to settle** — downloads finished, no stalls, zones loaded, the link steady for a few seconds (up to 5 minutes on a heavy join). Then it asks the server for the measuring slot, so only one player is measured at a time.
2. **Hardware fingerprint** — CPU cores, RAM, GPU. Scored into a CPU tier.
3. **Latency probe** — 10 ping RPCs, 200ms apart. performance computed over the trimmed samples (worst outlier dropped). Scores into a network tier.
4. **Frame-time sample** — 5 seconds of testing afer the world settles, ignoring the slowest 2% of frames. Scores into an FPS tier.
5. **Bandwidth probe** — 3 × 128KB echo samples, peak wins. A link that moves 500 KB/s or more is never rated LOW, even with a high ping.

The result is one of three tiers — **LOW**, **MED**, or **HIGH** — combining hardware capability and link quality. 
The tier is cached per server address for 7 days; reconnecting picks up where you left off, even after a dedicated server restarts.
A measurement taken while the connection is still busy can raise your tier but never lower it, and an interrupted one retries instead of leaving you on LOW.
The timing is tunable under `[07 - Auto-Tune - Probe]`, but the defaults are what you want.

### How the rolling monitor works

After the initial probe, the mod periodically re-probes latency every 5 minutes. Each result goes into a rolling buffer. This catches:

- **Initial probe got unlucky** (server happened to be busy) — re-probes will see the truth and promote the tier within 15-20 minutes.
- **Mid-session weather changes** (ISP route flap, server load) — tier auto-adjusts down or back up as conditions change.

The cache is updated whenever consensus changes, so the next reconnect starts with the corrected tier.

### How the server self-tune works

When the mod runs on a dedicated server with `Enable Server Auto-Tune = true` (the default), the server scores its own CPU/RAM at startup and applies a server-tier preset to:

- Steam send rate (min and max) and ZDO send rate
- Send queue size
- Steam send buffer size (per-connection outbound during initial sync)
- ZDO update throttle distance
- AI LOD near/far distances
- RPC Area-of-Interest radius
- Extended zone pre-load radius

| Server tier | Send rate | Send buffer | Send queue | ZDO send rate | Throttle distance | AI LOD near / far | RPC AoI | Extended zones |
|---|---|---|---|---|---|---|---|---|
| LOW | 512 KB/s – 2 MB/s | 2 MB | vanilla (10 KB) | 100% | 350 m | 80 / 200 m | 192 m | +0 |
| MED | 768 KB/s – 16 MB/s | 8 MB | 48 KB | 100% | 500 m | 100 / 300 m | 256 m | +1 |
| HIGH | 1 MB/s – 32 MB/s | 32 MB | 80 KB | 150% | 700 m | 150 / 500 m | 384 m | +2 |

Every tier stays at or above vanilla's values; the startup log confirms it with `AutoTune VanillaFloor self-check passed`. 

The server also collects tier reports from connected clients and periodically logs the median client tier with suggestions, 
so an admin can see at a glance whether the server's settings actually match the audience joining.

## Steam vs crossplay (PlayFab) — what each player actually gets

Worth reading before you judge the mod on your own server, because **some of the headline features do nothing at all on a crossplay connection** and there is no version of this mod that fixes that. The knobs do not exist on the other transport.

Valheim has two network backends. Steam-to-Steam players ride `ZSteamSocket`. Anyone on crossplay — Xbox, Game Pass, PlayStation, or a Steam player who joined through a crossplay code — rides `ZPlayFabSocket`. Every socket-level patch in this mod targets `ZSteamSocket`.

### The split

| Feature | Steam | Crossplay (PlayFab) |
|---|---|---|
| Deflate packet compression | ✅ | ❌ |
| Steam send rate Min/Max | ✅ | ❌ rate is PlayFab-governed |
| Send / receive buffer tuning | ✅ | ❌ no connection handle to set |
| HyperBoost + `FiresSteamworksPatcher` unlocks | ✅ | ❌ |
| Keepalive-first send ordering | ✅ | ❌ |
| Ping / connection-quality readouts | ✅ real Steam figures | ⚠️ byte counters only |
| Adaptive send window | ✅ grows with the line | ⚠️ replaced by a fixed safe cap — see below |
| Adaptive Upload / upload-based send rates | ✅ | ❌ — ZDO Send Rate is still set from your upload |
| Bulk-transfer gate (ServerSync unstick) | ✅ | ✅ |
| ZDO delta compression | ✅ | ✅ |
| Distance-based ZDO throttling | ✅ | ✅ |
| AI LOD throttling | ✅ | ✅ |
| RPC Area-of-Interest | ✅ | ✅ |
| WearNTear server optimisation | ✅ | ✅ |
| Server-authority patches (ownership, ships, zones, spawning) | ✅ | ✅ |
| Boat damage fix / helmsman ownership | ✅ | ✅ |
| Client auto-tune (zone batching, instantiation budget) | ✅ | ✅ |

**Rule of thumb:** anything that changes *how the bytes move* is Steam-only. Anything that changes *how many bytes there are* works for everyone. The second group is the larger half of the mod, so a crossplay server still gains plenty — it just does not get the transport tuning.

### Crossplay is handled, not ignored

A crossplay link does not simply fall back to vanilla. It gets a deliberately conservative fixed window instead of the adaptive one, because PlayFab misreports what it is holding:

> `ZPlayFabSocket.GetSendQueueSize` returns a quarter of real in-flight bytes, so a 10 KB gate permits roughly 40 KB outstanding on the transport least able to carry it: PlayFab resends only the oldest unacknowledged packet, three seconds after it goes missing, and its ACKs are cumulative, so everything queued behind a loss waits out that timer.

That is what `Crossplay In-Flight KB` (default 20 KB) controls. It is a real limit in real bytes, and it is deliberately low — a crossplay line recovers from loss far worse than a Steam one, so the mod holds less in flight rather than more.

### The case most likely to mislead you

⚠️ **A server running with `-crossplay` can put even Steam players on a PlayFab socket.** If your players joined through a crossplay code, the Steam column above may not apply to anyone on your server, and comparing two players' experience on the same server can be misleading.

Transport and install location are also two separate filters. A crossplay player loses the Steam column **even with the mod installed**, and a vanilla Steam client loses compression **even though its transport supports it**.

### How to tell which you are on

The mod says so, once per connection, the first time it sees a crossplay peer:

```
[Crossplay] <peer> over crossplay (PlayFab). FGN compression, Steam send rates and the
adaptive send window do not apply to a crossplay connection. Holding 20 KB of real
in-flight data ...
```

`fgn_links` shows the live per-peer picture, and a Steam peer reports real Steam figures where a crossplay peer reports byte counters only.

## Server-side networking improvements

These run on the dedicated server (the mod auto-detects). Effects are visible to all connected clients regardless of whether they have the mod installed.

| Feature | What it does |
|---------|--------------|
| **Deflate compression** | Compresses traffic with each player who also has the mod (negotiated per player). Significant bandwidth saving on busy servers, especially with large modded worlds. |
| **Higher Steam send rates** | Vanilla sends at a fixed ~150 KB/s. Auto-tune opens it up per server tier (table above); with auto-tune off the config default is 512 KB/s – 2 MB/s. Less waiting during big fights or zone bursts. |
| **Larger send queue** | Vanilla stops queueing ZDO updates at ~10 KB. Auto-tune uses 10 / 48 / 80 KB by tier; the config default is 32 KB. Fewer delayed updates under burst load. |
| **Steam SendBufferSize tuning** | Per-connection outbound buffer of 2 / 8 / 32 MB by tier. Directly addresses errors during initial-sync floods with heavy modpacks. |
| **ZDO delta compression** | On resyncs, only the fields that changed are sent — not the whole ZDO. Big savings on creatures/players where 1-2 fields change per tick. Text and byte-array fields (chest contents, for example) are still sent whole. |
| **Distance-based ZDO throttling** | While a player's connection is backing up, player positions are sent first and loose objects beyond the throttle distance (350 / 500 / 700 m by tier) go last. Buildings and terrain keep their place, and a healthy connection is left exactly as vanilla. |
| **AI LOD throttling** | While a player's connection is backing up, creatures the server has loaded that are beyond the far distance (200 / 300 / 500 m by tier) from every player update at half speed (`AI LOD Throttle Factor`). Tames are never throttled, and a healthy server runs every creature at full rate. |
| **WearNTear server optimization** | Skips the wear and support update for pieces that cannot be damaged at all (Infinity Hammer / admin-flagged pieces). Every other piece wears, takes weather damage and collapses exactly like vanilla. |
| **Every player sent each frame** | Vanilla works through one player per frame, so each player's world updates arrive at the frame rate divided by the number of players online. Every player now gets a turn each frame, inside a per-frame time budget. |
| **Send window sized to the connection** | Each player's send window grows while their line is clear and shrinks when it backs up, instead of one fixed queue size for everyone. Crossplay (PlayFab) players are held to a real 20 KB in flight (`Crossplay In-Flight KB`), because PlayFab only reports part of what it has queued. |
| **Live player positions** | The server picks what to send each player from where their character is now, not where it was up to 2 seconds ago (`Live Player Positions`). |
| **Skipping unchanged areas** | Each world update rescans every object around a player for anything unsent, which in a big base is tens of thousands of objects many times a second. Areas where nothing has entered, left or changed since their last scan are skipped, and everything is rescanned every two seconds regardless. |
| **Station inserts** | Ore, coal, food and ammo put into a smelter, kiln, fermenter, cooking station, fire, turret or shield generator are delivered to the station's real owner, so nothing is lost when someone else owns it or has just left. |

## Server-authority patches

Vanilla Valheim relies on whichever client is "near" an object to simulate it. With **Server-Side Simulation** on, the server takes over more of that work:

- **Server loads the world around every player** — objects exist on the server wherever players are, including a configurable extended radius (default +1 zone layer).
- **Spawning and raids run server-side** — the server decides when and where creatures and random events spawn, so raids fire and end properly with players spread out.
- **Creatures go to the best-placed player** - the server measures each player's round trip and moves a creature to whoever is fighting it, or to a nearby player with clearly lower ping, so its movement, attacks and the hits on it are worked out on the machine best placed to do it. The current owner's game hands the creature over itself, the way vanilla hands over a chest someone opens, so no update already in flight can undo the move. Bosses, tames, ridden creatures and creatures mid-attack are never moved, and only players who also have the mod can hand one over. ZDO ownership transfer (see "What runs in each configuration") is a separate, heavier option.

The **RPC Router with area based filtering** does not need Server-Side Simulation: broadcast RPCs targeting a specific position only forward to peers within range. Massive bandwidth saving on busy servers (DamageText, HealthChanged, SetTarget, etc. don't get broadcast worldwide).

All of this is server-side; players don't need anything extra installed for it.

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

### If your upload is the problem

Most of this mod assumes bandwidth is the server's problem. It is not always — a player on a
thin uplink (rural DSL, 4G, a poor ISP) floods their own upstream, their ACKs queue behind their
own outbound, and **they rubber-band for everyone else** while their own game looks fine.

**Auto-Tune handles this, and it is on by default.** On first login it measures your upload as
well as your download, and when your upload is the limit it sets your send rates just under your
real upload speed — below vanilla if that is what your line needs. It also lowers **ZDO Send
Rate** on a thin line, which is the change that actually stops the rubber-banding.

**It writes what it chose into your config**, so the config always shows what is running:

```
[AutoTune] Upload: 180 KB/s against a 2048 KB/s send cap — the line is the limit.
[AutoTune] client High: upload 180 KB/s is the limit, staying under it. Config set to
           Send Rate Max _150KB, Min _150KB, ZDO Send Rate _75, Queue Size _vanilla.
```

**Adaptive Upload** then keeps the live rate under what actually gets through, continuously —
so if someone else in the house starts an upload mid-session, it backs off instead of flooding.

**Want to set it yourself?** Turn off `Enable Client Auto-Tune` under `06 - Auto-Tune`. Your
config keeps the values Auto-Tune last chose, and from then on they are yours. While Auto-Tune is
on, edits to ZDO Send Rate, Queue Size, Send Rate Min and Send Rate Max are replaced on the next
tune — that is what keeps the config and the running values the same.

⚠️ Send Rate Min/Max and Adaptive Upload are **Steam only** — on crossplay the rate belongs to
PlayFab. **ZDO Send Rate** works on both, and Auto-Tune still sets it from your measured upload.

## Reading the log

Every 5 minutes the mod writes a short report, so you can see what it is doing without guessing:

- `[Compression]` — how much traffic compression saved, sent and received.
- `[Links]` (dedicated server) — each player's connection: send window, queue, and round trip broken into stages. `fgn_links` prints it on demand.
- `[Upload]` (clients, with `Log Level` set to Info) — what your game sent to the server, by message and by object type.
- `fgn_rtt` in a client console times a round trip to the server stage by stage.

## Installation

1. Install BepInEx (a recent BepInExPack_Valheim from Thunderstore is recommended).
2. Drop `VAGhettoNetworking.dll` into `BepInEx/plugins/` on every machine that should have it. Keep the server and every client on the same version.
3. Start Valheim or your dedicated server. The mod auto-detects which side it's running on and enables the appropriate features.

## Compatibility

- Designed to coexist with many mods, I use it on my own server with 60 other mods, and have tested it with many others
- Works alongside any content mods that don't touch ZNetScene/ZDOMan internals. (doesn't work with most other networking mods, besides better z log and timeout limits, both highly recommended to go with this)
- If you also run BetterNetworking, Serverside Simulations, or another networking mod — disable one. Running two networking mods at once will produce conflicting patches. This mod covers what those two do.
- ValheimCommunityPatch works alongside it: where both do the same job, this mod stands down and lets VCP handle it (see Server-Side Simulation ON above).


## Credits

Built on the shoulders of BetterNetworking and Serverside Simulations. 
The general approach to ZDO throttling, RPC routing/AoI filtering, and wire compression took inspiration from those mods and from the Comfy Valheim 
BetterZeeRouter pattern. The auto-tuner, rolling monitor, server self-tune, and Steam send-buffer tuning are original to this mod as far as I know...
but theres only so many different ways to do networking mods in valheim 

License: see `LICENSE` in the repo.

## Issues / questions


https://discord.gg/zQRgHbqms4
