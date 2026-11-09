using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MeshCore.Host.Tests;

/// <summary>Phase 7 features end to end: API → MeshRadio → client → protocol → simulated radio.</summary>
public class RadioFeatureTests
{
    private const string Hilltop = "505152535455"; // simulator repeater key prefix
    private const string Alice = "101112131415";

    private static async Task<JsonElement> Body(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);

    private static async Task<(ApiFactory App, HttpClient Admin)> StartAsync()
    {
        var app = new ApiFactory { UseSimulator = true };
        var admin = await app.AdminClientAsync();
        await app.WaitForRadioAsync();
        return (app, admin);
    }

    [Fact]
    public async Task Admin_changes_node_settings()
    {
        var (app, admin) = await StartAsync();
        using var _ = app;

        var named = await Body(await admin.PutAsJsonAsync("/api/v1/device/name", new { name = "Base Camp" }));
        var located = await Body(await admin.PutAsJsonAsync("/api/v1/device/location", new { latitude = 61.5, longitude = 16.25 }));
        var power = await Body(await admin.PutAsJsonAsync("/api/v1/device/tx-power", new { dbm = 17 }));
        var radio = await Body(await admin.PutAsJsonAsync("/api/v1/device/radio", new { frequencyMhz = 869.525, bandwidthKhz = 250.0, spreadingFactor = 11, codingRate = 5 }));
        var prefs = await Body(await admin.PutAsJsonAsync("/api/v1/device/preferences", new { manualAddContacts = true, advertLocationPolicy = 1, telemetryModes = 0, multiAcks = 1 }));

        Assert.Equal("Base Camp", named.GetProperty("node").GetProperty("name").GetString());
        Assert.Equal(61.5, located.GetProperty("node").GetProperty("latitude").GetDouble());
        Assert.Equal(17, power.GetProperty("node").GetProperty("radio").GetProperty("txPowerDbm").GetInt32());
        Assert.Equal(869.525, radio.GetProperty("node").GetProperty("radio").GetProperty("frequencyMhz").GetDouble(), 3);
        Assert.Equal(250.0, radio.GetProperty("node").GetProperty("radio").GetProperty("bandwidthKhz").GetDouble());
        Assert.True(prefs.GetProperty("node").GetProperty("preferences").GetProperty("manualAddContacts").GetBoolean());
    }

