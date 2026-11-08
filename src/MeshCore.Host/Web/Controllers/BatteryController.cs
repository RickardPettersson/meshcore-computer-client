using MeshCore.Host.Api;
using MeshCore.Host.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeshCore.Host.Web.Controllers;

public sealed record BatteryDevicesViewModel(IReadOnlyList<MyDeviceResponse> Devices, bool CanCheck);

public sealed record BatteryHistoryViewModel(BatteryHistoryResponse History, int Days, DateTimeOffset Now)
{
    public static readonly int[] Ranges = [1, 7, 30, 90];

    public IReadOnlyList<BatteryReadingResponse> Answered => [.. History.Readings.Where(r => r.Millivolts is not null)];

    public int Missed => History.Readings.Count(r => r.Millivolts is null);
}

/// <summary>Battery history of "my devices": this radio plus contacts marked as yours (via the API).</summary>
public sealed class BatteryController(MeshApiClient api, TimeProvider time) : WebController
{
    public async Task<IActionResult> Index(CancellationToken ct) =>
        View(new BatteryDevicesViewModel(await api.MyDevicesAsync(ct), IsAdmin));

    public async Task<IActionResult> History(string? node, int days = 7, CancellationToken ct = default)
    {
        days = BatteryHistoryViewModel.Ranges.Contains(days) ? days : 7;
        try
        {
            return View(new BatteryHistoryViewModel(await api.BatteryHistoryAsync(node ?? "self", days, ct), days, time.GetUtcNow()));
        }
        catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
        {
            return NotFound();
        }
    }

    [HttpPost, Authorize(AuthPolicies.Admin)]
    public async Task<IActionResult> Check(CancellationToken ct)
    {
        await api.CheckBatteriesNowAsync(ct);
        Flash("Checking now: a few seconds per device. Reload the page in a moment to see the new readings.");
        return RedirectBack(Url.Action(nameof(Index))!);
    }
}
