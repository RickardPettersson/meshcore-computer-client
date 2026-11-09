using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MeshCore.Client.Tests;
using MeshCore.Data;
using MeshCore.Host.Api;
using MeshCore.Host.Radio;
using MeshCore.Host.Web;
using MeshCore.Protocol;
using MeshCore.Protocol.Frames;
using MeshCore.Protocol.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static MeshCore.Client.Tests.FakeRadio;

namespace MeshCore.Host.Tests;

public class BatteryStoreTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_762_000_000);

    private static Contact ContactFrame(byte seed, string name) => Assert.IsType<Contact>(FrameParser.Parse(Contact(seed, name)));

    [Fact]
    public async Task My_device_survives_contact_syncs_from_the_radio()
    {
        await using var db = await TestDb.CreateAsync();
        var store = new MeshStore(db, new FakeTimeProvider(T0));
        await store.SaveContactsAsync([ContactFrame(10, "Alice")], fullList: true);
        var key = Convert.ToHexStringLower(Key(10));

        Assert.True(await store.SetMineAsync(key.ToUpperInvariant(), true));
        await store.SaveContactsAsync([ContactFrame(10, "Alice renamed")], fullList: true); // the radio knows nothing about it

        Assert.True(Assert.Single(await store.GetContactsAsync()).IsMine);
        Assert.False(await store.SetMineAsync(new string('f', 64), true));
    }

    [Fact]
    public async Task Readings_keep_answers_and_silences_and_old_ones_are_pruned()
    {
        await using var db = await TestDb.CreateAsync();
        var time = new FakeTimeProvider(T0);
        var store = new MeshStore(db, time);

        await store.AddBatteryReadingAsync("self", 4100, "radio");
        time.Advance(TimeSpan.FromHours(1));
        await store.AddBatteryReadingAsync("self", 4050, "radio");
        time.Advance(TimeSpan.FromHours(1));
        await store.AddBatteryReadingAsync("self", null, "not connected");
        await store.AddBatteryReadingAsync("ABCD", 3900, "telemetry");

        var latest = await store.GetLatestBatteryReadingsAsync();
        var history = await store.GetBatteryReadingsAsync("self", T0.AddMinutes(30));

        Assert.Null(latest["self"].Latest.Millivolts);                   // the last check got no answer…
        Assert.Equal(4050, latest["self"].LastAnswer!.Millivolts);       // …the last answer is kept apart
        Assert.Equal(3900, latest["abcd"].Latest.Millivolts);            // keys are stored lowercase
        Assert.Equal([4050, null], history.Select(r => r.Millivolts));   // oldest first, since the given time
        Assert.Equal(1, await store.PruneBatteryReadingsAsync(T0.AddMinutes(30)));
        Assert.Equal(3, (await store.GetBatteryReadingsAsync("self", T0.AddDays(-1))).Count + (await store.GetBatteryReadingsAsync("abcd", T0.AddDays(-1))).Count);
    }
}

public class BatteryLogTests
{
    private static readonly string Alice = Convert.ToHexStringLower(Key(10));

    private static byte[] Sent() => Concat([0x06, 0x00], U32(1), U32(0));

