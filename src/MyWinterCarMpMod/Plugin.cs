using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using MyWinterCarMpMod.Config;
using MyWinterCarMpMod.Net;
using MyWinterCarMpMod.Patches;
using MyWinterCarMpMod.Sync;
using MyWinterCarMpMod.UI;
using MyWinterCarMpMod.Util;
using UnityEngine;

namespace MyWinterCarMpMod
{
    [BepInPlugin("com.tudor.mywintercarmpmod", "My Winter Car MP Mod", "0.1.7")]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static bool AllowMultipleInstances;

        private Settings _settings;
        private Overlay _overlay;
        private LevelSync _levelSync;
        private HostSession _hostSession;
        private ClientSession _clientSession;
        private RemotePlayerAvatar _remoteAvatar;
        private DoorSync _doorSync;
        private VehicleSync _vehicleSync;
        private PickupSync _pickupSync;
        private TimeOfDaySync _timeSync;
        private NpcSync _npcSync;
        private WeatherScanner _weatherScanner;
        private RadioScanner _radioScanner;
        private BusSync _busSync;
        private ITransport _transport;
        private LanDiscovery _lanDiscovery;
        private Vector2 _lanScroll;
        private DevFreecam _devFreecam;
        private bool _devMenuVisible;
        private Vector2 _devMenuScroll;
        private readonly PlayerLocator _devPlayerLocator = new PlayerLocator();
        private Vector3 _devSavedPosition;
        private bool _devHasSavedPosition;
        private string _devStatus;

        private string _menuSteamIdText;
        private string _menuHostIpText;
        private string _menuHostPortText;
        private string _menuMessage;

        private bool _overlayVisible;
        private string _transportWarning;
        private int _markerIndex;
        private readonly string[] _markerNotes = new string[] { "Start", "Garage", "Satsuma", "Drive", "Checkpoint" };

        private string _buildId;
        private string _modVersion;
        private Harmony _harmony;
        private bool _steamPatchPending;
        private bool _steamRestartPatched;
        private bool _steamInitPatched;
        private bool _steamManagerAwakePatched;
        private string _configSuffix;
        private int _lastLevelIndex = int.MinValue;
        private string _lastLevelName = string.Empty;
        private const Mode DefaultClientMode = Mode.Client;

        private void Awake()
        {
            _settings = new Settings();
            ConfigFile config = ResolveConfigFile();
            _settings.Bind(config, Logger);
            CoerceModeIfNeeded(config);
            AllowMultipleInstances = _settings.AllowMultipleInstances.Value;
            ApplyHarmonyPatches();

            DebugLog.Initialize(Logger, _settings.VerboseLogging.Value);
            DebugLog.Info("Plugin awake. Version=" + Info.Metadata.Version + " BuildId=" + Application.version + "|" + Application.unityVersion);
            DebugLog.Info("Mode=" + _settings.Mode.Value + " Transport=" + _settings.Transport.Value + " HostPort=" + _settings.HostPort.Value + " DiscoveryPort=" + _settings.LanDiscoveryPort.Value);

            _overlay = new Overlay();
            _remoteAvatar = new RemotePlayerAvatar(_settings);
            _doorSync = new DoorSync(_settings);
            _vehicleSync = new VehicleSync(_settings);
            _doorSync.SetVehicleSync(_vehicleSync);
            _pickupSync = new PickupSync(_settings);
            _timeSync = new TimeOfDaySync(_settings);
            _npcSync = new NpcSync(_settings);
            _weatherScanner = new WeatherScanner(_settings);
            _radioScanner = new RadioScanner(_settings);
            _busSync = new BusSync(_settings, _doorSync);
            _devFreecam = new DevFreecam();
            _overlayVisible = _settings.OverlayEnabled.Value;
            _buildId = Application.version + "|" + Application.unityVersion;
            _modVersion = Info.Metadata.Version.ToString();
            InitializeMenuFields();

            MainThreadDispatcher.Initialize();

            _levelSync = new LevelSync(Logger, _settings.VerboseLogging.Value);
            if (_settings.Mode.Value == Mode.Host)
            {
                _levelSync.Initialize(OnHostLevelChanged);
            }
            else
            {
                _levelSync.Initialize(null);
            }
        }

