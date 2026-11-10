## 1.6.84

### Changed
- Single-player features moved to FiresFixes. FiresGhettoNetworking is now networking only. These are: fireplace fuel, sleep skip, spawn and terrain-compiler guards, spread-out terrain rebuilds, spread-out dungeon loading, saved-data reads, the tame check command, ground snap for clients and hosts, the invulnerable-piece support skip, and the cape and fall-through checks. FiresFixes uses the SAME config keys, so install FiresFixes 0.1.91 or newer to keep them. A dedicated server keeps its own ground snap in FiresGhettoNetworking.
- When EasyBakeMeshes' object pacer is running, FiresGhettoNetworking steps aside and lets it pace object creation. Without EasyBakeMeshes, FiresGhettoNetworking paces as before.
- Fill speed adapts to your machine ("Adaptive Fill Budget", on): the create budget is your target frame time minus the other work measured that frame. Standing still or loading fills faster; moving uses only spare time.

### New
- Server settings: Server Simulation Distance Ceiling (-1 = vanilla), Resources Go To Whoever Hits Them (off), Judge Peers By Update Gaps (off), Creatures Go To The Player Touching Them (off), and Server Hands Ship To Helmsman For Unmodded Owners (on).
- Send Portal Destination Early (off): when a client walks up to a connected portal, the server sends the ground at the far end ahead of time.
- Teleport loading only answers "ready" for objects whose own zone is loaded, so you don't arrive before the ground is there.
- When a connection backs up, other players' creatures are sent first (on by default).
- PvP Lag-Fair Dodge (off by default).

### Fixed
- If one feature can't attach (for example after a game update), only that one is removed and named, and the rest still work.

### Performance
- Clients: the removal scan no longer copies every nearby object into a list up to 10 times a second ("Removal Scan Without Area Set", on).
- The kinematic sync sweep only walks objects that actually sync, a slice per frame.
- The distant create pass walks a moving window of 4,000 entries instead of the whole list.
- Hosts with no other players skip owner-safety work on writes. Hosts build the leave-cleanup list when the first player joins.

