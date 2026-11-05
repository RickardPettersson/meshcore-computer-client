using MeshCore.Protocol.Frames;
using MeshCore.Protocol.Models;
using static MeshCore.Protocol.Tests.Wire;

namespace MeshCore.Protocol.Tests;

public class AdvancedCommandsTests
{
    private static readonly PublicKey K = PublicKey.Parse(KeyHex);

    [Fact]
    public void Location_is_micro_degrees()
    {
        Assert.Equal(Concat(B(0x0E), I32(59_329_323), I32(-18_068_581)), Commands.SetAdvertLatLon(59.329323, -18.068581));
        Assert.Throws<ArgumentOutOfRangeException>(() => Commands.SetAdvertLatLon(91, 0));
    }

    [Fact]
    public void Radio_params_are_khz_and_hz()
    {
        // Firmware: freq / 1000 → MHz, bw / 1000 → kHz.
        Assert.Equal(Concat(B(0x0B), U32(869_618), U32(62_500), B(8, 5)), Commands.SetRadioParams(869_618, 62_500, 8, 5));
    }

    [Fact]
    public void Tx_power_is_a_signed_byte()
    {
        Assert.Equal(Hex("0C F7"), Commands.SetTxPower(-9));
        Assert.Equal(Hex("0C 16"), Commands.SetTxPower(22));
    }

    [Fact]
    public void Tuning_and_other_params()
    {
        Assert.Equal(Concat(B(0x15), U32(500), U32(1000)), Commands.SetTuningParams(0.5, 1.0));
        Assert.Equal(Hex("2B"), Commands.GetTuningParams());
        Assert.Equal(Hex("26 01 15 01 02"), Commands.SetOtherParams(true, 0x15, 1, 2));
    }

    [Fact]
    public void Add_update_contact_mirrors_the_contact_frame()
    {
        var data = new ContactData(K, AdvertType.Repeater, ContactFlags.Favourite, new PathLength(2), Hex("AABB"), "Hilltop",
            DateTimeOffset.FromUnixTimeSeconds(Time), 59.5, 18.25);

        var cmd = Commands.AddUpdateContact(data);

        Assert.Equal(Concat(B(0x09), Key, B(0x02, 0x01, 0x02), Concat(Hex("AABB"), Repeat(0, 62)), Fixed("Hilltop", 32), U32(Time), I32(59_500_000), I32(18_250_000)), cmd);
        Assert.Equal(1 + 32 + 3 + 64 + 32 + 4 + 8, cmd.Length);
    }

    [Fact]
    public void Contact_key_commands()
    {
        Assert.Equal(Concat(B(0x1E), Key), Commands.GetContactByKey(K));
        Assert.Equal(Concat(B(0x10), Key), Commands.ShareContact(K));
        Assert.Equal(Concat(B(0x11), Key), Commands.ExportContact(K));
        Assert.Equal(Hex("11"), Commands.ExportContact());
        Assert.Equal(Concat(B(0x1D), Key), Commands.Logout(K));
        Assert.Equal(Concat(B(0x1B), Key), Commands.SendStatusRequest(K));
    }

    [Fact]
    public void Import_contact_needs_a_full_advert()
    {
        Assert.Throws<ArgumentException>(() => Commands.ImportContact(new byte[98]));
        Assert.Equal(100, Commands.ImportContact(new byte[99]).Length);
    }

    [Fact]
    public void Login_appends_the_password_capped_at_15_bytes()
    {
        Assert.Equal(Concat(B(0x1A), Key, Text("secret")), Commands.SendLogin(K, "secret"));
        Assert.Equal(1 + 32 + 15, Commands.SendLogin(K, new string('p', 30)).Length);
        Assert.Equal(33, Commands.SendLogin(K, "").Length); // guest
    }

    [Fact]
    public void Telemetry_request_has_three_reserved_bytes_before_the_key()
    {
        // Firmware: len >= 4 + 32 and the key at [4]; a 4-byte frame would mean "our own telemetry".
        Assert.Equal(Concat(B(0x27, 0, 0, 0), Key), Commands.SendTelemetryRequest(K));
    }

