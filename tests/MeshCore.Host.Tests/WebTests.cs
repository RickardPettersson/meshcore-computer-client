using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MeshCore.Host.Tests;

/// <summary>A browser-like session: keeps cookies, doesn't follow redirects, fills in antiforgery tokens.</summary>
internal sealed partial class Browser(ApiFactory app)
{
    private readonly HttpClient _http = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    public Task<HttpResponseMessage> GetAsync(string url) => _http.GetAsync(url);

    public async Task<string> PageAsync(string url)
    {
        var response = await _http.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>Loads <paramref name="formPage"/>, takes its antiforgery token, and posts <paramref name="fields"/> to <paramref name="action"/>.</summary>
    public async Task<HttpResponseMessage> SubmitAsync(string formPage, string action, params (string Name, string Value)[] fields)
    {
        var token = AntiforgeryRegex().Match(await PageAsync(formPage)).Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token), $"No antiforgery token on {formPage}.");
        return await _http.PostAsync(action, new FormUrlEncodedContent([.. fields.Select(f => KeyValuePair.Create(f.Name, f.Value)), KeyValuePair.Create("__RequestVerificationToken", token)]));
    }

    public Task<HttpResponseMessage> PostWithoutTokenAsync(string action, params (string Name, string Value)[] fields) =>
        _http.PostAsync(action, new FormUrlEncodedContent(fields.Select(f => KeyValuePair.Create(f.Name, f.Value))));

    public async Task SetupAsync(string username = "admin", string password = "correct horse battery")
    {
        var response = await SubmitAsync("/Account/Setup", "/Account/Setup",
            ("Username", username), ("Password", password), ("ConfirmPassword", password));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    public async Task<HttpResponseMessage> LoginAsync(string username, string password) =>
        await SubmitAsync("/Account/Login", "/Account/Login", ("Username", username), ("Password", password));

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryRegex();
}

public class WebTests
{
    /// <summary>Redirect target as path + query, whether the server sent it absolute or relative.</summary>
    private static string? Location(HttpResponseMessage r) => r.Headers.Location is { } l
        ? (l.IsAbsoluteUri ? l.PathAndQuery : l.OriginalString)
        : null;