### Also in this release (1.6.84)
- New setting [10 - Server Authority] Creature Ownership In Fights (only used with Balance Creature Ownership on): Vanilla (default, a creature in a fight is never moved, as before), LowestPing (fights don't stop the hand-off to the lowest-ping player), or FirstFighter (the first player to hit a creature keeps it until the fight ends).

## 1.6.51
- Fast Leave Cleanup finds its game method safely on every game build. Hexium's store check showed a false "expected to be broken" warning for 1.6.50; nothing was broken, and this version clears it. (fixes)

## 1.6.50
- **Fixed: new areas could generate with no trees, rocks or plants** in single-player or when hosting from the game (FGN 1.6.9 - 1.6.27). "Spread Terrain Rebuilds" delayed the ground of a brand-new area, and the game placed its trees before the ground existed. New areas now build their ground at once. Areas that already generated bare stay bare in that world. Dedicated servers were not affected. (fixes)
- Companion keep-alive and homestead settings move to FiresCompanions; they arrive with its next update. Until then, server keep-alive of companion trip areas is off. (network)
- Water and terrain at portal arrivals and teleports line up again (no seams). (fixes)
- Fermenters, cooking stations and shield generators: refunds and requests reach a headless server and name the right item. (fixes)
- Cleaner logs: normal play shows one-time events and warnings; test lines need Log Level Debug. (logs)
- "Remote Motion (experimental)" is off by default again, and updates no longer switch it on. If other players looked jittery when walking, this was why. (network)
- "Fast Leave Cleanup" is on: a player leaving no longer stalls the server (about 75-95 ms saved per leave on a big world). (performance)
- Faster object creation when arriving in an area, and smoother play while moving. (performance)
- Fewer warnings with WackysDatabase broken item clones (handled, nothing lost). (logs)

If you can't update yet: set "Spread Terrain Rebuilds = false" under [02 - Client Performance] in BepInEx\config\com.Fire.FiresGhettoNetworkMod.cfg, with the game closed.

## 1.6.28
- The keep-alive and homestead settings now live in FiresCompanions. Update FiresCompanions to 0.1.123 or newer with this version.

## 1.6.27
- Feasts redraw correctly when the whole stack arrives from another player ("Redraw Feasts When Their Stack Arrives", on). (fixes)
- The server takes control of wild creatures back from players so they behave the same for everyone. Tamed creatures stay with their players. (network)
- New "Server Simulates Tamed Creatures Too" (off by default): following tames stay with their player, idle tames and NPCs move to the server. (network)
- "Kinematic sync sweep" now lives in FGN's [Performance] section, on by default, with your old value from FiresCompanions or Core carried over. (performance)
- Creature and ship ownership now also covers prefabs that other mods register late. (network)
- Performance fixes for idle creatures, ships and random events. (performance)

## 1.6.9
- New "Spread Dungeon Room Spawns": dungeon rooms spawn over several frames.
- Time-Slice is now on by default (also for local hosts), with heavy objects created first and alone.
- "Spread Terrain Rebuilds" is on by default and covers every rebuild.
- Carts stay put on wall hangers, in buildings and on ramps.
- Dedicated servers run smoother; quieter logs.

## 1.5.78
- Bug fixes and improvements.

## 1.5.72
- Fires companions are never taken over by the server or moved between players any more, so a wild companion no longer T-poses or floats.
- With "Server ZDO Ownership" on, picking up items and editing terrain work again.
- "Balance Creature Ownership" no longer moves a creature in the middle of a fight, so group fights stay in sync.
- Players joining a big or brand-new world are no longer dropped while it downloads or builds.
- "Refuse Mismatched FGN Clients" only kicks players on a different FGN version, never a player who is still loading.
- Smelters, kilns, fires and other stations no longer lose or duplicate items when players don't have FGN.
- Cleaner settings screen: the basics show first, the rest is under Show Advanced (your saved values are kept).

## 1.5.65
- Lower CPU use, especially when playing alone.
- Fewer hitches near terrain changes, and a fix for an error when joining.
- Picking up items works in Server-Side Simulation areas.
- With Fires Unified Core installed, FGN shows players, upload, download and ping on the hub's Stats tab.

## 1.5.53
- Fixes and improvements.

## 1.5.51
- Works on Game Pass and the Microsoft Store version of Valheim (no Steam needed).

## 1.5.49
- Steam-only features are skipped where Steam isn't available.

## 1.5.41
- Server tuning suggestions are no longer written to the log by default.
- Fixed crossplay join codes stalling, a false version warning when rejoining, and an error with AzuAntiCheat.

## 1.5.36
- Fixed shield generator error spam on Server-Side Simulation servers.
- Fixed world data wipes with Expand World Prefabs, and players snapping back after teleporting.

## 1.5.28
- Saddles and taming work on Server-Side Simulation servers.
- Clearer setting descriptions.

## 1.5.19
- Fixed broken objects when used with ValheimCommunityPatch.
- Server-Side Simulation stays off when Valheim Performance Optimizations is installed.

## 1.5.17
- Better crossplay connections; crossplay servers can be joined by IP again.
- Fewer hitches, and joining a server is about half a second faster.
- Smarter auto-tune that measures one player at a time and remembers each server.
- Whoever takes the helm gets the ship, so steering answers right away.
- Update the server and every client to the same version.

## 1.4.31
- Updated for Valheim 1.0.
- Monsters go to the player with the best connection, so they stop rubber-banding.
- Chat, effects and damage numbers only go to players who can see them.
- Boats no longer sink or take damage on calm water or with players aboard.
- Tamed creatures stay in their pens, and crops and building wear work normally.
- Sleeping and waking on a busy server is quick again.

## 1.3.10
- Fixed an error flood on dedicated servers while pre-generating a world.
- Raids and random events no longer run forever on dedicated servers.
- Fixed crossplay servers failing to connect after installing FGN.
- Fewer "Disconnected" drops on busy servers.