    [Fact]
    public void Trace_path_layout_and_hash_size_flags()
    {
        Assert.Equal(Concat(B(0x24), U32(7), U32(0), B(0x00), Hex("AA01AA")), Commands.SendTracePath(7, 0, 1, Hex("AA01AA")));
        Assert.Equal(0x01, Commands.SendTracePath(7, 0, 2, Hex("AAAA0101"))[9]);
        Assert.Equal(0x02, Commands.SendTracePath(7, 0, 4, Hex("AAAAAAAA"))[9]);
        Assert.Throws<ArgumentOutOfRangeException>(() => Commands.SendTracePath(7, 0, 3, Hex("AAAAAA")));
        Assert.Throws<ArgumentException>(() => Commands.SendTracePath(7, 0, 2, Hex("AAAAAA")));
    }

    [Fact]
    public void Private_key_commands()
    {
        Assert.Equal(Hex("17"), Commands.ExportPrivateKey());
        Assert.Equal(65, Commands.ImportPrivateKey(new byte[64]).Length);
        Assert.Throws<ArgumentException>(() => Commands.ImportPrivateKey(new byte[32]));
    }

    [Fact]
    public void Cli_commands_use_text_type_1()
    {
        Assert.Equal(0x01, Commands.SendTextMessage(K.Prefix, "ver", DateTimeOffset.FromUnixTimeSeconds(Time), textType: TextType.CliData)[1]);
    }
}

public class AdvancedParserTests
{
    private static T Parse<T>(byte[] payload) where T : MeshFrame => Assert.IsType<T>(FrameParser.Parse(payload));

    [Fact]
    public void Login_success_new_format_with_admin()
    {
        var p = Parse<LoginResultPush>(Concat(B(0x85, 0x01), Hex("010203040506"), U32(Time), B(0x03, 0x01)));

        Assert.True(p.Success);
        Assert.True(p.IsAdmin);
        Assert.Equal("010203040506", p.Sender.Hex);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(Time), p.ServerTime);
        Assert.Equal((byte)3, p.AclPermissions);
    }

    [Fact]
    public void Login_success_legacy_and_failure()
    {
        var legacy = Parse<LoginResultPush>(Concat(B(0x85, 0x00), Hex("010203040506")));
        var fail = Parse<LoginResultPush>(Concat(B(0x86, 0x00), Hex("010203040506")));

        Assert.True(legacy.Success);
        Assert.False(legacy.IsAdmin);
        Assert.Null(legacy.ServerTime);
        Assert.False(fail.Success);
    }

    [Fact]
    public void Status_response_decodes_repeater_stats()
    {
        var stats = Concat(U16(4120), U16(1), I16(-118), I16(-92), U32(1500), U32(800), U32(120), U32(7200),
            U32(10), U32(20), U32(30), U32(40), U16(2), I16(26), U16(3), U16(4), U32(60), U32(5));

        var p = Parse<StatusResponsePush>(Concat(B(0x87, 0), Hex("010203040506"), stats));

        Assert.Equal(56, stats.Length);
        var s = Assert.IsType<RepeaterStats>(p.Stats);
        Assert.Equal(4120, s.BatteryMillivolts);
        Assert.Equal(-118, s.NoiseFloor);
        Assert.Equal(-92, s.LastRssi);
        Assert.Equal(TimeSpan.FromHours(2), s.Uptime);
        Assert.Equal(6.5, s.LastSnr);
        Assert.Equal(5u, s.ReceiveErrors);
    }

    [Fact]
    public void Status_response_that_isnt_a_repeater_keeps_raw_bytes()
    {
        var p = Parse<StatusResponsePush>(Concat(B(0x87, 0), Hex("010203040506"), Hex("DEADBEEF")));

        Assert.Null(p.Stats);
        Assert.Equal(Hex("DEADBEEF"), p.Raw.ToArray());
    }

    [Fact]
    public void Telemetry_response_decodes_lpp()
    {
        var p = Parse<TelemetryResponsePush>(Concat(B(0x8B, 0), Hex("010203040506"), B(1, 116, 0x01, 0x9A, 1, 103, 0xFF, 0x9C)));

        Assert.Equal("Voltage: 4.1 V", p.Readings[0].ToString());
        Assert.Equal(-10.0, p.Readings[1].Values[0]);
    }

    [Fact]
    public void Trace_data_has_hashes_then_snrs_then_final_snr()
    {
        // path_len 3, flags 0 (1-byte hashes): AA 01 AA; SNRs ×4: 20, -8, 12; final 24.
        var p = Parse<TraceDataPush>(Concat(B(0x89, 0, 3, 0), U32(7), U32(0), Hex("AA01AA"), B(20, unchecked((byte)-8), 12, 24)));

        Assert.Equal(7u, p.Tag);
        Assert.Equal(["aa", "01", "aa"], p.HopHashes);
        Assert.Equal([5.0, -2.0, 3.0], p.HopSnrs);
        Assert.Equal(6.0, p.FinalSnr);
    }

    [Fact]
    public void Two_byte_trace_hashes()
    {
        var p = Parse<TraceDataPush>(Concat(B(0x89, 0, 4, 1), U32(1), U32(0), Hex("AAAA0101"), B(4, 8, 12)));

        Assert.Equal(2, p.HashSize);
        Assert.Equal(["aaaa", "0101"], p.HopHashes);
        Assert.Equal(3.0, p.FinalSnr);
    }

    [Fact]
    public void Tuning_export_key_and_contact_pushes()
    {
        Assert.Equal(new TuningParams(0.5, 1.0), Parse<TuningParams>(Concat(B(0x17), U32(500), U32(1000))));
        Assert.Equal(Hex("1122"), Parse<ExportedContact>(Hex("0B 1122")).Advert.ToArray());
        Assert.Equal(64, Parse<PrivateKey>(Concat(B(0x0E), new byte[64])).Key.Length);
        Assert.Equal(KeyHex, Parse<ContactDeletedPush>(Concat(B(0x8F), Key)).PublicKey.Hex);
        Assert.True(Parse<ContactsFullPush>(Hex("90")).IsPush);
    }
}

