using System;
using BepInEx.Logging;
using MWCSpectatorSync.Config;
using MWCSpectatorSync.Sync;
using UnityEngine;

namespace MWCSpectatorSync.Net
{
    public sealed class HostSession
    {
        private static readonly string[] PlayerCameraComponentNames = new string[]
        {
            "MainCamera",
            "CarCameras",
            "CarCamerasController",
            "SmoothFollow",
            "S_Camera"
        };

        private readonly ITransport _transport;
        private readonly Settings _settings;
        private readonly ManualLogSource _log;
        private readonly bool _verbose;
        private readonly string _buildId;
        private readonly string _modVersion;
        private readonly LevelSync _levelSync;

        private bool _running;
        private bool _connected;
        private ulong _spectatorSteamId;
        private string _spectatorBuildId;
        private string _spectatorModVersion;
        private string _progressMarker = string.Empty;
        private float _nextSendTime;
        private Camera _cachedCamera;

        public HostSession(ITransport transport, Settings settings, LevelSync levelSync, ManualLogSource log, string buildId, string modVersion)
        {
            _transport = transport;
            _settings = settings;
            _levelSync = levelSync;
            _log = log;
            _verbose = settings.VerboseLogging.Value;
            _buildId = buildId ?? string.Empty;
            _modVersion = modVersion ?? string.Empty;
        }

        public bool IsRunning
        {
            get { return _running; }
        }

        public bool IsConnected
        {
            get { return _connected; }
        }

        public ulong SpectatorSteamId
        {
            get { return _spectatorSteamId; }
        }

        public string SpectatorBuildId
        {
            get { return _spectatorBuildId; }
        }

        public string SpectatorModVersion
        {
            get { return _spectatorModVersion; }
        }

        public string ProgressMarker
        {
            get { return _progressMarker; }
        }

        public bool Start()
        {
            _running = _transport.StartHost();
            _connected = false;
            _spectatorSteamId = 0ul;
            _spectatorBuildId = null;
            _spectatorModVersion = null;
            _nextSendTime = Time.realtimeSinceStartup;
            if (_running && _log != null)
            {
                _log.LogInfo("Host session started.");
            }
            return _running;
        }

        public void Stop()
        {
            if (_connected)
            {
                SendDisconnect();
            }
            _transport.Stop();
            _running = false;
            _connected = false;
            _spectatorSteamId = 0ul;
            if (_log != null)
            {
                _log.LogInfo("Host session stopped.");
            }
        }

        public void Update()
        {
            if (!_running)
            {
                return;
            }

            while (_transport.TryReceive(out TransportPacket packet))
            {
                HandlePacket(packet);
            }

            if (_connected)
            {
                float now = Time.realtimeSinceStartup;
                float interval = 1f / _settings.GetSendHzClamped();
                if (now >= _nextSendTime)
                {
                    _nextSendTime = now + interval;
                    SendCameraState();
                }
            }
        }

        public void SetProgressMarker(string marker)
        {
            _progressMarker = marker ?? string.Empty;
            if (_connected)
            {
                byte[] payload = Protocol.BuildProgressMarker(_progressMarker);
                _transport.Send(payload, _settings.ReliableForControl.Value);
            }
        }

        public void SendLevelChange(int levelIndex, string levelName)
        {
            if (!_connected)
            {
                return;
            }
            byte[] payload = Protocol.BuildLevelChange(levelIndex, levelName);
            _transport.Send(payload, _settings.ReliableForControl.Value);
        }

        private void HandlePacket(TransportPacket packet)
        {
            NetMessage message;
            string error;
            if (!Protocol.TryParse(packet.Payload, packet.Length, out message, out error))
            {
                if (_verbose && _log != null)
                {
                    _log.LogWarning("Host parse failed: " + error);
                }
                return;
            }

            switch (message.Type)
            {
                case MessageType.Hello:
                    HandleHello(packet.SenderId, message.Hello);
                    break;
                case MessageType.Ping:
                    _transport.SendTo(packet.SenderId, Protocol.BuildPong(), false);
                    break;
                case MessageType.Disconnect:
                    if (_verbose && _log != null)
                    {
                        _log.LogInfo("Spectator disconnected.");
                    }
                    _connected = false;
                    _spectatorSteamId = 0ul;
                    break;
            }
        }

