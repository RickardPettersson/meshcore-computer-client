using MeshCore.Host.Auth;
using MeshCore.Host.Radio;
using MeshCore.Protocol.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeshCore.Host.Api;

/// <summary>Node settings and identity. Changes are Admin only.</summary>
[ApiController]
[Route("api/v1/device")]
[Tags("Device")]
public sealed class DeviceSettingsController(MeshRadio radio) : ControllerBase
{
    [HttpPut("name"), Authorize(AuthPolicies.Admin)]
    public async Task<DeviceResponse> SetName(SetNameRequest request, CancellationToken ct)
    {
        await radio.SetNodeNameAsync(request.Name.Trim(), ct);
        return DeviceResponse.From(radio.Status);
    }

    [HttpPut("location"), Authorize(AuthPolicies.Admin)]
    public async Task<DeviceResponse> SetLocation(SetLocationRequest request, CancellationToken ct)
    {
        await radio.SetLocationAsync(request.Latitude, request.Longitude, ct);
        return DeviceResponse.From(radio.Status);
    }

    /// <summary>Changes frequency, bandwidth, SF and CR. All nodes must match, so this can cut the radio off from the mesh.</summary>
    [HttpPut("radio"), Authorize(AuthPolicies.Admin)]
    public async Task<DeviceResponse> SetRadio(SetRadioRequest request, CancellationToken ct)
    {
        await radio.SetRadioParamsAsync(
            (uint)Math.Round(request.FrequencyMhz * 1000), (uint)Math.Round(request.BandwidthKhz * 1000),
            (byte)request.SpreadingFactor, (byte)request.CodingRate, ct);
        return DeviceResponse.From(radio.Status);
    }

    [HttpPut("tx-power"), Authorize(AuthPolicies.Admin)]
    public async Task<DeviceResponse> SetTxPower(SetTxPowerRequest request, CancellationToken ct)
    {
        await radio.SetTxPowerAsync((sbyte)request.Dbm, ct);
        return DeviceResponse.From(radio.Status);
    }

    [HttpGet("tuning")]
    public async Task<TuningResponse> Tuning(CancellationToken ct)
    {
        var t = await radio.GetTuningParamsAsync(ct);
        return new TuningResponse(t.RxDelayBase, t.AirtimeFactor);
    }

    [HttpPut("tuning"), Authorize(AuthPolicies.Admin)]
    public async Task<TuningResponse> SetTuning(SetTuningRequest request, CancellationToken ct)
    {
        await radio.SetTuningParamsAsync(request.RxDelayBase, request.AirtimeFactor, ct);
        return await Tuning(ct);
    }

    [HttpPut("preferences"), Authorize(AuthPolicies.Admin)]
    public async Task<DeviceResponse> SetPreferences(SetPreferencesRequest request, CancellationToken ct)
    {
        await radio.SetOtherParamsAsync(request.ManualAddContacts, (byte)request.TelemetryModes, (byte)request.AdvertLocationPolicy, (byte)request.MultiAcks, ct);
        return DeviceResponse.From(radio.Status);
    }

    /// <summary>Our own signed advert ("contact card"), for others to import.</summary>
    [HttpGet("contact-card")]
    public async Task<ContactCardResponse> ContactCard(CancellationToken ct) => new(await radio.ExportContactAsync(null, ct));

    /// <summary>The node's private key. Anyone holding it can impersonate this node. 501 on firmware built without key export.</summary>
    [HttpGet("private-key"), Authorize(AuthPolicies.Admin)]
    public async Task<PrivateKeyResponse> PrivateKey(CancellationToken ct) => new(await radio.ExportPrivateKeyAsync(ct));

    /// <summary>Replaces the node's identity (its public key changes for everyone). 501 on firmware built without key import.</summary>
    [HttpPut("private-key"), Authorize(AuthPolicies.Admin)]
    public async Task<DeviceResponse> ImportPrivateKey(ImportPrivateKeyRequest request, CancellationToken ct)
    {
        await radio.ImportPrivateKeyAsync(request.PrivateKeyHex, ct);
        return DeviceResponse.From(radio.Status);
    }
}

/// <summary>Contact management, and talking to repeaters and room servers.</summary>
[ApiController]
[Route("api/v1/contacts")]
[Tags("Contacts")]
public sealed class ContactActionsController(MeshRadio radio, TimeProvider time) : ControllerBase
{
    /// <summary>Nodes heard advertising that aren't contacts yet (only when "add contacts manually" is on).</summary>
    [HttpGet("discovered")]
    public IEnumerable<DiscoveredNodeResponse> Discovered() =>
        radio.Discovered.Select(d => DiscoveredNodeResponse.From(d.Contact, d.HeardAt));

