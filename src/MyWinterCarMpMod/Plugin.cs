using System;
using BepInEx;
using MyWinterCarMpMod.Config;
using MyWinterCarMpMod.Net;
using MyWinterCarMpMod.Sync;
using MyWinterCarMpMod.UI;
using MyWinterCarMpMod.Util;
using UnityEngine;

namespace MyWinterCarMpMod
{
    [BepInPlugin("com.tudor.mywintercarmpmod", "My Winter Car MP Mod", "0.1.0")]
    public sealed class Plugin : BaseUnityPlugin
    {
        private Settings _settings;
        private Overlay _overlay;
        private LevelSync _levelSync;
        private HostSession _hostSession;
        private ClientSession _clientSession;
        private RemotePlayerAvatar _remoteAvatar;
        private ITransport _transport;

        private bool _overlayVisible;
        private string _transportWarning;
        private int _markerIndex;
        private readonly string[] _markerNotes = new string[] { "Start", "Garage", "Satsuma", "Drive", "Checkpoint" };

        private string _buildId;
        private string _modVersion;

        private void Awake()
        {
            _settings = new Settings();
            _settings.Bind(Config, Logger);

            _overlay = new Overlay();
            _remoteAvatar = new RemotePlayerAvatar();
            _overlayVisible = _settings.OverlayEnabled.Value;
            _buildId = Application.version + "|" + Application.unityVersion;
            _modVersion = Info.Metadata.Version.ToString();

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
            HandleHotkeys();

            if (_transport != null)
            {
                _transport.Update();
            }

            if (_levelSync != null)
            {
                _levelSync.Update();
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

        private void OnGUI()
        {
            OverlayState state = BuildOverlayState();
            _overlay.Draw(state);
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
        }

        private void HandleHotkeys()
        {
            if (Input.GetKeyDown(KeyCode.F8))
            {
                _overlayVisible = !_overlayVisible;
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
            ResetTransport();
            _transport = CreateTransport();
            _hostSession = new HostSession(_transport, _settings, _levelSync, Logger, _buildId, _modVersion);
            _hostSession.Start();
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
            ResetTransport();
            _transport = CreateTransport();
            _clientSession = new ClientSession(_transport, _settings, _levelSync, Logger, _buildId, _modVersion);
            _clientSession.Connect();
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
            if (_settings.Mode.Value == Mode.Host)
            {
                if (_hostSession == null || !_hostSession.IsConnected)
                {
                    _remoteAvatar.Clear();
                    return;
                }

                if (_hostSession.HasClientState)
                {
                    _remoteAvatar.Update(_hostSession.LatestClientState, true, allowApply, _settings.GetSmoothingPosition(), _settings.GetSmoothingRotation());
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
                    _remoteAvatar.Update(_clientSession.LatestHostState, true, allowApply, _settings.GetSmoothingPosition(), _settings.GetSmoothingRotation());
                }
            }
            else
            {
                _remoteAvatar.Clear();
            }
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

            if (_settings.Mode.Value == Mode.Host)
            {
                if (_hostSession != null && _hostSession.IsRunning)
                {
                    state.Status = _hostSession.IsConnected ? "Hosting (client connected)" : "Hosting (waiting)";
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
                if (_clientSession != null && _clientSession.IsRunning)
                {
                    state.Status = _clientSession.IsConnected ? "Connected" : "Connecting";
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
    }
}
