* v1.4.31 - fairer sending, station fixes and lighter servers
  - the server sizes each player's connection to what it actually carries, and opens it back up once the line clears
  - chat, effects and damage numbers only go to the players who can see them, instead of everyone on the server
  - monsters are handed to the player with the best connection, so they stop rubber-banding for everyone else, and whoever is fighting one keeps it
  - slow server frames are broken down in the log, so a stall can be traced to what actually caused it

* v1.4.23 - optimization fixes

* v1.4.22 - compression rebuild and server fixes
  - compression rebuilt: less CPU for the same or better savings, and damaged packets are caught instead of loaded
  - terrain edits no longer go missing when an area's ground loads late
  - dedicated servers print their join address (IP:port) once they finish starting 
  - smoother zone loading with "Enable Time-Slice Instantiation" on, and distant objects no longer wait behind nearby ones
  - crops keep growing on servers that take ownership of them
  - removed the "Max Active ZDOs" setting (it no longer did anything)
  - clearer compression stats in the log

* v1.4.15 - boats, sleeping and server fixes
  - boats no longer take constant damage while players are aboard
  - sleeping on a busy server no longer takes a minute or more (hopefully, not confirmed with crossplay and cross system clients yet)
  - RPC filtering, ZDO throttling and AI LOD now work on servers with Server-Side Simulation off, each on its own toggle (shout out Safwan for that suggestion)
  - Server-Side Simulation stays off when ValheimCommunityPatch is on the server, since the two fight over the same objects
  - with Server-Side Simulation on, the server keeps up with terrain edits again and stops loading areas without their ground
  - buildings the server looks after wear down and collapse normally again
  - the auto-tune queue sizes now properly apply, and compression uses less CPU

* v1.4.12 - hotfix updates
  - graves, dropped items and tames no longer sink through floors when an area loads
  - waking up after sleeping on a dedicated server is quick again
  - crossplay servers show the correct player limit in the server list
  - less error spam on dedicated servers

* v1.4.2 - fixed players staying frozen on other players' screens after going through a portal (a Valheim 1.0 bug)

* v1.4.1 - fixed a compatibility issue with ValheimCommunityPatch

* v1.4.0 - updated for Valheim 1.0

* v1.3.16 - pens, boats, and an honest toggle
  - Tamed and trapped creatures stay put. Leaving an area tore the world down over several frames, so a creature could outlive the walls around it, wander out through the gap, and have that escaped position saved. Anything that can move is now removed in the same frame vanilla removes it, and containment is destroyed last.
  - Boats no longer take damage on calm water. A server that owned a hull applied no buoyancy until that zone's water finished loading but left gravity running, so empty ships sank into the seabed and damaged themselves on landing. Server-owned hulls now hold still until their surroundings actually exist.
  - "Server-Side Ship Simulation" now does what it says. It was never wired to anything, so switching on ZDO ownership quietly handed ship physics to the server even with that setting off. Ships stay client-simulated unless you deliberately opt in.
  - Creatures are placed where the world says they are before the server's first physics step after it takes ownership, rather than one step afterwards.
  - Added diagnostics so the fixes report what they actually did: teardown now logs how many moving objects it removed in-frame, and a parked ship logs how long it waited for its surroundings to load.

* v1.3.11 - maintenance and fixes

* v1.3.10 dedicated-server terrain fix (world pre-gen, Upgrade World resets, crops)
  - Fixed a null-reference error flood on dedicated servers during bulk world pre-generation when using server authority 
* v1.3.9 stuck-raid fix for dedicated servers
  - Fixed raids / random events that could run forever on a dedicated server — mobs chasing players "for days," only stopping if FGN was removed. With no local player standing at the event origin, vanilla pauses the event timer the moment every player leaves the area, so the raid never reaches its end time — while FGN keeps spawning its event mobs around whoever is online. The timer now runs on wall-clock regardless of who is in the area, so raids end at their authored duration, with a 1-hour hard failsafe that force-ends any event that somehow outlives it.
  - Internal networking pass (per-packet compression negotiation, bulk-transfer gate, auto-tune probe). No config changes.