    [Fact]
    public async Task Settings_are_validated_and_admin_only()
    {
        var (app, admin) = await StartAsync();
        using var _ = app;
        await admin.PostAsJsonAsync("/api/v1/users", new { username = "viewer", temporaryPassword = "temporary pass 1" });
        var user = await app.SignInAsync("viewer", "temporary pass 1");
        await ApiFactory.AuthorizeAsync(user, await user.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = "temporary pass 1", newPassword = "my own secret pw" }));

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/v1/device/radio", new { frequencyMhz = 50.0, bandwidthKhz = 250.0, spreadingFactor = 11, codingRate = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync("/api/v1/device/tx-power", new { dbm = 30 })).StatusCode); // radio refuses > its max
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PutAsJsonAsync("/api/v1/device/name", new { name = "hijack" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/v1/device/private-key")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/v1/device/contact-card")).StatusCode);
    }

    [Fact]
    public async Task Private_key_export_reports_disabled_firmware_as_501()
    {
        var (app, admin) = await StartAsync();
        using var _ = app;

        var response = await admin.GetAsync("/api/v1/device/private-key");

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    [Fact]
    public async Task Tuning_round_trips()
    {
        var (app, admin) = await StartAsync();
        using var _ = app;

        var saved = await Body(await admin.PutAsJsonAsync("/api/v1/device/tuning", new { rxDelayBase = 2.5, airtimeFactor = 1.5 }));

        Assert.Equal(2.5, saved.GetProperty("rxDelayBase").GetDouble());
        Assert.Equal(1.5, saved.GetProperty("airtimeFactor").GetDouble());
    }

    [Fact]
    public async Task Favourite_and_rename_update_the_radio_and_local_list()
    {
        var (app, admin) = await StartAsync();
        using var _ = app;

        var fav = await Body(await admin.PutAsJsonAsync($"/api/v1/contacts/{Alice}/favourite", new { favourite = true }));
        var renamed = await Body(await admin.PutAsJsonAsync($"/api/v1/contacts/{Alice}/name", new { name = "Alice (hiking)" }));

        Assert.True(fav.GetProperty("favourite").GetBoolean());
        Assert.Equal("Alice (hiking)", renamed.GetProperty("name").GetString());
        Assert.True(renamed.GetProperty("favourite").GetBoolean());
    }

    [Fact]
    public async Task Repeater_login_status_telemetry_and_trace()
    {
        var (app, admin) = await StartAsync();
        using var _ = app;

        var login = await Body(await admin.PostAsJsonAsync($"/api/v1/contacts/{Hilltop}/login", new { password = "admin pw" }));
        var status = await Body(await admin.PostAsync($"/api/v1/contacts/{Hilltop}/status", null));
        var telemetry = await Body(await admin.PostAsync($"/api/v1/contacts/{Hilltop}/telemetry", null));
        var trace = await Body(await admin.PostAsync($"/api/v1/contacts/{Hilltop}/trace", null));

        Assert.True(login.GetProperty("success").GetBoolean());
        Assert.True(login.GetProperty("isAdmin").GetBoolean());
        Assert.Equal(4.12, status.GetProperty("stats").GetProperty("batteryVolts").GetDouble(), 2);
        Assert.Equal(-118, status.GetProperty("stats").GetProperty("noiseFloorDbm").GetInt32());
        // The simulator's temperature drifts between 12 and 24 °C over the day.
        Assert.Contains(telemetry.GetProperty("readings").EnumerateArray(),
            r => r.GetProperty("name").GetString() == "Temperature" && r.GetProperty("values")[0].GetDouble() is >= 12 and <= 24);
        Assert.Equal("50", Assert.Single(trace.GetProperty("hops").EnumerateArray()).GetProperty("hash").GetString());
        Assert.Equal(6.0, trace.GetProperty("finalSnrDb").GetDouble());
    }

    [Fact]
    public async Task Wrong_password_and_untraceable_routes_are_reported_clearly()
    {
        var (app, admin) = await StartAsync();
        using var _ = app;

        var login = await Body(await admin.PostAsJsonAsync($"/api/v1/contacts/{Hilltop}/login", new { password = "wrong" }));
        var trace = await admin.PostAsync($"/api/v1/contacts/{Alice}/trace", null); // Alice floods: no route

        Assert.False(login.GetProperty("success").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, trace.StatusCode);
        Assert.Contains("no known route", (await Body(trace)).GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cli_command_reply_arrives_as_a_cli_message()
    {
        var (app, admin) = await StartAsync();
        using var _ = app;
        await admin.PostAsJsonAsync($"/api/v1/contacts/{Hilltop}/login", new { password = "admin pw" });

        var sent = await admin.PostAsJsonAsync($"/api/v1/contacts/{Hilltop}/cli", new { command = "ver" });
        await HubTests.WaitForAsync(() => admin.GetFromJsonAsync<JsonElement>($"/api/v1/messages?contact={Hilltop}", ApiFactory.Json).Result
            .EnumerateArray().Any(m => m.GetProperty("direction").GetString() == "Incoming"));
        var thread = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/messages?contact={Hilltop}", ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        var sentBody = await Body(sent);
        Assert.Equal("CliData", sentBody.GetProperty("textType").GetString());
        Assert.Equal("Sent", sentBody.GetProperty("status").GetString()); // CLI is never ACKed, so no deadline
        var reply = thread.EnumerateArray().First(m => m.GetProperty("direction").GetString() == "Incoming");
        Assert.Equal("CliData", reply.GetProperty("textType").GetString());
        Assert.StartsWith("v1.18.0-sim", reply.GetProperty("text").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Contact_cards_export_and_import()
    {
        var (app, admin) = await StartAsync();
        using var _ = app;

        var mine = await Body(await admin.GetAsync("/api/v1/device/contact-card"));
        var alice = await Body(await admin.GetAsync($"/api/v1/contacts/{Alice}/export"));
        var imported = await admin.PostAsJsonAsync("/api/v1/contacts/import", new { advertHex = alice.GetProperty("advertHex").GetString() });
        var garbage = await admin.PostAsJsonAsync("/api/v1/contacts/import", new { advertHex = "nothex" });

        Assert.True(mine.GetProperty("advertHex").GetString()!.Length >= 200);
        Assert.Equal(HttpStatusCode.NoContent, imported.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
    }

    [Fact]
    public async Task Contact_details_page_shows_repeater_tools_and_results()
    {
        using var app = new ApiFactory { UseSimulator = true };
        var browser = new Browser(app);
        await browser.SetupAsync();
        await app.WaitForRadioAsync();

        var page = await browser.PageAsync($"/Contacts/Details/{Hilltop}");
        var status = await browser.SubmitAsync($"/Contacts/Details/{Hilltop}", "/Contacts/Status", ("key", "50515253545556575859" + "5a5b5c5d5e5f606162636465666768696a6b6c6d6e6f"));
        var settings = await browser.PageAsync("/Device/Settings");

        Assert.Contains("Log in", page, StringComparison.Ordinal);
        Assert.Contains("Trace route", page, StringComparison.Ordinal);
        Assert.Contains("Noise floor", await status.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("Radio (LoRa) settings", settings, StringComparison.Ordinal);
    }
}
