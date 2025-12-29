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
        PlayerState = 9
    }

    public struct HelloData
    {
        public ulong SenderSteamId;
        public string BuildId;
        public string ModVersion;
    }

    public struct HelloAckData
    {
        public ulong HostSteamId;
        public int ServerSendHz;
    }

    public struct CameraStateData
    {
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
        public long UnixTimeMs;
        public float PosX;
        public float PosY;
        public float PosZ;
        public float ViewRotX;
        public float ViewRotY;
        public float ViewRotZ;
        public float ViewRotW;
    }

    public struct LevelChangeData
    {
        public int LevelIndex;
        public string LevelName;
    }

    public struct ProgressMarkerData
    {
        public string Marker;
    }

    public sealed class NetMessage
    {
        public MessageType Type;
        public HelloData Hello;
        public HelloAckData HelloAck;
        public CameraStateData CameraState;
        public PlayerStateData PlayerState;
        public LevelChangeData LevelChange;
        public ProgressMarkerData ProgressMarker;
    }

    public static class Protocol
    {
        public const uint Magic = 0x4D575331;
        public const ushort Version = 1;
        private const int MaxStringBytes = 4096;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        public static byte[] BuildHello(ulong senderSteamId, string buildId, string modVersion)
        {
            using (MemoryStream stream = new MemoryStream(128))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, MessageType.Hello);
                writer.Write(senderSteamId);
                WriteString(writer, buildId);
                WriteString(writer, modVersion);
                return stream.ToArray();
            }
        }

        public static byte[] BuildHelloAck(ulong hostSteamId, int serverSendHz)
        {
            using (MemoryStream stream = new MemoryStream(64))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, MessageType.HelloAck);
                writer.Write(hostSteamId);
                writer.Write(serverSendHz);
                return stream.ToArray();
            }
        }

        public static byte[] BuildCameraState(CameraStateData state)
        {
            using (MemoryStream stream = new MemoryStream(64))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, MessageType.CameraState);
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

        public static byte[] BuildLevelChange(int levelIndex, string levelName)
        {
            using (MemoryStream stream = new MemoryStream(128))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, MessageType.LevelChange);
                writer.Write(levelIndex);
                WriteString(writer, levelName);
                return stream.ToArray();
            }
        }

        public static byte[] BuildProgressMarker(string marker)
        {
            using (MemoryStream stream = new MemoryStream(128))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, MessageType.ProgressMarker);
                WriteString(writer, marker);
                return stream.ToArray();
            }
        }

        public static byte[] BuildPing()
        {
            return BuildNoPayload(MessageType.Ping);
        }

        public static byte[] BuildPong()
        {
            return BuildNoPayload(MessageType.Pong);
        }

        public static byte[] BuildDisconnect()
        {
            return BuildNoPayload(MessageType.Disconnect);
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
                                BuildId = ReadString(reader),
                                ModVersion = ReadString(reader)
                            };
                            break;
                        case MessageType.HelloAck:
                            result.HelloAck = new HelloAckData
                            {
                                HostSteamId = reader.ReadUInt64(),
                                ServerSendHz = reader.ReadInt32()
                            };
                            break;
                        case MessageType.CameraState:
                            result.CameraState = new CameraStateData
                            {
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
                                LevelIndex = reader.ReadInt32(),
                                LevelName = ReadString(reader)
                            };
                            break;
                        case MessageType.ProgressMarker:
                            result.ProgressMarker = new ProgressMarkerData
                            {
                                Marker = ReadString(reader)
                            };
                            break;
                        case MessageType.Ping:
                        case MessageType.Pong:
                        case MessageType.Disconnect:
                            break;
                        default:
                            error = "Unknown message type.";
                            return false;
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
                throw new InvalidDataException("Invalid string length.");
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
