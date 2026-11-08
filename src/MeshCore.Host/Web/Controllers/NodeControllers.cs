using System.Net;
using MeshCore.Host.Api;
using MeshCore.Host.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeshCore.Host.Web.Controllers;

/// <summary>Node settings (Admin) and the contact card (everyone).</summary>
public sealed partial class DeviceController
{
    /// <summary>Our own signed advert, for friends to import.</summary>
    public async Task<IActionResult> Card(CancellationToken ct) =>
        View(new ContactCardViewModel((await api.DeviceAsync(ct)).Node?.Name ?? "This node", (await api.ContactCardAsync(ct)).AdvertHex));

    [HttpGet, Authorize(AuthPolicies.Admin)]
    public async Task<IActionResult> Settings(CancellationToken ct) => View(await SettingsModelAsync(null, ct));

    [HttpPost, Authorize(AuthPolicies.Admin)]
    public Task<IActionResult> Name(string name, CancellationToken ct) =>
        SaveAsync(() => api.SetNameAsync(name, ct), $"Node renamed to '{name}'. Send an advert so others see the new name.");

    [HttpPost, Authorize(AuthPolicies.Admin)]
    public Task<IActionResult> Location(double latitude, double longitude, CancellationToken ct) =>
        SaveAsync(() => api.SetLocationAsync(latitude, longitude, ct), "Location saved.");

    [HttpPost, Authorize(AuthPolicies.Admin)]
    public Task<IActionResult> TxPower(int dbm, CancellationToken ct) =>
        SaveAsync(() => api.SetTxPowerAsync(dbm, ct), $"TX power set to {dbm} dBm.");

    [HttpPost, Authorize(AuthPolicies.Admin)]
    public Task<IActionResult> RadioParams(SetRadioRequest request, CancellationToken ct) =>
        SaveAsync(() => api.SetRadioAsync(request, ct),
            $"Radio set to {request.FrequencyMhz:0.000} MHz, BW {request.BandwidthKhz:0.0} kHz, SF{request.SpreadingFactor}, CR{request.CodingRate}.");

    [HttpPost, Authorize(AuthPolicies.Admin)]
    public Task<IActionResult> Preferences(bool manualAddContacts, bool shareLocation, int multiAcks, int telemetryModes, CancellationToken ct) =>
        SaveAsync(() => api.SetPreferencesAsync(new SetPreferencesRequest(manualAddContacts, shareLocation ? 1 : 0, telemetryModes, multiAcks), ct), "Preferences saved.");

    [HttpPost, Authorize(AuthPolicies.Admin)]
    public Task<IActionResult> Tuning(double rxDelayBase, double airtimeFactor, CancellationToken ct) =>
        SaveAsync(() => api.SetTuningAsync(rxDelayBase, airtimeFactor, ct), "Tuning saved.");

    /// <summary>Shows the private key once, on request. Never stored or logged.</summary>
    [HttpPost, Authorize(AuthPolicies.Admin)]
    public async Task<IActionResult> RevealKey(CancellationToken ct)
    {
        try
        {
            return View(nameof(Settings), await SettingsModelAsync((await api.PrivateKeyAsync(ct)).PrivateKeyHex, ct));
        }
        catch (ApiException ex) when (ex.Status == HttpStatusCode.NotImplemented)
        {
            FlashError("This firmware was built without private key export.");
            return RedirectToAction(nameof(Settings));
        }
    }

    [HttpPost, Authorize(AuthPolicies.Admin)]
    public Task<IActionResult> ImportKey(string privateKeyHex, CancellationToken ct) =>
        SaveAsync(() => api.ImportPrivateKeyAsync(privateKeyHex, ct), "Identity replaced. This node now has a new public key; contacts will need to re-add it.");

    private async Task<IActionResult> SaveAsync(Func<Task> save, string success)
    {
        try
        {
            await save();
            Flash(success);
        }
        catch (ApiException ex) when (ex.Status is HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.NotImplemented)
        {
            FlashError(ex.Status == HttpStatusCode.NotImplemented ? "This firmware doesn't support that." : ex.Message);
        }
        return RedirectToAction(nameof(Settings));
    }

    private async Task<NodeSettingsViewModel> SettingsModelAsync(string? privateKey, CancellationToken ct)
    {
        var device = await api.DeviceAsync(ct);
        var tuning = device.State == Radio.RadioState.Connected ? await HomeController.TryAsync(() => api.TuningAsync(ct)) : null;
        return new NodeSettingsViewModel(device, tuning, privateKey);
    }
}

/// <summary>Contact details, contact management, and repeater / room server tools.</summary>
public sealed partial class ContactsController
{
    public async Task<IActionResult> Details(string id, CancellationToken ct) => View(new ContactDetailsViewModel(await api.ContactAsync(id, ct)));

