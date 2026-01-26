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
        SceneReady = 6,
        Ping = 7,
        Pong = 8,
        Disconnect = 9,
        PlayerState = 10,
        HelloReject = 11,
        DoorState = 12,
        VehicleState = 13,
        DoorEvent = 14,
        PickupState = 15,
        OwnershipRequest = 16,
        OwnershipUpdate = 17,
        WorldState = 18,
        WorldStateAck = 19,
        DoorHingeState = 20,
        TimeState = 21,
        VehicleControl = 22,
        NpcState = 23,
        VehicleSeat = 24,
        ScrapeState = 25,
        SorbetDashboardState = 26
    }

    public enum SyncObjectKind : byte
    {
        Door = 1,
        Vehicle = 2,
        Pickup = 3,
        Npc = 4
    }

    public enum OwnershipAction : byte
    {
        Request = 1,
        Release = 2
    }

    public enum OwnerKind : byte
    {
        None = 0,
        Host = 1,
        Client = 2
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

    public struct SceneReadyData
    {
        public uint SessionId;
        public int LevelIndex;
        public string LevelName;
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

    public struct DoorHingeStateData
    {
        public uint SessionId;
        public uint Sequence;
        public long UnixTimeMs;
        public uint DoorId;
        public float Angle;
    }

    public struct TimeStateData
    {
        public uint SessionId;
        public uint Sequence;
        public long UnixTimeMs;
        public float RotX;
        public float RotY;
        public float RotZ;
        public float RotW;
        public float SunIntensity;
        public float AmbientR;
        public float AmbientG;
        public float AmbientB;
        public float AmbientIntensity;
    }

    public struct DoorEventData
    {
        public uint SessionId;
        public uint Sequence;
        public uint DoorId;
        public byte Open;
        public string EventName;
    }

    public struct ScrapeStateData
    {
        public uint SessionId;
        public uint Sequence;
        public long UnixTimeMs;
        public uint DoorId;
        public int Layer;
        public float X;
        public float Xold;
        public float Distance;
    }

    public struct SorbetDashboardStateData
    {
        public uint SessionId;
        public uint Sequence;
        public long UnixTimeMs;
        public uint VehicleId;
        public byte Mask;
        public byte HeaterTempKind;
        public float HeaterTempValue;
        public byte HeaterBlowerKind;
        public float HeaterBlowerValue;
        public byte HeaterDirectionKind;
        public float HeaterDirectionValue;
        public byte WindowHeaterKind;
        public float WindowHeaterValue;
        public byte LightModesKind;
        public float LightModesValue;
        public byte HazardKind;
        public float HazardValue;
    }

    public struct VehicleStateData
    {
        public uint SessionId;
        public uint Sequence;
        public long UnixTimeMs;
        public uint VehicleId;
        public float PosX;
        public float PosY;
        public float PosZ;
        public float RotX;
        public float RotY;
        public float RotZ;
        public float RotW;
        public float VelX;
        public float VelY;
        public float VelZ;
        public float AngVelX;
        public float AngVelY;
        public float AngVelZ;
        public float Steer;
        public int Gear;
        public float EngineRpm;
    }

    public struct VehicleControlData
    {
        public uint SessionId;
        public uint Sequence;
        public long UnixTimeMs;
        public uint VehicleId;
        public float Throttle;
        public float Brake;
        public float Steer;
        public float Handbrake;
        public float Clutch;
        public int TargetGear;
        public byte Flags;
    }

    public struct VehicleSeatData
    {
        public uint SessionId;
        public uint Sequence;
        public long UnixTimeMs;
        public uint VehicleId;
        public byte SeatRole;
        public byte InSeat;
    }

    public struct NpcStateData
    {
        public uint SessionId;
        public uint Sequence;
        public long UnixTimeMs;
        public uint NpcId;
        public float PosX;
        public float PosY;
        public float PosZ;
        public float RotX;
        public float RotY;
        public float RotZ;
        public float RotW;
        public float VelX;
        public float VelY;
        public float VelZ;
        public float AngVelX;
        public float AngVelY;
        public float AngVelZ;
        public byte Flags;
    }

    public struct PickupStateData
    {
        public uint SessionId;
        public uint Sequence;
        public long UnixTimeMs;
        public uint PickupId;
        public float PosX;
        public float PosY;
        public float PosZ;
        public float RotX;
        public float RotY;
        public float RotZ;
        public float RotW;
        public byte Flags;
    }

    public struct OwnershipRequestData
    {
        public uint SessionId;
        public SyncObjectKind Kind;
        public uint ObjectId;
        public OwnershipAction Action;
    }

    public struct OwnershipUpdateData
    {
        public uint SessionId;
        public SyncObjectKind Kind;
        public uint ObjectId;
        public OwnerKind Owner;
    }

    public struct WorldStateData
    {
        public uint SessionId;
        public DoorStateData[] Doors;
        public DoorHingeStateData[] DoorHinges;
        public VehicleStateData[] Vehicles;
        public PickupStateData[] Pickups;
        public NpcStateData[] Npcs;
        public OwnershipUpdateData[] Ownership;
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
        public SceneReadyData SceneReady;
        public DoorStateData DoorState;
        public DoorHingeStateData DoorHingeState;
        public DoorEventData DoorEvent;
        public ScrapeStateData ScrapeState;
        public SorbetDashboardStateData SorbetDashboardState;
        public TimeStateData TimeState;
        public VehicleStateData VehicleState;
        public VehicleControlData VehicleControl;
        public VehicleSeatData VehicleSeat;
        public NpcStateData NpcState;
        public PickupStateData PickupState;
        public OwnershipRequestData OwnershipRequest;
        public OwnershipUpdateData OwnershipUpdate;
        public WorldStateData WorldState;
    }

    public static class Protocol
    {
        public const uint Magic = 0x4D575331;
        public const ushort Version = 13;
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

        public static byte[] BuildSceneReady(uint sessionId, int levelIndex, string levelName)
        {
            using (MemoryStream stream = new MemoryStream(128))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, MessageType.SceneReady);
                writer.Write(sessionId);
                writer.Write(levelIndex);
                WriteString(writer, levelName);
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

        public static byte[] BuildDoorHingeState(DoorHingeStateData state)
        {
            using (MemoryStream stream = new MemoryStream(32))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, MessageType.DoorHingeState);
                writer.Write(state.SessionId);
                writer.Write(state.Sequence);
                writer.Write(state.UnixTimeMs);
                writer.Write(state.DoorId);
                writer.Write(state.Angle);
                return stream.ToArray();
            }
        }

        public static byte[] BuildDoorEvent(DoorEventData state)
        {
            using (MemoryStream stream = new MemoryStream(64))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, MessageType.DoorEvent);
                writer.Write(state.SessionId);
                writer.Write(state.Sequence);
                writer.Write(state.DoorId);
                writer.Write(state.Open);
                if (!string.IsNullOrEmpty(state.EventName))
                {
                    writer.Write((byte)1);
                    writer.Write(state.EventName);
                }
                else
                {
                    writer.Write((byte)0);
                }
                return stream.ToArray();
            }
        }

        public static byte[] BuildScrapeState(ScrapeStateData state)
        {
            using (MemoryStream stream = new MemoryStream(48))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, MessageType.ScrapeState);
                writer.Write(state.SessionId);
                writer.Write(state.Sequence);
                writer.Write(state.UnixTimeMs);
                writer.Write(state.DoorId);
                writer.Write(state.Layer);
                writer.Write(state.X);
                writer.Write(state.Xold);
                writer.Write(state.Distance);
                return stream.ToArray();
            }
        }

        public static byte[] BuildSorbetDashboardState(SorbetDashboardStateData state)
        {
            using (MemoryStream stream = new MemoryStream(96))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, MessageType.SorbetDashboardState);
                writer.Write(state.SessionId);
                writer.Write(state.Sequence);
                writer.Write(state.UnixTimeMs);
                writer.Write(state.VehicleId);
                writer.Write(state.Mask);
                writer.Write(state.HeaterTempKind);
                writer.Write(state.HeaterTempValue);
                writer.Write(state.HeaterBlowerKind);
                writer.Write(state.HeaterBlowerValue);
                writer.Write(state.HeaterDirectionKind);
                writer.Write(state.HeaterDirectionValue);
                writer.Write(state.WindowHeaterKind);
                writer.Write(state.WindowHeaterValue);
                writer.Write(state.LightModesKind);
                writer.Write(state.LightModesValue);
                writer.Write(state.HazardKind);
                writer.Write(state.HazardValue);
                return stream.ToArray();
            }
        }

        public static byte[] BuildTimeState(TimeStateData state)
        {
            using (MemoryStream stream = new MemoryStream(64))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, MessageType.TimeState);
                writer.Write(state.SessionId);
                writer.Write(state.Sequence);
                writer.Write(state.UnixTimeMs);
                writer.Write(state.RotX);
                writer.Write(state.RotY);
                writer.Write(state.RotZ);
                writer.Write(state.RotW);
                writer.Write(state.SunIntensity);
                writer.Write(state.AmbientR);
                writer.Write(state.AmbientG);
                writer.Write(state.AmbientB);
                writer.Write(state.AmbientIntensity);
                return stream.ToArray();
            }
        }

        public static byte[] BuildVehicleState(VehicleStateData state)
        {
            using (MemoryStream stream = new MemoryStream(100))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, MessageType.VehicleState);
                writer.Write(state.SessionId);
                writer.Write(state.Sequence);
                writer.Write(state.UnixTimeMs);
                writer.Write(state.VehicleId);
                writer.Write(state.PosX);
                writer.Write(state.PosY);
                writer.Write(state.PosZ);
                writer.Write(state.RotX);
                writer.Write(state.RotY);
                writer.Write(state.RotZ);
                writer.Write(state.RotW);
                writer.Write(state.VelX);
                writer.Write(state.VelY);
                writer.Write(state.VelZ);
                writer.Write(state.AngVelX);
                writer.Write(state.AngVelY);
                writer.Write(state.AngVelZ);
                writer.Write(state.Steer);
                writer.Write(state.Gear);
                writer.Write(state.EngineRpm);
                return stream.ToArray();
            }
        }

        public static byte[] BuildNpcState(NpcStateData state)
        {
            using (MemoryStream stream = new MemoryStream(96))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, MessageType.NpcState);
                writer.Write(state.SessionId);
                writer.Write(state.Sequence);
                writer.Write(state.UnixTimeMs);
                writer.Write(state.NpcId);
                writer.Write(state.PosX);
                writer.Write(state.PosY);
                writer.Write(state.PosZ);
                writer.Write(state.RotX);
                writer.Write(state.RotY);
                writer.Write(state.RotZ);
                writer.Write(state.RotW);
                writer.Write(state.VelX);
                writer.Write(state.VelY);
                writer.Write(state.VelZ);
                writer.Write(state.AngVelX);
                writer.Write(state.AngVelY);
                writer.Write(state.AngVelZ);
                writer.Write(state.Flags);
                return stream.ToArray();
            }
        }

        public static byte[] BuildVehicleControl(VehicleControlData control)
        {
            using (MemoryStream stream = new MemoryStream(64))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, MessageType.VehicleControl);
                writer.Write(control.SessionId);
                writer.Write(control.Sequence);
                writer.Write(control.UnixTimeMs);
                writer.Write(control.VehicleId);
                writer.Write(control.Throttle);
                writer.Write(control.Brake);
                writer.Write(control.Steer);
                writer.Write(control.Handbrake);
                writer.Write(control.Clutch);
                writer.Write(control.TargetGear);
                writer.Write(control.Flags);
                return stream.ToArray();
            }
        }

        public static byte[] BuildVehicleSeat(VehicleSeatData state)
        {
            using (MemoryStream stream = new MemoryStream(32))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, MessageType.VehicleSeat);
                writer.Write(state.SessionId);
                writer.Write(state.Sequence);
                writer.Write(state.UnixTimeMs);
                writer.Write(state.VehicleId);
                writer.Write(state.SeatRole);
                writer.Write(state.InSeat);
                return stream.ToArray();
            }
        }

        public static byte[] BuildPickupState(PickupStateData state)
        {
            using (MemoryStream stream = new MemoryStream(96))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, MessageType.PickupState);
                writer.Write(state.SessionId);
                writer.Write(state.Sequence);
                writer.Write(state.UnixTimeMs);
                writer.Write(state.PickupId);
                writer.Write(state.PosX);
                writer.Write(state.PosY);
                writer.Write(state.PosZ);
                writer.Write(state.RotX);
                writer.Write(state.RotY);
                writer.Write(state.RotZ);
                writer.Write(state.RotW);
                writer.Write(state.Flags);
                return stream.ToArray();
            }
        }

        public static byte[] BuildOwnershipRequest(OwnershipRequestData request)
        {
            using (MemoryStream stream = new MemoryStream(24))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, MessageType.OwnershipRequest);
                writer.Write(request.SessionId);
                writer.Write((byte)request.Kind);
                writer.Write(request.ObjectId);
                writer.Write((byte)request.Action);
                return stream.ToArray();
            }
        }

        public static byte[] BuildOwnershipUpdate(OwnershipUpdateData update)
        {
            using (MemoryStream stream = new MemoryStream(24))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, MessageType.OwnershipUpdate);
                writer.Write(update.SessionId);
                writer.Write((byte)update.Kind);
                writer.Write(update.ObjectId);
                writer.Write((byte)update.Owner);
                return stream.ToArray();
            }
        }

        public static byte[] BuildWorldState(WorldStateData state)
        {
            using (MemoryStream stream = new MemoryStream(256))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, MessageType.WorldState);
                writer.Write(state.SessionId);
                WriteDoorStateList(writer, state.Doors);
                WriteDoorHingeStateList(writer, state.DoorHinges);
                WriteVehicleStateList(writer, state.Vehicles);
                WritePickupStateList(writer, state.Pickups);
                WriteNpcStateList(writer, state.Npcs);
                WriteOwnershipList(writer, state.Ownership);
                return stream.ToArray();
            }
        }

        public static byte[] BuildWorldStateAck(uint sessionId)
        {
            using (MemoryStream stream = new MemoryStream(16))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, MessageType.WorldStateAck);
                writer.Write(sessionId);
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
                        case MessageType.SceneReady:
                            result.SceneReady = new SceneReadyData
                            {
                                SessionId = reader.ReadUInt32(),
                                LevelIndex = reader.ReadInt32(),
                                LevelName = ReadString(reader)
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
                        case MessageType.DoorHingeState:
                            result.DoorHingeState = new DoorHingeStateData
                            {
                                SessionId = reader.ReadUInt32(),
                                Sequence = reader.ReadUInt32(),
                                UnixTimeMs = reader.ReadInt64(),
                                DoorId = reader.ReadUInt32(),
                                Angle = reader.ReadSingle()
                            };
                            break;
                        case MessageType.TimeState:
                            result.TimeState = new TimeStateData
                            {
                                SessionId = reader.ReadUInt32(),
                                Sequence = reader.ReadUInt32(),
                                UnixTimeMs = reader.ReadInt64(),
                                RotX = reader.ReadSingle(),
                                RotY = reader.ReadSingle(),
                                RotZ = reader.ReadSingle(),
                                RotW = reader.ReadSingle(),
                                SunIntensity = reader.ReadSingle(),
                                AmbientR = reader.ReadSingle(),
                                AmbientG = reader.ReadSingle(),
                                AmbientB = reader.ReadSingle(),
                                AmbientIntensity = reader.ReadSingle()
                            };
                            break;
                        case MessageType.DoorEvent:
                            DoorEventData doorEvent = new DoorEventData
                            {
                                SessionId = reader.ReadUInt32(),
                                Sequence = reader.ReadUInt32(),
                                DoorId = reader.ReadUInt32(),
                                Open = reader.ReadByte()
                            };
                            if (reader.BaseStream.Position < reader.BaseStream.Length)
                            {
                                byte hasEvent = reader.ReadByte();
                                if (hasEvent != 0 && reader.BaseStream.Position < reader.BaseStream.Length)
                                {
                                    doorEvent.EventName = reader.ReadString();
                                }
                            }
                            result.DoorEvent = doorEvent;
                            break;
                        case MessageType.ScrapeState:
                            result.ScrapeState = new ScrapeStateData
                            {
                                SessionId = reader.ReadUInt32(),
                                Sequence = reader.ReadUInt32(),
                                UnixTimeMs = reader.ReadInt64(),
                                DoorId = reader.ReadUInt32(),
                                Layer = reader.ReadInt32(),
                                X = reader.ReadSingle(),
                                Xold = reader.ReadSingle(),
                                Distance = reader.ReadSingle()
                            };
                            break;
                        case MessageType.SorbetDashboardState:
                            result.SorbetDashboardState = new SorbetDashboardStateData
                            {
                                SessionId = reader.ReadUInt32(),
                                Sequence = reader.ReadUInt32(),
                                UnixTimeMs = reader.ReadInt64(),
                                VehicleId = reader.ReadUInt32(),
                                Mask = reader.ReadByte(),
                                HeaterTempKind = reader.ReadByte(),
                                HeaterTempValue = reader.ReadSingle(),
                                HeaterBlowerKind = reader.ReadByte(),
                                HeaterBlowerValue = reader.ReadSingle(),
                                HeaterDirectionKind = reader.ReadByte(),
                                HeaterDirectionValue = reader.ReadSingle(),
                                WindowHeaterKind = reader.ReadByte(),
                                WindowHeaterValue = reader.ReadSingle(),
                                LightModesKind = reader.ReadByte(),
                                LightModesValue = reader.ReadSingle(),
                                HazardKind = reader.ReadByte(),
                                HazardValue = reader.ReadSingle()
                            };
                            break;
                        case MessageType.VehicleState:
                            result.VehicleState = new VehicleStateData
                            {
                                SessionId = reader.ReadUInt32(),
                                Sequence = reader.ReadUInt32(),
                                UnixTimeMs = reader.ReadInt64(),
                                VehicleId = reader.ReadUInt32(),
                                PosX = reader.ReadSingle(),
                                PosY = reader.ReadSingle(),
                                PosZ = reader.ReadSingle(),
                                RotX = reader.ReadSingle(),
                                RotY = reader.ReadSingle(),
                                RotZ = reader.ReadSingle(),
                                RotW = reader.ReadSingle(),
                                VelX = reader.ReadSingle(),
                                VelY = reader.ReadSingle(),
                                VelZ = reader.ReadSingle(),
                                AngVelX = reader.ReadSingle(),
                                AngVelY = reader.ReadSingle(),
                                AngVelZ = reader.ReadSingle(),
                                Steer = reader.ReadSingle(),
                                Gear = reader.ReadInt32(),
                                EngineRpm = reader.ReadSingle()
                            };
                            break;
                        case MessageType.VehicleControl:
                            result.VehicleControl = new VehicleControlData
                            {
                                SessionId = reader.ReadUInt32(),
                                Sequence = reader.ReadUInt32(),
                                UnixTimeMs = reader.ReadInt64(),
                                VehicleId = reader.ReadUInt32(),
                                Throttle = reader.ReadSingle(),
                                Brake = reader.ReadSingle(),
                                Steer = reader.ReadSingle(),
                                Handbrake = reader.ReadSingle(),
                                Clutch = reader.ReadSingle(),
                                TargetGear = reader.ReadInt32(),
                                Flags = reader.ReadByte()
                            };
                            break;
                        case MessageType.VehicleSeat:
                            result.VehicleSeat = new VehicleSeatData
                            {
                                SessionId = reader.ReadUInt32(),
                                Sequence = reader.ReadUInt32(),
                                UnixTimeMs = reader.ReadInt64(),
                                VehicleId = reader.ReadUInt32(),
                                SeatRole = reader.ReadByte(),
                                InSeat = reader.ReadByte()
                            };
                            break;
                        case MessageType.NpcState:
                            result.NpcState = new NpcStateData
                            {
                                SessionId = reader.ReadUInt32(),
                                Sequence = reader.ReadUInt32(),
                                UnixTimeMs = reader.ReadInt64(),
                                NpcId = reader.ReadUInt32(),
                                PosX = reader.ReadSingle(),
                                PosY = reader.ReadSingle(),
                                PosZ = reader.ReadSingle(),
                                RotX = reader.ReadSingle(),
                                RotY = reader.ReadSingle(),
                                RotZ = reader.ReadSingle(),
                                RotW = reader.ReadSingle(),
                                VelX = reader.ReadSingle(),
                                VelY = reader.ReadSingle(),
                                VelZ = reader.ReadSingle(),
                                AngVelX = reader.ReadSingle(),
                                AngVelY = reader.ReadSingle(),
                                AngVelZ = reader.ReadSingle(),
                                Flags = reader.ReadByte()
                            };
                            break;
                        case MessageType.PickupState:
                            result.PickupState = new PickupStateData
                            {
                                SessionId = reader.ReadUInt32(),
                                Sequence = reader.ReadUInt32(),
                                UnixTimeMs = reader.ReadInt64(),
                                PickupId = reader.ReadUInt32(),
                                PosX = reader.ReadSingle(),
                                PosY = reader.ReadSingle(),
                                PosZ = reader.ReadSingle(),
                                RotX = reader.ReadSingle(),
                                RotY = reader.ReadSingle(),
                                RotZ = reader.ReadSingle(),
                                RotW = reader.ReadSingle(),
                                Flags = reader.ReadByte()
                            };
                            break;
                        case MessageType.OwnershipRequest:
                            result.OwnershipRequest = new OwnershipRequestData
                            {
                                SessionId = reader.ReadUInt32(),
                                Kind = (SyncObjectKind)reader.ReadByte(),
                                ObjectId = reader.ReadUInt32(),
                                Action = (OwnershipAction)reader.ReadByte()
                            };
                            break;
                        case MessageType.OwnershipUpdate:
                            result.OwnershipUpdate = new OwnershipUpdateData
                            {
                                SessionId = reader.ReadUInt32(),
                                Kind = (SyncObjectKind)reader.ReadByte(),
                                ObjectId = reader.ReadUInt32(),
                                Owner = (OwnerKind)reader.ReadByte()
                            };
                            break;
                        case MessageType.WorldState:
                            result.WorldState = new WorldStateData
                            {
                                SessionId = reader.ReadUInt32(),
                                Doors = ReadDoorStateList(reader),
                                DoorHinges = ReadDoorHingeStateList(reader),
                                Vehicles = ReadVehicleStateList(reader),
                                Pickups = ReadPickupStateList(reader),
                                Npcs = ReadNpcStateList(reader),
                                Ownership = ReadOwnershipList(reader)
                            };
                            break;
                        case MessageType.WorldStateAck:
                            result.SessionId = reader.ReadUInt32();
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
                        case MessageType.SceneReady:
                            result.SessionId = result.SceneReady.SessionId;
                            break;
                        case MessageType.DoorState:
                            result.SessionId = result.DoorState.SessionId;
                            break;
                        case MessageType.DoorHingeState:
                            result.SessionId = result.DoorHingeState.SessionId;
                            break;
                        case MessageType.DoorEvent:
                            result.SessionId = result.DoorEvent.SessionId;
                            break;
                        case MessageType.ScrapeState:
                            result.SessionId = result.ScrapeState.SessionId;
                            break;
                        case MessageType.SorbetDashboardState:
                            result.SessionId = result.SorbetDashboardState.SessionId;
                            break;
                        case MessageType.TimeState:
                            result.SessionId = result.TimeState.SessionId;
                            break;
                        case MessageType.VehicleState:
                            result.SessionId = result.VehicleState.SessionId;
                            break;
                        case MessageType.VehicleControl:
                            result.SessionId = result.VehicleControl.SessionId;
                            break;
                        case MessageType.VehicleSeat:
                            result.SessionId = result.VehicleSeat.SessionId;
                            break;
                        case MessageType.NpcState:
                            result.SessionId = result.NpcState.SessionId;
                            break;
                        case MessageType.PickupState:
                            result.SessionId = result.PickupState.SessionId;
                            break;
                        case MessageType.OwnershipRequest:
                            result.SessionId = result.OwnershipRequest.SessionId;
                            break;
                        case MessageType.OwnershipUpdate:
                            result.SessionId = result.OwnershipUpdate.SessionId;
                            break;
                        case MessageType.WorldState:
                            result.SessionId = result.WorldState.SessionId;
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

        private static void WriteDoorStateList(BinaryWriter writer, DoorStateData[] states)
        {
            if (states == null)
            {
                writer.Write(0);
                return;
            }
            writer.Write(states.Length);
            for (int i = 0; i < states.Length; i++)
            {
                WriteDoorState(writer, states[i]);
            }
        }

        private static void WriteDoorHingeStateList(BinaryWriter writer, DoorHingeStateData[] states)
        {
            if (states == null)
            {
                writer.Write(0);
                return;
            }
            writer.Write(states.Length);
            for (int i = 0; i < states.Length; i++)
            {
                WriteDoorHingeState(writer, states[i]);
            }
        }

        private static void WriteVehicleStateList(BinaryWriter writer, VehicleStateData[] states)
        {
            if (states == null)
            {
                writer.Write(0);
                return;
            }
            writer.Write(states.Length);
            for (int i = 0; i < states.Length; i++)
            {
                WriteVehicleState(writer, states[i]);
            }
        }

        private static void WriteNpcStateList(BinaryWriter writer, NpcStateData[] states)
        {
            if (states == null)
            {
                writer.Write(0);
                return;
            }
            writer.Write(states.Length);
            for (int i = 0; i < states.Length; i++)
            {
                WriteNpcState(writer, states[i]);
            }
        }

        private static void WritePickupStateList(BinaryWriter writer, PickupStateData[] states)
        {
            if (states == null)
            {
                writer.Write(0);
                return;
            }
            writer.Write(states.Length);
            for (int i = 0; i < states.Length; i++)
            {
                WritePickupState(writer, states[i]);
            }
        }

        private static void WriteOwnershipList(BinaryWriter writer, OwnershipUpdateData[] states)
        {
            if (states == null)
            {
                writer.Write(0);
                return;
            }
            writer.Write(states.Length);
            for (int i = 0; i < states.Length; i++)
            {
                WriteOwnershipUpdate(writer, states[i]);
            }
        }

        private static DoorStateData[] ReadDoorStateList(BinaryReader reader)
        {
            int count = ReadCount(reader);
            if (count == 0)
            {
                return new DoorStateData[0];
            }
            DoorStateData[] states = new DoorStateData[count];
            for (int i = 0; i < count; i++)
            {
                states[i] = ReadDoorState(reader);
            }
            return states;
        }

        private static DoorHingeStateData[] ReadDoorHingeStateList(BinaryReader reader)
        {
            int count = ReadCount(reader);
            if (count == 0)
            {
                return new DoorHingeStateData[0];
            }
            DoorHingeStateData[] states = new DoorHingeStateData[count];
            for (int i = 0; i < count; i++)
            {
                states[i] = ReadDoorHingeState(reader);
            }
            return states;
        }

        private static VehicleStateData[] ReadVehicleStateList(BinaryReader reader)
        {
            int count = ReadCount(reader);
            if (count == 0)
            {
                return new VehicleStateData[0];
            }
            VehicleStateData[] states = new VehicleStateData[count];
            for (int i = 0; i < count; i++)
            {
                states[i] = ReadVehicleState(reader);
            }
            return states;
        }

        private static NpcStateData[] ReadNpcStateList(BinaryReader reader)
        {
            int count = ReadCount(reader);
            if (count == 0)
            {
                return new NpcStateData[0];
            }
            NpcStateData[] states = new NpcStateData[count];
            for (int i = 0; i < count; i++)
            {
                states[i] = ReadNpcState(reader);
            }
            return states;
        }

        private static PickupStateData[] ReadPickupStateList(BinaryReader reader)
        {
            int count = ReadCount(reader);
            if (count == 0)
            {
                return new PickupStateData[0];
            }
            PickupStateData[] states = new PickupStateData[count];
            for (int i = 0; i < count; i++)
            {
                states[i] = ReadPickupState(reader);
            }
            return states;
        }

        private static OwnershipUpdateData[] ReadOwnershipList(BinaryReader reader)
        {
            int count = ReadCount(reader);
            if (count == 0)
            {
                return new OwnershipUpdateData[0];
            }
            OwnershipUpdateData[] states = new OwnershipUpdateData[count];
            for (int i = 0; i < count; i++)
            {
                states[i] = ReadOwnershipUpdate(reader);
            }
            return states;
        }

        private static void WriteDoorState(BinaryWriter writer, DoorStateData state)
        {
            writer.Write(state.SessionId);
            writer.Write(state.Sequence);
            writer.Write(state.UnixTimeMs);
            writer.Write(state.DoorId);
            writer.Write(state.RotX);
            writer.Write(state.RotY);
            writer.Write(state.RotZ);
            writer.Write(state.RotW);
        }

        private static void WriteDoorHingeState(BinaryWriter writer, DoorHingeStateData state)
        {
            writer.Write(state.SessionId);
            writer.Write(state.Sequence);
            writer.Write(state.UnixTimeMs);
            writer.Write(state.DoorId);
            writer.Write(state.Angle);
        }

        private static void WriteVehicleState(BinaryWriter writer, VehicleStateData state)
        {
            writer.Write(state.SessionId);
            writer.Write(state.Sequence);
            writer.Write(state.UnixTimeMs);
            writer.Write(state.VehicleId);
            writer.Write(state.PosX);
            writer.Write(state.PosY);
            writer.Write(state.PosZ);
            writer.Write(state.RotX);
            writer.Write(state.RotY);
            writer.Write(state.RotZ);
            writer.Write(state.RotW);
            writer.Write(state.VelX);
            writer.Write(state.VelY);
            writer.Write(state.VelZ);
            writer.Write(state.AngVelX);
            writer.Write(state.AngVelY);
            writer.Write(state.AngVelZ);
            writer.Write(state.Steer);
            writer.Write(state.Gear);
            writer.Write(state.EngineRpm);
        }

        private static void WriteNpcState(BinaryWriter writer, NpcStateData state)
        {
            writer.Write(state.SessionId);
            writer.Write(state.Sequence);
            writer.Write(state.UnixTimeMs);
            writer.Write(state.NpcId);
            writer.Write(state.PosX);
            writer.Write(state.PosY);
            writer.Write(state.PosZ);
            writer.Write(state.RotX);
            writer.Write(state.RotY);
            writer.Write(state.RotZ);
            writer.Write(state.RotW);
            writer.Write(state.VelX);
            writer.Write(state.VelY);
            writer.Write(state.VelZ);
            writer.Write(state.AngVelX);
            writer.Write(state.AngVelY);
            writer.Write(state.AngVelZ);
            writer.Write(state.Flags);
        }

        private static void WritePickupState(BinaryWriter writer, PickupStateData state)
        {
            writer.Write(state.SessionId);
            writer.Write(state.Sequence);
            writer.Write(state.UnixTimeMs);
            writer.Write(state.PickupId);
            writer.Write(state.PosX);
            writer.Write(state.PosY);
            writer.Write(state.PosZ);
            writer.Write(state.RotX);
            writer.Write(state.RotY);
            writer.Write(state.RotZ);
            writer.Write(state.RotW);
            writer.Write(state.Flags);
        }

        private static void WriteOwnershipUpdate(BinaryWriter writer, OwnershipUpdateData state)
        {
            writer.Write(state.SessionId);
            writer.Write((byte)state.Kind);
            writer.Write(state.ObjectId);
            writer.Write((byte)state.Owner);
        }

        private static DoorStateData ReadDoorState(BinaryReader reader)
        {
            return new DoorStateData
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
        }

        private static DoorHingeStateData ReadDoorHingeState(BinaryReader reader)
        {
            return new DoorHingeStateData
            {
                SessionId = reader.ReadUInt32(),
                Sequence = reader.ReadUInt32(),
                UnixTimeMs = reader.ReadInt64(),
                DoorId = reader.ReadUInt32(),
                Angle = reader.ReadSingle()
            };
        }

        private static VehicleStateData ReadVehicleState(BinaryReader reader)
        {
            return new VehicleStateData
            {
                SessionId = reader.ReadUInt32(),
                Sequence = reader.ReadUInt32(),
                UnixTimeMs = reader.ReadInt64(),
                VehicleId = reader.ReadUInt32(),
                PosX = reader.ReadSingle(),
                PosY = reader.ReadSingle(),
                PosZ = reader.ReadSingle(),
                RotX = reader.ReadSingle(),
                RotY = reader.ReadSingle(),
                RotZ = reader.ReadSingle(),
                RotW = reader.ReadSingle(),
                VelX = reader.ReadSingle(),
                VelY = reader.ReadSingle(),
                VelZ = reader.ReadSingle(),
                AngVelX = reader.ReadSingle(),
                AngVelY = reader.ReadSingle(),
                AngVelZ = reader.ReadSingle(),
                Steer = reader.ReadSingle(),
                Gear = reader.ReadInt32(),
                EngineRpm = reader.ReadSingle()
            };
        }

        private static NpcStateData ReadNpcState(BinaryReader reader)
        {
            return new NpcStateData
            {
                SessionId = reader.ReadUInt32(),
                Sequence = reader.ReadUInt32(),
                UnixTimeMs = reader.ReadInt64(),
                NpcId = reader.ReadUInt32(),
                PosX = reader.ReadSingle(),
                PosY = reader.ReadSingle(),
                PosZ = reader.ReadSingle(),
                RotX = reader.ReadSingle(),
                RotY = reader.ReadSingle(),
                RotZ = reader.ReadSingle(),
                RotW = reader.ReadSingle(),
                VelX = reader.ReadSingle(),
                VelY = reader.ReadSingle(),
                VelZ = reader.ReadSingle(),
                AngVelX = reader.ReadSingle(),
                AngVelY = reader.ReadSingle(),
                AngVelZ = reader.ReadSingle(),
                Flags = reader.ReadByte()
            };
        }

        private static PickupStateData ReadPickupState(BinaryReader reader)
        {
            return new PickupStateData
            {
                SessionId = reader.ReadUInt32(),
                Sequence = reader.ReadUInt32(),
                UnixTimeMs = reader.ReadInt64(),
                PickupId = reader.ReadUInt32(),
                PosX = reader.ReadSingle(),
                PosY = reader.ReadSingle(),
                PosZ = reader.ReadSingle(),
                RotX = reader.ReadSingle(),
                RotY = reader.ReadSingle(),
                RotZ = reader.ReadSingle(),
                RotW = reader.ReadSingle(),
                Flags = reader.ReadByte()
            };
        }

        private static OwnershipUpdateData ReadOwnershipUpdate(BinaryReader reader)
        {
            return new OwnershipUpdateData
            {
                SessionId = reader.ReadUInt32(),
                Kind = (SyncObjectKind)reader.ReadByte(),
                ObjectId = reader.ReadUInt32(),
                Owner = (OwnerKind)reader.ReadByte()
            };
        }

        private static int ReadCount(BinaryReader reader)
        {
            const int maxCount = 4096;
            int count = reader.ReadInt32();
            if (count < 0 || count > maxCount)
            {
                throw new IOException("Invalid list count.");
            }
            return count;
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
