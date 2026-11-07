using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshCore.Host.Api;
using MeshCore.Host.Auth;
using Microsoft.AspNetCore.Authentication;

namespace MeshCore.Host.Web;

/// <summary>The API refused a call. <see cref="Detail"/> is the human-readable reason from its problem response.</summary>
public sealed class ApiException(HttpStatusCode status, string title, string? detail)
    : Exception(detail is { Length: > 0 } ? detail : title)
{
    public HttpStatusCode Status { get; } = status;

    public string Title { get; } = title;

    public string? Detail { get; } = detail;
}

/// <summary>
/// The website's only way to data: a typed client for <c>/api/v1</c>. The MVC side is a
/// Backend-for-Frontend; it never touches the radio or database directly.
/// </summary>
public sealed class MeshApiClient(HttpClient http)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    // ---- Auth (anonymous) -----------------------------------------------------

    public async Task<bool> IsSetupRequiredAsync(CancellationToken ct = default) =>
        (await GetAsync<SetupStatusResponse>("auth/setup-required", ct)).SetupRequired;

    public Task<TokenResponse> SetupAsync(string username, string password, CancellationToken ct = default) =>
        PostAsync<TokenResponse>("auth/setup", new CredentialsRequest(username, password), ct);

    public Task<TokenResponse> SignInAsync(string username, string password, CancellationToken ct = default) =>
        PostAsync<TokenResponse>("auth/token", new CredentialsRequest(username, password), ct);

    // ---- Auth (signed in) -----------------------------------------------------

    public Task<UserResponse> MeAsync(CancellationToken ct = default) => GetAsync<UserResponse>("auth/me", ct);

    public Task<TokenResponse> ChangePasswordAsync(string current, string next, CancellationToken ct = default) =>
        PostAsync<TokenResponse>("auth/change-password", new ChangePasswordRequest(current, next), ct);

    // ---- Users (Admin) ----------------------------------------------------------

    public Task<UserResponse[]> UsersAsync(CancellationToken ct = default) => GetAsync<UserResponse[]>("users", ct);

    public Task<UserResponse> UserAsync(string id, CancellationToken ct = default) => GetAsync<UserResponse>($"users/{Uri.EscapeDataString(id)}", ct);

    public Task<UserResponse> CreateUserAsync(string username, string temporaryPassword, UserRole role, CancellationToken ct = default) =>
        PostAsync<UserResponse>("users", new CreateUserRequest(username, temporaryPassword, role), ct);

    public Task<UserResponse> UpdateUserAsync(string id, UserRole? role, bool? enabled, CancellationToken ct = default) =>
        SendAsync<UserResponse>(HttpMethod.Put, $"users/{Uri.EscapeDataString(id)}", new UpdateUserRequest(role, enabled), ct);

    public Task<UserResponse> ResetPasswordAsync(string id, string temporaryPassword, CancellationToken ct = default) =>
        PostAsync<UserResponse>($"users/{Uri.EscapeDataString(id)}/reset-password", new ResetPasswordRequest(temporaryPassword), ct);

    public Task DeleteUserAsync(string id, CancellationToken ct = default) => SendAsync(HttpMethod.Delete, $"users/{Uri.EscapeDataString(id)}", null, ct);

    // ---- Device ---------------------------------------------------------------

    public Task<DeviceResponse> DeviceAsync(CancellationToken ct = default) => GetAsync<DeviceResponse>("device", ct);

    public Task<BatteryResponse> BatteryAsync(CancellationToken ct = default) => GetAsync<BatteryResponse>("device/battery", ct);

    public Task<DeviceTimeResponse> DeviceTimeAsync(CancellationToken ct = default) => GetAsync<DeviceTimeResponse>("device/time", ct);

    public Task<DeviceTimeResponse> SyncDeviceTimeAsync(CancellationToken ct = default) => PostAsync<DeviceTimeResponse>("device/time", null, ct);

    public Task SendAdvertAsync(bool flood, CancellationToken ct = default) => SendAsync(HttpMethod.Post, "device/advert", new AdvertRequest(flood), ct);

    public Task RebootAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Post, "device/reboot", null, ct);

    // ---- Node settings (Admin) ------------------------------------------------------

    public Task<DeviceResponse> SetNameAsync(string name, CancellationToken ct = default) =>
        SendAsync<DeviceResponse>(HttpMethod.Put, "device/name", new SetNameRequest(name), ct);

    public Task<DeviceResponse> SetLocationAsync(double latitude, double longitude, CancellationToken ct = default) =>
        SendAsync<DeviceResponse>(HttpMethod.Put, "device/location", new SetLocationRequest(latitude, longitude), ct);

    public Task<DeviceResponse> SetRadioAsync(SetRadioRequest request, CancellationToken ct = default) =>
        SendAsync<DeviceResponse>(HttpMethod.Put, "device/radio", request, ct);

    public Task<DeviceResponse> SetTxPowerAsync(int dbm, CancellationToken ct = default) =>
        SendAsync<DeviceResponse>(HttpMethod.Put, "device/tx-power", new SetTxPowerRequest(dbm), ct);

    public Task<TuningResponse> TuningAsync(CancellationToken ct = default) => GetAsync<TuningResponse>("device/tuning", ct);

    public Task<TuningResponse> SetTuningAsync(double rxDelayBase, double airtimeFactor, CancellationToken ct = default) =>
        SendAsync<TuningResponse>(HttpMethod.Put, "device/tuning", new SetTuningRequest(rxDelayBase, airtimeFactor), ct);

    public Task<DeviceResponse> SetPreferencesAsync(SetPreferencesRequest request, CancellationToken ct = default) =>
        SendAsync<DeviceResponse>(HttpMethod.Put, "device/preferences", request, ct);

    public Task<ContactCardResponse> ContactCardAsync(CancellationToken ct = default) => GetAsync<ContactCardResponse>("device/contact-card", ct);

    public Task<PrivateKeyResponse> PrivateKeyAsync(CancellationToken ct = default) => GetAsync<PrivateKeyResponse>("device/private-key", ct);

    public Task<DeviceResponse> ImportPrivateKeyAsync(string hex, CancellationToken ct = default) =>
        SendAsync<DeviceResponse>(HttpMethod.Put, "device/private-key", new ImportPrivateKeyRequest(hex), ct);

    // ---- Contact actions and remote nodes ---------------------------------------------

    public Task<DiscoveredNodeResponse[]> DiscoveredAsync(CancellationToken ct = default) => GetAsync<DiscoveredNodeResponse[]>("contacts/discovered", ct);

    public Task AddDiscoveredAsync(string key, CancellationToken ct = default) => SendAsync(HttpMethod.Post, $"contacts/discovered/{Uri.EscapeDataString(key)}", null, ct);

    public Task ImportContactAsync(string advertHex, CancellationToken ct = default) => SendAsync(HttpMethod.Post, "contacts/import", new ImportContactRequest(advertHex), ct);

    public Task<ContactResponse> SetFavouriteAsync(string key, bool favourite, CancellationToken ct = default) =>
        SendAsync<ContactResponse>(HttpMethod.Put, $"contacts/{Uri.EscapeDataString(key)}/favourite", new SetFavouriteRequest(favourite), ct);

    public Task<ContactResponse> RenameContactAsync(string key, string name, CancellationToken ct = default) =>
        SendAsync<ContactResponse>(HttpMethod.Put, $"contacts/{Uri.EscapeDataString(key)}/name", new RenameContactRequest(name), ct);

    public Task<ContactResponse> ShareContactAsync(string key, CancellationToken ct = default) =>
        PostAsync<ContactResponse>($"contacts/{Uri.EscapeDataString(key)}/share", null, ct);

    public Task<ContactCardResponse> ExportContactAsync(string key, CancellationToken ct = default) =>
        GetAsync<ContactCardResponse>($"contacts/{Uri.EscapeDataString(key)}/export", ct);

    public Task<LoginResponse> LoginAsync(string key, string? password, CancellationToken ct = default) =>
        PostAsync<LoginResponse>($"contacts/{Uri.EscapeDataString(key)}/login", new LoginRequest(password), ct);

    public Task LogoutAsync(string key, CancellationToken ct = default) => SendAsync(HttpMethod.Post, $"contacts/{Uri.EscapeDataString(key)}/logout", null, ct);

    public Task<StatusResponse> StatusAsync(string key, CancellationToken ct = default) => PostAsync<StatusResponse>($"contacts/{Uri.EscapeDataString(key)}/status", null, ct);

    public Task<TelemetryResponse> TelemetryAsync(string key, CancellationToken ct = default) =>
        PostAsync<TelemetryResponse>($"contacts/{Uri.EscapeDataString(key)}/telemetry", null, ct);

    public Task<TraceResponse> TraceAsync(string key, CancellationToken ct = default) => PostAsync<TraceResponse>($"contacts/{Uri.EscapeDataString(key)}/trace", null, ct);

    public Task<MessageResponse> CliAsync(string key, string command, CancellationToken ct = default) =>
        PostAsync<MessageResponse>($"contacts/{Uri.EscapeDataString(key)}/cli", new CliCommandRequest(command), ct);

    // ---- Contacts & channels ----------------------------------------------------

    public Task<ContactResponse[]> ContactsAsync(bool includeRemoved = false, CancellationToken ct = default) =>
        GetAsync<ContactResponse[]>($"contacts?includeRemoved={includeRemoved}", ct);

    public Task<ContactResponse> ContactAsync(string key, CancellationToken ct = default) => GetAsync<ContactResponse>($"contacts/{Uri.EscapeDataString(key)}", ct);

    public Task<ContactResponse[]> RefreshContactsAsync(CancellationToken ct = default) => PostAsync<ContactResponse[]>("contacts/refresh", null, ct);

    public Task ResetPathAsync(string key, CancellationToken ct = default) => SendAsync(HttpMethod.Post, $"contacts/{Uri.EscapeDataString(key)}/reset-path", null, ct);

    public Task RemoveContactAsync(string key, CancellationToken ct = default) => SendAsync(HttpMethod.Delete, $"contacts/{Uri.EscapeDataString(key)}", null, ct);

    public Task<ChannelResponse[]> ChannelsAsync(CancellationToken ct = default) => GetAsync<ChannelResponse[]>("channels", ct);

    public Task<ChannelSecretResponse> ChannelSecretAsync(byte index, CancellationToken ct = default) => GetAsync<ChannelSecretResponse>($"channels/{index}/secret", ct);

    public Task<ChannelResponse> SetChannelAsync(byte index, string name, string? secretHex, CancellationToken ct = default) =>
        SendAsync<ChannelResponse>(HttpMethod.Put, $"channels/{index}", new SetChannelRequest(name, secretHex), ct);

    public Task DeleteChannelAsync(byte index, CancellationToken ct = default) => SendAsync(HttpMethod.Delete, $"channels/{index}", null, ct);

    // ---- Messages ---------------------------------------------------------------

    public Task<ConversationResponse[]> ConversationsAsync(CancellationToken ct = default) => GetAsync<ConversationResponse[]>("conversations", ct);

    public Task<MessageResponse[]> MessagesAsync(string? contact = null, byte? channel = null, long? before = null, int take = 50, CancellationToken ct = default)
    {
        var query = new List<string> { $"take={take}" };
        if (contact is not null) query.Add($"contact={Uri.EscapeDataString(contact)}");
        if (channel is not null) query.Add($"channel={channel}");
        if (before is not null) query.Add($"before={before}");
        return GetAsync<MessageResponse[]>($"messages?{string.Join('&', query)}", ct);
    }

    public Task<MessageResponse[]> MessagesAfterAsync(string? contact, byte? channel, long afterId, CancellationToken ct = default) =>
        GetAsync<MessageResponse[]>(contact is not null
            ? $"messages?contact={Uri.EscapeDataString(contact)}&after={afterId}"
            : $"messages?channel={channel}&after={afterId}", ct);

    public Task MarkReadAsync(string conversationKey, long messageId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "conversations/read", new MarkReadRequest(conversationKey, messageId), ct);

    public Task<MessageResponse> SendDirectAsync(string contact, string text, CancellationToken ct = default) =>
        PostAsync<MessageResponse>("messages/direct", new SendDirectRequest(contact, text), ct);

    public Task<MessageResponse> SendChannelAsync(byte channel, string text, CancellationToken ct = default) =>
        PostAsync<MessageResponse>("messages/channel", new SendChannelRequest(channel, text), ct);

    // ---- Images -------------------------------------------------------------------

    public Task<ImageSettingsResponse> ImageSettingsAsync(CancellationToken ct = default) => GetAsync<ImageSettingsResponse>("images/settings", ct);

    /// <summary>Uploads a photo through the API (which shrinks it, strips its metadata and sends it to the image host).</summary>
    public async Task<ImageUploadResponse> UploadImageAsync(Stream file, string fileName, string? contentType, string? expires, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var content = new StreamContent(file);
        content.Headers.ContentType = MediaTypeHeaderValue.TryParse(contentType, out var type) ? type : new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", string.IsNullOrWhiteSpace(fileName) ? "image" : fileName);
        if (!string.IsNullOrWhiteSpace(expires))
        {
            form.Add(new StringContent(expires), "expires");
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, "images") { Content = form };
        using var response = await SendRawAsync(request, ct);
        return (await response.Content.ReadFromJsonAsync<ImageUploadResponse>(Json, ct))!;
    }

    // ---- Plumbing ---------------------------------------------------------------

    private Task<T> GetAsync<T>(string path, CancellationToken ct) => SendAsync<T>(HttpMethod.Get, path, null, ct);

    private Task<T> PostAsync<T>(string path, object? body, CancellationToken ct) => SendAsync<T>(HttpMethod.Post, path, body, ct);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await SendRawAsync(method, path, body, ct);
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private async Task SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await SendRawAsync(method, path, body, ct);
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }
        return await SendRawAsync(request, ct);
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            var problem = await ReadProblemAsync(response, ct);
            throw new ApiException(response.StatusCode, problem.Title ?? response.ReasonPhrase ?? "Request failed", problem.Detail ?? ValidationSummary(problem));
        }
    }

    private static async Task<Problem> ReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<Problem>(Json, ct) ?? new Problem();
        }
        catch (JsonException)
        {
            return new Problem();
        }
    }

    private static string? ValidationSummary(Problem p) =>
        p.Errors is { Count: > 0 } errors ? string.Join(" ", errors.SelectMany(e => e.Value)) : null;

    private sealed record Problem(string? Title = null, string? Detail = null, Dictionary<string, string[]>? Errors = null);
}

/// <summary>Adds the signed-in user's API token (kept inside their encrypted auth cookie) to API calls.</summary>
public sealed class ApiTokenHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    public const string TokenName = "access_token";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (accessor.HttpContext is { } http && await http.GetTokenAsync(TokenName) is { } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return await base.SendAsync(request, cancellationToken);
    }
}

internal static class TokenClaims
{
    /// <summary>
    /// Reads the claims out of a token our own API just issued (no need to re-validate it here: it came
    /// straight from the API over loopback, and the API re-checks it on every call).
    /// </summary>
    public static IEnumerable<System.Security.Claims.Claim> From(string accessToken) =>
        new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(accessToken).Claims
            .Where(c => c.Type is MeshClaims.UserId or MeshClaims.Username or MeshClaims.Role or MeshClaims.MustChangePassword)
            .Select(c => new System.Security.Claims.Claim(c.Type, c.Value));
}