    [HttpPost, Authorize(AuthPolicies.Admin)]
    public async Task<IActionResult> Favourite(string key, bool favourite, CancellationToken ct)
    {
        var c = await api.SetFavouriteAsync(key, favourite, ct);
        Flash(favourite ? $"{c.Name} is a favourite." : $"{c.Name} is no longer a favourite.");
        return RedirectBack(Url.Action(nameof(Details), new { id = key })!);
    }

    /// <summary>"My device": its battery is checked once an hour (Battery page).</summary>
    [HttpPost, Authorize(AuthPolicies.Admin)]
    public async Task<IActionResult> Mine(string key, bool mine, CancellationToken ct)
    {
        var c = await api.SetMineAsync(key, mine, ct);
        Flash(mine
            ? $"{c.Name} is marked as your device. Its battery is checked once an hour; see Battery."
            : $"{c.Name} is no longer marked as your device. Its battery history is kept.");
        return RedirectBack(Url.Action(nameof(Details), new { id = key })!);
    }

    [HttpPost, Authorize(AuthPolicies.Admin)]
    public async Task<IActionResult> Rename(string key, string name, CancellationToken ct)
    {
        var c = await api.RenameContactAsync(key, name, ct);
        Flash($"Renamed to '{c.Name}' on this radio.");
        return RedirectToAction(nameof(Details), new { id = key });
    }

    [HttpPost]
    public async Task<IActionResult> Share(string key, CancellationToken ct)
    {
        var c = await api.ShareContactAsync(key, ct);
        Flash($"{c.Name}'s advert was sent to nearby nodes.");
        return RedirectToAction(nameof(Details), new { id = key });
    }

    [HttpPost]
    public async Task<IActionResult> Export(string key, CancellationToken ct) =>
        await DetailsWithAsync(key, async m => m with { CardHex = (await api.ExportContactAsync(key, ct)).AdvertHex }, ct);

    [HttpPost]
    public async Task<IActionResult> Login(string key, string? password, CancellationToken ct) =>
        await DetailsWithAsync(key, async m => m with { Login = await api.LoginAsync(key, password, ct) }, ct);

    [HttpPost]
    public async Task<IActionResult> Logout(string key, CancellationToken ct)
    {
        await api.LogoutAsync(key, ct);
        Flash("Logged out.");
        return RedirectToAction(nameof(Details), new { id = key });
    }

    [HttpPost]
    public async Task<IActionResult> Status(string key, CancellationToken ct) =>
        await DetailsWithAsync(key, async m => m with { Status = await api.StatusAsync(key, ct) }, ct);

    [HttpPost]
    public async Task<IActionResult> Telemetry(string key, CancellationToken ct) =>
        await DetailsWithAsync(key, async m => m with { Telemetry = await api.TelemetryAsync(key, ct) }, ct);

    [HttpPost]
    public async Task<IActionResult> Trace(string key, CancellationToken ct) =>
        await DetailsWithAsync(key, async m => m with { Trace = await api.TraceAsync(key, ct) }, ct);

    [HttpGet, Authorize(AuthPolicies.Admin)]
    public IActionResult Import() => View();

    [HttpPost, Authorize(AuthPolicies.Admin)]
    public async Task<IActionResult> Import(string advertHex, CancellationToken ct)
    {
        try
        {
            await api.ImportContactAsync(advertHex.Trim(), ct);
            Flash("Contact imported.");
            return RedirectToAction(nameof(Index));
        }
        catch (ApiException ex) when (ex.Status is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
        {
            FlashError(ex.Status == HttpStatusCode.Conflict ? "The radio couldn't verify that contact card." : ex.Message);
            return RedirectToAction(nameof(Import));
        }
    }

    [HttpPost, Authorize(AuthPolicies.Admin)]
    public async Task<IActionResult> AddDiscovered(string key, string name, bool chat, CancellationToken ct)
    {
        await api.AddDiscoveredAsync(key, ct);
        Flash($"{name} added to contacts.");
        return chat ? RedirectToAction(nameof(MessagesController.Direct), "Messages", new { id = key }) : RedirectToAction(nameof(Index));
    }

    /// <summary>Runs a remote request and shows its result on the details page. Slow or failed remote answers
    /// show as a message rather than an error page.</summary>
    private async Task<IActionResult> DetailsWithAsync(string key, Func<ContactDetailsViewModel, Task<ContactDetailsViewModel>> run, CancellationToken ct)
    {
        var model = new ContactDetailsViewModel(await api.ContactAsync(key, ct));
        try
        {
            model = await run(model);
        }
        catch (ApiException ex) when (ex.Status is HttpStatusCode.GatewayTimeout or HttpStatusCode.BadRequest or HttpStatusCode.Conflict
                                          or HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable)
        {
            model = model with { Error = ex.Message };
        }
        return View(nameof(Details), model);
    }
}