        private void Update()
        {
            AllowMultipleInstances = _settings.AllowMultipleInstances.Value;
            DebugLog.SetVerbose(_settings.VerboseLogging.Value);
            HandleHotkeys();
            UpdateDevTools();
            UpdateLanDiscovery();

            if (_transport != null)
            {
                _transport.Update();
            }

            if (_levelSync != null)
            {
                _levelSync.Update();
                HandleLocalLevelChange();
            }

            if (_doorSync != null && _levelSync != null)
            {
                bool allowScan = !IsMainMenuScene();
                _doorSync.UpdateScene(_levelSync.CurrentLevelIndex, _levelSync.CurrentLevelName, allowScan);
                _doorSync.Update(Time.realtimeSinceStartup);
            }

            if (_busSync != null && _levelSync != null)
            {
                bool allowScan = !IsMainMenuScene();
                _busSync.UpdateScene(_levelSync.CurrentLevelIndex, _levelSync.CurrentLevelName, allowScan);
                _busSync.Update(Time.realtimeSinceStartup);
            }

            if (_vehicleSync != null && _levelSync != null)
            {
                bool allowScan = !IsMainMenuScene();
                _vehicleSync.UpdateScene(_levelSync.CurrentLevelIndex, _levelSync.CurrentLevelName, allowScan);
                _vehicleSync.UpdateInputCache(Time.realtimeSinceStartup);
            }

            if (_pickupSync != null && _levelSync != null)
            {
                bool allowScan = !IsMainMenuScene();
                _pickupSync.UpdateScene(_levelSync.CurrentLevelIndex, _levelSync.CurrentLevelName, allowScan);
                _pickupSync.Update(Time.realtimeSinceStartup);
            }

            if (_timeSync != null && _levelSync != null)
            {
                bool allowScan = !IsMainMenuScene();
                _timeSync.UpdateScene(_levelSync.CurrentLevelIndex, _levelSync.CurrentLevelName, allowScan);
            }

            if (_npcSync != null && _levelSync != null)
            {
                bool allowScan = !IsMainMenuScene();
                _npcSync.UpdateScene(_levelSync.CurrentLevelIndex, _levelSync.CurrentLevelName, allowScan);
                _npcSync.Update(Time.realtimeSinceStartup);
            }

            if (_weatherScanner != null && _levelSync != null)
            {
                bool allowScan = !IsMainMenuScene();
                _weatherScanner.UpdateScene(_levelSync.CurrentLevelIndex, _levelSync.CurrentLevelName, allowScan);
                _weatherScanner.Update(Time.realtimeSinceStartup);
            }

            if (_radioScanner != null && _levelSync != null)
            {
                bool allowScan = !IsMainMenuScene();
                _radioScanner.UpdateScene(_levelSync.CurrentLevelIndex, _levelSync.CurrentLevelName, allowScan);
                _radioScanner.Update(Time.realtimeSinceStartup);
            }

            if (_settings.VerboseLogging.Value && _levelSync != null)
            {
                bool allowScan = !IsMainMenuScene();
                PlayMakerScanner.ScanInteractiveFsms(_levelSync.CurrentLevelIndex, _levelSync.CurrentLevelName, allowScan);
                PlayMakerScanner.ScanVehicleDoorFsms(_levelSync.CurrentLevelIndex, _levelSync.CurrentLevelName, allowScan, "sorbet");
                PlayMakerScanner.ScanScrapeFsms(_levelSync.CurrentLevelIndex, _levelSync.CurrentLevelName, allowScan, "sorbet");
            }

            if (_settings.Mode.Value == Mode.Host && _hostSession != null)
            {
                _hostSession.Update();
            }
            else if (_settings.Mode.Value == Mode.Client && _clientSession != null)
            {
                _clientSession.Update();
            }

            ApplyRemotePlayerState();
        }

        private void FixedUpdate()
        {
            if (_vehicleSync == null || _settings == null)
            {
                return;
            }

            OwnerKind localOwner = OwnerKind.None;
            if (_settings.Mode.Value == Mode.Host)
            {
                localOwner = OwnerKind.Host;
            }
            else if (_settings.Mode.Value == Mode.Client)
            {
                localOwner = OwnerKind.Client;
            }

            bool includeUnowned = localOwner == OwnerKind.Host;
            _vehicleSync.FixedUpdate(Time.realtimeSinceStartup, Time.fixedDeltaTime, localOwner, includeUnowned);

            if (_npcSync != null)
            {
                _npcSync.FixedUpdate(Time.realtimeSinceStartup, Time.fixedDeltaTime, localOwner);
            }
        }

        private void OnGUI()
        {
            OverlayState state = BuildOverlayState();
            _overlay.Draw(state);
            DrawMainMenuPanel();
            DrawLanPanel();
            DrawDevMenuPanel();
        }

        private void OnDestroy()
        {
            if (_hostSession != null)
            {
                _hostSession.Stop();
            }
            if (_clientSession != null)
            {
                _clientSession.Disconnect();
            }
            if (_transport != null)
            {
                _transport.Dispose();
                _transport = null;
            }
            if (_levelSync != null)
            {
                _levelSync.Dispose();
            }
            if (_remoteAvatar != null)
            {
                _remoteAvatar.Clear();
            }
            if (_doorSync != null)
            {
                _doorSync.Clear();
            }
            if (_vehicleSync != null)
            {
                _vehicleSync.Clear();
            }
            if (_pickupSync != null)
            {
                _pickupSync.Clear();
            }
            if (_npcSync != null)
            {
                _npcSync.Clear();
            }
            if (_devFreecam != null && _devFreecam.IsActive)
            {
                _devFreecam.Disable();
            }
            if (_lanDiscovery != null)
            {
                _lanDiscovery.Dispose();
                _lanDiscovery = null;
            }
            if (_harmony != null)
            {
                _harmony.UnpatchSelf();
                _harmony = null;
            }
            DebugLog.Dispose();
        }

        private void HandleHotkeys()
        {
            if (Input.GetKeyDown(KeyCode.F8))
            {
                _overlayVisible = !_overlayVisible;
            }
            if (_settings.DevMenuEnabled.Value && Input.GetKeyDown(KeyCode.F5))
            {
                _devMenuVisible = !_devMenuVisible;
            }

            if (_settings.Mode.Value == Mode.Host)
            {
                if (Input.GetKeyDown(KeyCode.F6))
                {
                    ToggleHost();
                }
                if (Input.GetKeyDown(KeyCode.F9))
                {
                    SetNextProgressMarker();
                }
            }
            else if (_settings.Mode.Value == Mode.Client)
            {
                if (Input.GetKeyDown(KeyCode.F7))
                {
                    ToggleClient();
                }
            }
        }

