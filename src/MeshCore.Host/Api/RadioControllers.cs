using System.Security.Cryptography;
using MeshCore.Host.Auth;
using MeshCore.Host.Radio;
using MeshCore.Protocol.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeshCore.Host.Api;

/// <summary>The radio itself: connection status, battery, clock, adverts, reboot.</summary>
[ApiController]
[Route("api/v1/device")]
[Tags("Device")]
public sealed class DeviceController(MeshRadio radio, TimeProvider time) : ControllerBase
{
    /// <summary>Connection state, node identity, radio settings and hardware. Works while disconnected too.</summary>
    [HttpGet]
    public DeviceResponse Get() => DeviceResponse.From(radio.Status);

    [HttpGet("battery")]
    public async Task<BatteryResponse> Battery(CancellationToken ct)
    {
        var b = await radio.GetBatteryAsync(ct);
        return new BatteryResponse(b.BatteryMillivolts, b.BatteryVolts, b.StorageUsedKb, b.StorageTotalKb);
    }

    [HttpGet("time")]
    public async Task<DeviceTimeResponse> Time(CancellationToken ct)
    {
        var device = await radio.GetDeviceTimeAsync(ct);
        var now = time.GetUtcNow();
        return new DeviceTimeResponse(device, now, Math.Round((device - now).TotalSeconds, 1));
    }

    /// <summary>Sets the radio clock to this computer's time. The radio refuses to go backwards (409).</summary>
    [HttpPost("time")]
    [Authorize(AuthPolicies.Admin)]
    public async Task<DeviceTimeResponse> SyncTime(CancellationToken ct)
    {
        await radio.SetDeviceTimeAsync(time.GetUtcNow(), ct);
        return await Time(ct);
    }

