# \# FiresGhettoNetworkMod

# 

# \*\*Duct-taped networking wizardry for Valheim\*\*

# 

# Look, let's be real: Valheim's vanilla multiplayer is... \*fine\*. It's P2P, it's janky when you have more than a handful of friends, and zone crossing can feel like waiting for dial-up in 2025.

# &nbsp;So we said "screw it" and Frankenstein'd together the best parts of server-side authority mods, slapped on some compression, cranked the Steam send rates, and called it a day.

# 

# This mod is basically BetterNetworking and Serverside Simulations had a baby, with a few extra bells we thought were cool (extended zone pre-loading, AI LOD, ZDO throttling, player interpolation, etc.).

# &nbsp;It's designed for beefy dedicated servers that can handle the extra load while making clients feel like they're playing single-player (hopefully).

# 

# \## Is It Better Than The Originals?

# Probably not!  

# Those other mods were made by talented people who know what they're doing... We just duct-taped them together, added some spicy extras, and hoped for the best. If you want pure server authority — go use Serverside Simulations. If you want compression done right, better networking or something of the sort there are plenty of dedicated mods for that.  

# 

# This is the "I want ALL the things but don't want 17 mods" option. It works great on our overkill server, your mileage may vary.

# 

# \## Features (The Duct Tape List)

# \- \*\*Server pre-loads extra zones\*\* — less stutter when running around like a maniac (hopefully)

# \- \*\*ZSTD compression\*\* — because who doesn't love smaller packets?

# \- \*\*Higher Steam send rates + bigger queues\*\* — less "waiting for server" in big fights.

# \- \*\*Server-side AI \& events\*\* — monsters spawn even if you're exploring solo (balanced with player proximity).

# \- \*\*ZDO \& AI throttling\*\* — distant stuff updates slower (saves CPU/bandwidth, combat stays crisp).

# \- \*\*Client interpolation\*\* — other players don't rubber-band as much.

# \- \*\*Configurable everything\*\* — including a big red warning not to enable server features on clients (infinite loading = bad times).

# 

# \## Installation

# 1\. Install BepInEx.

# 2\. Drop this into `BepInEx/plugins/`.

# 3\. Pray the duct tape holds.

# 

# \## Warning

# \*\*SERVER-ONLY FEATURES ARE SERVER-ONLY.\*\*  

# The mod auto-disables them on clients, but if you somehow force it... enjoy your infinite loading screen.

# 

# \## Credits

# \- Shout out to better networking and serverside simulations... theyre the real heros

# \- Compression tricks borrowed from anywhere I could find (I have no shame)

# \- Tested on our my own Dedicated server, it didn't blow up yet

# 

If it breaks, you get to keep both pieces. Feel free to fork it, fix it, make it better...
on a good day I barely know what I'm doing. I just made this for me and only published it for easier sharing on our server
===

# 

# Enjoy smoother(ish) Valheim! 🔥🩹

