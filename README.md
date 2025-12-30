# My Winter Car MP Mod (WIP)

Early two-player co-op prototype for "My Winter Car" (Unity Mono). Currently syncs only player presence (position + view yaw), level changes, and a simple progress marker. This is not full co-op yet.

## Current Status
Host and client are connected in the main menu; loading into the game is still being stabilized.

![Main menu status](https://github.com/user-attachments/assets/94f9fc03-6071-4a5f-b855-8c95d658f65c)

## Current Features
- Two-player host/client.
- Steam P2P or TCP LAN (LAN discovery + in-game join panel).
- Main menu co-op panel + on-screen overlay.
- Session handshake, keepalive pings, timeouts, auto-reconnect.
- Per-instance debug logs for easier troubleshooting.

## Known Limitations
- No world/physics/vehicles/items/AI/time-of-day sync.
- Remote player is a visual-only capsule (no collisions or interactions).
- Level sync is still flaky; the client can remain in the main menu after host continues.
- LAN/Steam P2P is still experimental and may require retries.

## Requirements
- My Winter Car (Windows).
- BepInEx 5 (Mono, x64).
- Steam P2P requires launching via Steam; local multi-instance testing uses LAN.

## Quick Start (Main Menu)
Host:
1. Set `Mode = Host`.
2. Pick `Transport = SteamP2P` or `Transport = TcpLan`.
3. Click `Host Game` in the co-op panel (or press `F6`).
4. Optional: press `F9` to set a progress marker.

Client:
1. Set `Mode = Client`.
2. Steam P2P: set `SpectatorHostSteamId` to the host SteamID64.
3. LAN: use `Join LAN` or set `SpectatorHostIP`/`HostPort`.
4. Click `Join Steam` or `Join LAN` (or press `F7`).

Then the host selects Continue/New Game; the client should load into the same level.

Local testing (two instances):
- Set `Compatibility/AllowMultipleInstances = true` and restart the game.
- This skips Steam bootstrap, so use LAN transport only.

## Config (BepInEx)
General:
- `Mode = Host | Client`
- `Transport = SteamP2P | TcpLan`
- `SendHz = 20`
- `SmoothingPosition = 0.15`
- `SmoothingRotation = 0.15`
- `OverlayEnabled = true`
- `VerboseLogging = false`

UI:
- `MainMenuPanelEnabled = true`

Compatibility:
- `AllowMultipleInstances = false`

Steam P2P:
- `SpectatorHostSteamId = 0`
- `AllowOnlySteamId = 0`
- `P2PChannel = 0`
- `ReliableForControl = true`

TCP LAN:
- `HostBindIP = 0.0.0.0`
- `HostPort = 27055`
- `SpectatorHostIP = 127.0.0.1`

LanDiscovery:
- `Enabled = true`
- `Port = 27056`
- `BroadcastIntervalSeconds = 1.5`
- `HostTimeoutSeconds = 5`

Networking:
- `ConnectionTimeoutSeconds = 10`
- `HelloRetrySeconds = 2`
- `KeepAliveSeconds = 2`
- `AutoReconnect = true`
- `ReconnectDelaySeconds = 3`
- `MaxReconnectAttempts = 5` (0 = infinite)
- `LevelSyncIntervalSeconds = 5`

Spectator:
- `SpectatorLockdown = true` (legacy, unused in co-op)

## Logs
- BepInEx global log: `BepInEx/LogOutput.log`
- Per-instance mod log: `BepInEx/LogOutput_MyWinterCarMpMod_<pid>.log`

## Build Notes
- Target framework is .NET Framework 3.5 for older Unity (Mono).
- Update `GameDir` in `src/MyWinterCarMpMod/MyWinterCarMpMod.csproj` to match your install path.