        private void ToggleHost()
        {
            if (_hostSession == null || !_hostSession.IsRunning)
            {
                StartHost();
            }
            else
            {
                _hostSession.Stop();
            }
        }

        private void StartHost()
        {
            if (_clientSession != null && _clientSession.IsRunning)
            {
                _clientSession.Disconnect();
            }
            ResetTransport();
            _transport = CreateTransport();
            _hostSession = new HostSession(_transport, _settings, _levelSync, _doorSync, _vehicleSync, _pickupSync, _timeSync, _npcSync, Logger, _buildId, _modVersion);
            _hostSession.Start();
            DebugLog.Info("Host started. Transport=" + _transport.Kind);
            if (_remoteAvatar != null)
            {
                _remoteAvatar.Clear();
            }
        }

        private void ToggleClient()
        {
            if (_clientSession == null || !_clientSession.IsRunning)
            {
                StartClient();
            }
            else
            {
                _clientSession.Disconnect();
            }
        }

        private void StartClient()
        {
            if (_hostSession != null && _hostSession.IsRunning)
            {
                _hostSession.Stop();
            }
            ResetTransport();
            _transport = CreateTransport();
            _clientSession = new ClientSession(_transport, _settings, _levelSync, _doorSync, _vehicleSync, _pickupSync, _timeSync, _npcSync, Logger, _buildId, _modVersion);
            _clientSession.Connect();
            DebugLog.Info("Client connect requested. Transport=" + _transport.Kind);
            if (_remoteAvatar != null)
            {
                _remoteAvatar.Clear();
            }
        }

        private ITransport CreateTransport()
        {
            _transportWarning = null;
            if (_settings.Transport.Value == TransportKind.SteamP2P)
            {
                SteamP2PTransport steam = new SteamP2PTransport(_settings.GetP2PChannel(), Logger, _settings.VerboseLogging.Value);
                if (!steam.IsAvailable)
                {
                    _transportWarning = steam.Status + " Falling back to TCP LAN.";
                    if (Logger != null)
                    {
                        Logger.LogWarning(_transportWarning);
                    }
                    steam.Dispose();
                    return CreateTcpTransport();
                }
                return steam;
            }
            return CreateTcpTransport();
        }

        private ITransport CreateTcpTransport()
        {
            return new TcpTransport(_settings.HostBindIP.Value, _settings.HostPort.Value, _settings.SpectatorHostIP.Value, Logger, _settings.VerboseLogging.Value);
        }

        private void ResetTransport()
        {
            if (_transport != null)
            {
                _transport.Dispose();
                _transport = null;
            }
        }

        private void SetNextProgressMarker()
        {
            string note = _markerNotes[_markerIndex % _markerNotes.Length];
            _markerIndex++;
            string marker = DateTime.Now.ToString("HH:mm:ss") + " " + note;
            if (_hostSession != null)
            {
                _hostSession.SetProgressMarker(marker);
            }
        }

        private void ApplyRemotePlayerState()
        {
            if (_remoteAvatar == null || _levelSync == null)
            {
                return;
            }

            bool allowApply = _levelSync.IsReady;
            if (!allowApply || IsMainMenuScene())
            {
                _remoteAvatar.Clear();
                return;
            }

            if (_settings.Mode.Value == Mode.Host)
            {
                if (_hostSession == null || !_hostSession.IsConnected)
                {
                    _remoteAvatar.Clear();
                    return;
                }

                if (_hostSession.HasClientState)
                {
                    bool seatOverride;
                    PlayerStateData state = ApplySeatOverride(_hostSession.LatestClientState, out seatOverride);
                    float posSmooth = seatOverride ? 1f : _settings.GetSmoothingPosition();
                    float rotSmooth = seatOverride ? 1f : _settings.GetSmoothingRotation();
                    _remoteAvatar.Update(state, true, allowApply, posSmooth, rotSmooth, seatOverride);
                }
            }
            else if (_settings.Mode.Value == Mode.Client)
            {
                if (_clientSession == null || !_clientSession.IsConnected)
                {
                    _remoteAvatar.Clear();
                    return;
                }

                if (_clientSession.HasHostState)
                {
                    bool seatOverride;
                    PlayerStateData state = ApplySeatOverride(_clientSession.LatestHostState, out seatOverride);
                    float posSmooth = seatOverride ? 1f : _settings.GetSmoothingPosition();
                    float rotSmooth = seatOverride ? 1f : _settings.GetSmoothingRotation();
                    _remoteAvatar.Update(state, true, allowApply, posSmooth, rotSmooth, seatOverride);
                }
            }
            else
            {
                _remoteAvatar.Clear();
            }
        }

