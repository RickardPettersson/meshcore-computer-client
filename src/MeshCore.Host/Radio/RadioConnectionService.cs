using System.Net.Sockets;
using MeshCore.Client;
using MeshCore.Client.Discovery;
using MeshCore.Client.Transport;
using Microsoft.Extensions.Options;

namespace MeshCore.Host.Radio;

/// <summary>
/// Keeps the app connected to the radio: connects, hands the client to <see cref="MeshRadio"/>,
/// polls as a safety net, and reconnects with backoff when the link drops.
/// </summary>
public sealed partial class RadioConnectionService(
    MeshRadio radio,
    IOptions<RadioOptions> options,
    ILoggerFactory loggerFactory,
    TimeProvider time,
    ILogger<RadioConnectionService> logger) : BackgroundService
{
    private readonly RadioOptions _options = options.Value;

    /// <summary>Overridable in tests to supply a fake radio instead of a real transport.</summary>
    internal Func<MeshCoreEndpoint, ITransport> TransportFactory { get; set; } = e => e.CreateTransport(loggerFactory);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        MeshCoreEndpoint? endpoint = null;
        if (!string.Equals(_options.Connection, "auto", StringComparison.OrdinalIgnoreCase))
        {
            endpoint = string.Equals(_options.Connection, SimulatorEndpoint.Name, StringComparison.OrdinalIgnoreCase)
                ? new SimulatorEndpoint(_options.SimulatorMessageInterval)
                : MeshCoreEndpoint.Parse(_options.Connection);
        }
        var description = endpoint?.ToString() ?? "USB (auto-detect)";
        var failures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            radio.SetStatus(new RadioStatus(RadioState.Connecting, description));
            var outcome = await RunOnceAsync(endpoint, description, stoppingToken);
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            failures = outcome.Stable ? 0 : failures + 1;
            var (state, delay) = outcome.TakenOver
                ? (RadioState.InUseElsewhere, _options.TakeoverBackoff)
                : (RadioState.WaitingToRetry, _options.ReconnectDelays[Math.Min(Math.Max(failures, 1), _options.ReconnectDelays.Length) - 1]);

            var next = time.GetUtcNow() + delay;
            radio.SetStatus(new RadioStatus(state, description, LastError: outcome.Error, NextAttempt: next));
            LogRetry(state, description, delay, outcome.Error);
            try
            {
                await Task.Delay(delay, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        radio.SetStatus(new RadioStatus(RadioState.Stopped, description));
    }

    private sealed record Outcome(bool Stable, bool TakenOver, string? Error);

    private async Task<Outcome> RunOnceAsync(MeshCoreEndpoint? endpoint, string description, CancellationToken stoppingToken)
    {
        var clientOptions = new MeshCoreClientOptions { AppName = _options.AppName, TimeProvider = time };
        MeshCoreClient? client = null;
        var connectedAt = time.GetUtcNow();
        try
        {
            if (endpoint is null)
            {
                var candidates = CompanionLocator.Candidates(SerialPortDiscovery.GetPorts());
                var found = await new CompanionLocator(clientOptions, loggerFactory).FindAsync(candidates, stoppingToken);
                if (found is null)
                {
                    return new Outcome(false, false, "No companion radio found on USB.");
                }
                client = found.Client;
                description = found.Port.PortName;
            }
            else
            {
                client = new MeshCoreClient(TransportFactory(endpoint), clientOptions, loggerFactory.CreateLogger<MeshCoreClient>());
                await client.ConnectAsync(stoppingToken);
            }

            var lost = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Disconnected += (_, e) => lost.TrySetResult(e.Error);
            if (!client.IsConnected)
            {
                lost.TrySetResult(null); // Dropped between connect and subscribing.
            }
            connectedAt = time.GetUtcNow();
            await radio.AttachAsync(client, description, connectedAt, stoppingToken);

            // Stay here until the link drops, polling now and then in case a push was missed.
            using var poll = new PeriodicTimer(_options.PollInterval, time);
            var tick = poll.WaitForNextTickAsync(stoppingToken).AsTask();
            while (true)
            {
                var done = await Task.WhenAny(lost.Task, tick);
                if (done == lost.Task)
                {
                    var error = await lost.Task;
                    return Classify(error, connectedAt);
                }

                await tick; // Throws when stopping.
                await radio.ExpireAcksAsync(stoppingToken);
                await radio.SyncMessagesAsync(stoppingToken);
                tick = poll.WaitForNextTickAsync(stoppingToken).AsTask();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return new Outcome(true, false, null);
        }
        catch (Exception ex) when (ex is MeshCoreException or IOException or SocketException or UnauthorizedAccessException or FormatException)
        {
            return Classify(ex, connectedAt);
        }
        finally
        {
            if (client is not null)
            {
                radio.Detach(client);
                await client.DisposeAsync();
            }
        }
    }

    private Outcome Classify(Exception? error, DateTimeOffset connectedAt)
    {
        var stable = time.GetUtcNow() - connectedAt >= _options.StableConnection;
        // A clean close from a WiFi radio means another client connected and took the radio over.
        var takenOver = error is EndOfStreamException
                        || error is MeshCoreDisconnectedException { InnerException: EndOfStreamException };
        var message = takenOver
            ? "The radio closed the connection; another app is probably connected to it."
            : error?.Message;
        return new Outcome(stable, takenOver, message);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Radio {State} on {Endpoint}; retrying in {Delay}. {Error}")]
    private partial void LogRetry(RadioState state, string endpoint, TimeSpan delay, string? error);
}
