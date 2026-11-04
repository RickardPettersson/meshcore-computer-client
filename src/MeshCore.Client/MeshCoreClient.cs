using System.Threading.Channels;
using MeshCore.Client.Transport;
using MeshCore.Protocol;
using MeshCore.Protocol.Frames;
using MeshCore.Protocol.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshCore.Client;

public sealed class MeshFrameEventArgs(MeshFrame frame) : EventArgs
{
    public MeshFrame Frame { get; } = frame;
}

public sealed class DisconnectedEventArgs(Exception? error) : EventArgs
{
    /// <summary>Why the link dropped; null for a normal close.</summary>
    public Exception? Error { get; } = error;
}

/// <summary>
/// Talks to one MeshCore companion radio. Commands are sent one at a time (the protocol has no
/// request IDs) and matched to their replies; pushes are raised as <see cref="PushReceived"/>.
/// </summary>
/// <remarks>
/// Event handlers run on a dedicated pump, not on the read loop, so a handler may call back into
/// the client (e.g. drain messages on <see cref="MessagesWaitingPush"/>) without deadlocking.
/// </remarks>
public sealed partial class MeshCoreClient : IAsyncDisposable
{
    private const int DefaultChannelCount = 8;
    private const int MaxDrainCount = 1000;

    private readonly ITransport _transport;
    private readonly MeshCoreClientOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _commandLane = new(1, 1);
    private readonly Channel<MeshFrame> _pushes = Channel.CreateUnbounded<MeshFrame>(new() { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _stop = new();
    private PendingCommand? _pending;
    private Task? _readLoop;
    private Task? _pushPump;
    private int _connected;

    public MeshCoreClient(ITransport transport, MeshCoreClientOptions? options = null, ILogger<MeshCoreClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
        _options = options ?? new MeshCoreClientOptions();
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>Unsolicited notifications (adverts, ACKs, messages waiting, …).</summary>
    public event EventHandler<MeshFrameEventArgs>? PushReceived;

    /// <summary>The link closed or failed. Raised once.</summary>
    public event EventHandler<DisconnectedEventArgs>? Disconnected;

    public bool IsConnected => Volatile.Read(ref _connected) == 1;

    public string TransportDescription => _transport.Description;

    /// <summary>From the handshake; refreshed by <see cref="RefreshSelfInfoAsync"/>.</summary>
    public SelfInfo? SelfInfo { get; private set; }

    /// <summary>From the handshake.</summary>
    public DeviceInfo? DeviceInfo { get; private set; }

    /// <summary>Opens the transport and runs the handshake: app start, device query, clock sync.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_stop.IsCancellationRequested, this);
        if (_readLoop is not null)
        {
            throw new InvalidOperationException("Already connected. Create a new client to reconnect.");
        }

        await _transport.OpenAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _connected, 1);
        _readLoop = Task.Run(ReadLoopAsync, CancellationToken.None);
        _pushPump = Task.Run(PushPumpAsync, CancellationToken.None);

        SelfInfo = await AppStartWithRetryAsync(cancellationToken).ConfigureAwait(false);
        DeviceInfo = await ExecuteAsync<DeviceInfo>(Commands.DeviceQuery(), cancellationToken).ConfigureAwait(false);
        LogConnected(SelfInfo.Name, DeviceInfo.Model, DeviceInfo.FirmwareVersion, _transport.Description);

        if (_options.SyncDeviceClock)
        {
            await TrySyncClockAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // ---- Device -------------------------------------------------------------

    public async Task<SelfInfo> RefreshSelfInfoAsync(CancellationToken cancellationToken = default) =>
        SelfInfo = await ExecuteAsync<SelfInfo>(Commands.AppStart(_options.AppName), cancellationToken).ConfigureAwait(false);

    public Task<BatteryAndStorage> GetBatteryAndStorageAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync<BatteryAndStorage>(Commands.GetBatteryAndStorage(), cancellationToken);

    public async Task<DateTimeOffset> GetDeviceTimeAsync(CancellationToken cancellationToken = default) =>
        (await ExecuteAsync<CurrentTime>(Commands.GetDeviceTime(), cancellationToken).ConfigureAwait(false)).Time;

    /// <summary>Sets the radio clock. Throws <see cref="MeshCoreCommandException"/> with
    /// <see cref="ErrorCode.IllegalArgument"/> if <paramref name="time"/> is earlier than the radio's clock.</summary>
    public Task SetDeviceTimeAsync(DateTimeOffset time, CancellationToken cancellationToken = default) =>
        ExecuteAsync<OkResponse>(Commands.SetDeviceTime(time), cancellationToken);

    public Task SendAdvertAsync(bool flood, CancellationToken cancellationToken = default) =>
        ExecuteAsync<OkResponse>(Commands.SendSelfAdvert(flood), cancellationToken);

    public async Task SetAdvertNameAsync(string name, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync<OkResponse>(Commands.SetAdvertName(name), cancellationToken).ConfigureAwait(false);
        await RefreshSelfInfoAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reboots the radio. It sends no reply; the link drops shortly after.</summary>
    public Task RebootAsync(CancellationToken cancellationToken = default) =>
        SendWithoutReplyAsync(Commands.Reboot(), cancellationToken);

    // ---- Contacts -----------------------------------------------------------

    /// <summary>All contacts, or only those changed after <paramref name="since"/>.</summary>
    public async Task<IReadOnlyList<Contact>> GetContactsAsync(DateTimeOffset? since = null, CancellationToken cancellationToken = default)
    {
        var collector = new ContactListCommand();
        await ExecuteAsync(Commands.GetContacts(since), collector, _options.ContactsTimeout, cancellationToken).ConfigureAwait(false);
        return collector.Contacts;
    }

    public Task RemoveContactAsync(PublicKey key, CancellationToken cancellationToken = default) =>
        ExecuteAsync<OkResponse>(Commands.RemoveContact(key), cancellationToken);

    public Task ResetPathAsync(PublicKey key, CancellationToken cancellationToken = default) =>
        ExecuteAsync<OkResponse>(Commands.ResetPath(key), cancellationToken);

    // ---- Channels -----------------------------------------------------------

    public Task<ChannelInfo> GetChannelAsync(byte index, CancellationToken cancellationToken = default) =>
        ExecuteAsync<ChannelInfo>(Commands.GetChannel(index), cancellationToken);

    /// <summary>Reads every channel slot the radio has (count from <see cref="DeviceInfo"/>).</summary>
    public async Task<IReadOnlyList<ChannelInfo>> GetChannelsAsync(bool includeEmpty = false, CancellationToken cancellationToken = default)
    {
        var count = DeviceInfo?.MaxChannels ?? DefaultChannelCount;
        var channels = new List<ChannelInfo>(count);
        for (var i = 0; i < count; i++)
        {
            ChannelInfo channel;
            try
            {
                channel = await GetChannelAsync((byte)i, cancellationToken).ConfigureAwait(false);
            }
            catch (MeshCoreCommandException ex) when (ex.Error == ErrorCode.NotFound)
            {
                break; // Past the last slot on firmware that reports fewer channels than expected.
            }

            if (includeEmpty || !channel.IsEmpty)
            {
                channels.Add(channel);
            }
        }
        return channels;
    }

    public Task SetChannelAsync(byte index, string name, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken = default) =>
        ExecuteAsync<OkResponse>(Commands.SetChannel(index, name, secret.Span), cancellationToken);

    // ---- Messages -----------------------------------------------------------

    /// <summary>Sends a direct message. Match <see cref="MessageSent.ExpectedAck"/> against a later
    /// <see cref="SendConfirmedPush"/> to know it was delivered.</summary>
    public Task<MessageSent> SendTextMessageAsync(PublicKeyPrefix recipient, string text, byte attempt = 0, CancellationToken cancellationToken = default) =>
        ExecuteAsync<MessageSent>(Commands.SendTextMessage(recipient, text, Now, attempt), cancellationToken);

    /// <summary>Sends to a group channel. Channel messages are never acknowledged.</summary>
    public Task SendChannelTextMessageAsync(byte channelIndex, string text, CancellationToken cancellationToken = default) =>
        ExecuteAsync<OkResponse>(Commands.SendChannelTextMessage(channelIndex, text, Now), cancellationToken);

    /// <summary>
    /// Pops the next queued message (<see cref="ContactMessage"/>, <see cref="ChannelMessage"/>, or another
    /// frame type newer firmware may add), or null when the queue is empty.
    /// The radio forgets the message once it's returned, so store it.
    /// </summary>
    public async Task<MeshFrame?> SyncNextMessageAsync(CancellationToken cancellationToken = default)
    {
        var frame = await ExecuteAsync(Commands.SyncNextMessage(), new AnyResponseCommand(), _options.CommandTimeout, cancellationToken).ConfigureAwait(false);
        return frame is NoMoreMessages ? null : frame;
    }

    /// <summary>Pops queued messages until the radio's queue is empty.</summary>
    public async Task<IReadOnlyList<MeshFrame>> DrainMessagesAsync(CancellationToken cancellationToken = default)
    {
        var messages = new List<MeshFrame>();
        while (messages.Count < MaxDrainCount && await SyncNextMessageAsync(cancellationToken).ConfigureAwait(false) is { } message)
        {
            messages.Add(message);
        }
        return messages;
    }

    // ---- Command plumbing ---------------------------------------------------

    private DateTimeOffset Now => _options.TimeProvider.GetUtcNow();

    private async Task<T> ExecuteAsync<T>(byte[] command, CancellationToken cancellationToken) where T : MeshFrame =>
        (T)await ExecuteAsync(command, new SingleResponseCommand<T>(), _options.CommandTimeout, cancellationToken).ConfigureAwait(false);

    private async Task<MeshFrame> ExecuteAsync(byte[] command, PendingCommand pending, TimeSpan timeout, CancellationToken cancellationToken)
    {
        EnsureConnected();
        pending.Command = (CommandCode)command[0];

        await _commandLane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            Volatile.Write(ref _pending, pending);
            LogSending(pending.Command, command.Length);
            await _transport.SendAsync(command, cancellationToken).ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            try
            {
                return await pending.Completion.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new MeshCoreTimeoutException($"No reply to {pending.Command} within {timeout.TotalSeconds:0.#} s.");
            }
        }
        finally
        {
            Volatile.Write(ref _pending, null);
            _commandLane.Release();
        }
    }

    private async Task SendWithoutReplyAsync(byte[] command, CancellationToken cancellationToken)
    {
        EnsureConnected();
        await _commandLane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _transport.SendAsync(command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _commandLane.Release();
        }
    }

    private async Task<SelfInfo> AppStartWithRetryAsync(CancellationToken cancellationToken)
    {
        var attempts = Math.Max(1, _options.HandshakeAttempts);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ExecuteAsync<SelfInfo>(Commands.AppStart(_options.AppName), cancellationToken).ConfigureAwait(false);
            }
            catch (MeshCoreTimeoutException) when (attempt < attempts)
            {
                LogHandshakeRetry(attempt, attempts, _transport.Description);
            }
        }
    }

    private async Task TrySyncClockAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SetDeviceTimeAsync(Now, cancellationToken).ConfigureAwait(false);
        }
        catch (MeshCoreCommandException ex) when (ex.Error == ErrorCode.IllegalArgument)
        {
            // The firmware never moves its clock backwards; it's already ahead of us.
            LogClockAhead();
        }
    }

    private void EnsureConnected()
    {
        ObjectDisposedException.ThrowIf(_stop.IsCancellationRequested, this);
        if (!IsConnected)
        {
            throw new MeshCoreDisconnectedException($"Not connected to a radio on {_transport.Description}.");
        }
    }

    // ---- Read loop & push pump ---------------------------------------------

    private async Task ReadLoopAsync()
    {
        Exception? error = null;
        try
        {
            await foreach (var payload in _transport.Frames.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                if (payload.Length == 0)
                {
                    continue;
                }

                var frame = FrameParser.Parse(payload);
                if (frame is MalformedFrame bad)
                {
                    LogMalformed(bad.Code, bad.Reason);
                }

                if (frame.IsPush)
                {
                    _pushes.Writer.TryWrite(frame);
                }
                else if (Volatile.Read(ref _pending) is not { } pending || !pending.Offer(frame))
                {
                    LogUnsolicited(frame.Code);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // Whatever ended the transport, the client must report the link as down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            error = ex;
        }

        OnLinkClosed(error);
    }

    private void OnLinkClosed(Exception? error)
    {
        if (Interlocked.Exchange(ref _connected, 0) == 0)
        {
            return;
        }

        _pushes.Writer.TryComplete();
        Volatile.Read(ref _pending)?.Fail(new MeshCoreDisconnectedException(
            $"Lost connection to the radio on {_transport.Description}.", error ?? new EndOfStreamException()));
        if (error is null)
        {
            LogClosed(_transport.Description);
        }
        else if (error is EndOfStreamException)
        {
            // The remote end closed the link cleanly (e.g. WiFi takeover); no stack trace needed.
            LogRemoteClosed(_transport.Description, error.Message);
        }
        else
        {
            LogDisconnected(error, _transport.Description);
        }
        Disconnected?.Invoke(this, new DisconnectedEventArgs(error));
    }

    private async Task PushPumpAsync()
    {
        await foreach (var frame in _pushes.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                PushReceived?.Invoke(this, new MeshFrameEventArgs(frame));
            }
#pragma warning disable CA1031 // A faulty subscriber must not stop push delivery.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogPushHandlerFailed(ex, frame.Code);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
        if (_readLoop is not null)
        {
            await _readLoop.ConfigureAwait(false);
        }
        OnLinkClosed(null);
        if (_pushPump is not null)
        {
            await _pushPump.ConfigureAwait(false);
        }

        _commandLane.Dispose();
        _stop.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to '{Name}' ({Model}, firmware {Firmware}) on {Port}")]
    private partial void LogConnected(string name, string? model, string? firmware, string port);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Handshake attempt {Attempt}/{Attempts} on {Port} timed out; retrying")]
    private partial void LogHandshakeRetry(int attempt, int attempts, string port);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Radio clock is ahead of this computer; left unchanged")]
    private partial void LogClockAhead();

    [LoggerMessage(Level = LogLevel.Trace, Message = "→ {Command} ({Length} bytes)")]
    private partial void LogSending(CommandCode command, int length);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ignored unsolicited response 0x{Code:X2}")]
    private partial void LogUnsolicited(byte code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Malformed frame 0x{Code:X2}: {Reason}")]
    private partial void LogMalformed(byte code, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lost connection to {Port}")]
    private partial void LogDisconnected(Exception error, string port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lost connection to {Port}: {Reason}")]
    private partial void LogRemoteClosed(string port, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Closed connection to {Port}")]
    private partial void LogClosed(string port);

    [LoggerMessage(Level = LogLevel.Error, Message = "Push handler failed for frame 0x{Code:X2}")]
    private partial void LogPushHandlerFailed(Exception ex, byte code);
}
