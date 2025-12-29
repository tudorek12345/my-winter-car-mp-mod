using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using MWCSpectatorSync.Config;
using MWCSpectatorSync.Net;
using MWCSpectatorSync.Sync;
using MWCSpectatorSync.UI;
using MWCSpectatorSync.Util;
using UnityEngine;

namespace MWCSpectatorSync
{
    [BepInPlugin("com.tudor.mwcspectatorsync", "MWC Spectator Sync", "0.1.0")]
    public sealed class Plugin : BaseUnityPlugin
    {
        private Settings _settings;
        private Overlay _overlay;
        private LevelSync _levelSync;
        private HostSession _hostSession;
        private SpectatorSession _spectatorSession;
        private CameraFollower _cameraFollower;
        private ITransport _transport;

        private bool _overlayVisible;
        private string _transportWarning;
        private bool _lockdownApplied;
        private int _markerIndex;
        private readonly string[] _markerNotes = new string[] { "Start", "Garage", "Satsuma", "Drive", "Checkpoint" };
        private readonly List<MonoBehaviour> _lockdownDisabled = new List<MonoBehaviour>();
        private int _lockdownLevelIndex = int.MinValue;
        private string _lockdownLevelName = string.Empty;
        private readonly Dictionary<MonoBehaviour, object> _startGameMapControllers = new Dictionary<MonoBehaviour, object>();

        private static readonly string[] LockdownTokens = new string[]
        {
            "Player",
            "Input",
            "Controller",
            "Character",
            "Motor",
            "Drive",
            "MouseLook"
        };

        private static readonly string[] ExplicitInputTypeNames = new string[]
        {
            "SmoothMouseLook",
            "SimpleSmoothMouseLook",
            "MouseCarController",
            "AxisCarController",
            "MobileCarController",
            "SimpleCarController",
            "LogitechSteeringWheelSample",
            "RapidInputDemo",
            "PathInputDemo"
        };

        private static readonly string[] CameraControlTypeNames = new string[]
        {
            "MainCamera",
            "CarCameras",
            "CarCamerasController",
            "SmoothFollow",
            "S_Camera",
            "CameraInputDemo"
        };

        private string _buildId;
        private string _modVersion;

