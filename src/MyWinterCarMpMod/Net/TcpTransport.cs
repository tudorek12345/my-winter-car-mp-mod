using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using BepInEx.Logging;
using MyWinterCarMpMod.Config;
using MyWinterCarMpMod.Util;

namespace MyWinterCarMpMod.Net
{
    public sealed class TcpTransport : ITransport
    {
        private const int MaxPayloadBytes = 262144;
        private readonly ManualLogSource _log;
        private readonly bool _verbose;
        private readonly string _bindIp;
        private readonly int _port;
        private readonly string _hostIp;
        private readonly Queue<TransportPacket> _incoming = new Queue<TransportPacket>();
        private readonly object _incomingLock = new object();
        private readonly object _sendLock = new object();

        private TcpListener _listener;
        private TcpClient _client;
        private NetworkStream _stream;
        private Thread _thread;
        private volatile bool _running;
        private bool _isHost;
        private string _status = "TCP idle.";

        public TcpTransport(string bindIp, int port, string hostIp, ManualLogSource log, bool verbose)
        {
            _bindIp = bindIp;
            _port = port;
            _hostIp = hostIp;
            _log = log;
            _verbose = verbose;
        }

        public TransportKind Kind
        {
            get { return TransportKind.TcpLan; }
        }

        public bool IsAvailable
        {
            get { return true; }
        }

        public bool IsConnected
        {
            get { return IsClientConnected(); }
        }

        public string Status
        {
            get { return _status; }
        }

        public ulong LocalSteamId
        {
            get { return 0ul; }
        }

        public bool StartHost()
        {
            Stop();
            _isHost = true;

            IPAddress bindAddress = IPAddress.Any;
            if (!string.IsNullOrEmpty(_bindIp))
            {
                IPAddress parsed;
                if (IPAddress.TryParse(_bindIp, out parsed))
                {
                    bindAddress = parsed;
                }
            }

            try
            {
                _listener = new TcpListener(bindAddress, _port);
                _listener.Start();
                _running = true;
                _thread = new Thread(HostAcceptLoop);
                _thread.IsBackground = true;
                _thread.Start();
                _status = "TCP host listening on " + _bindIp + ":" + _port;
                DebugLog.Info("TCP host listening on " + _bindIp + ":" + _port);
                return true;
            }
            catch (Exception ex)
            {
                _status = "TCP host failed: " + ex.Message;
                if (_log != null)
                {
                    _log.LogWarning(_status);
                }
                return false;
            }
        }

        public bool Connect()
        {
            Stop();
            _isHost = false;

            try
            {
                _client = new TcpClient();
                _client.NoDelay = true;
                _client.Connect(_hostIp, _port);
                _stream = _client.GetStream();
                _running = true;
                _thread = new Thread(ReadLoop);
                _thread.IsBackground = true;
                _thread.Start();
                _status = "TCP connected to " + _hostIp + ":" + _port;
                DebugLog.Info("TCP connected to " + _hostIp + ":" + _port);
                return true;
            }
            catch (Exception ex)
            {
                _status = "TCP connect failed: " + ex.Message;
                if (_log != null)
                {
                    _log.LogWarning(_status);
                }
                return false;
            }
        }

        public void Disconnect()
        {
            CloseClient();
        }

        public void Stop()
        {
            _running = false;
            CloseClient();

            if (_listener != null)
            {
                try
                {
                    _listener.Stop();
                }
                catch (Exception)
                {
                }
                _listener = null;
            }

            if (_thread != null && _thread.IsAlive)
            {
                try
                {
                    _thread.Join(500);
                }
                catch (Exception)
                {
                }
                _thread = null;
            }

            lock (_incomingLock)
            {
                _incoming.Clear();
            }
            _status = "TCP stopped.";
        }

        public void Update()
        {
        }

        public bool Send(byte[] payload, bool reliable)
        {
            return SendTo(0ul, payload, reliable);
        }

