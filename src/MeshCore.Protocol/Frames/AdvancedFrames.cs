using MeshCore.Protocol.Models;

namespace MeshCore.Protocol.Frames;

/// <summary><c>RESP_CODE_TUNING_PARAMS</c>: receive-delay base and airtime factor.</summary>
public sealed record TuningParams(double RxDelayBase, double AirtimeFactor) : MeshFrame((byte)ResponseCode.TuningParams);

/// <summary><c>RESP_CODE_EXPORT_CONTACT</c>: a signed advert packet that <c>CMD_IMPORT_CONTACT</c> accepts.</summary>
public sealed record ExportedContact(ReadOnlyMemory<byte> Advert) : MeshFrame((byte)ResponseCode.ExportContact);

/// <summary><c>RESP_CODE_PRIVATE_KEY</c>: the node's 64-byte private key. Handle with care.</summary>
public sealed record PrivateKey(ReadOnlyMemory<byte> Key) : MeshFrame((byte)ResponseCode.PrivateKey);

/// <summary><c>PUSH_CODE_LOGIN_SUCCESS</c> / <c>PUSH_CODE_LOGIN_FAIL</c>.</summary>
/// <param name="IsAdmin">Bit 0 of the permissions byte: the login has admin rights (needed for remote CLI).</param>
public sealed record LoginResultPush(
    byte Code, bool Success, PublicKeyPrefix Sender, bool IsAdmin, byte Permissions, DateTimeOffset? ServerTime, byte? AclPermissions, byte? FirmwareLevel)
    : MeshFrame(Code);

/// <summary><c>PUSH_CODE_STATUS_RESPONSE</c>: a repeater's or room server's status. Decoded into
/// <see cref="Stats"/> when it's the repeater layout; <see cref="Raw"/> always holds the bytes.</summary>
public sealed record StatusResponsePush(PublicKeyPrefix Sender, ReadOnlyMemory<byte> Raw, RepeaterStats? Stats)
    : MeshFrame((byte)PushCode.StatusResponse);

/// <summary><c>PUSH_CODE_TELEMETRY_RESPONSE</c>: sensor readings in CayenneLPP format.</summary>
public sealed record TelemetryResponsePush(PublicKeyPrefix Sender, IReadOnlyList<LppReading> Readings, ReadOnlyMemory<byte> Raw)
    : MeshFrame((byte)PushCode.TelemetryResponse);

/// <summary><c>PUSH_CODE_TRACE_DATA</c>: a trace came back. <see cref="HopSnrs"/>[i] is the SNR hop i heard
/// the trace at; <see cref="FinalSnr"/> is how we heard the last hop.</summary>
public sealed record TraceDataPush(uint Tag, uint AuthCode, int HashSize, IReadOnlyList<string> HopHashes, IReadOnlyList<double> HopSnrs, double FinalSnr)
    : MeshFrame((byte)PushCode.TraceData);

/// <summary><c>RESP_CODE_CLI_REPLY</c>: the companion's answer to <c>CMD_RUN_CLI_COMMAND</c>
/// (often starts with <c>"&gt; "</c>, or says <c>"Unknown command"</c>).</summary>
public sealed record CliReply(string Text) : MeshFrame((byte)ResponseCode.CliReply);

/// <summary><c>PUSH_CODE_CONTACT_DELETED</c>: the radio overwrote its oldest contact to make room.</summary>
public sealed record ContactDeletedPush(PublicKey PublicKey) : MeshFrame((byte)PushCode.ContactDeleted);

/// <summary><c>PUSH_CODE_CONTACTS_FULL</c>: the radio has no room for new contacts.</summary>
public sealed record ContactsFullPush() : MeshFrame((byte)PushCode.ContactsFull);

/// <summary>
/// <c>RepeaterStats</c> from the repeater firmware (<c>examples/simple_repeater/MyMesh.h</c>), 56 bytes, little-endian.
/// </summary>
public sealed record RepeaterStats(
    int BatteryMillivolts,
    int TxQueueLength,
    int NoiseFloor,
    int LastRssi,
    uint PacketsReceived,
    uint PacketsSent,
    TimeSpan AirTime,
    TimeSpan Uptime,
    uint SentFlood,
    uint SentDirect,
    uint ReceivedFlood,
    uint ReceivedDirect,
    int ErrorEvents,
    double LastSnr,
    int DirectDuplicates,
    int FloodDuplicates,
    TimeSpan? RxAirTime,
    uint? ReceiveErrors)
{
    public const int MinimumSize = 48;

    public static RepeaterStats? TryParse(ReadOnlySpan<byte> data)
    {
        if (data.Length < MinimumSize)
        {
            return null;
        }
        static ushort U16(ReadOnlySpan<byte> d, int o) => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(d[o..]);
        static short I16(ReadOnlySpan<byte> d, int o) => System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(d[o..]);
        static uint U32(ReadOnlySpan<byte> d, int o) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(d[o..]);
        return new RepeaterStats(
            BatteryMillivolts: U16(data, 0),
            TxQueueLength: U16(data, 2),
            NoiseFloor: I16(data, 4),
            LastRssi: I16(data, 6),
            PacketsReceived: U32(data, 8),
            PacketsSent: U32(data, 12),
            AirTime: TimeSpan.FromSeconds(U32(data, 16)),
            Uptime: TimeSpan.FromSeconds(U32(data, 20)),
            SentFlood: U32(data, 24),
            SentDirect: U32(data, 28),
            ReceivedFlood: U32(data, 32),
            ReceivedDirect: U32(data, 36),
            ErrorEvents: U16(data, 40),
            LastSnr: I16(data, 42) / 4.0,
            DirectDuplicates: U16(data, 44),
            FloodDuplicates: U16(data, 46),
            RxAirTime: data.Length >= 52 ? TimeSpan.FromSeconds(U32(data, 48)) : null,
            ReceiveErrors: data.Length >= 56 ? U32(data, 52) : null);
    }
}
