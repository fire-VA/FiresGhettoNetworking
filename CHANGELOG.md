## 1.6.65
- Less work standing in big bases: when nothing near you is waiting to load or unload, the game's 30-per-second check over every nearby object runs 10 times a second instead (new setting "Skip Idle Object Area Passes", on). (performance)

## 1.6.64
- FGN now keeps network-synced bodies' kinematic state right itself (the "Kinematic sync sweep" setting works on its own). (performance)
- Mods can ask FGN to stream chosen objects far beyond the active area, and to wait for FGN's bandwidth test before a big transfer, through FGN's own API. (network)

## 1.6.63
- FGN no longer looks for any other mod. Mods that show FGN's stats, keep their creatures at full speed, or use the kinematic sync sweep setting now read or register through FGN's own API. (network)

## 1.6.62
- RPC census names come from registrations and the RPCs this game sends, no longer from scanning loaded assemblies. (logs)
- Server object removal no longer looks for any other mod: a mod that keeps objects loaded on a server registers through FGN's own API instead. (network)

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

## 1.6.44
- Fast Leave Cleanup is now on by default: a player leaving no longer stalls the server (about 75-95 ms saved per leave on a big world).

## 1.6.42
- "Remote Motion (experimental)" was accidentally set to on by default at some point, and an older update could also switch it on by itself in settings you already had. If other players look jittery when they walk, turn it off in FGN's settings. New installs now have it off by default, and updates no longer switch it on.

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
