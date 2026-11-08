using MeshCore.Client;
using MeshCore.Data;
using MeshCore.Protocol;
using MeshCore.Protocol.Models;
using Microsoft.Extensions.Options;

namespace MeshCore.Host.Radio;

/// <summary>Battery history (<c>BatteryLog</c> in appsettings.json).</summary>
public sealed class BatteryLogOptions
{
    public const string SectionName = "BatteryLog";

    public bool Enabled { get; set; } = true;

    /// <summary>How often each device is checked.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How long readings are kept.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(90);

    /// <summary>Pause between asking two devices, to spread the airtime.</summary>
    public TimeSpan DelayBetweenDevices { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Once per <see cref="BatteryLogOptions.Interval"/>: reads our own radio's battery (no airtime) and asks every contact
/// marked "my device" for its battery over the mesh (a telemetry request; for repeaters and room servers you're logged
/// in to, a status request as fallback). A device that doesn't answer is stored as "no answer", so the history shows
/// whether a device went quiet because its battery ran down or for some other reason.
/// The schedule follows the last stored check, so restarting the app doesn't cause extra requests.
/// </summary>
public sealed partial class BatteryLogService(
    MeshRadio radio, MeshStore store, IOptions<BatteryLogOptions> options, TimeProvider time, ILogger<BatteryLogService> logger)
    : BackgroundService
{
    private readonly SemaphoreSlim _running = new(1, 1);

    /// <summary>When the last round finished (null: none yet since start).</summary>
    public DateTimeOffset? LastRun { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.Enabled)
        {
            return;
        }
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), time, stoppingToken); // let the radio connect and sync first
            while (!stoppingToken.IsCancellationRequested)
            {
                var last = (await store.GetLatestBatteryReadingsAsync(stoppingToken)).GetValueOrDefault(BatteryReadingEntity.Self).Latest?.At;
                var wait = last is { } l ? l + o.Interval - time.GetUtcNow() : TimeSpan.Zero;
                if (wait > TimeSpan.Zero)
                {
                    // Check again at least every minute, so a manual check or a changed clock is picked up.
                    await Task.Delay(wait < TimeSpan.FromMinutes(1) ? wait : TimeSpan.FromMinutes(1), time, stoppingToken);
                    continue;
                }
                await RunOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>Checks every device now (also used by the "Check now" button). Waits if a round is already running.</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        await _running.WaitAsync(cancellationToken);
        try
        {
            var o = options.Value;
            if (!radio.Status.IsConnected)
            {
                // Without the radio nothing else can be asked; record that the radio itself was unreachable.
                await store.AddBatteryReadingAsync(BatteryReadingEntity.Self, null, "not connected", cancellationToken);
                return;
            }

            await CheckSelfAsync(cancellationToken);
            var mine = (await store.GetContactsAsync(cancellationToken: cancellationToken)).Where(c => c.IsMine).ToList();
            foreach (var contact in mine)
            {
                await Task.Delay(o.DelayBetweenDevices, time, cancellationToken);
                if (!radio.Status.IsConnected)
                {
                    break;
                }
                await CheckContactAsync(contact, cancellationToken);
            }

            var pruned = await store.PruneBatteryReadingsAsync(time.GetUtcNow() - o.Retention, cancellationToken);
            LogRound(mine.Count, pruned);
        }
        finally
        {
            LastRun = time.GetUtcNow();
            _running.Release();
        }
    }

    private async Task CheckSelfAsync(CancellationToken ct)
    {
        try
        {
            var battery = await radio.GetBatteryAsync(ct);
            await store.AddBatteryReadingAsync(BatteryReadingEntity.Self, battery.BatteryMillivolts, "radio", ct);
        }
        catch (Exception ex) when (ex is MeshCoreException or InvalidOperationException)
        {
            await store.AddBatteryReadingAsync(BatteryReadingEntity.Self, null, Reason(ex), ct);
        }
    }

    private async Task CheckContactAsync(ContactEntity contact, CancellationToken ct)
    {
        var key = PublicKey.Parse(contact.PublicKey);
        string note;
        try
        {
            var telemetry = await radio.RequestTelemetryAsync(key, ct);
            if (VoltageOf(telemetry.Readings) is { } mv)
            {
                await store.AddBatteryReadingAsync(contact.PublicKey, mv, "telemetry", ct);
                return;
            }
            note = "no voltage in telemetry";
        }
        catch (Exception ex) when (ex is MeshCoreException or InvalidOperationException)
        {
            note = Reason(ex);
        }

        if (contact.Type is AdvertType.Repeater or AdvertType.Room)
        {
            try
            {
                // Works when we're logged in to it (Contacts → the repeater → Log in).
                var status = await radio.RequestStatusAsync(key, ct);
                if (status.Stats is { BatteryMillivolts: > 0 } stats)
                {
                    await store.AddBatteryReadingAsync(contact.PublicKey, stats.BatteryMillivolts, "status", ct);
                    return;
                }
            }
            catch (Exception ex) when (ex is MeshCoreException or InvalidOperationException)
            {
                // Keep the telemetry reason: it's the main way.
            }
        }
        LogNoAnswer(contact.Name, note);
        await store.AddBatteryReadingAsync(contact.PublicKey, null, note, ct);
    }

    /// <summary>The node's own voltage: LPP type 116 on channel 1 (MeshCore's "self" channel), else the first one.</summary>
    internal static int? VoltageOf(IReadOnlyList<LppReading> readings)
    {
        var voltage = readings.FirstOrDefault(r => r.Type == 116 && r.Channel == 1) ?? readings.FirstOrDefault(r => r.Type == 116);
        return voltage is { Values.Count: > 0 } v && v.Values[0] > 0 ? (int)Math.Round(v.Values[0] * 1000) : null;
    }

    private static string Reason(Exception ex) => ex switch
    {
        MeshCoreTimeoutException => "no answer",
        MeshCoreDisconnectedException => "not connected",
        MeshCoreCommandException c => $"refused ({c.Error})",
        _ => "error",
    };

    public override void Dispose()
    {
        _running.Dispose();
        base.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Battery check done: own radio + {Devices} device(s); {Pruned} old reading(s) removed")]
    private partial void LogRound(int devices, int pruned);

    [LoggerMessage(Level = LogLevel.Information, Message = "Battery check: {Name} didn't answer ({Reason})")]
    private partial void LogNoAnswer(string name, string reason);
}
