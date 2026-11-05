using MeshCore.Protocol.Binary;
using MeshCore.Protocol.Frames;
using MeshCore.Protocol.Models;

namespace MeshCore.Protocol;

/// <summary>Decodes radio-to-app frame payloads (without the serial header) into typed frames.
/// Layouts follow the firmware source; see <c>docs/PROTOCOL.md</c> for the references.</summary>
public static class FrameParser
{
    private const int DeviceInfoBuildDateSize = 12;
    private const int DeviceInfoModelSize = 40;
    private const int DeviceInfoVersionSize = 20;
    private const int SignerPrefixSize = 4;
    private const double CoordinateScale = 1_000_000.0;

    /// <summary>
    /// Decodes <paramref name="payload"/>. Never throws for bad input: unknown codes give
    /// <see cref="UnknownFrame"/> and truncated bodies give <see cref="MalformedFrame"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The payload is empty.</exception>
    public static MeshFrame Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            throw new ArgumentException("Frame payload is empty.", nameof(payload));
        }

        var code = payload[0];
        var reader = new FrameReader(payload[1..]);
        try
        {
            return Decode(code, ref reader) ?? new UnknownFrame(code, payload.ToArray());
        }
        catch (ProtocolException ex)
        {
            return new MalformedFrame(code, payload.ToArray(), ex.Message);
        }
    }

    private static MeshFrame? Decode(byte code, ref FrameReader r) => code switch
    {
        (byte)ResponseCode.Ok => new OkResponse(),
        (byte)ResponseCode.Error => new ErrorResponse(r.Remaining > 0 ? (ErrorCode)r.ReadByte() : ErrorCode.Unknown),
        (byte)ResponseCode.Disabled => new DisabledResponse(),
        (byte)ResponseCode.SelfInfo => ReadSelfInfo(ref r),
        (byte)ResponseCode.DeviceInfo => ReadDeviceInfo(ref r),
        (byte)ResponseCode.ContactsStart => new ContactsStart(r.ReadUInt32()),
        (byte)ResponseCode.Contact => ReadContact(ref r),
        (byte)ResponseCode.EndOfContacts => new EndOfContacts(r.ReadUnixTime()),
        (byte)ResponseCode.CurrentTime => new CurrentTime(r.ReadUnixTime()),
        (byte)ResponseCode.Sent => new MessageSent(r.ReadByte() == 1, r.ReadUInt32(), TimeSpan.FromMilliseconds(r.ReadUInt32())),
        (byte)ResponseCode.NoMoreMessages => new NoMoreMessages(),
        (byte)ResponseCode.ContactMessageReceived => ReadContactMessage(code, hasSnr: false, ref r),
        (byte)ResponseCode.ContactMessageReceivedV3 => ReadContactMessage(code, hasSnr: true, ref r),
        (byte)ResponseCode.ChannelMessageReceived => ReadChannelMessage(code, hasSnr: false, ref r),
        (byte)ResponseCode.ChannelMessageReceivedV3 => ReadChannelMessage(code, hasSnr: true, ref r),
        (byte)ResponseCode.ChannelInfo => new ChannelInfo(
            r.ReadByte(), r.ReadFixedString(ProtocolLimits.NameSize), r.ReadBytes(ProtocolLimits.ChannelSecretSize).ToArray()),
        (byte)ResponseCode.BatteryAndStorage => ReadBatteryAndStorage(ref r),

        (byte)PushCode.Advert => new AdvertPush(ReadKey(ref r)),
        (byte)PushCode.PathUpdated => new PathUpdatedPush(ReadKey(ref r)),
        (byte)PushCode.SendConfirmed => new SendConfirmedPush(r.ReadUInt32(), TimeSpan.FromMilliseconds(r.ReadUInt32())),
        (byte)PushCode.MessagesWaiting => new MessagesWaitingPush(),
        (byte)PushCode.NewAdvert => new NewAdvertPush(ReadContact(ref r)),
        (byte)PushCode.LoginSuccess or (byte)PushCode.LoginFail => ReadLoginResult(code, ref r),
        (byte)PushCode.StatusResponse => ReadStatusResponse(ref r),
        (byte)PushCode.TelemetryResponse => ReadTelemetryResponse(ref r),
        (byte)PushCode.TraceData => ReadTraceData(ref r),
        (byte)PushCode.ContactDeleted => new ContactDeletedPush(ReadKey(ref r)),
        (byte)PushCode.ContactsFull => new ContactsFullPush(),
        (byte)ResponseCode.TuningParams => new TuningParams(r.ReadUInt32() / 1000.0, r.ReadUInt32() / 1000.0),
        (byte)ResponseCode.ExportContact => new ExportedContact(r.ReadBytes(r.Remaining).ToArray()),
        (byte)ResponseCode.PrivateKey => new PrivateKey(r.ReadBytes(64).ToArray()),
        _ => null,
    };

    private static PublicKey ReadKey(ref FrameReader r) => new(r.ReadBytes(ProtocolLimits.PublicKeySize));

    private static double ReadCoordinate(ref FrameReader r) => r.ReadInt32() / CoordinateScale;

    private static double ReadSnr(ref FrameReader r) => r.ReadSByte() / 4.0;

    private static SelfInfo ReadSelfInfo(ref FrameReader r) => new(
        Type: (AdvertType)r.ReadByte(),
        TxPowerDbm: r.ReadSByte(),
        MaxTxPowerDbm: r.ReadSByte(),
        PublicKey: ReadKey(ref r),
        Latitude: ReadCoordinate(ref r),
        Longitude: ReadCoordinate(ref r),
        MultiAcks: r.ReadByte(),
        AdvertLocationPolicy: r.ReadByte(),
        TelemetryModes: r.ReadByte(),
        ManualAddContacts: r.ReadByte() != 0,
        FrequencyKhz: r.ReadUInt32(),
        BandwidthHz: r.ReadUInt32(),
        SpreadingFactor: r.ReadByte(),
        CodingRate: r.ReadByte(),
        Name: r.ReadRemainingString());

    private static DeviceInfo ReadDeviceInfo(ref FrameReader r)
    {
        var version = r.ReadByte();
        int? maxContacts = r.Remaining >= 1 ? r.ReadByte() * 2 : null;
        int? maxChannels = r.Remaining >= 1 ? r.ReadByte() : null;
        uint? blePin = r.Remaining >= 4 ? r.ReadUInt32() : null;
        var buildDate = r.Remaining >= DeviceInfoBuildDateSize ? r.ReadFixedString(DeviceInfoBuildDateSize) : null;
        var model = r.Remaining >= DeviceInfoModelSize ? r.ReadFixedString(DeviceInfoModelSize) : null;
        var firmware = r.Remaining >= DeviceInfoVersionSize ? r.ReadFixedString(DeviceInfoVersionSize) : null;
        bool? repeat = r.Remaining >= 1 ? r.ReadByte() != 0 : null;
        byte? pathHashMode = r.Remaining >= 1 ? r.ReadByte() : null;
        return new DeviceInfo(version, maxContacts, maxChannels, blePin, buildDate, model, firmware, repeat, pathHashMode);
    }

    private static LoginResultPush ReadLoginResult(byte code, ref FrameReader r)
    {
        var permissions = r.ReadByte();
        var sender = new PublicKeyPrefix(r.ReadBytes(ProtocolLimits.PublicKeyPrefixSize));
        // Newer servers add their clock, ACL permissions and firmware level; legacy "OK" logins don't.
        DateTimeOffset? serverTime = r.Remaining >= 4 ? r.ReadUnixTime() : null;
        byte? acl = r.Remaining >= 1 ? r.ReadByte() : null;
        byte? level = r.Remaining >= 1 ? r.ReadByte() : null;
        var success = code == (byte)PushCode.LoginSuccess;
        return new LoginResultPush(code, success, sender, success && (permissions & 0x01) != 0, permissions, serverTime, acl, level);
    }

    private static StatusResponsePush ReadStatusResponse(ref FrameReader r)
    {
        r.Skip(1); // reserved
        var sender = new PublicKeyPrefix(r.ReadBytes(ProtocolLimits.PublicKeyPrefixSize));
        var raw = r.ReadBytes(r.Remaining).ToArray();
        return new StatusResponsePush(sender, raw, RepeaterStats.TryParse(raw));
    }

    private static TelemetryResponsePush ReadTelemetryResponse(ref FrameReader r)
    {
        r.Skip(1); // reserved
        var sender = new PublicKeyPrefix(r.ReadBytes(ProtocolLimits.PublicKeyPrefixSize));
        var raw = r.ReadBytes(r.Remaining).ToArray();
        return new TelemetryResponsePush(sender, CayenneLpp.Decode(raw), raw);
    }

    private static TraceDataPush ReadTraceData(ref FrameReader r)
    {
        r.Skip(1); // reserved
        var pathLength = r.ReadByte();
        var flags = r.ReadByte();
        var tag = r.ReadUInt32();
        var auth = r.ReadUInt32();
        var hashSize = 1 << (flags & 0x03);
        var hashes = r.ReadBytes(pathLength);
        var hops = pathLength / hashSize;
        var hopHashes = new List<string>(hops);
        for (var i = 0; i < hops; i++)
        {
            hopHashes.Add(Convert.ToHexStringLower(hashes.Slice(i * hashSize, hashSize)));
        }
        var snrs = new List<double>(hops);
        for (var i = 0; i < hops; i++)
        {
            snrs.Add(ReadSnr(ref r));
        }
        return new TraceDataPush(tag, auth, hashSize, hopHashes, snrs, ReadSnr(ref r));
    }

    private static BatteryAndStorage ReadBatteryAndStorage(ref FrameReader r)
    {
        var millivolts = r.ReadUInt16();
        // Storage figures were added in a later firmware; older builds send the battery only.
        return r.Remaining >= 8
            ? new BatteryAndStorage(millivolts, r.ReadUInt32(), r.ReadUInt32())
            : new BatteryAndStorage(millivolts, null, null);
    }

    private static Contact ReadContact(ref FrameReader r)
    {
        var key = ReadKey(ref r);
        var type = (AdvertType)r.ReadByte();
        var flags = (ContactFlags)r.ReadByte();
        var pathLength = new PathLength(r.ReadByte());
        var pathField = r.ReadBytes(ProtocolLimits.MaxPathSize);
        var path = pathField[..Math.Min(pathLength.ByteLength, pathField.Length)].ToArray();
        return new Contact(
            PublicKey: key,
            Type: type,
            Flags: flags,
            OutPathLength: pathLength,
            OutPath: path,
            Name: r.ReadFixedString(ProtocolLimits.NameSize),
            LastAdvert: r.ReadUnixTime(),
            Latitude: ReadCoordinate(ref r),
            Longitude: ReadCoordinate(ref r),
            LastModified: r.ReadUnixTime());
    }

    private static ContactMessage ReadContactMessage(byte code, bool hasSnr, ref FrameReader r)
    {
        double? snr = null;
        if (hasSnr)
        {
            snr = ReadSnr(ref r);
            r.Skip(2);
        }

        var sender = new PublicKeyPrefix(r.ReadBytes(ProtocolLimits.PublicKeyPrefixSize));
        var path = new PathLength(r.ReadByte());
        var textType = (TextType)r.ReadByte();
        var timestamp = r.ReadUnixTime();
        PublicKeyPrefix? signer = textType == TextType.SignedPlain ? new PublicKeyPrefix(r.ReadBytes(SignerPrefixSize)) : null;
        return new ContactMessage(code, snr, sender, path, textType, timestamp, signer, r.ReadRemainingString());
    }

    private static ChannelMessage ReadChannelMessage(byte code, bool hasSnr, ref FrameReader r)
    {
        double? snr = null;
        if (hasSnr)
        {
            snr = ReadSnr(ref r);
            r.Skip(2);
        }

        return new ChannelMessage(
            code,
            snr,
            ChannelIndex: r.ReadByte(),
            PathLength: new PathLength(r.ReadByte()),
            TextType: (TextType)r.ReadByte(),
            SenderTimestamp: r.ReadUnixTime(),
            Text: r.ReadRemainingString());
    }
}