    /// <summary>Alice answers telemetry with 4.15 V (LPP channel 1, type 116, 0.01 V units).</summary>
    private static FakeRadio AliceAnswers() => ApiFactory.DefaultRadio()
        .On(0x27, _ => [Sent(), Concat([0x8B, 0], Key(10)[..6], [1, 116, 0x01, 0x9F, 1, 103, 0x00, 0xD7])]);

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return await r.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);
    }

    [Fact]
    public async Task Hourly_check_logs_this_radio_and_my_devices()
    {
        using var app = new ApiFactory { Radio = AliceAnswers() };
        var admin = await app.AdminClientAsync();
        await app.WaitForRadioAsync();

        var marked = await Json(await admin.PutAsJsonAsync($"/api/v1/contacts/{Alice}/mine", new { mine = true }));
        await app.Services.GetRequiredService<BatteryLogService>().RunOnceAsync();
        var devices = (await Json(await admin.GetAsync("/api/v1/battery"))).EnumerateArray().ToList();
        var history = await Json(await admin.GetAsync($"/api/v1/battery/{Alice}?days=1"));

        Assert.True(marked.GetProperty("isMine").GetBoolean());
        Assert.Equal(["self", Alice], devices.Select(d => d.GetProperty("node").GetString()));
        Assert.Equal(4012, devices[0].GetProperty("latest").GetProperty("millivolts").GetInt32());
        Assert.Equal("radio", devices[0].GetProperty("latest").GetProperty("note").GetString());
        Assert.Equal(4150, devices[1].GetProperty("latest").GetProperty("millivolts").GetInt32());
        Assert.Equal(95, devices[1].GetProperty("latest").GetProperty("percent").GetInt32()); // 4.15 V
        var reading = Assert.Single(history.GetProperty("readings").EnumerateArray());
        Assert.Equal("telemetry", reading.GetProperty("note").GetString());
        Assert.Equal(21.5, reading.GetProperty("temperatureC").GetDouble());                  // from the same telemetry answer
        Assert.Equal(JsonValueKind.Null, devices[0].GetProperty("latest").GetProperty("temperatureC").ValueKind); // this radio: none
        Assert.Equal("Alice", history.GetProperty("device").GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_device_that_refuses_is_logged_without_voltage()
    {
        using var app = new ApiFactory { Radio = ApiFactory.DefaultRadio().On(0x27, [0x01, 0x02]) }; // ERR: not found
        var admin = await app.AdminClientAsync();
        await app.WaitForRadioAsync();
        await admin.PutAsJsonAsync($"/api/v1/contacts/{Alice}/mine", new { mine = true });

        await app.Services.GetRequiredService<BatteryLogService>().RunOnceAsync();
        var alice = (await Json(await admin.GetAsync("/api/v1/battery"))).EnumerateArray().Last();

        Assert.Equal(JsonValueKind.Null, alice.GetProperty("latest").GetProperty("millivolts").ValueKind);
        Assert.StartsWith("refused", alice.GetProperty("latest").GetProperty("note").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, alice.GetProperty("lastAnswer").ValueKind);
    }

    [Fact]
    public async Task Only_admins_mark_devices_and_unknown_nodes_are_404()
    {
        using var app = new ApiFactory();
        var admin = await app.AdminClientAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync($"/api/v1/contacts/{new string('f', 64)}/mine", new { mine = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/battery/{new string('f', 64)}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().GetAsync("/api/v1/battery")).StatusCode);
    }

    [Theory]
    [InlineData(new byte[] { 1, 116, 0x01, 0x9F }, 4150)]
    [InlineData(new byte[] { 2, 116, 0x01, 0x40, 1, 116, 0x01, 0x9F }, 4150)] // channel 1 (the node itself) wins
    [InlineData(new byte[] { 2, 116, 0x01, 0x40 }, 3200)]                     // else the first voltage
    [InlineData(new byte[] { 1, 103, 0x00, 0xD7 }, null)]                     // temperature only
    public void Voltage_is_taken_from_the_telemetry(byte[] lpp, int? millivolts) =>
        Assert.Equal(millivolts, BatteryLogService.VoltageOf(CayenneLpp.Decode(lpp)));

    [Theory]
    [InlineData(new byte[] { 1, 103, 0x00, 0xD7 }, 21.5)]
    [InlineData(new byte[] { 2, 103, 0x00, 0x64, 1, 103, 0xFF, 0x9C }, -10.0)] // channel 1 (the node itself) wins; below zero works
    [InlineData(new byte[] { 2, 103, 0x00, 0x64 }, 10.0)]                      // else the first temperature
    [InlineData(new byte[] { 1, 116, 0x01, 0x9F }, null)]                      // voltage only
    public void Temperature_is_taken_from_the_telemetry(byte[] lpp, double? celsius) =>
        Assert.Equal(celsius, BatteryLogService.TemperatureOf(CayenneLpp.Decode(lpp)));

    [Fact]
    public void Telemetry_shows_battery_percent_for_the_nodes_own_voltage_only()
    {
        // ch 1 voltage 4.10 V (the node's battery), ch 2 voltage 3.20 V (another sensor), ch 1 temperature 21.5 °C.
        var readings = CayenneLpp.Decode([1, 116, 0x01, 0x9A, 2, 116, 0x01, 0x40, 1, 103, 0x00, 0xD7]).Select(TelemetryReadingResponse.From).ToList();
        Assert.Equal([BatteryLevel.Percent(4100), null, null], readings.Select(r => r.BatteryPercent));
    }

    [Fact]
    public void Repeater_status_shows_battery_percent()
    {
        var stats = Assert.IsType<StatusResponsePush>(FrameParser.Parse(Concat([0x87, 0], Key(10)[..6], [0xD4, 0x0D], new byte[54]))).Stats;
        Assert.Equal(BatteryLevel.Percent(3540), RepeaterStatsResponse.From(stats!).BatteryPercent);
    }
}

public class BatteryWebTests
{
    private static readonly string Alice = Convert.ToHexStringLower(Key(10));

    [Fact]
    public async Task Mark_a_contact_and_see_its_history_graph()
    {
        using var app = new ApiFactory
        {
            Radio = ApiFactory.DefaultRadio().On(0x27, _ => [Concat([0x06, 0x00], U32(1), U32(0)), Concat([0x8B, 0], Key(10)[..6], [1, 116, 0x01, 0x9F])]),
        };
        var browser = new Browser(app);
        await browser.SetupAsync();
        await app.WaitForRadioAsync();

        var before = await browser.PageAsync($"/Contacts/Details/{Alice}");
        var mark = await browser.SubmitAsync($"/Contacts/Details/{Alice}", "/Contacts/Mine", ("key", Alice), ("mine", "true"));
        var store = app.Services.GetRequiredService<MeshStore>();
        await store.AddBatteryReadingAsync(Alice, 4100, 19.04, "telemetry", default);
        await store.AddBatteryReadingAsync(Alice, null, "no answer");
        await store.AddBatteryReadingAsync(Alice, 4150, 20.5, "telemetry", default);
        await store.AddBatteryReadingAsync(Alice, 4140, 21.25, "telemetry", default);
        var after = await browser.PageAsync($"/Contacts/Details/{Alice}");
        var list = await browser.PageAsync("/Battery");
        var graph = await browser.PageAsync($"/Battery/History?node={Alice}&days=1");

        Assert.Contains("Mark as my device", before, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Redirect, mark.StatusCode);
        Assert.Contains("btn btn-success", after, StringComparison.Ordinal);                 // the "My device ✓" button
        Assert.Contains($"href=\"/Battery/History?node={Alice}\"", list, StringComparison.Ordinal);
        Assert.Contains("Alice", list, StringComparison.Ordinal);
        Assert.Contains("<svg class=\"app-bchart\"", graph, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(graph, "<polyline class=\"line\""));  // 4.15 → 4.14 (the first point stands alone)
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(graph, "<rect class=\"miss\""));
        Assert.Contains("1 without an answer", graph, StringComparison.Ordinal);

        // Temperature: rounded to 0.1 °C, newest shown in the list, its own graph and a table column on the history page.
        Assert.Contains($"href=\"/Contacts/Details/{Alice}\"", list, StringComparison.Ordinal); // name opens the contact
        Assert.Contains("21.3 °C", list, StringComparison.Ordinal);
        Assert.Contains("<svg class=\"app-bchart app-tchart\"", graph, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(graph, "<polyline class=\"tline\""));
        Assert.Contains("19.0 °C", graph, StringComparison.Ordinal);
        Assert.Contains("<th class=\"text-end\">Temp</th>", graph, StringComparison.Ordinal);
    }

    [Fact]
    public async Task My_devices_is_in_the_menu_and_hides_temperature_when_there_is_none()
    {
        using var app = new ApiFactory();
        var browser = new Browser(app);
        await browser.SetupAsync();
        await app.WaitForRadioAsync();
        await app.Services.GetRequiredService<MeshStore>().AddBatteryReadingAsync(BatteryReadingEntity.Self, 4000, "radio");

        var list = await browser.PageAsync("/Battery");
        var graph = await browser.PageAsync("/Battery/History?node=self&days=1");

        Assert.Contains("class=\"nav-link active\" aria-current=\"page\" href=\"/Battery\"", list, StringComparison.Ordinal);
        Assert.Contains("href=\"/Device\"", list, StringComparison.Ordinal); // this radio's name opens the Device page
        Assert.DoesNotContain("°C", list, StringComparison.Ordinal);
        Assert.DoesNotContain("app-tchart", graph, StringComparison.Ordinal);
        Assert.DoesNotContain(">Temp</th>", graph, StringComparison.Ordinal);
    }

    [Fact]
    public async Task This_radio_has_a_history_page_linked_from_the_device_page()
    {
        using var app = new ApiFactory();
        var browser = new Browser(app);
        await browser.SetupAsync();
        await app.WaitForRadioAsync();

        var device = await browser.PageAsync("/Device");
        var empty = await browser.PageAsync("/Battery/History?node=self");
        await app.Services.GetRequiredService<BatteryLogService>().RunOnceAsync();
        var one = await browser.PageAsync("/Battery/History?node=self&days=30");

        Assert.Contains("href=\"/Battery/History?node=self\"", device, StringComparison.Ordinal);
        Assert.Contains("No checks in this period yet", empty, StringComparison.Ordinal);
        Assert.Contains("79%", one, StringComparison.Ordinal);
        Assert.Matches("<a class=\"btn btn-primary\"[^>]*aria-current=\"page\"[^>]*>30 days</a>", one);
    }
}

public class BatteryChartTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_762_000_000);

    private static BatteryReadingResponse R(int hour, int? mv) =>
        new(T0.AddHours(hour), mv, mv / 1000.0, mv is { } m ? BatteryLevel.Percent(m) : null, mv is null ? "no answer" : "telemetry");

    [Fact]
    public void Line_breaks_at_silences_and_long_gaps()
    {
        var svg = BatteryChart.Render([R(0, 4100), R(1, 4080), R(2, null), R(3, 4000), R(4, 3990), R(10, 3950), R(11, 3940)],
            T0, T0.AddHours(12), TimeSpan.FromHours(1)).ToString()!;

        Assert.Equal(3, System.Text.RegularExpressions.Regex.Count(svg, "<polyline")); // 0–1, 3–4, 10–11
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(svg, "<rect class=\"miss\""));
        Assert.Contains("4.0 V", svg, StringComparison.Ordinal);
        Assert.Contains(">40%<", svg, StringComparison.Ordinal);    // right axis: 3.8 V ≈ 40%
    }
}