        private PlayerStateData ApplySeatOverride(PlayerStateData state, out bool seatOverride)
        {
            seatOverride = false;
            if (_vehicleSync == null)
            {
                return state;
            }

            Transform seatTransform;
            if (_vehicleSync.TryGetRemoteSeatTransform(out seatTransform) && seatTransform != null)
            {
                Vector3 seatPos = seatTransform.position;
                state.PosX = seatPos.x;
                state.PosY = seatPos.y;
                state.PosZ = seatPos.z;
                Quaternion seatRot = seatTransform.rotation;
                state.ViewRotX = seatRot.x;
                state.ViewRotY = seatRot.y;
                state.ViewRotZ = seatRot.z;
                state.ViewRotW = seatRot.w;
                seatOverride = true;
            }

            return state;
        }

        private void UpdateDevTools()
        {
            if (!_settings.DevMenuEnabled.Value)
            {
                _devMenuVisible = false;
                if (_devFreecam != null && _devFreecam.IsActive)
                {
                    _devFreecam.Disable();
                }
                return;
            }

            if (_devFreecam != null && _devFreecam.IsActive)
            {
                _devFreecam.MoveSpeed = _settings.DevFreecamSpeed.Value;
                _devFreecam.Update();
            }
        }

        private void HandleLocalLevelChange()
        {
            if (_levelSync == null)
            {
                return;
            }

            int levelIndex = _levelSync.CurrentLevelIndex;
            string levelName = _levelSync.CurrentLevelName ?? string.Empty;
            if (levelIndex == _lastLevelIndex && string.Equals(levelName, _lastLevelName, StringComparison.Ordinal))
            {
                return;
            }

            _lastLevelIndex = levelIndex;
            _lastLevelName = levelName;

            if (_hostSession != null)
            {
                _hostSession.OnLocalLevelChanged();
            }
            if (_clientSession != null)
            {
                _clientSession.OnLocalLevelChanged();
            }
            if (_remoteAvatar != null)
            {
                _remoteAvatar.Clear();
            }
            DebugLog.Verbose("Local level changed. Index=" + levelIndex + " Name=" + levelName);
        }

        private void OnHostLevelChanged(int levelIndex, string levelName)
        {
            if (_hostSession != null && _hostSession.IsRunning)
            {
                _hostSession.SendLevelChange(levelIndex, levelName);
            }
        }

        private OverlayState BuildOverlayState()
        {
            OverlayState state = new OverlayState();
            state.Visible = _overlayVisible && _settings.OverlayEnabled.Value;
            state.Title = "My Winter Car MP Mod v" + _modVersion;
            state.Mode = _settings.Mode.Value == Mode.Client ? "Client" : _settings.Mode.Value.ToString();
            state.Transport = _transport != null ? _transport.Kind.ToString() : _settings.Transport.Value.ToString();
            state.Warning = _transportWarning;
            state.LevelName = _levelSync != null ? _levelSync.CurrentLevelName : string.Empty;
            state.SendHz = _settings.GetSendHzClamped();
            state.TimeInfo = BuildTimeInfo();

            if (_settings.Mode.Value == Mode.Host)
            {
                if (_hostSession != null)
                {
                    state.Status = _hostSession.Status;
                    if (_hostSession.ClientSteamId != 0)
                    {
                        state.RemoteSteamId = _hostSession.ClientSteamId.ToString();
                    }
                    state.ProgressMarker = _hostSession.ProgressMarker;
                }
                else
                {
                    state.Status = "Host idle";
                }

                if (_transport != null && _transport.LocalSteamId != 0)
                {
                    state.LocalSteamId = _transport.LocalSteamId.ToString();
                }

                state.Hint = "F6 host toggle  F9 marker  F8 overlay";
            }
            else if (_settings.Mode.Value == Mode.Client)
            {
                if (_clientSession != null)
                {
                    state.Status = _clientSession.Status;
                    state.RemoteSteamId = _clientSession.HostSteamId != 0 ? _clientSession.HostSteamId.ToString() : _settings.SpectatorHostSteamId.Value.ToString();
                    state.ProgressMarker = _clientSession.ProgressMarker;
                    state.ServerSendHz = _clientSession.ServerSendHz;
                }
                else
                {
                    state.Status = "Client idle";
                    state.RemoteSteamId = _settings.SpectatorHostSteamId.Value.ToString();
                }

                if (_transport != null && _transport.LocalSteamId != 0)
                {
                    state.LocalSteamId = _transport.LocalSteamId.ToString();
                }

                state.Hint = "F7 client toggle  F8 overlay";
            }

            if (_settings.Mode.Value == Mode.Client && _settings.SpectatorHostSteamId.Value == 0 && _settings.Transport.Value == TransportKind.SteamP2P)
            {
                state.Warning = "Set SpectatorHostSteamId before connecting.";
            }

            return state;
        }

        private string BuildTimeInfo()
        {
            if (_timeSync == null || !_timeSync.Enabled)
            {
                return string.Empty;
            }

            TimeStateData state;
            bool fromRemote;
            if (!_timeSync.TryGetLastState(out state, out fromRemote))
            {
                return "Time: waiting";
            }

            Quaternion rot = new Quaternion(state.RotX, state.RotY, state.RotZ, state.RotW);
            Vector3 dir = rot * Vector3.forward;
            float elevation = Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg;
            string source = fromRemote ? "remote" : "local";
            return string.Format("Time: sunI={0:0.00} ambI={1:0.00} elev={2:0.0} ({3})",
                state.SunIntensity, state.AmbientIntensity, elevation, source);
        }