public class CayenneLppTests
{
    [Fact]
    public void Decodes_common_sensor_types_big_endian()
    {
        var r = CayenneLpp.Decode(Concat(
            B(1, 103, 0x01, 0x10),   // temperature 27.2 °C
            B(2, 104, 0x61),         // humidity 48.5 %
            B(3, 115, 0x27, 0x87),   // pressure 1011.9 hPa
            B(4, 120, 87)));         // percentage 87 %

        Assert.Equal(["Temperature: 27.2 °C", "Humidity: 48.5 %", "Pressure: 1011.9 hPa", "Percentage: 87 %"], r.Select(x => x.ToString()));
        Assert.Equal([1, 2, 3, 4], r.Select(x => (int)x.Channel));
    }

    [Fact]
    public void Decodes_gps_and_multi_axis_values()
    {
        // GPS: lat 59.3293, lon 18.0686, alt 25.50 m (3 bytes each, signed)
        var gps = CayenneLpp.Decode(Concat(B(1, 136), Hex("090D8D"), Hex("02C1CE"), Hex("0009F6")));
        var accel = CayenneLpp.Decode(Concat(B(1, 113), I16Be(1000), I16Be(-500), I16Be(0)));

        Assert.Equal([59.3293, 18.0686, 25.5], gps[0].Values);
        Assert.Equal([1.0, -0.5, 0.0], accel[0].Values);
    }

    [Fact]
    public void Stops_at_unknown_types_and_zero_padding()
    {
        Assert.Single(CayenneLpp.Decode(Concat(B(1, 116, 0x01, 0x9A), B(1, 250, 1, 2, 3))));
        Assert.Single(CayenneLpp.Decode(Concat(B(1, 116, 0x01, 0x9A), new byte[6])));
        Assert.Empty(CayenneLpp.Decode([]));
    }

    private static byte[] I16Be(short v) => [(byte)(v >> 8), (byte)v];
}