        private void HandleHello(ulong senderId, HelloData hello)
        {
            if (_connected && senderId != _spectatorSteamId)
            {
                if (_verbose && _log != null)
                {
                    _log.LogInfo("Ignored Hello from additional spectator " + senderId);
                }
                return;
            }

            if (!IsAllowedSpectator(hello.SenderSteamId))
            {
                if (_log != null)
                {
                    _log.LogWarning("Rejected spectator " + hello.SenderSteamId + " (allowlist).");
                }
                _transport.SendTo(senderId, Protocol.BuildDisconnect(), true);
                return;
            }

            if (!string.IsNullOrEmpty(_buildId) && hello.BuildId != _buildId && _log != null)
            {
                _log.LogWarning("Spectator buildId mismatch: " + hello.BuildId + " (host " + _buildId + ")");
            }
            if (!string.IsNullOrEmpty(_modVersion) && hello.ModVersion != _modVersion && _log != null)
            {
                _log.LogWarning("Spectator modVersion mismatch: " + hello.ModVersion + " (host " + _modVersion + ")");
            }

            _spectatorSteamId = senderId != 0 ? senderId : hello.SenderSteamId;
            _spectatorBuildId = hello.BuildId;
            _spectatorModVersion = hello.ModVersion;
            _connected = true;

            byte[] ack = Protocol.BuildHelloAck(_transport.LocalSteamId, _settings.GetSendHzClamped());
            _transport.SendTo(senderId, ack, true);

            SendLevelChange(_levelSync.CurrentLevelIndex, _levelSync.CurrentLevelName);
            if (!string.IsNullOrEmpty(_progressMarker))
            {
                _transport.Send(Protocol.BuildProgressMarker(_progressMarker), _settings.ReliableForControl.Value);
            }
            SendCameraState();

            if (_log != null)
            {
                _log.LogInfo("Spectator connected: " + _spectatorSteamId);
            }
        }

        private bool IsAllowedSpectator(ulong senderSteamId)
        {
            ulong allowOnly = _settings.AllowOnlySteamId.Value;
            if (allowOnly == 0)
            {
                return true;
            }

            if (senderSteamId == allowOnly)
            {
                return true;
            }

            if (_transport.Kind == TransportKind.TcpLan && senderSteamId == 0)
            {
                if (_log != null)
                {
                    _log.LogWarning("AllowOnlySteamId set, but TCP spectator has no SteamID. Allowing.");
                }
                return true;
            }

            return false;
        }

        private void SendCameraState()
        {
            CameraStateData state;
            if (!TryGetCameraState(out state))
            {
                return;
            }
            byte[] payload = Protocol.BuildCameraState(state);
            _transport.Send(payload, false);
        }

        private bool TryGetCameraState(out CameraStateData state)
        {
            state = new CameraStateData();
            Camera cam = GetBestCamera();
            if (cam == null)
            {
                return false;
            }

            Transform t = cam.transform;
            Quaternion rot = t.rotation;
            state.UnixTimeMs = GetUnixTimeMs();
            state.PosX = t.position.x;
            state.PosY = t.position.y;
            state.PosZ = t.position.z;
            state.RotX = rot.x;
            state.RotY = rot.y;
            state.RotZ = rot.z;
            state.RotW = rot.w;
            state.Fov = cam.fieldOfView;
            return true;
        }

        private Camera GetBestCamera()
        {
            if (IsCameraUsable(_cachedCamera))
            {
                return _cachedCamera;
            }

            Camera cam = Camera.main;
            if (IsCameraUsable(cam))
            {
                _cachedCamera = cam;
                return cam;
            }

            Camera best = null;
            Camera[] cameras = Camera.allCameras;
            for (int i = 0; i < cameras.Length; i++)
            {
                Camera candidate = cameras[i];
                if (!IsCameraUsable(candidate))
                {
                    continue;
                }

                if (IsLikelyPlayerCamera(candidate))
                {
                    _cachedCamera = candidate;
                    return candidate;
                }

                if (best == null)
                {
                    best = candidate;
                }
            }

            _cachedCamera = best;
            return best;
        }

        private static bool IsCameraUsable(Camera cam)
        {
            if (cam == null || !cam.enabled || !cam.gameObject.activeInHierarchy)
            {
                return false;
            }
            return !IsMapCamera(cam);
        }

        private static bool IsLikelyPlayerCamera(Camera cam)
        {
            if (cam == null)
            {
                return false;
            }

            if (cam.CompareTag("MainCamera") || string.Equals(cam.name, "MainCamera", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (cam.GetComponent<AudioListener>() != null)
            {
                return true;
            }

            return HasAnyComponent(cam, PlayerCameraComponentNames);
        }

        private static bool HasAnyComponent(Camera cam, string[] typeNames)
        {
            for (int i = 0; i < typeNames.Length; i++)
            {
                if (cam.GetComponent(typeNames[i]) != null)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsMapCamera(Camera cam)
        {
            if (cam == null)
            {
                return false;
            }

            GameObject obj = cam.gameObject;
            if (IsMapCameraObject(obj))
            {
                return true;
            }

            Transform parent = obj != null ? obj.transform.parent : null;
            if (parent != null && IsMapCameraObject(parent.gameObject))
            {
                return true;
            }

            return false;
        }

        private static bool IsMapCameraObject(GameObject obj)
        {
            if (obj == null)
            {
                return false;
            }
            if (string.Equals(obj.name, "MapCamera", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return obj.CompareTag("MapCamera");
        }

        private void SendDisconnect()
        {
            _transport.Send(Protocol.BuildDisconnect(), true);
        }

        private static long GetUnixTimeMs()
        {
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return (long)(DateTime.UtcNow - epoch).TotalMilliseconds;
        }
    }
}
