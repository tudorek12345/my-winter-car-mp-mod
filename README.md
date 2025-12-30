# <img width="1056" height="946" alt="image-removebg-preview" src="https://github.com/user-attachments/assets/62184b8b-d419-4166-a875-3862906b61c0" />
My Winter Car Multiplayer Mod (WIP)

Adds a two-player co-op prototype for "My Winter Car" (Unity Mono). The build syncs player presence (position + view) as a remote avatar, plus level changes and a simple progress marker. Full co-op/world sync comes later.
CURRENT STATUS: INGAME 
<img width="1271" height="672" alt="image" src="https://github.com/user-attachments/assets/29681de9-e444-4139-9b6b-d69a8addb011" />

## Requirements
- My Winter Car installed and launched via Steam
- BepInEx 5 (Mono, x64)
- Steamworks.NET available in the game's Managed folder (or adjust references)

## Host Steps
1. Set `Mode = Host`.
2. (Optional) Set `AllowOnlySteamId` to restrict to a single client SteamID64.
3. Press `F6` to start hosting.
4. Share the Host SteamID64 shown in the overlay.
5. Press `F9` to set the progress marker (timestamp + preset note).

## Client Steps
1. Set `Mode = Client`.
2. Set `SpectatorHostSteamId` to the host SteamID64 (Steam P2P).
3. Press `F7` to connect.
4. You should see a remote player avatar; press `F8` to toggle the overlay.

## Transport
- Default: Steam P2P. If Steam init fails or the game is not running under Steam, it auto-falls back to TCP LAN and shows a warning in the overlay.
- TCP LAN fallback: set `HostBindIP`, `HostPort`, and `SpectatorHostIP` in config.

## Config (BepInEx)
General:
- `Mode = Host | Client`
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
- `SpectatorLockdown = true` (legacy, unused for co-op)

## Current Work (Now)
- Player locator prefers main/player cameras and decompiled player controllers (`CharacterMotor`, `FPSInputController`, `FirstPersonController`) to find the local player.
- Two-way player state sync (host <-> client) for position + view rotation.
- Remote player avatar (capsule) with smoothing to reduce jitter.

## Current Scope
- Two-player only (host + client).
- Bidirectional player presence sync (position + view rotation).
- Host drives level change and progress marker.
- Steam P2P transport with TCP LAN fallback.
- No world/physics/vehicle/inventory/AI/time-of-day sync.

## Future (Full Co-op)
- Plan is full state replication with two-way sync (players, vehicles, items, AI, physics).
- Likely needs an authoritative host with client input replication/reconciliation for physics stability.
- Will be built incrementally after presence sync is solid.

## Known Limitations
- No syncing of physics, vehicles, items, AI, doors, or inventory.
- Remote player avatar is visual-only (no collisions or interactions yet).

## Troubleshooting
- Steam init failed: ensure the game is launched via Steam; fallback to TCP LAN.
- TCP connection fails: check firewall rules for the host port (default 27055).
- Version mismatch: ensure both players use the same mod build (Hello buildId/modVersion).

## Build Notes
- Target framework is .NET Framework 3.5 for older Unity (Mono).
- If your Steamworks.NET build requires 4.x, retarget the project to .NET 4.7.2 and update references accordingly.
- Update the `GameDir` property in `src/MyWinterCarMpMod/MyWinterCarMpMod.csproj` to match your install.