        private void UpdateLanDiscovery()
        {
            if (!_settings.LanDiscoveryEnabled.Value)
            {
                if (_lanDiscovery != null)
                {
                    _lanDiscovery.Dispose();
                    _lanDiscovery = null;
                }
                return;
            }

            EnsureLanDiscovery();
            int discoveryPort = _settings.LanDiscoveryPort.Value;
            TransportKind activeTransport = _transport != null ? _transport.Kind : _settings.Transport.Value;

            if (_settings.Mode.Value == Mode.Host)
            {
                if (_hostSession != null && _hostSession.IsRunning && activeTransport == TransportKind.TcpLan)
                {
                    _lanDiscovery.StopListening();
                    _lanDiscovery.BroadcastHost(discoveryPort, _settings.HostPort.Value, _buildId, _modVersion, _settings.GetLanBroadcastIntervalSeconds());
                }
                else
                {
                    _lanDiscovery.StopListening();
                }
            }
            else if (_settings.Mode.Value == Mode.Client)
            {
                _lanDiscovery.StartListening(discoveryPort);
                _lanDiscovery.Prune(TimeSpan.FromSeconds(_settings.GetLanHostTimeoutSeconds()));
            }
            else
            {
                _lanDiscovery.StopListening();
            }
        }

        private void EnsureLanDiscovery()
        {
            if (_lanDiscovery == null)
            {
                _lanDiscovery = new LanDiscovery(Logger, _settings.VerboseLogging.Value);
            }
        }