    /// <summary>Broadcasts our advert: zero-hop (neighbours) or flooded through the mesh.</summary>
    [HttpPost("advert")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Advert(AdvertRequest request, CancellationToken ct)
    {
        await radio.SendAdvertAsync(request.Flood, ct);
        return NoContent();
    }

    [HttpPost("reboot")]
    [Authorize(AuthPolicies.Admin)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Reboot(CancellationToken ct)
    {
        await radio.RebootAsync(ct);
        return Accepted();
    }
}

/// <summary>Contacts, as last synced from the radio (removed ones are kept for message history).</summary>
[ApiController]
[Route("api/v1/contacts")]
[Tags("Contacts")]
public sealed class ContactsController(MeshRadio radio) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<ContactResponse>> List([FromQuery] bool includeRemoved = false, CancellationToken ct = default) =>
        (await radio.Store.GetContactsAsync(includeRemoved, ct)).Select(ContactResponse.From);

    /// <param name="key">Full public key, or a unique prefix of at least 12 hex characters.</param>
    [HttpGet("{key}")]
    public async Task<ActionResult<ContactResponse>> Get(string key, CancellationToken ct) =>
        await Find(key, ct) is { } c ? ContactResponse.From(c) : NotFound();

    /// <summary>Re-reads the contact list from the radio.</summary>
    [HttpPost("refresh")]
    public async Task<IEnumerable<ContactResponse>> Refresh(CancellationToken ct)
    {
        await radio.RefreshContactsAsync(ct);
        return await List(false, ct);
    }

    /// <summary>Forgets the learned route, so the next message floods to find a new one.</summary>
    [HttpPost("{key}/reset-path")]
    [Authorize(AuthPolicies.Admin)]
    public async Task<ActionResult<ContactResponse>> ResetPath(string key, CancellationToken ct)
    {
        if (await Find(key, ct) is not { } c)
        {
            return NotFound();
        }
        await radio.ResetPathAsync(PublicKey.Parse(c.PublicKey), ct);
        return await Get(c.PublicKey, ct);
    }

    /// <summary>Removes the contact from the radio. Its message history stays.</summary>
    [HttpDelete("{key}")]
    [Authorize(AuthPolicies.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(string key, CancellationToken ct)
    {
        if (await Find(key, ct) is not { IsOnRadio: true } c)
        {
            return NotFound();
        }
        await radio.RemoveContactAsync(PublicKey.Parse(c.PublicKey), ct);
        return NoContent();
    }

    private async Task<Data.ContactEntity?> Find(string key, CancellationToken ct)
    {
        if (key.Length < 12 || !key.All(Uri.IsHexDigit))
        {
            return null;
        }
        var matches = (await radio.Store.GetContactsAsync(includeRemoved: true, ct))
            .Where(c => c.PublicKey.StartsWith(key.ToLowerInvariant(), StringComparison.Ordinal)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }
}

/// <summary>Group channels. Secrets live only on the radio.</summary>
[ApiController]
[Route("api/v1/channels")]
[Tags("Channels")]
public sealed class ChannelsController(MeshRadio radio) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<ChannelResponse>> List(CancellationToken ct) =>
        (await radio.Store.GetChannelsAsync(ct)).Select(ChannelResponse.From);

    [HttpGet("{index:int}")]
    public async Task<ActionResult<ChannelResponse>> Get(byte index, CancellationToken ct) =>
        (await radio.Store.GetChannelsAsync(ct)).FirstOrDefault(c => c.Index == index) is { } c ? ChannelResponse.From(c) : NotFound();

    /// <summary>The channel key, read from the radio, for sharing the channel with others.</summary>
    [HttpGet("{index:int}/secret")]
    [Authorize(AuthPolicies.Admin)]
    public async Task<ActionResult<ChannelSecretResponse>> Secret(byte index, CancellationToken ct)
    {
        var channel = await radio.GetChannelFromRadioAsync(index, ct);
        return channel.IsEmpty ? NotFound() : new ChannelSecretResponse(channel.Index, channel.Name, Convert.ToHexStringLower(channel.Secret.Span));
    }

    /// <summary>Creates or replaces a channel slot. Without a secret, a random key is generated.</summary>
    [HttpPut("{index:int}")]
    [Authorize(AuthPolicies.Admin)]
    public async Task<ActionResult<ChannelResponse>> Set(byte index, SetChannelRequest request, CancellationToken ct)
    {
        var secret = request.SecretHex is { } hex ? Convert.FromHexString(hex) : RandomNumberGenerator.GetBytes(16);
        await radio.SetChannelAsync(index, request.Name, secret, ct);
        return await Get(index, ct);
    }

    [HttpDelete("{index:int}")]
    [Authorize(AuthPolicies.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(byte index, CancellationToken ct)
    {
        await radio.DeleteChannelAsync(index, ct);
        return NoContent();
    }
}

/// <summary>Message history (from the local database) and sending.</summary>
[ApiController]
[Route("api/v1/messages")]
[Tags("Messages")]
public sealed class MessagesController(MeshRadio radio) : ControllerBase
{
    /// <summary>Newest first. Filter by <paramref name="contact"/> (key prefix) or <paramref name="channel"/>;
    /// page back with <paramref name="before"/> = the oldest id you have.</summary>
    [HttpGet]
    public async Task<IEnumerable<MessageResponse>> List(
        [FromQuery] string? contact, [FromQuery] byte? channel, [FromQuery] long? before, [FromQuery] int take = 50, CancellationToken ct = default)
    {
        var prefix = contact is { Length: >= 12 } ? contact[..12].ToLowerInvariant() : contact;
        return (await radio.Store.GetMessagesAsync(prefix, channel, before, take, ct)).Select(MessageResponse.From);
    }

    /// <summary>Sends a direct message. Watch its status for Delivered (ACK) or NotConfirmed.</summary>
    [HttpPost("direct")]
    [ProducesResponseType<MessageResponse>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> SendDirect(SendDirectRequest request, CancellationToken ct) =>
        Accepted(MessageResponse.From(await radio.SendDirectAsync(request.Contact, request.Text, User.UserId(), ct)));

    /// <summary>Sends to a channel. The limit is 160 bytes minus our node name and ": ".</summary>
    [HttpPost("channel")]
    [ProducesResponseType<MessageResponse>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> SendChannel(SendChannelRequest request, CancellationToken ct) =>
        Accepted(MessageResponse.From(await radio.SendChannelAsync(request.Channel, request.Text, User.UserId(), ct)));

    /// <summary>Fetches queued messages from the radio now (normally automatic).</summary>
    [HttpPost("sync")]
    public async Task<SyncResponse> Sync(CancellationToken ct) => new(await radio.SyncMessagesAsync(ct));
}

/// <summary>The chat list: every channel, plus every contact with direct-message history, most recent first.</summary>
[ApiController]
[Route("api/v1/conversations")]
[Tags("Messages")]
public sealed class ConversationsController(MeshRadio radio) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<ConversationResponse>> List(CancellationToken ct)
    {
        var latest = await radio.Store.GetLatestPerConversationAsync(ct);
        var channels = await radio.Store.GetChannelsAsync(ct);
        var contacts = (await radio.Store.GetContactsAsync(includeRemoved: true, ct)).ToDictionary(c => c.KeyPrefix);

        var channelChats = channels.Select(c => new ConversationResponse(
            Data.MessageKind.Channel, null, c.Index, c.Name,
            latest.FirstOrDefault(m => m.Kind == Data.MessageKind.Channel && m.ChannelIndex == c.Index) is { } m ? MessageResponse.From(m) : null));
        var directChats = latest.Where(m => m.Kind == Data.MessageKind.Direct && m.ContactKeyPrefix is not null).Select(m => new ConversationResponse(
            Data.MessageKind.Direct, m.ContactKeyPrefix, null,
            contacts.TryGetValue(m.ContactKeyPrefix!, out var c) ? c.Name : m.ContactKeyPrefix!, MessageResponse.From(m)));

        return channelChats.Concat(directChats)
            .OrderByDescending(c => c.LastMessage?.Id ?? 0)
            .ThenBy(c => c.ChannelIndex ?? 255);
    }
}
