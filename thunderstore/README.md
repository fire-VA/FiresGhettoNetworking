# Fires Ghetto Networking

**Updated for Valheim 1.0.**

Smoother multiplayer for Valheim servers. Vanilla networking is built for a handful of friends and starts to strain around 8 players. This mod fixes the bottlenecks in one drop-in plugin, and tunes itself so you don't have to.

## What you get

- **Less lag in busy fights and big bases**: compressed traffic, higher send rates, and the closest things sent first when a connection backs up.
- **Auto-Tune**: on first join it checks your PC and connection and picks the right settings. The server tunes itself too.
- **Less wasted traffic**: damage numbers, health bars and effects only go to players near the action.
- **Smoother other players**: other players' movement and arrows are drawn where they really are (when they have the mod too).
- **Boat fixes**: no more random boat damage or sinking on calm water, and whoever takes the helm steers without delay.
- **Station fix**: smelters, kilns, fires and other stations no longer lose or duplicate items, even for players without the mod.
- **Join fixes**: slow PCs loading a big world aren't dropped, and big config syncs no longer stall joins.
- **Optional Server-Side Simulation**: the dedicated server runs the world around every player (spawns, raids, zones).

## Install

- **Server only, clients only, or both.** Server-only works, and players without the mod can still join.
- Installing on both sides adds compression, smoother player movement and the boat fix.
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
- `[03 - Player Sync] Remote Motion (experimental)` (on by default)

Everything else is under **Show Advanced** in the config manager.

**Players:** the only section meant for you is `Player Sync`. Everything else is server-driven or auto-tuned, and server-only settings are ignored on a client.

## Server-Side Simulation

Off by default. Turn on `Enable Server-Side Simulation` and the server loads the world around every player, decides when and where creatures and raids spawn, and handles zones. Monsters are still run by the nearby players' games.

**Server ZDO Ownership** goes further and moves creatures onto the server. **We don't recommend it**: it is the least-tested option and can cause trouble with boats and carts. If you try it, use **Selective**. Tamed creatures always stay with the players.

## Steam vs crossplay

Players on crossplay (Xbox, Game Pass, PlayStation, or anyone who joined with a crossplay code) don't get compression or the Steam send-rate tuning; those only exist on Steam connections. Everything that cuts how much is sent still works for them. A server started with `-crossplay` can put even Steam players on crossplay. `fgn_links` in the console shows who is on what.

## Admin commands

- `fgn_links` - each player's connection.
- `fgn_rtt` - times a round trip to the server.
- `fgn_netsim` - adds fake lag to your own connection for testing.
- `fgn_sss_areas N` - simulates N extra busy areas to see what Server-Side Simulation would cost (`0` clears it).

## Compatibility

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

<!-- screenshots: [seasons] to capture on a nice world (not SuperFlat). Thunderstore/Hexium show only HOSTED images; where to host is Fire's call. -->
<!-- ![Screenshot](HOSTED_URL) -->
