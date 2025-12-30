using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using BepInEx.Logging;
using MyWinterCarMpMod.Util;

namespace MyWinterCarMpMod.Net
{
    public struct LanHostInfo
    {
        public string Address;
        public int Port;
        public string BuildId;
        public string ModVersion;
        public DateTime LastSeenUtc;
    }

    public sealed class LanDiscovery : IDisposable
    {
        private const string Magic = "MWCMPL";
        private readonly object _lock = new object();
        private readonly Dictionary<string, LanHostInfo> _hosts = new Dictionary<string, LanHostInfo>();
        private readonly ManualLogSource _log;
        private readonly bool _verbose;

        private UdpClient _listener;
        private Thread _thread;
        private volatile bool _running;
        private int _listenPort;
        private DateTime _nextBroadcastUtc;
        private UdpClient _broadcaster;

        public LanDiscovery(ManualLogSource log, bool verbose)
        {
            _log = log;
            _verbose = verbose;
        }

        public void StartListening(int port)
        {
            if (_listener != null && _listenPort == port)
            {
                return;
            }

            StopListening();

            try
            {
                _listener = new UdpClient(port);
                _listener.Client.ReceiveTimeout = 1000;
                _listenPort = port;
                _running = true;
                _thread = new Thread(ListenLoop);
                _thread.IsBackground = true;
                _thread.Start();
                DebugLog.Info("LAN discovery listening on UDP " + port);
            }
            catch (Exception ex)
            {
                if (_log != null)
                {
                    _log.LogWarning("LAN discovery listen failed: " + ex.Message);
                }
                StopListening();
            }
        }

        public void StopListening()
        {
            _running = false;

            if (_listener != null)
            {
                try
                {
                    _listener.Close();
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
                    _thread.Join(200);
                }
                catch (Exception)
                {
                }
                _thread = null;
            }
            if (_listenPort != 0)
            {
                DebugLog.Verbose("LAN discovery stopped.");
            }
        }

        public void BroadcastHost(int discoveryPort, int hostPort, string buildId, string modVersion, float intervalSeconds)
        {
            DateTime now = DateTime.UtcNow;
            if (now < _nextBroadcastUtc)
            {
                return;
            }
            _nextBroadcastUtc = now.AddSeconds(intervalSeconds);

            try
            {
                if (_broadcaster == null)
                {
                    _broadcaster = new UdpClient();
                    _broadcaster.EnableBroadcast = true;
                }

                string payload = string.Format("{0}|{1}|{2}|{3}", Magic, hostPort, Encode(buildId), Encode(modVersion));
                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                _broadcaster.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, discoveryPort));
                DebugLog.Verbose("LAN broadcast sent. HostPort=" + hostPort + " DiscoveryPort=" + discoveryPort);
            }
            catch (Exception ex)
            {
                if (_verbose && _log != null)
                {
                    _log.LogWarning("LAN broadcast failed: " + ex.Message);
                }
            }
        }

        public LanHostInfo[] GetHostsSnapshot()
        {
            lock (_lock)
            {
                LanHostInfo[] list = new LanHostInfo[_hosts.Count];
                int index = 0;
                foreach (LanHostInfo host in _hosts.Values)
                {
                    list[index++] = host;
                }
                return list;
            }
        }

        public void Prune(TimeSpan maxAge)
        {
            DateTime now = DateTime.UtcNow;
            lock (_lock)
            {
                List<string> remove = null;
                foreach (KeyValuePair<string, LanHostInfo> entry in _hosts)
                {
                    if (now - entry.Value.LastSeenUtc > maxAge)
                    {
                        if (remove == null)
                        {
                            remove = new List<string>();
                        }
                        remove.Add(entry.Key);
                    }
                }

                if (remove != null)
                {
                    for (int i = 0; i < remove.Count; i++)
                    {
                        _hosts.Remove(remove[i]);
                    }
                }
            }
        }

        public void Dispose()
        {
            StopListening();
            if (_broadcaster != null)
            {
                try
                {
                    _broadcaster.Close();
                }
                catch (Exception)
                {
                }
                _broadcaster = null;
            }
        }

        private void ListenLoop()
        {
            IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                try
                {
                    byte[] data = _listener.Receive(ref remote);
                    if (data == null || data.Length == 0)
                    {
                        continue;
                    }
                    HandlePacket(remote, data);
                }
                catch (SocketException ex)
                {
                    if (ex.SocketErrorCode == SocketError.TimedOut)
                    {
                        continue;
                    }
                    if (_verbose && _log != null)
                    {
                        _log.LogWarning("LAN discovery socket error: " + ex.Message);
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_verbose && _log != null)
                    {
                        _log.LogWarning("LAN discovery error: " + ex.Message);
                    }
                }
            }
        }

        private void HandlePacket(IPEndPoint remote, byte[] data)
        {
            string text = Encoding.UTF8.GetString(data);
            string[] parts = text.Split('|');
            if (parts.Length < 3 || !string.Equals(parts[0], Magic, StringComparison.Ordinal))
            {
                return;
            }

            int port;
            if (!int.TryParse(parts[1], out port))
            {
                return;
            }

            string buildId = parts.Length > 2 ? Decode(parts[2]) : string.Empty;
            string modVersion = parts.Length > 3 ? Decode(parts[3]) : string.Empty;
            string key = remote.Address + ":" + port;

            LanHostInfo info = new LanHostInfo
            {
                Address = remote.Address.ToString(),
                Port = port,
                BuildId = buildId,
                ModVersion = modVersion,
                LastSeenUtc = DateTime.UtcNow
            };

            lock (_lock)
            {
                _hosts[key] = info;
            }
            DebugLog.Verbose("LAN host discovered: " + info.Address + ":" + info.Port + " v" + info.ModVersion);
        }

        private static string Encode(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            return Convert.ToBase64String(bytes);
        }

        private static string Decode(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }
            try
            {
                byte[] bytes = Convert.FromBase64String(value);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}
