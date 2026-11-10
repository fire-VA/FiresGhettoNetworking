# Fires Ghetto Networking

**Updated for Valheim 1.0.**

Smoother multiplayer for Valheim servers. Vanilla networking is built for a handful of friends and starts to strain around 8 players. This mod works on the bottlenecks in one drop-in plugin, and tunes itself so you don't have to.

**Fires Ghetto Networking is a networking mod only.** The general fixes it used to carry for single player and performance have moved to **Fires Easy Bake Meshes** and **Fires Fixes**, with the same config section and setting names. It doesn't need either of them, and neither of them needs it.

## What's new in 1.6.84

Changes since 1.6.51, in plain words:

- **Networking only now.** These features moved to Fires Fixes (0.1.91 or newer), because they help single player and hosts as much as servers: slower fuel saving on long-burning fires, the sleep skip, the spawn and terrain guards, spread-out terrain rebuilds, spread-out dungeon loading, faster reading of saved objects, the tame check command, the ground snap fix for players and hosts, the support skip for pieces that can't be damaged, and the cape and fall-through log checks. Fires Fixes uses the same setting names. A dedicated server keeps its own ground snap here. If you used any of these, install Fires Fixes as well.
- **Object loading around you.** When Fires Easy Bake Meshes is pacing how fast objects are created around you, this mod steps aside and lets it. Without it, this mod paces them as before, now adjusting to your machine ("Adaptive Fill Budget", on): more while you stand still or load, only the spare time while you move.
- **New setting: "Creature Ownership In Fights"** (see Creature ownership below).
- **Busy connections keep the important things first.** When a player's connection is backed up, creatures and players run by someone else are still sent to them first, as the game intends ("Keep Other Players Creatures First When Congested", on).
- **New server settings**, all for dedicated servers:
  - "Server Simulation Distance Ceiling" (-1 = vanilla): an admin limit on how far players' games may run the world around them.
  - "Resources Go To Whoever Hits Them" (off): a tree, log or rock looked after by another player's game is handed to the player chopping or mining it, so hits don't feel late.
  - "Judge Peers By Update Gaps" (off): spots a player whose game keeps stalling from gaps in what it sends, not only from ping.
  - "Creatures Go To The Player Touching Them" (off).
  - "Server Hands Ship To Helmsman For Unmodded Owners" (on): when the player who owns a ship doesn't have this mod, the server gives the ship to whoever takes the helm, so steering doesn't lag.
- **Portals and teleports.** "Send Portal Destination Early" (off): while a player stands at a connected portal, the server starts sending what's at the far end in spare bandwidth. Needs the mod on the server and the player. Teleport loading also now waits until the ground at the destination is there, so you don't arrive before it.
- **Safer after game updates.** If one feature can't attach, only that one is removed and named in the log, and the rest keep working.
- **Less work in the background:** fewer copies of the nearby-object list on players' games, a lighter sweep over moving objects, a smaller window for distant objects, and hosts with nobody else connected skip work they don't need.

## What you get

- **Less lag in busy fights and big bases**: compressed traffic, higher send rates, and the closest things sent first when a connection backs up.
- **Auto-Tune**: on first join it checks your PC and connection and picks the right settings. The server tunes itself too.
- **Less wasted traffic**: damage numbers, health bars and effects only go to players near the action.
- **Boat fixes**: no more random boat damage or sinking on calm water, and whoever takes the helm steers without delay.
- **Station fix**: smelters, kilns, fires and other stations no longer lose or duplicate items, even for players without the mod.
- **Join fixes**: slow PCs loading a big world aren't dropped, and big config syncs no longer stall joins.
- **Optional smoother other players** ("Remote Motion", off by default): other players' movement can be drawn more smoothly when they have the mod too.
- **Optional Server-Side Simulation**: the dedicated server runs the world around every player (spawns, raids, zones).

## Install

- **Safe on any side: server only, players only, or both.** Server-only works, and players without the mod can still join.
- Installing on both sides adds compression and the boat fix.
- Keep the server and every client on the same version.
- Put `VAGhettoNetworking.dll` in `BepInEx/plugins/`. The mod detects whether it is running on a server or a client.

## Requires

