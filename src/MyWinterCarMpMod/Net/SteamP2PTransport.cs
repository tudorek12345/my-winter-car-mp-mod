using System;
using System.Collections.Generic;
using BepInEx.Logging;
using MyWinterCarMpMod.Config;
using Steamworks;

namespace MyWinterCarMpMod.Net
{
    public sealed class SteamP2PTransport : ITransport
    {
        private const int MaxPayloadBytes = 262144;
        private readonly ManualLogSource _log;
        private readonly bool _verbose;
        private readonly byte _channel;
        private readonly Queue<TransportPacket> _incoming = new Queue<TransportPacket>();
        private readonly object _lock = new object();

        private bool _initAttempted;
        private bool _steamInitialized;
        private bool _hasRemote;
        private CSteamID _remoteId;
        private string _status = "Steam P2P not initialized.";

        private Callback<P2PSessionRequest_t> _sessionRequest;
        private Callback<P2PSessionConnectFail_t> _sessionFail;

        public SteamP2PTransport(byte channel, ManualLogSource log, bool verbose)
        {
            _channel = channel;
            _log = log;
            _verbose = verbose;
            TryInitSteam();
        }

        public TransportKind Kind
        {
            get { return TransportKind.SteamP2P; }
        }

        public bool IsAvailable
        {
            get { return _steamInitialized; }
        }

        public bool IsConnected
        {
            get { return _hasRemote; }
        }

        public string Status
        {
            get { return _status; }
        }

        public ulong LocalSteamId
        {
            get { return _steamInitialized ? SteamUser.GetSteamID().m_SteamID : 0ul; }
        }

        public bool StartHost()
        {
            if (!IsAvailable)
            {
                _status = "Steam unavailable.";
                return false;
            }
            _status = "Steam host ready.";
            _hasRemote = false;
            return true;
        }

        public bool Connect()
        {
            if (!IsAvailable)
            {
                _status = "Steam unavailable.";
                return false;
            }
            _status = "Steam spectator ready.";
            return true;
        }

        public void Disconnect()
        {
            if (_steamInitialized && _hasRemote)
            {
                SteamNetworking.CloseP2PSessionWithUser(_remoteId);
            }
            _hasRemote = false;
        }

        public void Stop()
        {
            Disconnect();
            ClearIncoming();
        }

        public void Update()
        {
            if (!_steamInitialized)
            {
                return;
            }

            SteamAPI.RunCallbacks();

            uint size;
            while (SteamNetworking.IsP2PPacketAvailable(out size, _channel))
            {
                if (size == 0)
                {
                    break;
                }

                byte[] buffer = new byte[size];
                CSteamID remote;
                uint bytesRead;
                if (SteamNetworking.ReadP2PPacket(buffer, size, out bytesRead, out remote, _channel))
                {
                    TransportPacket packet = new TransportPacket
                    {
                        SenderId = remote.m_SteamID,
                        Payload = buffer,
                        Length = (int)bytesRead
                    };
                    lock (_lock)
                    {
                        _incoming.Enqueue(packet);
                    }
                }
            }
        }

        public bool Send(byte[] payload, bool reliable)
        {
            if (!_hasRemote)
            {
                return false;
            }
            return SendTo(_remoteId.m_SteamID, payload, reliable);
        }

        public bool SendTo(ulong remoteId, byte[] payload, bool reliable)
        {
            if (!IsAvailable || payload == null || payload.Length == 0)
            {
                return false;
            }
            if (payload.Length > MaxPayloadBytes)
            {
                if (_log != null)
                {
                    _log.LogWarning("Steam send payload too large: " + payload.Length + " bytes.");
                }
                return false;
            }

            if (remoteId == 0)
            {
                return false;
            }

            CSteamID steamId = new CSteamID(remoteId);
            EP2PSend sendType = reliable ? EP2PSend.k_EP2PSendReliable : EP2PSend.k_EP2PSendUnreliableNoDelay;
            bool ok = SteamNetworking.SendP2PPacket(steamId, payload, (uint)payload.Length, sendType, _channel);
            if (ok)
            {
                _remoteId = steamId;
                _hasRemote = true;
            }
            return ok;
        }

        public bool TryReceive(out TransportPacket packet)
        {
            lock (_lock)
            {
                if (_incoming.Count > 0)
                {
                    packet = _incoming.Dequeue();
                    return true;
                }
            }
            packet = default(TransportPacket);
            return false;
        }

        public void Dispose()
        {
            Stop();
        }

        private void TryInitSteam()
        {
            if (_initAttempted)
            {
                return;
            }
            _initAttempted = true;

            try
            {
                _steamInitialized = SteamAPI.Init();
            }
            catch (Exception ex)
            {
                _steamInitialized = false;
                _status = "Steam init exception: " + ex.Message;
                if (_log != null)
                {
                    _log.LogWarning(_status);
                }
                return;
            }

            if (_steamInitialized)
            {
                _status = "Steam P2P initialized.";
                _sessionRequest = Callback<P2PSessionRequest_t>.Create(OnSessionRequest);
                _sessionFail = Callback<P2PSessionConnectFail_t>.Create(OnSessionConnectFail);
            }
            else
            {
                _status = "SteamAPI.Init failed.";
                if (_log != null)
                {
                    _log.LogWarning(_status);
                }
            }
        }

        private void OnSessionRequest(P2PSessionRequest_t request)
        {
            if (!_steamInitialized)
            {
                return;
            }
            if (_verbose && _log != null)
            {
                _log.LogInfo("P2P session request from " + request.m_steamIDRemote.m_SteamID);
            }
            SteamNetworking.AcceptP2PSessionWithUser(request.m_steamIDRemote);
        }

        private void OnSessionConnectFail(P2PSessionConnectFail_t fail)
        {
            if (_log != null)
            {
                _log.LogWarning("P2P session failed: " + fail.m_eP2PSessionError);
            }
            _hasRemote = false;
        }

        private void ClearIncoming()
        {
            lock (_lock)
            {
                _incoming.Clear();
            }
        }
    }
}
