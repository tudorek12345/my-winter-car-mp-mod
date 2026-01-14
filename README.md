# My Winter Car MP Mod 
<img width="858" height="858" alt="mwc_logo_circle_transparent_large_trans" src="https://github.com/user-attachments/assets/d6e00873-dcd0-45fa-bb9c-e11620fa22c7" />


## Current Status
Host and client can connect in-game and both see each other. Interior room doors plus the cabin front/back entrances sync. Cabin sink/tap and fridge interactions sync. Time-of-day lighting sync is working. Remote avatar now uses a proper model, AssetBundle (static mesh), but scale/offset are still being tuned. The Sorbet car is in active development: doors sync partially, driving/ownership is still WIP.

OFFICIAL DISCORD
https://discord.gg/GQeC5tCH2w

## Current Version [STATUS]
0.1.5 - 2026-01-14
Remote avatar now loads a proper model, AssetBundle; scale/offset tuning in progress.
Cabin sink/tap and fridge interactions sync.
Sorbet vehicle sync is still WIP: doors partially sync, driving/ownership still in progress.


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
- `VerboseLogging = true` (set false to reduce log noise)

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

DoorSync:
- `Enabled = true`
- `PlayMakerEvents = true`
- `SendHz = 10`
- `AngleThreshold = 1`
- `NameFilter = door,ovi,tap,faucet,sink` (empty = all hinges)

PickupSync:
- `Enabled = false` (set true to sync cabin pickups like phone/props)
- `ClientSend = false`
- `SendHz = 12`
- `PositionThreshold = 0.02`
- `RotationThreshold = 2.0`
- `NameFilter = ` (optional filter, comma-separated)

VehicleSync:
- `Enabled = true` (experimental, Sorbet sync WIP)
- `ClientSend = true`
- `OwnershipEnabled = true`
- `SeatDistance = 1.2`
- `SendHz = 10`
- `PositionThreshold = 0.05`
- `RotationThreshold = 1.0`

Avatar:
- `BundlePath = plugins\MyWinterCarMpMod\mpdata`
- `AssetName = assets/mpplayermodel/mpplayermodel.fbx`
- `Scale = 3.0` (tune)
- `YOffset = 0.2` (tune)

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

## Avatar Setup (AssetBundle)
1. Build or provide an AssetBundle that contains a player prefab or mesh. The current default uses the MSCMP `mpdata` bundle.
2. Set `Avatar.BundlePath` to the bundle path and `Avatar.AssetName` to the prefab/mesh name (default: `assets/mpplayermodel/mpplayermodel.fbx`).
3. Tune `Avatar.Scale` and `Avatar.YOffset` if the model is too big/small or sinks into the ground (scale/offset tuning is still in progress).

## Dev Guide
See `instructions.txt` for iteration, build, deploy, and multi-instance notes.

## Build Notes
- Target framework is .NET Framework 3.5 for older Unity (Mono).
- Update `GameDir` in `src/MyWinterCarMpMod/MyWinterCarMpMod.csproj` or pass `-p:GameDir=...`.
- If .NET 3.5 reference assemblies are missing, use the game's Managed folder:
  ```powershell
  dotnet build src/MyWinterCarMpMod/MyWinterCarMpMod.csproj -c Release `
    -p:GameDir="C:\Games\My.Winter.Car\game" `
    -p:FrameworkPathOverride="C:\Games\My.Winter.Car\game\MyWinterCar_Data\Managed"
  ```