    [Fact]
    public async Task A_fresh_install_sends_every_page_to_setup()
    {
        using var app = new ApiFactory();
        var browser = new Browser(app);

        Assert.Equal("/Account/Setup", Location(await browser.GetAsync("/")));
        Assert.Equal("/Account/Setup", Location(await browser.GetAsync("/Account/Login")));
        Assert.Contains("Create Admin account", await browser.PageAsync("/Account/Setup"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_signs_the_admin_in_with_a_locked_down_cookie()
    {
        using var app = new ApiFactory();
        var browser = new Browser(app);
        await app.WaitForRadioAsync();

        var token = await browser.SubmitAsync("/Account/Setup", "/Account/Setup",
            ("Username", "admin"), ("Password", "correct horse battery"), ("ConfirmPassword", "correct horse battery"));
        var cookie = token.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("MeshCore.Auth=", StringComparison.Ordinal));
        var dashboard = await browser.PageAsync("/");

        Assert.Equal("/", Location(token));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Dashboard", dashboard, StringComparison.Ordinal);
        Assert.Contains("Base", dashboard, StringComparison.Ordinal);           // node name, via the API
        Assert.Contains("You&#x27;re the Admin", dashboard, StringComparison.Ordinal);
        Assert.Equal("/Account/Login", Location(await browser.GetAsync("/Account/Setup")));
    }

    [Fact]
    public async Task Setup_form_validates_and_keeps_the_user_on_the_page()
    {
        using var app = new ApiFactory();
        var browser = new Browser(app);

        var response = await browser.SubmitAsync("/Account/Setup", "/Account/Setup",
            ("Username", "admin"), ("Password", "correct horse battery"), ("ConfirmPassword", "something else"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("The passwords don&#x27;t match.", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Admin_adds_a_user_who_signs_in_and_must_choose_a_password()
    {
        using var app = new ApiFactory();
        var admin = new Browser(app);
        await admin.SetupAsync();

        var created = await admin.SubmitAsync("/Settings/CreateUser", "/Settings/CreateUser",
            ("Username", "rickard"), ("TemporaryPassword", "temporary pass 1"), ("Role", "User"));
        Assert.Equal("/Settings/Users", Location(created));
        Assert.Contains("rickard", await admin.PageAsync("/Settings/Users"), StringComparison.Ordinal);

        var user = new Browser(app);
        var login = await user.LoginAsync("rickard", "temporary pass 1");
        Assert.Equal("/Account/ChangePassword", Location(login));
        Assert.Equal("/Account/ChangePassword", Location(await user.GetAsync("/Contacts")));
        Assert.Contains("Choose your password", await user.PageAsync("/Account/ChangePassword"), StringComparison.Ordinal);

        var changed = await user.SubmitAsync("/Account/ChangePassword", "/Account/ChangePassword",
            ("CurrentPassword", "temporary pass 1"), ("NewPassword", "my own secret pw"), ("ConfirmPassword", "my own secret pw"), ("Forced", "true"));
        Assert.Equal("/", Location(changed));

        var dashboard = await user.PageAsync("/");
        Assert.Contains("Signed in as rickard", dashboard, StringComparison.Ordinal);
        Assert.DoesNotContain("/Settings/Users", dashboard, StringComparison.Ordinal); // no admin menu
        Assert.StartsWith("/Account/AccessDenied", Location(await user.GetAsync("/Settings/Users")), StringComparison.Ordinal);
        Assert.StartsWith("/Account/AccessDenied", Location(await user.GetAsync("/Channels/Edit")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wrong_password_shows_a_message_without_saying_which_part_was_wrong()
    {
        using var app = new ApiFactory();
        await new Browser(app).SetupAsync();

        var response = await new Browser(app).LoginAsync("admin", "not it at all");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Wrong username or password.", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forms_without_an_antiforgery_token_are_rejected()
    {
        using var app = new ApiFactory();
        var browser = new Browser(app);
        await browser.SetupAsync();

        var response = await browser.PostWithoutTokenAsync("/Settings/CreateUser", ("Username", "sneaky"), ("TemporaryPassword", "temporary pass 1"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_website_cookie_does_not_open_the_api()
    {
        using var app = new ApiFactory();
        var browser = new Browser(app);
        await browser.SetupAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/v1/device")).StatusCode);
    }

    [Fact]
    public async Task Disabling_a_user_ends_their_browser_session()
    {
        using var app = new ApiFactory();
        var admin = new Browser(app);
        await admin.SetupAsync();
        await admin.SubmitAsync("/Settings/CreateUser", "/Settings/CreateUser", ("Username", "guest"), ("TemporaryPassword", "temporary pass 1"), ("Role", "User"));
        var guest = new Browser(app);
        await guest.LoginAsync("guest", "temporary pass 1");
        await guest.SubmitAsync("/Account/ChangePassword", "/Account/ChangePassword",
            ("CurrentPassword", "temporary pass 1"), ("NewPassword", "a different secret"), ("ConfirmPassword", "a different secret"));
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync("/")).StatusCode);

        var usersPage = await admin.PageAsync("/Settings/Users");
        var guestId = Regex.Match(usersPage, "id=\"reset-([0-9a-f]{32})\"").Groups[1].Value;
        await admin.SubmitAsync("/Settings/Users", "/Settings/UpdateUser", ("id", guestId), ("enabled", "false"));

        var next = await guest.GetAsync("/");
        Assert.StartsWith("/Account/Login", Location(next), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pages_send_strict_security_headers()
    {
        using var app = new ApiFactory();

        var response = await new Browser(app).GetAsync("/Account/Setup");

        Assert.Contains("default-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Chat_page_sends_through_the_api_and_shows_the_message()
    {
        using var app = new ApiFactory();
        var browser = new Browser(app);
        await browser.SetupAsync();
        await app.WaitForRadioAsync();

        var sent = await browser.SubmitAsync("/Messages/Channel/0", "/Messages/Send", ("channel", "0"), ("text", "hello from the website"));
        var chat = await browser.PageAsync("/Messages/Channel/0");
        var chats = await browser.PageAsync("/Messages");

        Assert.Equal("/Messages/Channel/0", Location(sent));
        Assert.Contains("hello from the website", chat, StringComparison.Ordinal);
        Assert.Contains("data-byte-limit=\"154\"", chat, StringComparison.Ordinal); // 160 - "Base: "
        Assert.Contains("You: hello from the website", chats, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Radio_offline_still_renders_pages_with_a_warning()
    {
        using var app = new ApiFactory { Radio = null };
        var browser = new Browser(app);
        await browser.SetupAsync();

        var dashboard = await browser.PageAsync("/");
        var device = await browser.PageAsync("/Device");

        Assert.Contains("The radio isn't connected", dashboard, StringComparison.Ordinal);
        Assert.Contains("Not available while the radio is disconnected.", device, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_pages_get_a_friendly_404()
    {
        using var app = new ApiFactory();
        var browser = new Browser(app);
        await browser.SetupAsync();

        var response = await browser.GetAsync("/no/such/page");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("That page doesn&#x27;t exist", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
