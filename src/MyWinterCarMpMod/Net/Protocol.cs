using System;
using System.IO;
using System.Text;

namespace MyWinterCarMpMod.Net
{
    public enum MessageType : ushort
    {
        Hello = 1,
        HelloAck = 2,
        CameraState = 3,
        LevelChange = 4,
        ProgressMarker = 5,
        Ping = 6,
        Pong = 7,
        Disconnect = 8,
        PlayerState = 9,
        HelloReject = 10,
        DoorState = 11
    }

    public struct HelloData
    {
        public ulong SenderSteamId;
        public uint ClientNonce;
        public string BuildId;
        public string ModVersion;
    }

    public struct HelloAckData
    {
        public ulong HostSteamId;
        public uint ClientNonce;
        public uint SessionId;
        public int ServerSendHz;
    }

    public struct HelloRejectData
    {
        public uint ClientNonce;
        public string Reason;
    }

    public struct CameraStateData
    {
        public uint SessionId;
        public uint Sequence;
        public long UnixTimeMs;
        public float PosX;
        public float PosY;
        public float PosZ;
        public float RotX;
        public float RotY;
        public float RotZ;
        public float RotW;
        public float Fov;
    }

    public struct PlayerStateData
    {
        public uint SessionId;
        public uint Sequence;
        public long UnixTimeMs;
        public float PosX;
        public float PosY;
        public float PosZ;
        public float ViewRotX;
        public float ViewRotY;
        public float ViewRotZ;
        public float ViewRotW;
    }

    public struct PingData
    {
        public uint SessionId;
        public long UnixTimeMs;
    }

    public struct PongData
    {
        public uint SessionId;
        public long UnixTimeMs;
    }

    public struct LevelChangeData
    {
        public uint SessionId;
        public int LevelIndex;
        public string LevelName;
    }

    public struct ProgressMarkerData
    {
        public uint SessionId;
        public string Marker;
    }

    public struct DoorStateData
    {
        public uint SessionId;
        public uint Sequence;
        public long UnixTimeMs;
        public uint DoorId;
        public float RotX;
        public float RotY;
        public float RotZ;
        public float RotW;
    }

    public sealed class NetMessage
    {
        public MessageType Type;
        public uint SessionId;
        public HelloData Hello;
        public HelloAckData HelloAck;
        public HelloRejectData HelloReject;
        public CameraStateData CameraState;
        public PlayerStateData PlayerState;
        public PingData Ping;
        public PongData Pong;
        public LevelChangeData LevelChange;
        public ProgressMarkerData ProgressMarker;
        public DoorStateData DoorState;
    }

