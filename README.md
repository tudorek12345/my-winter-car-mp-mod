# My Winter Car MP Mod 
<img width="858" height="858" alt="mwc_logo_circle_transparent_large_trans" src="https://github.com/user-attachments/assets/d6e00873-dcd0-45fa-bb9c-e11620fa22c7" />




OFFICIAL DISCORD
https://discord.gg/GQeC5tCH2w

## Current Version [STATUS]
0.1.8 - 2026-02-06

Highlights:
- Sorbet control sync extended to include turn signals, ignition, starter, and interior light in dashboard state payload.
- BUS nav payload (target speed/route/waypoint/start/end) is now serialized in realtime `NpcState` packets, not only in world snapshots.
- Scrape replication hardened with remote-baseline dedupe and remote-hold echo suppression to reduce scrape audio/state spam loops.
- Scrape finish states now drive `OFF`/inside-reset handling to prevent lingering remote scrape audio loops.
- Door PlayMaker spam reduced by filtering non-Sorbet/non-BUS vehicle event-only button loops and suppressing Sorbet dashboard EventOnly chatter (dashboard payload is authoritative).
- NPC animator discovery improved by matching both object name and hierarchy path tokens.
- Default NPC name filter expanded for pub/shop coverage (`nappo,pub,bar,npc,teimo,shop,seller,cashier,customer,bartender`).
- Pickup sync remains staged: stable set first (beer, cigarettes, sausage), metadata-filter expansion optional.

Known active WIP:
- Pub/teimo dialogue/ticket/order interaction parity is not complete yet.
- BUS route/ticket flow is partially synced and still under active tuning.
- Some AI/traffic edge cases still need dedicated FSM event replication per actor/system.


## Requirements & Installation

### Prerequisites
- My Winter Car (Windows).
- Steam P2P requires launching via Steam; local multi-instance testing uses LAN.

### BepInEx Setup
1. Download **BepInEx 5.4.21 (x64)** from the official releases.
2. Extract the contents to your game folder.
3. Edit `BepInEx/config/BepInEx.cfg` and set the entry point:
   ```ini
   [General]
   Assembly = UnityEngine.dll
   Type = MonoBehaviour
   ```

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

### Double Folder Method (Bypass Steam Locks)
To run multiple game instances locally without Steam interference:

1. Copy your entire game folder to a second location (e.g., `MyWinterCar_Client`).
2. Launch the first instance from your original game folder via Steam, in the menu tick the multi instance.
3. Launch the second instance directly from the copied folder by running `MyWinterCar.exe`.

## Building from Source

### Prerequisites
- **.NET Framework 3.5**
- Visual Studio or compatible build tools

### Required References
The following DLLs must be referenced from your game installation:
- `Assembly-CSharp.dll`
- `UnityEngine.dll`
- `UnityEngine.UI.dll`
- `PlayMaker.dll`
- `BepInEx.dll`
- `0Harmony.dll` (**Warning**: Use `0Harmony.dll`, NOT `0Harmony20.dll`)

### Build Steps
1. Clone the repository
2. Add all required references to the project
3. Build in Release mode
4. Copy the output DLL to `BepInEx/plugins/MWCMP/`

## Configuration Reference

### UI
```ini
MainMenuPanelEnabled = true
```

### Compatibility
```ini
AllowMultipleInstances = false
```

### Steam P2P
```ini
SpectatorHostSteamId = 0
AllowOnlySteamId = 0
P2PChannel = 0
ReliableForControl = true
```

### TCP LAN
```ini
HostBindIP = 0.0.0.0
HostPort = 27055
SpectatorHostIP = 127.0.0.1
```

### LanDiscovery
```ini
Enabled = true
Port = 27056
BroadcastIntervalSeconds = 1.5
HostTimeoutSeconds = 5
```

### DoorSync
```ini
Enabled = true
PlayMakerEvents = true
SendHz = 10
AngleThreshold = 1
NameFilter = door,ovi,tap,faucet,sink
```
*Note: Leave `NameFilter` empty to include all hinges*

### PickupSync
```ini
Enabled = false
ClientSend = false
SendHz = 12
PositionThreshold = 0.02
RotationThreshold = 2.0
NameFilter =
```
*Note: Set `Enabled = true` to sync cabin pickups like phone/props. `NameFilter` is optional, comma-separated.*

### VehicleSync
```ini
Enabled = true
ClientSend = true
OwnershipEnabled = true
SeatDistance = 1.2
SendHz = 10
PositionThreshold = 0.05
RotationThreshold = 1.0
```
*Note: Experimental Sorbet sync, work in progress*

### Avatar
```ini
BundlePath = plugins\MyWinterCarMpMod\mpdata
AssetName = assets/mpplayermodel/mpplayermodel.fbx
Scale = 3.8
YOffset = 0.85
```
*Note: Tune `Scale` and `YOffset` values as needed*

### Networking
```ini
ConnectionTimeoutSeconds = 10
HelloRetrySeconds = 2
KeepAliveSeconds = 2
AutoReconnect = true
ReconnectDelaySeconds = 3
MaxReconnectAttempts = 5
LevelSyncIntervalSeconds = 5
```
*Note: Set `MaxReconnectAttempts = 0` for infinite attempts*

### Spectator
```ini
SpectatorLockdown = true
```
*Note: Legacy setting, unused in co-op mode*

## Logs
- BepInEx global log: `BepInEx/LogOutput.log`
- Per-instance mod log: `BepInEx/LogOutput_MyWinterCarMpMod_<pid>.log`

## Avatar Setup (AssetBundle)
1. Build or provide an AssetBundle that contains a player prefab or mesh. The current default uses the MWCMP `mpdata` bundle.
2. Set `Avatar.BundlePath` to the bundle path and `Avatar.AssetName` to the prefab/mesh name (default: `assets/mpplayermodel/mpplayermodel.fbx`).
3. Tune `Avatar.Scale` and `Avatar.YOffset` if the model is too big/small or sinks into the ground (auto-ground offset is applied; scale/offset is configurable).



