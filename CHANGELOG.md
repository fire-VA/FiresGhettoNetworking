* v1.0.2 update for not playing well with wackies db
* v1.1.0 compatibility updates
* v1.1.1 fixed single player running logic as dedicated server and spamming errors finally
* v1.1.5 fixes to server authority ai patches. improved performance, stopped causing issues with interactions and monsters... 
* v1.1.6 better server checking for renamed assemblys 
* v1.1.7 various tweaks and a bug fix for accidentally hiding the player-player poistion sync in configs 
* v1.1.8 better client position handling for players with higher ping differences (only works when installed on server and client)
* v1.1.9 adjustments to player positioning patches as well as default config settings, added a new beta feature for login snapshots as well
* v1.2.0 removed log snapshots beta, will have to become its own mod someday soon
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
* v1.3.0 server + bandwidth performance pass:
  - ZDO delta compression — server only re-sends ZDO fields that changed (wire-compatible with vanilla)
  - Server-side WearNTear skip — UpdateWear short-circuits for stable / invulnerable pieces
  - Client-side support skip — invulnerable pieces stop running UpdateSupport, max-support pinned at Awake
  - Shared invulnerability classifier — Immune + Ignore both treated as zero-damage
  - AoI-filtered SpawnedZone rebroadcast — server-side rebroadcast bound by AoI radius instead of all-peers
  - Public-test build gating — ready for live valheim updates
  - Internal reorg — 
* v1.3.1 fixes for compression conflicts with SC
* v1.3.2 realized the error of my ways
  - compat with serversync send limits
  - added experimental server ownership toggles... basically ports the rest of server simulations. off by default