        public bool SendTo(ulong remoteId, byte[] payload, bool reliable)
        {
            if (_stream == null || payload == null || payload.Length == 0)
            {
                return false;
            }
            if (payload.Length > MaxPayloadBytes)
            {
                _status = "TCP send payload too large: " + payload.Length + " bytes.";
                if (_log != null)
                {
                    _log.LogWarning(_status);
                }
                DebugLog.Warn(_status);
                return false;
            }

            try
            {
                lock (_sendLock)
                {
                    byte[] lenBytes = BitConverter.GetBytes(payload.Length);
                    _stream.Write(lenBytes, 0, lenBytes.Length);
                    _stream.Write(payload, 0, payload.Length);
                    _stream.Flush();
                }
                return true;
            }
            catch (Exception ex)
            {
                _status = "TCP send failed: " + ex.Message;
                if (_log != null)
                {
                    _log.LogWarning(_status);
                }
                DebugLog.Warn(_status);
                CloseClient();
                return false;
            }
        }

        public bool TryReceive(out TransportPacket packet)
        {
            lock (_incomingLock)
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

        private void HostAcceptLoop()
        {
            while (_running)
            {
                try
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    if (!_running)
                    {
                        client.Close();
                        break;
                    }

                    _client = client;
                    _client.NoDelay = true;
                    _stream = _client.GetStream();
                    _status = "TCP client connected.";
                    DebugLog.Info("TCP client connected.");
                    ReadLoop();
                }
                catch (SocketException)
                {
                    if (_running && _log != null)
                    {
                        _log.LogWarning("TCP accept loop stopped.");
                    }
                }
                catch (Exception ex)
                {
                    if (_log != null)
                    {
                        _log.LogWarning("TCP accept error: " + ex.Message);
                    }
                }
            }
        }

        private void ReadLoop()
        {
            try
            {
                while (_running && IsClientConnected())
                {
                    int length = ReadInt32(_stream);
                    if (length <= 0)
                    {
                        break;
                    }
                    if (length > MaxPayloadBytes)
                    {
                        _status = "TCP payload too large: " + length + " bytes.";
                        if (_log != null)
                        {
                            _log.LogWarning(_status);
                        }
                        DebugLog.Warn(_status);
                        break;
                    }
                    byte[] payload = ReadExact(_stream, length);
                    if (payload == null)
                    {
                        break;
                    }

                    TransportPacket packet = new TransportPacket
                    {
                        SenderId = 0ul,
                        Payload = payload,
                        Length = payload.Length
                    };

                    lock (_incomingLock)
                    {
                        _incoming.Enqueue(packet);
                    }
                }
            }
            catch (Exception ex)
            {
                string msg = "TCP read loop error: " + ex.Message;
                if (_log != null)
                {
                    _log.LogWarning(msg);
                    _log.LogDebug(ex.ToString());
                }
                DebugLog.Warn(msg);
                DebugLog.Verbose(ex.ToString());
            }
            finally
            {
                if (_verbose && _log != null)
                {
                    _log.LogInfo("TCP read loop ended.");
                }
                DebugLog.Verbose("TCP read loop ended. IsHost=" + _isHost);
                CloseClient();
                if (_isHost && _running)
                {
                    _status = "TCP waiting for new client.";
                }
                else
                {
                    _status = "TCP disconnected.";
                }
            }
        }

        private void CloseClient()
        {
            if (_client != null)
            {
                try
                {
                    _client.Close();
                }
                catch (Exception)
                {
                }
                _client = null;
            }
            _stream = null;
        }

        private bool IsClientConnected()
        {
            if (_client == null)
            {
                return false;
            }
            try
            {
                Socket socket = _client.Client;
                if (socket == null)
                {
                    return false;
                }
                bool disconnected = socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0;
                return !disconnected && _client.Connected;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static int ReadInt32(NetworkStream stream)
        {
            byte[] buffer = ReadExact(stream, 4);
            if (buffer == null)
            {
                return 0;
            }
            return BitConverter.ToInt32(buffer, 0);
        }

        private static byte[] ReadExact(NetworkStream stream, int length)
        {
            if (length <= 0)
            {
                return null;
            }
            byte[] buffer = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = stream.Read(buffer, offset, length - offset);
                if (read <= 0)
                {
                    return null;
                }
                offset += read;
            }
            return buffer;
        }
    }
}
