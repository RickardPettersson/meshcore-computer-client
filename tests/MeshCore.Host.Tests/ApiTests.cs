using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MeshCore.Host.Tests;

public class AuthApiTests
{
    private static async Task<JsonElement> Body(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);

    [Fact]
    public async Task First_run_setup_creates_an_admin_once()
    {
        using var app = new ApiFactory();
        var anon = app.CreateClient();

        Assert.True((await Body(await anon.GetAsync("/api/v1/auth/setup-required"))).GetProperty("setupRequired").GetBoolean());
        var admin = await app.AdminClientAsync();
        var me = await Body(await admin.GetAsync("/api/v1/auth/me"));
        var again = await anon.PostAsJsonAsync("/api/v1/auth/setup", new { username = "intruder", password = "trying again later" });

        Assert.Equal("Admin", me.GetProperty("role").GetString());
        Assert.False((await Body(await anon.GetAsync("/api/v1/auth/setup-required"))).GetProperty("setupRequired").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Theory]
    [InlineData("ab", "long enough password", "Username must be")]
    [InlineData("has space", "long enough password", "may only contain")]
    [InlineData("admin", "short", "at least 8")]
    [InlineData("admin", "myadminpass", "must not contain the username")]
    public async Task Setup_validates_credentials(string username, string password, string expected)
    {
        using var app = new ApiFactory();

        var response = await app.CreateClient().PostAsJsonAsync("/api/v1/auth/setup", new { username, password });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, (await Body(response)).GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Everything_needs_a_token()
    {
        using var app = new ApiFactory();
        var anon = app.CreateClient();

        foreach (var url in new[] { "/api/v1/device", "/api/v1/contacts", "/api/v1/channels", "/api/v1/messages", "/api/v1/users", "/api/v1/auth/me" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(url)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/healthz")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/openapi/v1.json")).StatusCode);
    }

    [Fact]
    public async Task Wrong_password_is_401_and_five_in_a_row_lock_the_account()
    {
        using var app = new ApiFactory();
        await app.AdminClientAsync();
        var anon = app.CreateClient();
        var wrong = new { username = "admin", password = "not the password" };

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/v1/auth/token", wrong)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Locked, (await anon.PostAsJsonAsync("/api/v1/auth/token", wrong)).StatusCode);
        // Even the right password is refused while locked.
        var right = await anon.PostAsJsonAsync("/api/v1/auth/token", new { username = "admin", password = "correct horse battery" });
        Assert.Equal(HttpStatusCode.Locked, right.StatusCode);
    }

    [Fact]
    public async Task Unknown_user_gets_the_same_401_as_a_wrong_password()
    {
        using var app = new ApiFactory();
        await app.AdminClientAsync();

        var response = await app.CreateClient().PostAsJsonAsync("/api/v1/auth/token", new { username = "nobody", password = "whatever pass" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Wrong username or password", (await Body(response)).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Admin_adds_a_user_who_must_change_the_temporary_password()
    {
        using var app = new ApiFactory();
        var admin = await app.AdminClientAsync();

        var created = await admin.PostAsJsonAsync("/api/v1/users", new { username = "rickard", temporaryPassword = "temporary pass 1", role = "User" });
        var user = await app.SignInAsync("rickard", "temporary pass 1");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.True((await Body(await user.GetAsync("/api/v1/auth/me"))).GetProperty("mustChangePassword").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/v1/channels")).StatusCode);

        var changed = await user.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = "temporary pass 1", newPassword = "my own secret pw" });
        changed.EnsureSuccessStatusCode();
        await ApiFactory.AuthorizeAsync(user, changed);

        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/v1/channels")).StatusCode);
    }

    [Fact]
    public async Task Users_get_403_on_admin_endpoints()
    {
        using var app = new ApiFactory();
        var admin = await app.AdminClientAsync();
        await admin.PostAsJsonAsync("/api/v1/users", new { username = "viewer", temporaryPassword = "temporary pass 1" });
        var user = await app.SignInAsync("viewer", "temporary pass 1");
        var changed = await user.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = "temporary pass 1", newPassword = "a different secret" });
        await ApiFactory.AuthorizeAsync(user, changed);

        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/v1/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsJsonAsync("/api/v1/users", new { username = "x123", temporaryPassword = "temporary pass 1" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/v1/channels/0/secret")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsync("/api/v1/device/reboot", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.DeleteAsync("/api/v1/channels/0")).StatusCode);
    }

    [Fact]
    public async Task Disabling_or_resetting_a_user_revokes_their_tokens()
    {
        using var app = new ApiFactory();
        var admin = await app.AdminClientAsync();
        var created = await Body(await admin.PostAsJsonAsync("/api/v1/users", new { username = "guest", temporaryPassword = "temporary pass 1" }));
        var id = created.GetProperty("id").GetString();
        var user = await app.SignInAsync("guest", "temporary pass 1");
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/v1/auth/me")).StatusCode);

        await admin.PutAsJsonAsync($"/api/v1/users/{id}", new { enabled = false });

        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/v1/auth/me")).StatusCode);
        var signIn = await app.CreateClient().PostAsJsonAsync("/api/v1/auth/token", new { username = "guest", password = "temporary pass 1" });
        Assert.Equal(HttpStatusCode.Unauthorized, signIn.StatusCode);
    }

    [Fact]
    public async Task The_last_admin_cannot_be_demoted_disabled_or_deleted()
    {
        using var app = new ApiFactory();
        var admin = await app.AdminClientAsync();
        var me = await Body(await admin.GetAsync("/api/v1/auth/me"));
        var id = me.GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/v1/users/{id}", new { role = "User" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/v1/users/{id}", new { enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.DeleteAsync($"/api/v1/users/{id}")).StatusCode); // can't delete yourself

        // With a second admin, the first can be demoted.
        var second = await Body(await admin.PostAsJsonAsync("/api/v1/users", new { username = "admin2", temporaryPassword = "temporary pass 1", role = "Admin" }));
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/users/{second.GetProperty("id").GetString()}", new { enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/users/{id}", new { role = "User" })).StatusCode);
    }

    [Fact]
    public async Task Duplicate_usernames_are_rejected_case_insensitively()
    {
        using var app = new ApiFactory();
        var admin = await app.AdminClientAsync();

        var response = await admin.PostAsJsonAsync("/api/v1/users", new { username = "ADMIN", temporaryPassword = "temporary pass 1" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Passwords_are_stored_hashed_and_the_jwt_key_encrypted()
    {
        using var app = new ApiFactory();
        await app.AdminClientAsync(password: "correct horse battery");
        var store = (Auth.JsonUserStore)app.Services.GetService(typeof(Auth.IUserStore))!;

        var file = await File.ReadAllTextAsync(store.FilePath);

        Assert.DoesNotContain("correct horse battery", file, StringComparison.Ordinal);
        Assert.Contains("\"passwordHash\": \"AQAAAA", file, StringComparison.Ordinal); // Identity v3 hash format
        Assert.Contains("\"jwtKeyProtected\": \"CfDJ8", file, StringComparison.Ordinal);  // Data Protection payload
    }
}

public class RadioApiTests
{
    private static async Task<JsonElement> Body(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);

    [Fact]
    public async Task Device_contacts_and_channels_come_from_the_radio()
    {
        using var app = new ApiFactory();
        var admin = await app.AdminClientAsync();
        await app.WaitForRadioAsync();

        var device = await Body(await admin.GetAsync("/api/v1/device"));
        var battery = await Body(await admin.GetAsync("/api/v1/device/battery"));
        var contacts = await Body(await admin.GetAsync("/api/v1/contacts"));
        var channels = await Body(await admin.GetAsync("/api/v1/channels"));
        var secret = await Body(await admin.GetAsync("/api/v1/channels/0/secret"));

        Assert.Equal("Connected", device.GetProperty("state").GetString());
        Assert.Equal("Base", device.GetProperty("node").GetProperty("name").GetString());
        Assert.Equal("Fake Board", device.GetProperty("hardware").GetProperty("model").GetString());
        Assert.Equal(4.012, battery.GetProperty("volts").GetDouble(), 3);
        Assert.Equal(84, battery.GetProperty("percent").GetInt32()); // 4.012 V on the 3.0–4.2 V scale
        Assert.Equal("Alice", Assert.Single(contacts.EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal("Public", Assert.Single(channels.EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal(new string('8', 1) + "b", secret.GetProperty("secretHex").GetString()![..2]);
    }

    [Fact]
    public async Task Sending_records_the_message_and_who_sent_it()
    {
        using var app = new ApiFactory();
        var admin = await app.AdminClientAsync();
        await app.WaitForRadioAsync();
        var me = await Body(await admin.GetAsync("/api/v1/auth/me"));
        var alice = (await Body(await admin.GetAsync("/api/v1/contacts")))[0].GetProperty("publicKey").GetString();

        var dm = await admin.PostAsJsonAsync("/api/v1/messages/direct", new { contact = alice, text = "hi Alice" });
        var ch = await admin.PostAsJsonAsync("/api/v1/messages/channel", new { channel = 0, text = "hello all" });
        var history = await Body(await admin.GetAsync("/api/v1/messages?channel=0"));

        Assert.Equal(HttpStatusCode.Accepted, dm.StatusCode);
        var sent = await Body(dm);
        Assert.Equal("Sent", sent.GetProperty("status").GetString());
        Assert.Equal(me.GetProperty("id").GetString(), sent.GetProperty("sentByUserId").GetString());
        Assert.Equal(HttpStatusCode.Accepted, ch.StatusCode);
        Assert.Equal("hello all", Assert.Single(history.EnumerateArray()).GetProperty("text").GetString());
    }

    [Fact]
    public async Task Channel_text_over_the_limit_is_a_400()
    {
        using var app = new ApiFactory();
        var admin = await app.AdminClientAsync();
        await app.WaitForRadioAsync();

        var response = await admin.PostAsJsonAsync("/api/v1/messages/channel", new { channel = 0, text = new string('x', 155) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("limit is 154", (await Body(response)).GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Radio_offline_is_503_but_history_still_works()
    {
        using var app = new ApiFactory { Radio = null };
        var admin = await app.AdminClientAsync();

        var send = await admin.PostAsJsonAsync("/api/v1/messages/channel", new { channel = 0, text = "anyone?" });
        var device = await Body(await admin.GetAsync("/api/v1/device"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, send.StatusCode);
        Assert.Equal("Radio not connected", (await Body(send)).GetProperty("title").GetString());
        Assert.NotEqual("Connected", device.GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/messages")).StatusCode);
    }

    [Fact]
    public async Task Radio_errors_map_to_http_status_codes()
    {
        var radio = ApiFactory.DefaultRadio().On(0x20, Client.Tests.FakeRadio.Error(3)); // set channel: table full
        using var app = new ApiFactory { Radio = radio };
        var admin = await app.AdminClientAsync();
        await app.WaitForRadioAsync();

        var full = await admin.PutAsJsonAsync("/api/v1/channels/1", new { name = "Hiking" });
        var outOfRange = await admin.PutAsJsonAsync("/api/v1/channels/9", new { name = "Nope" });
        var badSecret = await admin.PutAsJsonAsync("/api/v1/channels/1", new { name = "Hiking", secretHex = "abc" });

        Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, outOfRange.StatusCode);
        Assert.Contains("channels 0–3", (await Body(outOfRange)).GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, badSecret.StatusCode);
    }
}