    public static class Protocol
    {
        public const uint Magic = 0x4D575331;
        public const ushort Version = 3;
        private const int MaxStringBytes = 4096;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        public static byte[] BuildHello(ulong senderSteamId, uint clientNonce, string buildId, string modVersion)
        {
            using (MemoryStream stream = new MemoryStream(128))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, MessageType.Hello);
                writer.Write(senderSteamId);
                writer.Write(clientNonce);
                WriteString(writer, buildId);
                WriteString(writer, modVersion);
                return stream.ToArray();
            }
        }

        public static byte[] BuildHelloAck(ulong hostSteamId, uint clientNonce, uint sessionId, int serverSendHz)
        {
            using (MemoryStream stream = new MemoryStream(64))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, MessageType.HelloAck);
                writer.Write(hostSteamId);
                writer.Write(clientNonce);
                writer.Write(sessionId);
                writer.Write(serverSendHz);
                return stream.ToArray();
            }
        }

        public static byte[] BuildHelloReject(uint clientNonce, string reason)
        {
            using (MemoryStream stream = new MemoryStream(128))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, MessageType.HelloReject);
                writer.Write(clientNonce);
                WriteString(writer, reason);
                return stream.ToArray();
            }
        }

        public static byte[] BuildCameraState(CameraStateData state)
        {
            using (MemoryStream stream = new MemoryStream(64))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, MessageType.CameraState);
                writer.Write(state.SessionId);
                writer.Write(state.Sequence);
                writer.Write(state.UnixTimeMs);
                writer.Write(state.PosX);
                writer.Write(state.PosY);
                writer.Write(state.PosZ);
                writer.Write(state.RotX);
                writer.Write(state.RotY);
                writer.Write(state.RotZ);
                writer.Write(state.RotW);
                writer.Write(state.Fov);
                return stream.ToArray();
            }
        }

        public static byte[] BuildPlayerState(PlayerStateData state)
        {
            using (MemoryStream stream = new MemoryStream(64))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, MessageType.PlayerState);
                writer.Write(state.SessionId);
                writer.Write(state.Sequence);
                writer.Write(state.UnixTimeMs);
                writer.Write(state.PosX);
                writer.Write(state.PosY);
                writer.Write(state.PosZ);
                writer.Write(state.ViewRotX);
                writer.Write(state.ViewRotY);
                writer.Write(state.ViewRotZ);
                writer.Write(state.ViewRotW);
                return stream.ToArray();
            }
        }

        public static byte[] BuildLevelChange(uint sessionId, int levelIndex, string levelName)
        {
            using (MemoryStream stream = new MemoryStream(128))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, MessageType.LevelChange);
                writer.Write(sessionId);
                writer.Write(levelIndex);
                WriteString(writer, levelName);
                return stream.ToArray();
            }
        }

        public static byte[] BuildProgressMarker(uint sessionId, string marker)
        {
            using (MemoryStream stream = new MemoryStream(128))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, MessageType.ProgressMarker);
                writer.Write(sessionId);
                WriteString(writer, marker);
                return stream.ToArray();
            }
        }

        public static byte[] BuildPing(uint sessionId, long unixTimeMs)
        {
            using (MemoryStream stream = new MemoryStream(24))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, MessageType.Ping);
                writer.Write(sessionId);
                writer.Write(unixTimeMs);
                return stream.ToArray();
            }
        }

        public static byte[] BuildPong(uint sessionId, long unixTimeMs)
        {
            using (MemoryStream stream = new MemoryStream(24))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, MessageType.Pong);
                writer.Write(sessionId);
                writer.Write(unixTimeMs);
                return stream.ToArray();
            }
        }

        public static byte[] BuildDisconnect(uint sessionId)
        {
            using (MemoryStream stream = new MemoryStream(16))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, MessageType.Disconnect);
                writer.Write(sessionId);
                return stream.ToArray();
            }
        }

        public static byte[] BuildDoorState(DoorStateData state)
        {
            using (MemoryStream stream = new MemoryStream(48))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, MessageType.DoorState);
                writer.Write(state.SessionId);
                writer.Write(state.Sequence);
                writer.Write(state.UnixTimeMs);
                writer.Write(state.DoorId);
                writer.Write(state.RotX);
                writer.Write(state.RotY);
                writer.Write(state.RotZ);
                writer.Write(state.RotW);
                return stream.ToArray();
            }
        }

        public static bool TryParse(byte[] payload, int length, out NetMessage message, out string error)
        {
            message = null;
            error = null;

            if (payload == null || length < 8)
            {
                error = "Payload too short.";
                return false;
            }

            try
            {
                using (MemoryStream stream = new MemoryStream(payload, 0, length))
                using (BinaryReader reader = new BinaryReader(stream))
                {
                    uint magic = reader.ReadUInt32();
                    ushort version = reader.ReadUInt16();
                    ushort typeValue = reader.ReadUInt16();

                    if (magic != Magic)
                    {
                        error = "Magic mismatch.";
                        return false;
                    }

                    if (version != Version)
                    {
                        error = "Version mismatch.";
                        return false;
                    }

                    MessageType type = (MessageType)typeValue;
                    NetMessage result = new NetMessage { Type = type };

                    switch (type)
                    {
                        case MessageType.Hello:
                            result.Hello = new HelloData
                            {
                                SenderSteamId = reader.ReadUInt64(),
                                ClientNonce = reader.ReadUInt32(),
                                BuildId = ReadString(reader),
                                ModVersion = ReadString(reader)
                            };
                            break;
                        case MessageType.HelloAck:
                            result.HelloAck = new HelloAckData
                            {
                                HostSteamId = reader.ReadUInt64(),
                                ClientNonce = reader.ReadUInt32(),
                                SessionId = reader.ReadUInt32(),
                                ServerSendHz = reader.ReadInt32()
                            };
                            break;
                        case MessageType.HelloReject:
                            result.HelloReject = new HelloRejectData
                            {
                                ClientNonce = reader.ReadUInt32(),
                                Reason = ReadString(reader)
                            };
                            break;
                        case MessageType.CameraState:
                            result.CameraState = new CameraStateData
                            {
                                SessionId = reader.ReadUInt32(),
                                Sequence = reader.ReadUInt32(),
                                UnixTimeMs = reader.ReadInt64(),
                                PosX = reader.ReadSingle(),
                                PosY = reader.ReadSingle(),
                                PosZ = reader.ReadSingle(),
                                RotX = reader.ReadSingle(),
                                RotY = reader.ReadSingle(),
                                RotZ = reader.ReadSingle(),
                                RotW = reader.ReadSingle(),
                                Fov = reader.ReadSingle()
                            };
                            break;
                        case MessageType.PlayerState:
                            result.PlayerState = new PlayerStateData
                            {
                                SessionId = reader.ReadUInt32(),
                                Sequence = reader.ReadUInt32(),
                                UnixTimeMs = reader.ReadInt64(),
                                PosX = reader.ReadSingle(),
                                PosY = reader.ReadSingle(),
                                PosZ = reader.ReadSingle(),
                                ViewRotX = reader.ReadSingle(),
                                ViewRotY = reader.ReadSingle(),
                                ViewRotZ = reader.ReadSingle(),
                                ViewRotW = reader.ReadSingle()
                            };
                            break;
                        case MessageType.LevelChange:
                            result.LevelChange = new LevelChangeData
                            {
                                SessionId = reader.ReadUInt32(),
                                LevelIndex = reader.ReadInt32(),
                                LevelName = ReadString(reader)
                            };
                            break;
                        case MessageType.ProgressMarker:
                            result.ProgressMarker = new ProgressMarkerData
                            {
                                SessionId = reader.ReadUInt32(),
                                Marker = ReadString(reader)
                            };
                            break;
                        case MessageType.Ping:
                            result.Ping = new PingData
                            {
                                SessionId = reader.ReadUInt32(),
                                UnixTimeMs = reader.ReadInt64()
                            };
                            result.SessionId = result.Ping.SessionId;
                            break;
                        case MessageType.Pong:
                            result.Pong = new PongData
                            {
                                SessionId = reader.ReadUInt32(),
                                UnixTimeMs = reader.ReadInt64()
                            };
                            result.SessionId = result.Pong.SessionId;
                            break;
                        case MessageType.Disconnect:
                            result.SessionId = reader.ReadUInt32();
                            break;
                        case MessageType.DoorState:
                            result.DoorState = new DoorStateData
                            {
                                SessionId = reader.ReadUInt32(),
                                Sequence = reader.ReadUInt32(),
                                UnixTimeMs = reader.ReadInt64(),
                                DoorId = reader.ReadUInt32(),
                                RotX = reader.ReadSingle(),
                                RotY = reader.ReadSingle(),
                                RotZ = reader.ReadSingle(),
                                RotW = reader.ReadSingle()
                            };
                            break;
                        default:
                            error = "Unknown message type.";
                            return false;
                    }

                    switch (type)
                    {
                        case MessageType.HelloAck:
                            result.SessionId = result.HelloAck.SessionId;
                            break;
                        case MessageType.CameraState:
                            result.SessionId = result.CameraState.SessionId;
                            break;
                        case MessageType.PlayerState:
                            result.SessionId = result.PlayerState.SessionId;
                            break;
                        case MessageType.LevelChange:
                            result.SessionId = result.LevelChange.SessionId;
                            break;
                        case MessageType.ProgressMarker:
                            result.SessionId = result.ProgressMarker.SessionId;
                            break;
                        case MessageType.DoorState:
                            result.SessionId = result.DoorState.SessionId;
                            break;
                    }

                    message = result;
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static byte[] BuildNoPayload(MessageType type)
        {
            using (MemoryStream stream = new MemoryStream(16))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, type);
                return stream.ToArray();
            }
        }

        private static void WriteHeader(BinaryWriter writer, MessageType type)
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write((ushort)type);
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                writer.Write(0);
                return;
            }

            byte[] bytes = Utf8.GetBytes(value);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadString(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > MaxStringBytes)
            {
                throw new IOException("Invalid string length.");
            }
            if (length == 0)
            {
                return string.Empty;
            }
            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
            {
                throw new EndOfStreamException("Unexpected end of string payload.");
            }
            return Utf8.GetString(bytes);
        }
    }
}
