using System.ComponentModel.DataAnnotations;
using MeshCore.Host.Radio;
using MeshCore.Protocol;
using MeshCore.Protocol.Frames;
using MeshCore.Protocol.Models;

namespace MeshCore.Host.Api;

// ---- Node settings ----------------------------------------------------------------

public sealed record SetNameRequest([Required, StringLength(31, MinimumLength = 1)] string Name);

public sealed record SetLocationRequest([Range(-90.0, 90.0)] double Latitude, [Range(-180.0, 180.0)] double Longitude);

/// <summary>LoRa settings. Every node in a mesh must use the same values, so changing them cuts this radio off from
/// nodes that don't change too.</summary>
public sealed record SetRadioRequest(
    [Range(150.0, 2500.0)] double FrequencyMhz,
    [Range(7.0, 500.0)] double BandwidthKhz,
    [Range(5, 12)] int SpreadingFactor,
    [Range(5, 8)] int CodingRate);

public sealed record SetTxPowerRequest([Range(-9, 30)] int Dbm);

public sealed record TuningResponse(double RxDelayBase, double AirtimeFactor);

public sealed record SetTuningRequest([Range(0.0, 20.0)] double RxDelayBase, [Range(0.0, 9.0)] double AirtimeFactor);

/// <param name="ManualAddContacts">Don't add new nodes automatically; list them under "heard nodes" instead.</param>
/// <param name="AdvertLocationPolicy">0 = keep location out of adverts, 1 = include it.</param>
/// <param name="TelemetryModes">Packed base/location/environment telemetry permissions: (env &lt;&lt; 4) | (loc &lt;&lt; 2) | base.</param>
public sealed record SetPreferencesRequest(
    bool ManualAddContacts,
    [Range(0, 1)] int AdvertLocationPolicy,
    [Range(0, 63)] int TelemetryModes,
    [Range(0, 3)] int MultiAcks);

public sealed record ContactCardResponse(string AdvertHex);

public sealed record PrivateKeyResponse(string PrivateKeyHex);

public sealed record ImportPrivateKeyRequest(
    [Required, RegularExpression("^[0-9a-fA-F]{128}$", ErrorMessage = "A private key is 128 hex characters.")] string PrivateKeyHex);

// ---- Contacts ------------------------------------------------------------------------

public sealed record SetFavouriteRequest(bool Favourite);

public sealed record RenameContactRequest([Required, StringLength(31, MinimumLength = 1)] string Name);

public sealed record ImportContactRequest([Required, RegularExpression("^[0-9a-fA-F]{200,400}$", ErrorMessage = "Paste the exported contact as hex.")] string AdvertHex);

public sealed record DiscoveredNodeResponse(string PublicKey, string Name, AdvertType Type, int? Hops, DateTimeOffset HeardAt)
{
    public static DiscoveredNodeResponse From(Contact c, DateTimeOffset heardAt) =>
        new(c.PublicKey.Hex, c.Name, c.Type, c.OutPathLength.IsNone ? null : c.OutPathLength.HopCount, heardAt);
}

// ---- Repeaters and room servers ---------------------------------------------------------

public sealed record LoginRequest([StringLength(15)] string? Password);

public sealed record LoginResponse(bool Success, bool IsAdmin, DateTimeOffset? ServerTime)
{
    public static LoginResponse From(LoginResultPush p) => new(p.Success, p.IsAdmin, p.ServerTime);
}

public sealed record RepeaterStatsResponse(
    double BatteryVolts, int TxQueueLength, int NoiseFloorDbm, int LastRssiDbm, double LastSnrDb,
    uint PacketsReceived, uint PacketsSent, uint SentFlood, uint SentDirect, uint ReceivedFlood, uint ReceivedDirect,
    int DirectDuplicates, int FloodDuplicates, int ErrorEvents, double UptimeHours, double AirTimeMinutes, double? RxAirTimeMinutes, uint? ReceiveErrors)
{
    public static RepeaterStatsResponse From(RepeaterStats s) => new(
        s.BatteryMillivolts / 1000.0, s.TxQueueLength, s.NoiseFloor, s.LastRssi, s.LastSnr,
        s.PacketsReceived, s.PacketsSent, s.SentFlood, s.SentDirect, s.ReceivedFlood, s.ReceivedDirect,
        s.DirectDuplicates, s.FloodDuplicates, s.ErrorEvents, Math.Round(s.Uptime.TotalHours, 2), Math.Round(s.AirTime.TotalMinutes, 1),
        s.RxAirTime is { } rx ? Math.Round(rx.TotalMinutes, 1) : null, s.ReceiveErrors);
}

/// <param name="Stats">Decoded when it's a repeater; null for other node types (see <paramref name="RawHex"/>).</param>
public sealed record StatusResponse(RepeaterStatsResponse? Stats, string RawHex);

public sealed record TelemetryReadingResponse(int Channel, string Name, IReadOnlyList<double> Values, string Unit, string Display)
{
    public static TelemetryReadingResponse From(LppReading r) => new(r.Channel, r.Name, r.Values, r.Unit, r.ToString());
}

public sealed record TelemetryResponse(IReadOnlyList<TelemetryReadingResponse> Readings, string RawHex);

/// <param name="Hops">Out-and-back route; SNR is how each hop heard the trace. The last entry is how we heard it come home.</param>
public sealed record TraceResponse(IReadOnlyList<TraceHopResponse> Hops, double FinalSnrDb, double RoundTripMs)
{
    public static TraceResponse From(TraceResult t) => new(
        [.. t.HopHashes.Select((h, i) => new TraceHopResponse(h, i < t.HopSnrs.Count ? t.HopSnrs[i] : null))],
        t.FinalSnr, Math.Round(t.RoundTrip.TotalMilliseconds));
}

public sealed record TraceHopResponse(string Hash, double? SnrDb);

public sealed record CliCommandRequest([Required, StringLength(ProtocolLimits.MaxTextBytes, MinimumLength = 1)] string Command);
