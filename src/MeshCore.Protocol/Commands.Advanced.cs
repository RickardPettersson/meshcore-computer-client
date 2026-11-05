using MeshCore.Protocol.Binary;
using MeshCore.Protocol.Models;

namespace MeshCore.Protocol;

/// <summary>Contact fields as written by <c>CMD_ADD_UPDATE_CONTACT</c> (same order as a contact frame).</summary>
public sealed record ContactData(
    PublicKey PublicKey,
    AdvertType Type,
    ContactFlags Flags,
    PathLength OutPathLength,
    ReadOnlyMemory<byte> OutPath,
    string Name,
    DateTimeOffset LastAdvert,
    double Latitude,
    double Longitude)
{
    public static ContactData From(Frames.Contact c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return new(c.PublicKey, c.Type, c.Flags, c.OutPathLength, c.OutPath, c.Name, c.LastAdvert, c.Latitude, c.Longitude);
    }
}

public static partial class Commands
{
    private const double CoordinateScale = 1_000_000.0;
    private const int TuningScale = 1000;
    private const int TelemetryReservedSize = 3;

    // ---- Node settings ---------------------------------------------------------

    /// <summary>Sets the location included in adverts. Reply: OK, or <see cref="ErrorCode.IllegalArgument"/> if out of range.</summary>
    public static byte[] SetAdvertLatLon(double latitude, double longitude)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Math.Abs(latitude), 90);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Math.Abs(longitude), 180);
        return new FrameWriter().Code(CommandCode.SetAdvertLatLon)
            .Int32((int)Math.Round(latitude * CoordinateScale)).Int32((int)Math.Round(longitude * CoordinateScale)).ToArray();
    }

    /// <summary>
    /// Changes the LoRa settings. All nodes in a mesh must share them, so a wrong value cuts the radio off.
    /// The firmware accepts 150–2500 MHz, 7–500 kHz bandwidth, SF 5–12 and CR 5–8. Reply: OK.
    /// </summary>
    public static byte[] SetRadioParams(uint frequencyKhz, uint bandwidthHz, byte spreadingFactor, byte codingRate) =>
        new FrameWriter().Code(CommandCode.SetRadioParams)
            .UInt32(frequencyKhz).UInt32(bandwidthHz).Byte(spreadingFactor).Byte(codingRate).ToArray();

    /// <summary>Reply: OK, or <see cref="ErrorCode.IllegalArgument"/> outside −9…max dBm.</summary>
    public static byte[] SetTxPower(sbyte dbm) => [(byte)CommandCode.SetRadioTxPower, unchecked((byte)dbm)];

    /// <summary>Reply: <see cref="Frames.TuningParams"/>.</summary>
    public static byte[] GetTuningParams() => [(byte)CommandCode.GetTuningParams];

    /// <summary>Reply: OK.</summary>
    public static byte[] SetTuningParams(double rxDelayBase, double airtimeFactor) =>
        new FrameWriter().Code(CommandCode.SetTuningParams)
            .UInt32(checked((uint)Math.Round(rxDelayBase * TuningScale))).UInt32(checked((uint)Math.Round(airtimeFactor * TuningScale))).ToArray();

    /// <summary>
    /// Other preferences. <paramref name="telemetryModes"/> packs base/location/environment modes as
    /// <c>(env &lt;&lt; 4) | (loc &lt;&lt; 2) | base</c>, the same byte as in <see cref="Frames.SelfInfo.TelemetryModes"/>. Reply: OK.
    /// </summary>
    public static byte[] SetOtherParams(bool manualAddContacts, byte telemetryModes, byte advertLocationPolicy, byte multiAcks) =>
        [(byte)CommandCode.SetOtherParams, manualAddContacts ? (byte)1 : (byte)0, telemetryModes, advertLocationPolicy, multiAcks];

    // ---- Contacts ----------------------------------------------------------------

    /// <summary>Adds a contact or updates one (flags, name, path…). Reply: OK, or <see cref="ErrorCode.TableFull"/>.</summary>
    public static byte[] AddUpdateContact(ContactData contact)
    {
        ArgumentNullException.ThrowIfNull(contact);
        var path = new byte[ProtocolLimits.MaxPathSize];
        contact.OutPath.Span[..Math.Min(contact.OutPath.Length, path.Length)].CopyTo(path);
        return new FrameWriter().Code(CommandCode.AddUpdateContact)
            .Bytes(contact.PublicKey.ToBytes())
            .Byte((byte)contact.Type).Byte((byte)contact.Flags).Byte(contact.OutPathLength.Raw)
            .Bytes(path)
            .FixedString(contact.Name, ProtocolLimits.NameSize)
            .UnixTime(contact.LastAdvert)
            .Int32((int)Math.Round(contact.Latitude * CoordinateScale)).Int32((int)Math.Round(contact.Longitude * CoordinateScale))
            .ToArray();
    }

    /// <summary>Reply: <see cref="Frames.Contact"/>, or <see cref="ErrorCode.NotFound"/>.</summary>
    public static byte[] GetContactByKey(PublicKey key) => new FrameWriter().Code(CommandCode.GetContactByKey).Bytes(key.ToBytes()).ToArray();

    /// <summary>Re-broadcasts a contact's advert to nearby nodes (zero hop). Reply: OK.</summary>
    public static byte[] ShareContact(PublicKey key) => new FrameWriter().Code(CommandCode.ShareContact).Bytes(key.ToBytes()).ToArray();

    /// <summary>A contact's signed advert, or our own when <paramref name="key"/> is null. Reply: <see cref="Frames.ExportedContact"/>.</summary>
    public static byte[] ExportContact(PublicKey? key = null) =>
        key is { } k ? new FrameWriter().Code(CommandCode.ExportContact).Bytes(k.ToBytes()).ToArray() : [(byte)CommandCode.ExportContact];

    /// <summary>Adds a contact from an exported advert. Reply: OK, or <see cref="ErrorCode.IllegalArgument"/> if it doesn't verify.</summary>
    public static byte[] ImportContact(ReadOnlySpan<byte> advert)
    {
        if (advert.Length <= 2 + ProtocolLimits.PublicKeySize + 64)
        {
            throw new ArgumentException("That isn't an exported MeshCore contact (too short).", nameof(advert));
        }
        return new FrameWriter().Code(CommandCode.ImportContact).Bytes(advert).ToArray();
    }

    // ---- Repeaters and room servers ------------------------------------------------

    /// <summary>Logs in to a repeater or room server. Reply: <see cref="Frames.MessageSent"/>, then <see cref="Frames.LoginResultPush"/>.</summary>
    public static byte[] SendLogin(PublicKey key, string password) =>
        new FrameWriter().Code(CommandCode.SendLogin).Bytes(key.ToBytes()).Bytes(Utf8.Truncate(password ?? "", 15)).ToArray();

    /// <summary>Reply: OK.</summary>
    public static byte[] Logout(PublicKey key) => new FrameWriter().Code(CommandCode.Logout).Bytes(key.ToBytes()).ToArray();

    /// <summary>Reply: <see cref="Frames.MessageSent"/>, then <see cref="Frames.StatusResponsePush"/>.</summary>
    public static byte[] SendStatusRequest(PublicKey key) => new FrameWriter().Code(CommandCode.SendStatusRequest).Bytes(key.ToBytes()).ToArray();

    /// <summary>Reply: <see cref="Frames.MessageSent"/>, then <see cref="Frames.TelemetryResponsePush"/>.</summary>
    public static byte[] SendTelemetryRequest(PublicKey key) =>
        new FrameWriter().Code(CommandCode.SendTelemetryRequest).Zeros(TelemetryReservedSize).Bytes(key.ToBytes()).ToArray();

    /// <summary>
    /// Sends a trace along <paramref name="path"/> (hop hashes, out and back). Each hop records the SNR it heard.
    /// Reply: <see cref="Frames.MessageSent"/>, then <see cref="Frames.TraceDataPush"/> with the same tag.
    /// </summary>
    /// <param name="hashSize">Bytes per hop hash (1–4).</param>
    public static byte[] SendTracePath(uint tag, uint authCode, int hashSize, ReadOnlySpan<byte> path)
    {
        // The firmware reads hash size as 1 << (flags & 3), so only 1, 2 and 4 bytes can be traced.
        var flags = hashSize switch
        {
            1 => (byte)0,
            2 => (byte)1,
            4 => (byte)2,
            _ => throw new ArgumentOutOfRangeException(nameof(hashSize), hashSize, "Trace paths support 1, 2 or 4-byte hop hashes."),
        };
        if (path.IsEmpty || path.Length % hashSize != 0)
        {
            throw new ArgumentException("A trace path needs at least one hop, as whole hashes.", nameof(path));
        }
        return new FrameWriter().Code(CommandCode.SendTracePath).UInt32(tag).UInt32(authCode).Byte(flags).Bytes(path).ToArray();
    }

    // ---- Identity -------------------------------------------------------------------

    /// <summary>Reply: <see cref="Frames.PrivateKey"/>, or <see cref="Frames.DisabledResponse"/> on builds without key export.</summary>
    public static byte[] ExportPrivateKey() => [(byte)CommandCode.ExportPrivateKey];

    /// <summary>Replaces the node's identity. Reply: OK, <see cref="ErrorCode.IllegalArgument"/> for an invalid key, or disabled.</summary>
    public static byte[] ImportPrivateKey(ReadOnlySpan<byte> privateKey)
    {
        if (privateKey.Length != 64)
        {
            throw new ArgumentException("A MeshCore private key is 64 bytes.", nameof(privateKey));
        }
        return new FrameWriter().Code(CommandCode.ImportPrivateKey).Bytes(privateKey).ToArray();
    }
}