* v1.3.8 crossplay default fix + per-peer adaptive send rate 
  - Crossplay servers could fail to connect after installing FGN. "Force Crossplay" now defaults to "vanilla" 
  - Per-peer adaptive send rate — each client now ramps from the auto-tune baseline up toward its own real link capacity 
  - Auto-tune HIGH send ceiling raised (~8 -> ~32 MB/s) with a larger send buffer and queue, so a healthy link isn't artificially throttled.
  - HYPERBOOST toggle — one-switch max-throughput override (Steam send/recv rates + buffers) for benchmarking or high-bandwidth links. Default off; pinning everything wide open really isn't a great idea, but if you really want to, have at it (requires fires steamworks patcher on client as well as server)
  - Patcher-dependent settings (the recv-buffer tuning, including HyperBoost's recv side)
  - Crops should now grow correctly under server-side simulation
  - Plays nice with Render Limits now — if Jere's Render Limits is installed, FGN stops stacking its extended zone radius on top and lets Render Limits own the zone load distances (server authority stays intact)
* v1.3.7 disconnect + fall-through + steamworks-limit fixes
  - Clients no longer choke on default Steam bandwidth limits during big transfers.
  - The bandwidth lift now applies through other mods' socket wrappers instead of getting lost.
  - Fixed a compression bug that caused "Disconnected" drops on busy servers.
  - Items and graves stay put until the floor under them finishes loading instead of falling through.
  - Fall-through diagnostic logging is now off by default behind a toggle.
* v1.3.6 tombstone/tames fix 
  - graves and tames should stop dropping through structures on zone reload 
* v1.3.5 auto-tune adjustment
  - a strong PC on a high-ping connection no longer gets dropped to low settings. your hardware sets the ceiling, a bad connection only nudges it down a step
  - player smoothing/prediction are left to the clients preferences again, auto-tune doesn't force them on
* v1.3.4 stability pass for big/busy servers
  - smoother on rented/shared hosts, now TRYING to read the actual server box specs not the hosts specs
  - "Update Rate" renamed to "ZDO Send Rate"
  - better disconnect logging to track down any hiccups
* v1.3.3 multi-player area fixes + permission-mod compat
  - players flying / hits from across the map fixed. BulkTransferGuard was the cause, replaced with BulkTransferGate (raises ServerSync send queue gate at the source instead of suppressing ZDOMan.SendZDOs)
  - delta compression self-heals now — full vanilla keyframe every 5s per peer/zdo so a dropped packet doesn't desync fields forever
  - RPC router stopped wasting cycles forwarding server-targeted RPCs to itself
  - IsActiveAreaLoaded gate uses vanilla active-area only, megabases stop choking on it
  - ported SSS defensive trio (humanoid null guard, WNT collider re-init, ship request-handoff)
  - CreateDestroyObjects NRE prune. fires when permission mods like TargetPortalProtection block portal destroy on the dedi and leave m_instances out of sync
  - broad server ownership auto-skips portals when TargetPortalProtection is installed
  - misc log + diagnostic tidy
* v1.3.2 realized the error of my ways
  - compat with serversync send limits
  - added experimental server ownership toggles... basically ports the rest of server simulations. off by default
* v1.3.1 fixes for compression conflicts with SC
* v1.3.0 server + bandwidth performance pass:
  - ZDO delta compression — server only re-sends ZDO fields that changed (wire-compatible with vanilla)
  - Server-side WearNTear skip — UpdateWear short-circuits for stable / invulnerable pieces
  - Client-side support skip — invulnerable pieces stop running UpdateSupport, max-support pinned at Awake
  - Shared invulnerability classifier — Immune + Ignore both treated as zero-damage
  - AoI-filtered SpawnedZone rebroadcast — server-side rebroadcast bound by AoI radius instead of all-peers
  - Public-test build gating — ready for live valheim updates
  - Internal reorg — 
* v1.2.1 per-client Auto-Tune system added:
  - Network/hardware probe runs on first login (latency, jitter, frame time, bandwidth, hardware fingerprint), picks a LOW/MED/HIGH tier, applies tier-appropriate settings automatically
  - Two-axis tier scoring (machine vs link) — strong client on a clean-latency link no longer dragged down by server-bottlenecked bandwidth measurements
  - Rolling monitor re-probes every 5 minutes after initial probe; tier auto-corrects if conditions change mid-session or initial probe was contaminated by startup traffic
  - Server self-tune scores host CPU/RAM and applies tier presets to ZDO throttle / AI LOD / RPC AoI / queue size / extended zone radius / Steam buffers (default ON)
  - Per-server cache (7-day TTL, hardware-fingerprint-aware) so reconnecting picks up the established tier
  - Steam SendBufferSize wired up (per-connection outbound buffer, up to 2MB) — addresses k_EResultLimitExceeded errors during initial-sync floods
  - Asymmetric send-rate pattern (Min stays at safe baseline, Max scales with tier) — no risk of pushing a slow client harder than they can handle
  - ZoneLoadBatchSize (formerly stubbed) now wired into ZNetScene's per-frame creation cap via runtime helper; HIGH-tier clients consume zone backlog 4x faster
  - ZPackage Receive Buffer config bound and applied (no-op on Valheim's bundled Steamworks build but ready when SDK updates)
  - Default `Enable Server Auto-Tune` flipped ON (was OFF in 1.2.0)
  - Default `Enable Client-Side Interpolation` flipped OFF (was ON before — the rest of the networking improvements deliver smooth positions on their own; turn back ON if you still see snap/teleport)
  - README rewritten with proper documentation of features, auto-tune, server-authority patches, and the four client-side knobs that are the only settings users should ever touch
* v1.2.0 removed log snapshots beta, will have to become its own mod someday soon
* v1.1.9 adjustments to player positioning patches as well as default config settings, added a new beta feature for login snapshots as well
* v1.1.8 better client position handling for players with higher ping differences (only works when installed on server and client)
* v1.1.7 various tweaks and a bug fix for accidentally hiding the player-player poistion sync in configs 
* v1.1.6 better server checking for renamed assemblys 
* v1.1.5 fixes to server authority ai patches. improved performance, stopped causing issues with interactions and monsters... 
* v1.1.1 fixed single player running logic as dedicated server and spamming errors finally
* v1.1.0 compatibility updates
* v1.0.2 update for not playing well with wackies db