        private void Awake()
        {
            _settings = new Settings();
            _settings.Bind(Config, Logger);

            _overlay = new Overlay();
            _cameraFollower = new CameraFollower();
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
            else if (_settings.Mode.Value == Mode.Spectator && _spectatorSession != null)
            {
                _spectatorSession.Update();
                ApplySpectatorCamera();
                ApplySpectatorLockdownIfNeeded();
            }
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
            if (_spectatorSession != null)
            {
                _spectatorSession.Disconnect();
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
            ClearSpectatorLockdown();
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
            else if (_settings.Mode.Value == Mode.Spectator)
            {
                if (Input.GetKeyDown(KeyCode.F7))
                {
                    ToggleSpectator();
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
        }

        private void ToggleSpectator()
        {
            if (_spectatorSession == null || !_spectatorSession.IsRunning)
            {
                StartSpectator();
            }
            else
            {
                _spectatorSession.Disconnect();
                _lockdownApplied = false;
            }
        }

        private void StartSpectator()
        {
            ResetTransport();
            _transport = CreateTransport();
            _spectatorSession = new SpectatorSession(_transport, _settings, _levelSync, Logger, _buildId, _modVersion);
            _spectatorSession.Connect();
            _lockdownApplied = false;
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

        private void ApplySpectatorCamera()
        {
            if (_spectatorSession == null)
            {
                return;
            }

            bool allowApply = _spectatorSession.IsConnected && _levelSync.IsReady;
            _cameraFollower.Update(_spectatorSession.LatestCameraState, _spectatorSession.HasCameraState, allowApply, _settings.GetSmoothingPosition(), _settings.GetSmoothingRotation());
        }

        private void ApplySpectatorLockdownIfNeeded()
        {
            if (!_settings.SpectatorLockdown.Value)
            {
                if (_lockdownApplied)
                {
                    ClearSpectatorLockdown();
                }
                _lockdownApplied = false;
                return;
            }

            if (_spectatorSession == null || !_spectatorSession.IsConnected)
            {
                if (_lockdownApplied)
                {
                    ClearSpectatorLockdown();
                }
                _lockdownApplied = false;
                return;
            }

            if (_levelSync != null && !_levelSync.IsReady)
            {
                return;
            }

            if (!_lockdownApplied || HasLevelChangedSinceLockdown())
            {
                _lockdownApplied = true;
                ApplySpectatorLockdown();
                UpdateLockdownLevelSnapshot();
            }
        }

        private void ApplySpectatorLockdown()
        {
            int disabled = 0;
            MonoBehaviour[] behaviours = FindObjectsOfType<MonoBehaviour>();

            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null || !behaviour.enabled)
                {
                    continue;
                }

                string typeName = behaviour.GetType().Name;
                if (IsCameraControlScript(typeName) || IsExplicitInputScript(typeName) || MatchesAny(typeName, LockdownTokens))
                {
                    behaviour.enabled = false;
                    if (!_lockdownDisabled.Contains(behaviour))
                    {
                        _lockdownDisabled.Add(behaviour);
                    }
                    disabled++;
                }
            }

            ApplySpectatorMapToggleOverride();

            Logger.LogInfo("Spectator lockdown disabled " + disabled + " behaviours.");
        }

        private void ClearSpectatorLockdown()
        {
            for (int i = 0; i < _lockdownDisabled.Count; i++)
            {
                MonoBehaviour behaviour = _lockdownDisabled[i];
                if (behaviour != null)
                {
                    behaviour.enabled = true;
                }
            }
            _lockdownDisabled.Clear();
            RestoreSpectatorMapToggleOverride();
        }

        private void UpdateLockdownLevelSnapshot()
        {
            if (_levelSync == null)
            {
                _lockdownLevelIndex = int.MinValue;
                _lockdownLevelName = string.Empty;
                return;
            }
            _lockdownLevelIndex = _levelSync.CurrentLevelIndex;
            _lockdownLevelName = _levelSync.CurrentLevelName ?? string.Empty;
        }

        private bool HasLevelChangedSinceLockdown()
        {
            if (_levelSync == null)
            {
                return false;
            }

            if (_lockdownLevelIndex != _levelSync.CurrentLevelIndex)
            {
                return true;
            }

            string currentName = _levelSync.CurrentLevelName ?? string.Empty;
            return _lockdownLevelName != currentName;
        }

        private static bool MatchesAny(string value, string[] tokens)
        {
            for (int i = 0; i < tokens.Length; i++)
            {
                if (ContainsToken(value, tokens[i]))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool ContainsToken(string value, string token)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(token))
            {
                return false;
            }
            return value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsExplicitInputScript(string typeName)
        {
            for (int i = 0; i < ExplicitInputTypeNames.Length; i++)
            {
                if (string.Equals(typeName, ExplicitInputTypeNames[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsCameraControlScript(string typeName)
        {
            for (int i = 0; i < CameraControlTypeNames.Length; i++)
            {
                if (string.Equals(typeName, CameraControlTypeNames[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private void ApplySpectatorMapToggleOverride()
        {
            MonoBehaviour[] behaviours = FindObjectsOfType<MonoBehaviour>();
            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null)
                {
                    continue;
                }

                if (!string.Equals(behaviour.GetType().Name, "StartGame", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                FieldInfo field = behaviour.GetType().GetField("mapCameraController", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field == null)
                {
                    continue;
                }

                if (!_startGameMapControllers.ContainsKey(behaviour))
                {
                    _startGameMapControllers.Add(behaviour, field.GetValue(behaviour));
                }

                field.SetValue(behaviour, null);
            }
        }

        private void RestoreSpectatorMapToggleOverride()
        {
            if (_startGameMapControllers.Count == 0)
            {
                return;
            }

            foreach (KeyValuePair<MonoBehaviour, object> entry in _startGameMapControllers)
            {
                MonoBehaviour behaviour = entry.Key;
                if (behaviour == null)
                {
                    continue;
                }

                FieldInfo field = behaviour.GetType().GetField("mapCameraController", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field == null)
                {
                    continue;
                }

                field.SetValue(behaviour, entry.Value);
            }
            _startGameMapControllers.Clear();
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
            state.Title = "MWC Spectator Sync v" + _modVersion;
            state.Mode = _settings.Mode.Value.ToString();
            state.Transport = _transport != null ? _transport.Kind.ToString() : _settings.Transport.Value.ToString();
            state.Warning = _transportWarning;
            state.LevelName = _levelSync != null ? _levelSync.CurrentLevelName : string.Empty;
            state.SendHz = _settings.GetSendHzClamped();

            if (_settings.Mode.Value == Mode.Host)
            {
                if (_hostSession != null && _hostSession.IsRunning)
                {
                    state.Status = _hostSession.IsConnected ? "Hosting (spectator connected)" : "Hosting (waiting)";
                    if (_hostSession.SpectatorSteamId != 0)
                    {
                        state.RemoteSteamId = _hostSession.SpectatorSteamId.ToString();
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
            else
            {
                if (_spectatorSession != null && _spectatorSession.IsRunning)
                {
                    state.Status = _spectatorSession.IsConnected ? "Connected" : "Connecting";
                    state.RemoteSteamId = _spectatorSession.HostSteamId != 0 ? _spectatorSession.HostSteamId.ToString() : _settings.SpectatorHostSteamId.Value.ToString();
                    state.ProgressMarker = _spectatorSession.ProgressMarker;
                    state.ServerSendHz = _spectatorSession.ServerSendHz;
                }
                else
                {
                    state.Status = "Spectator idle";
                    state.RemoteSteamId = _settings.SpectatorHostSteamId.Value.ToString();
                }

                if (_transport != null && _transport.LocalSteamId != 0)
                {
                    state.LocalSteamId = _transport.LocalSteamId.ToString();
                }

                state.Hint = "F7 connect toggle  F8 overlay";
            }

            if (_settings.Mode.Value == Mode.Spectator && _settings.SpectatorHostSteamId.Value == 0 && _settings.Transport.Value == TransportKind.SteamP2P)
            {
                state.Warning = "Set SpectatorHostSteamId before connecting.";
            }

            return state;
        }
    }
}