        private void DrawLanPanel()
        {
            if (!_overlayVisible || !_settings.OverlayEnabled.Value)
            {
                return;
            }

            if (IsMainMenuScene() && _settings.MainMenuPanelEnabled.Value)
            {
                return;
            }

            if (!_settings.LanDiscoveryEnabled.Value)
            {
                return;
            }

            if (_settings.Mode.Value != Mode.Client)
            {
                return;
            }

            EnsureLanDiscovery();
            LanHostInfo[] hosts = _lanDiscovery.GetHostsSnapshot();

            Rect area = new Rect(10f, 260f, 520f, 220f);
            GUILayout.BeginArea(area, GUI.skin.box);
            GUILayout.Label("LAN Hosts");

            if (hosts.Length == 0)
            {
                GUILayout.Label("No hosts discovered.");
            }
            else
            {
                _lanScroll = GUILayout.BeginScrollView(_lanScroll, false, true);
                for (int i = 0; i < hosts.Length; i++)
                {
                    LanHostInfo host = hosts[i];
                    string label = host.Address + ":" + host.Port;
                    if (!string.IsNullOrEmpty(host.ModVersion))
                    {
                        label += "  v" + host.ModVersion;
                    }
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(label);
                    if (GUILayout.Button("Join", GUILayout.Width(60f)))
                    {
                        ConnectLanHost(host);
                    }
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
            }

            GUILayout.EndArea();
        }

        private void DrawDevMenuPanel()
        {
            if (!_settings.DevMenuEnabled.Value || !_devMenuVisible)
            {
                return;
            }

            float width = 260f;
            float height = 230f;
            float x = Screen.width - width - 10f;
            float y = 10f;
            if (x < 10f)
            {
                x = 10f;
            }

            Rect area = new Rect(x, y, width, height);
            GUILayout.BeginArea(area, GUI.skin.box);
            GUILayout.Label("Dev Menu");

            bool freecamActive = _devFreecam != null && _devFreecam.IsActive;
            bool nextFreecam = GUILayout.Toggle(freecamActive, "Freecam (RMB to look)", GUI.skin.button);
            if (nextFreecam != freecamActive && _devFreecam != null)
            {
                if (nextFreecam)
                {
                    if (!_devFreecam.Enable(_settings.DevFreecamSpeed.Value))
                    {
                        _devStatus = "No usable camera for freecam.";
                    }
                }
                else
                {
                    _devFreecam.Disable();
                }
            }

            float speed = _settings.DevFreecamSpeed.Value;
            GUILayout.Label("Freecam Speed: " + speed.ToString("0.0"));
            float newSpeed = GUILayout.HorizontalSlider(speed, 0.5f, 20f);
            if (Mathf.Abs(newSpeed - speed) > 0.01f)
            {
                _settings.DevFreecamSpeed.Value = newSpeed;
                if (_devFreecam != null)
                {
                    _devFreecam.MoveSpeed = newSpeed;
                }
            }

            bool inMenu = IsMainMenuScene();
            GUI.enabled = !inMenu;
            if (GUILayout.Button("Save Position"))
            {
                SaveDevPosition();
            }
            GUI.enabled = _devHasSavedPosition && !inMenu;
            if (GUILayout.Button("Teleport to Saved"))
            {
                TeleportLocal(_devSavedPosition);
            }
            GUI.enabled = !inMenu;
            string remoteLabel = (_settings.Mode.Value == Mode.Host) ? "Teleport to Client" : "Teleport to Host";
            if (GUILayout.Button(remoteLabel))
            {
                TeleportToRemote();
            }
            GUI.enabled = true;

            if (inMenu)
            {
                GUILayout.Label("Teleport disabled in main menu.");
            }

            if (!string.IsNullOrEmpty(_devStatus))
            {
                GUILayout.Label(_devStatus);
            }

            GUILayout.EndArea();
        }

        private void ConnectLanHost(LanHostInfo host)
        {
            _settings.Mode.Value = Mode.Client;
            _settings.Transport.Value = TransportKind.TcpLan;
            _settings.SpectatorHostIP.Value = host.Address;
            _settings.HostPort.Value = host.Port;

            if (_clientSession != null && _clientSession.IsRunning)
            {
                _clientSession.Disconnect();
            }

            StartClient();
        }

        private void SaveDevPosition()
        {
            Vector3 position;
            if (!TryGetLocalPosition(out position))
            {
                _devStatus = "Local player not found.";
                return;
            }

            _devSavedPosition = position;
            _devHasSavedPosition = true;
            _devStatus = "Saved position.";
        }

        private void TeleportToRemote()
        {
            Vector3 position;
            if (!TryGetRemotePosition(out position))
            {
                return;
            }

            TeleportLocal(position);
        }

        private void TeleportLocal(Vector3 position)
        {
            Transform body;
            Transform view;
            if (!_devPlayerLocator.TryGetLocalTransforms(out body, out view))
            {
                _devStatus = "Local player not found.";
                return;
            }

            Rigidbody bodyRb = body != null ? body.GetComponent<Rigidbody>() : null;
            if (bodyRb != null)
            {
                bodyRb.velocity = Vector3.zero;
                bodyRb.angularVelocity = Vector3.zero;
                bodyRb.position = position;
            }

            if (body != null)
            {
                body.position = position;
            }
            if (view != null && view != body)
            {
                view.position = position;
            }

            _devStatus = "Teleported.";
        }

        private bool TryGetLocalPosition(out Vector3 position)
        {
            Transform body;
            Transform view;
            if (!_devPlayerLocator.TryGetLocalTransforms(out body, out view))
            {
                position = Vector3.zero;
                return false;
            }

            if (body != null)
            {
                position = body.position;
                return true;
            }
            if (view != null)
            {
                position = view.position;
                return true;
            }

            position = Vector3.zero;
            return false;
        }

        private bool TryGetRemotePosition(out Vector3 position)
        {
            position = Vector3.zero;
            if (_settings.Mode.Value == Mode.Host)
            {
                if (_hostSession == null || !_hostSession.HasClientState)
                {
                    _devStatus = "No client state yet.";
                    return false;
                }

                PlayerStateData state = _hostSession.LatestClientState;
                position = new Vector3(state.PosX, state.PosY, state.PosZ);
                _devStatus = "Teleported to client.";
                return true;
            }

            if (_settings.Mode.Value == Mode.Client)
            {
                if (_clientSession == null || !_clientSession.HasHostState)
                {
                    _devStatus = "No host state yet.";
                    return false;
                }

                PlayerStateData state = _clientSession.LatestHostState;
                position = new Vector3(state.PosX, state.PosY, state.PosZ);
                _devStatus = "Teleported to host.";
                return true;
            }

            _devStatus = "Teleport only available in host/client.";
            return false;
        }

        private void DrawMainMenuPanel()
        {
            if (!_settings.MainMenuPanelEnabled.Value)
            {
                return;
            }

            if (!IsMainMenuScene())
            {
                return;
            }

            float width = Mathf.Min(520f, Screen.width - 20f);
            float height = Mathf.Min(340f, Screen.height - 20f);
            float y = (_overlayVisible && _settings.OverlayEnabled.Value) ? 260f : 10f;
            if (y + height > Screen.height - 10f)
            {
                y = Mathf.Max(10f, Screen.height - height - 10f);
            }

            Rect area = new Rect(10f, y, width, height);
            GUILayout.BeginArea(area, GUI.skin.box);
            GUILayout.Label("Co-op");

            string status = "Idle";
            if (_settings.Mode.Value == Mode.Host && _hostSession != null)
            {
                status = _hostSession.Status;
            }
            else if (_settings.Mode.Value == Mode.Client && _clientSession != null)
            {
                status = _clientSession.Status;
            }
            GUILayout.Label("Mode: " + _settings.Mode.Value + "  Transport: " + _settings.Transport.Value);
            GUILayout.Label("Status: " + status);

            Mode currentMode = _settings.Mode.Value;
            GUILayout.BeginHorizontal();
            GUILayout.Label("Mode", GUILayout.Width(80f));
            bool hostSelected = currentMode == Mode.Host;
            bool clientSelected = currentMode == Mode.Client;
            if (GUILayout.Toggle(hostSelected, "Host", GUI.skin.button) && !hostSelected)
            {
                _settings.Mode.Value = Mode.Host;
            }
            if (GUILayout.Toggle(clientSelected, "Client", GUI.skin.button) && !clientSelected)
            {
                _settings.Mode.Value = Mode.Client;
            }
            GUILayout.EndHorizontal();

            TransportKind currentTransport = _settings.Transport.Value;
            GUILayout.BeginHorizontal();
            GUILayout.Label("Transport", GUILayout.Width(80f));
            bool steamSelected = currentTransport == TransportKind.SteamP2P;
            bool lanSelected = currentTransport == TransportKind.TcpLan;
            if (GUILayout.Toggle(steamSelected, "Steam P2P", GUI.skin.button) && !steamSelected)
            {
                _settings.Transport.Value = TransportKind.SteamP2P;
            }
            if (GUILayout.Toggle(lanSelected, "LAN (TCP)", GUI.skin.button) && !lanSelected)
            {
                _settings.Transport.Value = TransportKind.TcpLan;
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            string hostLabel = (_hostSession != null && _hostSession.IsRunning) ? "Stop Host" : "Host Game";
            if (GUILayout.Button(hostLabel, GUILayout.Width(120f)))
            {
                if (_hostSession != null && _hostSession.IsRunning)
                {
                    _hostSession.Stop();
                }
                else
                {
                    _settings.Mode.Value = Mode.Host;
                    StartHost();
                }
            }

            bool clientRunning = _clientSession != null && _clientSession.IsRunning;
            GUI.enabled = clientRunning;
            if (GUILayout.Button("Disconnect", GUILayout.Width(120f)))
            {
                _clientSession.Disconnect();
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.Space(4f);
            GUILayout.Label("Join via Steam (Client)");
            GUILayout.BeginHorizontal();
            GUILayout.Label("SteamID64", GUILayout.Width(80f));
            _menuSteamIdText = GUILayout.TextField(_menuSteamIdText ?? string.Empty, GUILayout.Width(200f));
            if (GUILayout.Button("Join Steam", GUILayout.Width(120f)))
            {
                JoinSteamFromMenu();
            }
            GUILayout.EndHorizontal();

            GUILayout.Label("Join via LAN (Client)");
            GUILayout.BeginHorizontal();
            GUILayout.Label("IP", GUILayout.Width(30f));
            _menuHostIpText = GUILayout.TextField(_menuHostIpText ?? string.Empty, GUILayout.Width(140f));
            GUILayout.Label("Port", GUILayout.Width(40f));
            _menuHostPortText = GUILayout.TextField(_menuHostPortText ?? string.Empty, GUILayout.Width(60f));
            if (GUILayout.Button("Join LAN", GUILayout.Width(120f)))
            {
                JoinLanFromMenu();
            }
            GUILayout.EndHorizontal();

            bool allowMulti = _settings.AllowMultipleInstances.Value;
            bool newAllowMulti = GUILayout.Toggle(allowMulti, "Allow multiple instances (skip Steam bootstrap)");
            if (newAllowMulti != allowMulti)
            {
                _settings.AllowMultipleInstances.Value = newAllowMulti;
                AllowMultipleInstances = newAllowMulti;
            }
            GUILayout.Label("Restart required for Steam changes.");

            if (!string.IsNullOrEmpty(_menuMessage))
            {
                GUILayout.Label(_menuMessage);
            }

            GUILayout.Space(4f);
            GUILayout.Label("LAN Hosts");
            if (!_settings.LanDiscoveryEnabled.Value)
            {
                GUILayout.Label("LAN discovery disabled.");
            }
            else
            {
                EnsureLanDiscovery();
                LanHostInfo[] hosts = _lanDiscovery.GetHostsSnapshot();
                if (hosts.Length == 0)
                {
                    GUILayout.Label("No hosts discovered.");
                }
                else
                {
                    _lanScroll = GUILayout.BeginScrollView(_lanScroll, false, true, GUILayout.Height(90f));
                    for (int i = 0; i < hosts.Length; i++)
                    {
                        LanHostInfo host = hosts[i];
                        string label = host.Address + ":" + host.Port;
                        if (!string.IsNullOrEmpty(host.ModVersion))
                        {
                            label += "  v" + host.ModVersion;
                        }
                        GUILayout.BeginHorizontal();
                        GUILayout.Label(label);
                        if (GUILayout.Button("Join", GUILayout.Width(60f)))
                        {
                            ConnectLanHost(host);
                        }
                        GUILayout.EndHorizontal();
                    }
                    GUILayout.EndScrollView();
                }
            }

            GUILayout.EndArea();
        }

        private void InitializeMenuFields()
        {
            _menuSteamIdText = _settings.SpectatorHostSteamId.Value.ToString();
            _menuHostIpText = _settings.SpectatorHostIP.Value;
            _menuHostPortText = _settings.HostPort.Value.ToString();
            _menuMessage = string.Empty;
        }

        private void JoinSteamFromMenu()
        {
            ulong steamId;
            if (!TryParseSteamId(_menuSteamIdText, out steamId))
            {
                _menuMessage = "Invalid SteamID64.";
                return;
            }

            _menuMessage = string.Empty;
            _settings.Mode.Value = Mode.Client;
            _settings.Transport.Value = TransportKind.SteamP2P;
            _settings.SpectatorHostSteamId.Value = steamId;
            StartClient();
        }

        private void JoinLanFromMenu()
        {
            int port;
            if (!TryParsePort(_menuHostPortText, out port))
            {
                _menuMessage = "Invalid LAN port.";
                return;
            }
            if (string.IsNullOrEmpty(_menuHostIpText))
            {
                _menuMessage = "LAN IP required.";
                return;
            }

            _menuMessage = string.Empty;
            _settings.Mode.Value = Mode.Client;
            _settings.Transport.Value = TransportKind.TcpLan;
            _settings.SpectatorHostIP.Value = _menuHostIpText.Trim();
            _settings.HostPort.Value = port;
            StartClient();
        }

        private static bool TryParseSteamId(string text, out ulong steamId)
        {
            if (string.IsNullOrEmpty(text))
            {
                steamId = 0;
                return false;
            }
            return ulong.TryParse(text.Trim(), out steamId) && steamId != 0;
        }

        private static bool TryParsePort(string text, out int port)
        {
            if (!int.TryParse(text, out port))
            {
                return false;
            }
            if (port <= 0 || port > 65535)
            {
                return false;
            }
            return true;
        }

        private bool IsMainMenuScene()
        {
            if (_levelSync == null)
            {
                return false;
            }

            string name = _levelSync.CurrentLevelName;
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            string normalized = name.Replace(" ", string.Empty);
            return string.Equals(normalized, "MainMenu", StringComparison.OrdinalIgnoreCase);
        }

        private void ApplyHarmonyPatches()
        {
            if (_harmony != null)
            {
                return;
            }

            _harmony = new Harmony("com.tudor.mywintercarmpmod");

            bool patchedAll = TryPatchSteamMethods();
            if (!patchedAll)
            {
                HookSteamAssemblyLoad();
            }
        }

        private bool TryPatchSteamMethods()
        {
            bool missing = false;

            if (!_steamRestartPatched)
            {
                MethodBase restartMethod = AccessTools.Method("Steamworks.SteamAPI:RestartAppIfNecessary");
                if (restartMethod != null)
                {
                    _harmony.Patch(restartMethod, prefix: new HarmonyMethod(typeof(SteamPatches), "RestartAppIfNecessaryPrefix"));
                    _steamRestartPatched = true;
                    if (Logger != null)
                    {
                        Logger.LogInfo("Patched Steamworks.SteamAPI:RestartAppIfNecessary");
                    }
                }
                else
                {
                    missing = true;
                }
            }

            if (!_steamInitPatched)
            {
                MethodBase initMethod = AccessTools.Method("Steamworks.SteamAPI:Init");
                if (initMethod != null)
                {
                    _harmony.Patch(initMethod, prefix: new HarmonyMethod(typeof(SteamPatches), "SteamApiInitPrefix"));
                    _steamInitPatched = true;
                    if (Logger != null)
                    {
                        Logger.LogInfo("Patched Steamworks.SteamAPI:Init");
                    }
                }
                else
                {
                    missing = true;
                }
            }

            if (!_steamManagerAwakePatched)
            {
                MethodBase awakeMethod = AccessTools.Method("SteamManager:Awake");
                if (awakeMethod != null)
                {
                    _harmony.Patch(awakeMethod, prefix: new HarmonyMethod(typeof(SteamPatches), "SteamManagerAwakePrefix"));
                    _steamManagerAwakePatched = true;
                    if (Logger != null)
                    {
                        Logger.LogInfo("Patched SteamManager:Awake");
                    }
                }
                else
                {
                    missing = true;
                }
            }

            return !missing;
        }

        private void HookSteamAssemblyLoad()
        {
            if (_steamPatchPending)
            {
                return;
            }

            _steamPatchPending = true;
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            if (Logger != null)
            {
                Logger.LogInfo("Steam API not loaded yet; waiting to apply patches.");
            }
        }

        private void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            if (!_steamPatchPending)
            {
                return;
            }

            if (TryPatchSteamMethods())
            {
                _steamPatchPending = false;
                AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
                if (Logger != null)
                {
                    Logger.LogInfo("Steam API patches applied after assembly load.");
                }
            }
        }

        private ConfigFile ResolveConfigFile()
        {
            string suffix = ResolveConfigSuffix();
            if (string.IsNullOrEmpty(suffix))
            {
                return Config;
            }

            string sanitized = SanitizeConfigSuffix(suffix);
            _configSuffix = sanitized;
            string fileName = "com.tudor.mywintercarmpmod." + sanitized + ".cfg";
            string path = Path.Combine(Paths.ConfigPath, fileName);
            if (Logger != null)
            {
                Logger.LogInfo("Using config override: " + path);
            }
            return new ConfigFile(path, true);
        }

        private void CoerceModeIfNeeded(ConfigFile config)
        {
            // Many launches came up as Spectator due to stale configs; force non-spectator for co-op.
            if (_settings.Mode != null && _settings.Mode.Value == Mode.Spectator)
            {
                _settings.Mode.Value = DefaultClientMode;
                if (config != null)
                {
                    config.Save();
                }
                if (Logger != null)
                {
                    Logger.LogWarning("Mode was Spectator; coerced to Client for co-op.");
                }
            }
        }

        private static string ResolveConfigSuffix()
        {
            string env = Environment.GetEnvironmentVariable("MWC_MPM_CONFIG");
            if (!string.IsNullOrEmpty(env))
            {
                return env;
            }

            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg == null)
                {
                    continue;
                }
                if (arg.StartsWith("--mwc-config=", StringComparison.OrdinalIgnoreCase))
                {
                    return arg.Substring("--mwc-config=".Length);
                }
                if (arg.Equals("--mwc-config", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    return args[i + 1];
                }
            }

            return null;
        }

        private static string SanitizeConfigSuffix(string suffix)
        {
            if (string.IsNullOrEmpty(suffix))
            {
                return null;
            }

            char[] chars = suffix.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                bool ok = (c >= 'a' && c <= 'z') ||
                          (c >= 'A' && c <= 'Z') ||
                          (c >= '0' && c <= '9') ||
                          c == '_' ||
                          c == '-';
                if (!ok)
                {
                    chars[i] = '_';
                }
            }
            return new string(chars);
        }
    }
}