    [HttpPost("discovered/{key}"), Authorize(AuthPolicies.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> AddDiscovered(string key, CancellationToken ct)
    {
        await radio.AddDiscoveredAsync(PublicKey.Parse(key), ct);
        return NoContent();
    }

    [HttpPost("import"), Authorize(AuthPolicies.Admin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Import(ImportContactRequest request, CancellationToken ct)
    {
        await radio.ImportContactAsync(request.AdvertHex, ct);
        return NoContent();
    }

    [HttpPut("{key}/favourite"), Authorize(AuthPolicies.Admin)]
    public async Task<ActionResult<ContactResponse>> SetFavourite(string key, SetFavouriteRequest request, CancellationToken ct) =>
        await WithContactAsync(key, async k => await radio.SetFavouriteAsync(k, request.Favourite, ct), ct);

    /// <summary>Renames the contact on this radio only.</summary>
    [HttpPut("{key}/name"), Authorize(AuthPolicies.Admin)]
    public async Task<ActionResult<ContactResponse>> Rename(string key, RenameContactRequest request, CancellationToken ct) =>
        await WithContactAsync(key, async k => await radio.RenameContactAsync(k, request.Name, ct), ct);

    /// <summary>Re-broadcasts this contact's advert to nearby nodes so they can add it too.</summary>
    [HttpPost("{key}/share")]
    public async Task<ActionResult<ContactResponse>> Share(string key, CancellationToken ct) =>
        await WithContactAsync(key, async k => await radio.ShareContactAsync(k, ct), ct);

    [HttpGet("{key}/export")]
    public async Task<ActionResult<ContactCardResponse>> Export(string key, CancellationToken ct) =>
        await FindKeyAsync(key, ct) is { } k ? new ContactCardResponse(await radio.ExportContactAsync(k, ct)) : NotFound();

    /// <summary>Logs in to a repeater or room server (blank password for guest access, if it allows it).</summary>
    [HttpPost("{key}/login")]
    public async Task<ActionResult<LoginResponse>> Login(string key, LoginRequest request, CancellationToken ct) =>
        await FindKeyAsync(key, ct) is { } k ? LoginResponse.From(await radio.LoginAsync(k, request.Password ?? "", ct)) : NotFound();

    [HttpPost("{key}/logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(string key, CancellationToken ct)
    {
        if (await FindKeyAsync(key, ct) is not { } k)
        {
            return NotFound();
        }
        await radio.LogoutAsync(k, ct);
        return NoContent();
    }

    [HttpPost("{key}/status")]
    public async Task<ActionResult<StatusResponse>> Status(string key, CancellationToken ct)
    {
        if (await FindKeyAsync(key, ct) is not { } k)
        {
            return NotFound();
        }
        var s = await radio.RequestStatusAsync(k, ct);
        return new StatusResponse(s.Stats is { } stats ? RepeaterStatsResponse.From(stats) : null, Convert.ToHexStringLower(s.Raw.Span));
    }

    [HttpPost("{key}/telemetry")]
    public async Task<ActionResult<TelemetryResponse>> Telemetry(string key, CancellationToken ct)
    {
        if (await FindKeyAsync(key, ct) is not { } k)
        {
            return NotFound();
        }
        var t = await radio.RequestTelemetryAsync(k, ct);
        return new TelemetryResponse([.. t.Readings.Select(TelemetryReadingResponse.From)], Convert.ToHexStringLower(t.Raw.Span));
    }

    /// <summary>Traces the route to the node and back, with the SNR at every hop. Needs a learned route.</summary>
    [HttpPost("{key}/trace")]
    public async Task<ActionResult<TraceResponse>> Trace(string key, CancellationToken ct) =>
        await FindKeyAsync(key, ct) is { } k ? TraceResponse.From(await radio.TraceAsync(k, time, ct)) : NotFound();

    /// <summary>Runs a CLI command on a repeater you're logged in to as admin. The reply arrives as a message in that conversation.</summary>
    [HttpPost("{key}/cli"), Authorize(AuthPolicies.Admin)]
    [ProducesResponseType<MessageResponse>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Cli(string key, CliCommandRequest request, CancellationToken ct) =>
        await FindKeyAsync(key, ct) is { } k
            ? Accepted(MessageResponse.From(await radio.SendCliCommandAsync(k, request.Command, User.UserId(), ct)))
            : NotFound();

    private async Task<ActionResult<ContactResponse>> WithContactAsync(string key, Func<PublicKey, Task> action, CancellationToken ct)
    {
        if (await FindKeyAsync(key, ct) is not { } k)
        {
            return NotFound();
        }
        await action(k);
        var updated = (await radio.Store.GetContactsAsync(includeRemoved: true, ct)).First(c => c.PublicKey == k.Hex);
        return ContactResponse.From(updated);
    }

    private async Task<PublicKey?> FindKeyAsync(string key, CancellationToken ct)
    {
        if (key.Length < 12 || !key.All(Uri.IsHexDigit))
        {
            return null;
        }
        var matches = (await radio.Store.GetContactsAsync(includeRemoved: false, ct))
            .Where(c => c.PublicKey.StartsWith(key.ToLowerInvariant(), StringComparison.Ordinal)).ToList();
        return matches.Count == 1 ? PublicKey.Parse(matches[0].PublicKey) : null;
    }
}
