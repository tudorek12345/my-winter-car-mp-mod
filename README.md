# MWC Spectator Sync (MVP)

Adds a Steam P2P spectator mode for "My Winter Car" (Unity Mono). The spectator camera follows the host camera and syncs level changes plus a simple progress marker. Spectator sync first; full co-op later. This is just starting and trying to make it work.

## Requirements
- My Winter Car installed and launched via Steam
- BepInEx 5 (Mono, x64)
- Steamworks.NET available in the game's Managed folder (or adjust references)

## Install
1. Build `MWCSpectatorSync.dll` from `src/MWCSpectatorSync/MWCSpectatorSync.csproj`.
2. Copy the DLL to `<GameDir>\BepInEx\plugins\MWCSpectatorSync\`.
3. Launch the game once to generate the config at `BepInEx\config\com.tudor.mwcspectatorsync.cfg`.

## Host Steps
1. Set `Mode = Host`.
2. (Optional) Set `AllowOnlySteamId` to restrict to a single spectator SteamID64.
3. Press `F6` to start hosting.
4. Share the Host SteamID64 shown in the overlay.
5. Press `F9` to set the progress marker (timestamp + preset note).

## Spectator Steps
1. Set `Mode = Spectator`.
2. Set `SpectatorHostSteamId` to the host SteamID64 (Steam P2P).
3. Press `F7` to connect.
4. The spectator camera follows the host; press `F8` to toggle the overlay.

## Transport
- Default: Steam P2P. If Steam init fails or the game is not running under Steam, it auto-falls back to TCP LAN and shows a warning in the overlay.
- TCP LAN fallback: set `HostBindIP`, `HostPort`, and `SpectatorHostIP` in config.

## Config (BepInEx)
General:
- `Mode = Host | Spectator`
- `Transport = SteamP2P | TcpLan`
- `SendHz = 20`
- `SmoothingPosition = 0.15`
- `SmoothingRotation = 0.15`
- `OverlayEnabled = true`
- `VerboseLogging = false`

Steam P2P:
- `SpectatorHostSteamId = 0`
- `AllowOnlySteamId = 0`
- `P2PChannel = 0`
- `ReliableForControl = true`

TCP LAN:
- `HostBindIP = 0.0.0.0`
- `HostPort = 27055`
- `SpectatorHostIP = 127.0.0.1`

Spectator:
- `SpectatorLockdown = true` (best-effort disable input scripts)

## Current Work (Now)
- Camera selection ignores map cameras and prefers player cameras based on decompiled game scripts (`MainCamera`, `CarCameras`, `CarCamerasController`, `SmoothFollow`, `S_Camera`).
- Spectator lockdown disables additional input/camera scripts from the decompiled code (`SmoothMouseLook`, `SimpleSmoothMouseLook`, car controller scripts) to prevent local input fighting the synced camera.
- Spectator mode blocks map toggle (`M`) by clearing `StartGame.mapCameraController` and blocks camera switching (`C`) by disabling `CarCamerasController` when `SpectatorLockdown` is enabled.

## Current Scope
- One-way sync: host -> spectator only.
- Syncs camera position/rotation/FOV, level changes, and a progress marker.
- Steam P2P transport with TCP LAN fallback.
- No world/physics/vehicle/inventory/AI/time-of-day sync.

## Future (Full Co-op)
- Plan is full state replication with two-way sync (players, vehicles, items, AI, physics).
- Likely needs an authoritative host with reconciliation to keep physics stable.
- Will be built incrementally after spectator sync is solid.

## Known Limitations
- No syncing of physics, vehicles, items, AI, doors, or inventory.
- No co-op interaction or streaming; only state replication.
- Spectator lockdown is conservative and may not disable all input scripts.

## Troubleshooting
- Steam init failed: ensure the game is launched via Steam; fallback to TCP LAN.
- TCP connection fails: check firewall rules for the host port (default 27055).
- Version mismatch: ensure both players use the same mod build (Hello buildId/modVersion).

## Build Notes
- Target framework is .NET Framework 3.5 for older Unity (Mono).
- If your Steamworks.NET build requires 4.x, retarget the project to .NET 4.7.2 and update references accordingly.
- Update the `GameDir` property in `src/MWCSpectatorSync/MWCSpectatorSync.csproj` to match your install.