- BepInExPack for Valheim. Nothing else.

## Settings

Most people should not touch the config. Auto-Tune handles it. The settings you see by default:

- `[01 - General] Log Level`
- `[04 - Networking] Enable Compression`
- `[06 - Auto-Tune] Enable Client Auto-Tune`, `Enable Server Auto-Tune`
- `[09 - Dedicated Server] Force Crossplay`, `Player Limit`, `Advertised Player Limit`
- `[10 - Server Authority] Enable Server-Side Simulation` (off by default)
- `[13 - Compatibility] Refuse Mismatched FGN Clients` (off by default)
- `[03 - Player Sync] Remote Motion (experimental)` (off by default)

Everything else is under **Show Advanced** in the config manager.

**Players:** the only section meant for you is `Player Sync`. Everything else is server-driven or auto-tuned, and server-only settings are ignored on a client.

## Server-Side Simulation

Off by default. Turn on `Enable Server-Side Simulation` and the server loads the world around every player, decides when and where creatures and raids spawn, and handles zones. Monsters are still run by the nearby players' games.

**Server ZDO Ownership** goes further and moves creatures onto the server. It is off by default, and **we don't recommend it**: it is the least-tested option and can cause trouble with boats and carts. If you try it, use **Selective**. Tamed creatures always stay with the players.

## Creature ownership

In vanilla, each creature is run by one player's game. All of these options are **off by default**, so creatures behave as in vanilla until you change them. They're under `[10 - Server Authority]` in Show Advanced.

- **Balance Creature Ownership** (off): hands idle creatures to the nearby player with a clearly lower ping, so they rubber-band less. "Ownership Ping Margin" (40 ms) sets how much lower that ping must be.
- **Creature Ownership In Fights** (Vanilla): decides what happens to a creature that is fighting. It only does anything with Balance Creature Ownership on and server ownership off.
  - **Vanilla** (default): a creature in a fight is never moved; it stays with whoever has it, as before.
  - **LowestPing**: a fight doesn't stop the hand-off; the creature may move to the nearby player with a clearly lower ping, fight or not.
  - **FirstFighter**: the first player to hit the creature takes it and keeps it until the fight ends.
- **Creatures Go To The Player Touching Them** (off): with Balance Creature Ownership on, an idle creature a player stands right next to goes to that player when its owner is further away. Fights still keep it where it is.

## Steam vs crossplay

Players on crossplay (Xbox, Game Pass, PlayStation, or anyone who joined with a crossplay code) don't get compression or the Steam send-rate tuning; those only exist on Steam connections. Everything that cuts how much is sent still works for them. A server started with `-crossplay` can put even Steam players on crossplay. `fgn_links` in the console shows who is on what.

## Admin commands

- `fgn_links` - each player's connection.
- `fgn_rtt` - times a round trip to the server.
- `fgn_netsim` - adds fake lag to your own connection for testing.
- `fgn_sss_areas N` - simulates N extra busy areas to see what Server-Side Simulation would cost (`0` clears it).

## Compatibility

- Works on its own. It doesn't rely on Fires Easy Bake Meshes or Fires Fixes, and they don't rely on it. Running them together is fine.
- If you run Fires Fixes too, update both together, so the features that moved aren't missing or doubled.
- Tested on a server with 60+ other mods. Pairs well with Better ZLog and timeout-limit mods.
- **ValheimCommunityPatch**: compatible. Server-Side Simulation stays off while it is installed.
- **Valheim Performance Optimizations**: not compatible on a server. Server-Side Simulation and ZDO ownership stay off with it.
- **TooManyLags - FiresGhettoNetworking Addon**: a third-party add-on we have not tested. It overrides your FGN settings while it runs. If anything breaks, remove it and test with FGN alone before reporting.
- Don't stack other networking or "optimization" mods; they patch the same parts of the game and can undo each other.

## Credits

Built on the shoulders of BetterNetworking and Serverside Simulations, with ideas from Comfy Valheim's BetterZeeRouter. The auto-tuner and server self-tune are original to this mod.

## Issues / questions

https://discord.gg/zQRgHbqms4

## Screenshots

<!-- ![Screenshot](HOSTED_URL) -->
