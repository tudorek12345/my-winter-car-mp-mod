using System;
using MWCSpectatorSync.Config;

namespace MWCSpectatorSync.Net
{
    public struct TransportPacket
    {
        public ulong SenderId;
        public byte[] Payload;
        public int Length;
    }

    public interface ITransport : IDisposable
    {
        TransportKind Kind { get; }
        bool IsAvailable { get; }
        bool IsConnected { get; }
        string Status { get; }
        ulong LocalSteamId { get; }

        bool StartHost();
        bool Connect();
        void Disconnect();
        void Stop();

        void Update();
        bool Send(byte[] payload, bool reliable);
        bool SendTo(ulong remoteId, byte[] payload, bool reliable);
        bool TryReceive(out TransportPacket packet);
    }
}
