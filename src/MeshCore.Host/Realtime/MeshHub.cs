using System.Text.Json.Serialization;
using MeshCore.Host.Api;
using MeshCore.Host.Auth;
using MeshCore.Host.Radio;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace MeshCore.Host.Realtime;

/// <summary>
/// Live updates. Server-to-client only; clients send through the API (or the website) as usual.
/// Mapped twice: <c>/hubs/mesh</c> for the website (cookie) and <c>/api/v1/hub</c> for API clients
/// (bearer token, or <c>?access_token=</c> for WebSockets).
/// </summary>
[Authorize(AuthPolicies.User)]
public sealed class MeshHub : Hub
{
    public const string WebPath = "/hubs/mesh";
    public const string ApiPath = "/api/v1/hub";

    /// <summary>A message was stored or changed status. Payload: <see cref="MessageResponse"/>.</summary>
    public const string MessageEvent = "message";

    /// <summary>The radio connection changed. Payload: <see cref="DeviceResponse"/>.</summary>
    public const string RadioEvent = "radio";

    /// <summary>The contact list changed (no payload; re-fetch).</summary>
    public const string ContactsEvent = "contacts";

    /// <summary>The channel list changed (no payload; re-fetch).</summary>
    public const string ChannelsEvent = "channels";
}

/// <summary>Forwards <see cref="MeshRadio"/> events to every connected client.</summary>
public sealed partial class MeshHubBroadcaster(MeshRadio radio, IHubContext<MeshHub> hub, ILogger<MeshHubBroadcaster> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        radio.MessageChanged += OnMessage;
        radio.StatusChanged += OnStatus;
        radio.ContactsChanged += OnContacts;
        radio.ChannelsChanged += OnChannels;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        radio.MessageChanged -= OnMessage;
        radio.StatusChanged -= OnStatus;
        radio.ContactsChanged -= OnContacts;
        radio.ChannelsChanged -= OnChannels;
        return Task.CompletedTask;
    }

    private void OnMessage(object? sender, MessageEventArgs e) => Send(MeshHub.MessageEvent, MessageResponse.From(e.Message));

    private void OnStatus(object? sender, RadioStatus status) => Send(MeshHub.RadioEvent, DeviceResponse.From(status));

    private void OnContacts(object? sender, EventArgs e) => Send(MeshHub.ContactsEvent, null);

    private void OnChannels(object? sender, EventArgs e) => Send(MeshHub.ChannelsEvent, null);

    private void Send(string method, object? payload) => _ = SendAsync(method, payload);

    private async Task SendAsync(string method, object? payload)
    {
        try
        {
            await (payload is null ? hub.Clients.All.SendAsync(method) : hub.Clients.All.SendAsync(method, payload));
        }
#pragma warning disable CA1031 // A failed broadcast must never affect the radio service.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogBroadcastFailed(ex, method);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Broadcasting '{Method}' failed")]
    private partial void LogBroadcastFailed(Exception ex, string method);
}

public static class RealtimeSetup
{
    public static IServiceCollection AddMeshCoreRealtime(this IServiceCollection services)
    {
        services.AddSignalR(o => o.EnableDetailedErrors = false)
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        services.AddHostedService<MeshHubBroadcaster>();
        return services;
    }
}
