using MeshCore.Data;
using MeshCore.Host.Auth;
using MeshCore.Host.Radio;
using MeshCore.Protocol;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace MeshCore.Host.Api;

/// <param name="Millivolts">Null when the device didn't answer.</param>
/// <param name="Note">How it was read (<c>radio</c>, <c>telemetry</c>, <c>status</c>) or why not (<c>no answer</c>, …).</param>
public sealed record BatteryReadingResponse(DateTimeOffset At, int? Millivolts, double? Volts, int? Percent, string? Note)
{
    public static BatteryReadingResponse From(BatteryReadingEntity r) =>
        new(r.At, r.Millivolts, r.Millivolts / 1000.0, r.Millivolts is { } mv ? BatteryLevel.Percent(mv) : null, r.Note);
}

/// <param name="Node"><c>self</c> for this radio, else the contact's public key.</param>
/// <param name="Latest">The last check, answered or not.</param>
/// <param name="LastAnswer">The last check that got a battery value.</param>
public sealed record MyDeviceResponse(
    string Node, string Name, AdvertType Type, bool IsSelf, BatteryReadingResponse? Latest, BatteryReadingResponse? LastAnswer);

public sealed record BatteryHistoryResponse(MyDeviceResponse Device, DateTimeOffset Since, IReadOnlyList<BatteryReadingResponse> Readings, TimeSpan Interval);

public sealed record SetMineRequest(bool Mine);

/// <summary>
/// Battery history of "my devices": this radio plus the contacts marked as yours, checked once an hour.
/// A check without an answer is kept too, so you can tell a flat battery from a device that's gone for another reason.
/// </summary>
[ApiController]
[Route("api/v1")]
[Tags("Battery")]
public sealed class BatteryController(MeshStore store, MeshRadio radio, BatteryLogService logger, IOptions<BatteryLogOptions> options,
    IHostApplicationLifetime lifetime, TimeProvider time) : ControllerBase
{
    /// <summary>Marks a contact as "my device" (its battery gets logged) or not. Each check is one small packet per hour.</summary>
    [HttpPut("contacts/{key}/mine"), Authorize(AuthPolicies.Admin)]
    public async Task<ActionResult<ContactResponse>> SetMine(string key, SetMineRequest request, CancellationToken ct)
    {
        if (!await store.SetMineAsync(key, request.Mine, ct))
        {
            return NotFound();
        }
        var contacts = await store.GetContactsAsync(includeRemoved: true, ct);
        return ContactResponse.From(contacts.First(c => string.Equals(c.PublicKey, key, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>This radio and every contact marked as yours, with their latest battery check.</summary>
    [HttpGet("battery")]
    public async Task<IEnumerable<MyDeviceResponse>> Devices(CancellationToken ct)
    {
        var latest = await store.GetLatestBatteryReadingsAsync(ct);
        var contacts = await store.GetContactsAsync(includeRemoved: true, ct);
        return [Self(latest), .. contacts.Where(c => c.IsMine).Select(c => Make(c.PublicKey, c.Name, c.Type, false, latest))];
    }

    /// <summary>One device's checks over the last <paramref name="days"/> days (1–90), oldest first.</summary>
    [HttpGet("battery/{node}")]
    public async Task<ActionResult<BatteryHistoryResponse>> History(string node, [FromQuery] int days = 7, CancellationToken ct = default)
    {
        var latest = await store.GetLatestBatteryReadingsAsync(ct);
        MyDeviceResponse? device;
        if (string.Equals(node, BatteryReadingEntity.Self, StringComparison.OrdinalIgnoreCase))
        {
            device = Self(latest);
        }
        else
        {
            var key = node.ToLowerInvariant();
            var contact = (await store.GetContactsAsync(includeRemoved: true, ct)).FirstOrDefault(c => c.PublicKey == key);
            device = contact is null ? null : Make(contact.PublicKey, contact.Name, contact.Type, false, latest);
        }
        if (device is null)
        {
            return NotFound();
        }
        var since = time.GetUtcNow() - TimeSpan.FromDays(Math.Clamp(days, 1, 90));
        var readings = await store.GetBatteryReadingsAsync(device.Node, since, ct);
        return new BatteryHistoryResponse(device, since, [.. readings.Select(BatteryReadingResponse.From)], options.Value.Interval);
    }

    /// <summary>Checks every device now instead of waiting for the next hour. Runs in the background; a few seconds per device.</summary>
    [HttpPost("battery/check"), Authorize(AuthPolicies.Admin)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public IActionResult CheckNow()
    {
        var stopping = lifetime.ApplicationStopping;
        _ = Task.Run(() => logger.RunOnceAsync(stopping), stopping);
        return Accepted();
    }

    private MyDeviceResponse Self(IReadOnlyDictionary<string, (BatteryReadingEntity Latest, BatteryReadingEntity? LastAnswer)> latest)
    {
        var self = radio.Status.Self;
        return Make(BatteryReadingEntity.Self, self?.Name ?? "This radio", self?.Type ?? AdvertType.Chat, true, latest);
    }

    private static MyDeviceResponse Make(string node, string name, AdvertType type, bool isSelf,
        IReadOnlyDictionary<string, (BatteryReadingEntity Latest, BatteryReadingEntity? LastAnswer)> latest) =>
        latest.TryGetValue(node, out var l)
            ? new(node, name, type, isSelf, BatteryReadingResponse.From(l.Latest), l.LastAnswer is { } a ? BatteryReadingResponse.From(a) : null)
            : new(node, name, type, isSelf, null, null);
}
