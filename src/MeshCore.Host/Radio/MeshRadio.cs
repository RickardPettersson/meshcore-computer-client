using MeshCore.Client;
using MeshCore.Data;
using MeshCore.Protocol;
using MeshCore.Protocol.Binary;
using MeshCore.Protocol.Frames;
using MeshCore.Protocol.Models;

namespace MeshCore.Host.Radio;

public sealed class MessageEventArgs(MessageEntity message, bool isNew) : EventArgs
{
    public MessageEntity Message { get; } = message;

    /// <summary>True for a newly stored message; false for a status change (sent, delivered…).</summary>
    public bool IsNew { get; } = isNew;
}

/// <summary>
/// The app's single handle on the radio. Everything else (API, website, console) goes through it:
/// it keeps the database in step with the radio and sends messages. The connection itself is
/// managed by <see cref="RadioConnectionService"/>, which attaches and detaches clients.
/// </summary>
public sealed partial class MeshRadio(MeshStore store, ILogger<MeshRadio> logger) : IDisposable
{
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly Lock _statusLock = new();
    private MeshCoreClient? _client;
    private RadioStatus _status = new(RadioState.Stopped, "");

    public RadioStatus Status
    {
        get
        {
            lock (_statusLock)
            {
                return _status;
            }
        }
    }

    public event EventHandler<RadioStatus>? StatusChanged;

    public event EventHandler<MessageEventArgs>? MessageChanged;

    public event EventHandler? ContactsChanged;

    public event EventHandler? ChannelsChanged;

    /// <summary>The store, for reads (history, contacts, channels).</summary>
    public MeshStore Store => store;

    // ---- Sending ------------------------------------------------------------

    /// <summary>Sends a direct message to a contact and stores it. Delivery updates arrive via <see cref="MessageChanged"/>.</summary>
    /// <param name="contactKey">The contact's public key, or at least its 6-byte (12 hex) prefix.</param>
    /// <exception cref="ArgumentException">Text is empty or longer than <see cref="ProtocolLimits.MaxTextBytes"/>.</exception>
    /// <exception cref="MeshCoreDisconnectedException">No radio is connected.</exception>
    public async Task<MessageEntity> SendDirectAsync(string contactKey, string text, string? userId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contactKey);
        ValidateText(text, ProtocolLimits.MaxTextBytes);
        var key = contactKey.Trim().ToLowerInvariant();
        const int prefixHexLength = ProtocolLimits.PublicKeyPrefixSize * 2;
        if (key.Length < prefixHexLength)
        {
            throw new ArgumentException($"A contact key needs at least {prefixHexLength} hex characters.", nameof(contactKey));
        }
        var prefix = PublicKeyPrefix.Parse(key[..prefixHexLength]);
        var client = RequireClient();

        var row = await store.SaveOutgoingAsync(MessageKind.Direct, prefix.Hex, null, client.SelfInfo?.Name ?? "", text, userId, cancellationToken: cancellationToken);
        Raise(row, isNew: true);
        try
        {
            var sent = await client.SendTextMessageAsync(prefix, text, cancellationToken: cancellationToken);
            row = await store.MarkSentAsync(row.Id, sent, cancellationToken) ?? row;
        }
        catch (MeshCoreException ex)
        {
            row = await store.MarkFailedAsync(row.Id, ex.Message, CancellationToken.None) ?? row;
            Raise(row, isNew: false);
            throw;
        }
        Raise(row, isNew: false);
        return row;
    }

    /// <summary>Sends to a channel and stores it. Channel messages have no delivery confirmation.</summary>
    public async Task<MessageEntity> SendChannelAsync(byte channelIndex, string text, string? userId = null, CancellationToken cancellationToken = default)
    {
        var client = RequireClient();
        var selfName = client.SelfInfo?.Name ?? "";
        ValidateText(text, ProtocolLimits.MaxChannelTextBytes(selfName));

        var row = await store.SaveOutgoingAsync(MessageKind.Channel, null, channelIndex, selfName, text, userId, cancellationToken: cancellationToken);
        Raise(row, isNew: true);
        try
        {
            await client.SendChannelTextMessageAsync(channelIndex, text, cancellationToken);
            row = await store.MarkSentAsync(row.Id, null, cancellationToken) ?? row;
        }
        catch (MeshCoreException ex)
        {
            row = await store.MarkFailedAsync(row.Id, ex.Message, CancellationToken.None) ?? row;
            Raise(row, isNew: false);
            throw;
        }
        Raise(row, isNew: false);
        return row;
    }

    private static void ValidateText(string text, int maxBytes)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Message text is empty.", nameof(text));
        }

        var bytes = Utf8.ByteCount(text);
        if (bytes > maxBytes)
        {
            throw new ArgumentException($"Message is {bytes} bytes; the limit is {maxBytes}.", nameof(text));
        }
    }

    private MeshCoreClient RequireClient() =>
        Volatile.Read(ref _client) is { IsConnected: true } c
            ? c
            : throw new MeshCoreDisconnectedException("The radio is not connected.");

    // ---- Syncing ------------------------------------------------------------

    /// <summary>Pops every queued message off the radio and stores it. Safe to call concurrently.</summary>
    public async Task<int> SyncMessagesAsync(CancellationToken cancellationToken = default)
    {
        var client = RequireClient();
        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            var count = 0;
            // One at a time: each message is stored before the next is popped, so a crash loses at most one.
            while (await client.SyncNextMessageAsync(cancellationToken) is { } frame)
            {
                if (await store.SaveIncomingAsync(frame, cancellationToken) is { } row)
                {
                    count++;
                    LogReceived(row.Kind, row.SenderName ?? row.ContactKeyPrefix, row.Text);
                    Raise(row, isNew: true);
                }
                else
                {
                    LogIgnoredFrame(frame.Code);
                }
            }
            return count;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task RefreshContactsAsync(CancellationToken cancellationToken = default)
    {
        var contacts = await RequireClient().GetContactsAsync(cancellationToken: cancellationToken);
        await store.SaveContactsAsync(contacts, fullList: true, cancellationToken);
        ContactsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RefreshChannelsAsync(CancellationToken cancellationToken = default)
    {
        var channels = await RequireClient().GetChannelsAsync(includeEmpty: true, cancellationToken);
        await store.SaveChannelsAsync(channels, cancellationToken);
        ChannelsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Device, contact and channel operations -----------------------------

    public Task<BatteryAndStorage> GetBatteryAsync(CancellationToken cancellationToken = default) =>
        RequireClient().GetBatteryAndStorageAsync(cancellationToken);

    public Task<DateTimeOffset> GetDeviceTimeAsync(CancellationToken cancellationToken = default) =>
        RequireClient().GetDeviceTimeAsync(cancellationToken);

    /// <summary>Sets the radio clock. Fails with <see cref="ErrorCode.IllegalArgument"/> if the radio is ahead.</summary>
    public Task SetDeviceTimeAsync(DateTimeOffset time, CancellationToken cancellationToken = default) =>
        RequireClient().SetDeviceTimeAsync(time, cancellationToken);

    public Task SendAdvertAsync(bool flood, CancellationToken cancellationToken = default) =>
        RequireClient().SendAdvertAsync(flood, cancellationToken);

    /// <summary>Reboots the radio. The link drops and the connection service reconnects.</summary>
    public Task RebootAsync(CancellationToken cancellationToken = default) =>
        RequireClient().RebootAsync(cancellationToken);

    public async Task RemoveContactAsync(PublicKey key, CancellationToken cancellationToken = default)
    {
        await RequireClient().RemoveContactAsync(key, cancellationToken);
        await RefreshContactsAsync(cancellationToken);
    }

    public async Task ResetPathAsync(PublicKey key, CancellationToken cancellationToken = default)
    {
        await RequireClient().ResetPathAsync(key, cancellationToken);
        await RefreshContactsAsync(cancellationToken);
    }

    /// <summary>Reads a channel straight from the radio, including its secret (never stored locally).</summary>
    public Task<ChannelInfo> GetChannelFromRadioAsync(byte index, CancellationToken cancellationToken = default) =>
        RequireClient().GetChannelAsync(CheckChannelIndex(index), cancellationToken);

    public async Task SetChannelAsync(byte index, string name, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken = default)
    {
        await RequireClient().SetChannelAsync(CheckChannelIndex(index), name, secret, cancellationToken);
        await RefreshChannelsAsync(cancellationToken);
    }

    /// <summary>Clears a channel slot (empty name, all-zero key), as the official apps do.</summary>
    public Task DeleteChannelAsync(byte index, CancellationToken cancellationToken = default) =>
        SetChannelAsync(index, "", new byte[ProtocolLimits.ChannelSecretSize], cancellationToken);

    private byte CheckChannelIndex(byte index)
    {
        var max = Status.Device?.MaxChannels;
        return max is { } m && index >= m
            ? throw new ArgumentOutOfRangeException(nameof(index), index, $"This radio has channels 0–{m - 1}.")
            : index;
    }

    /// <summary>Marks direct messages still waiting for an ACK past their deadline.</summary>
    public async Task ExpireAcksAsync(CancellationToken cancellationToken = default)
    {
        foreach (var row in await store.ExpireUnconfirmedAsync(cancellationToken))
        {
            Raise(row, isNew: false);
        }
    }

    // ---- Connection lifecycle (called by RadioConnectionService) -----------

    internal async Task AttachAsync(MeshCoreClient client, string endpoint, DateTimeOffset now, CancellationToken cancellationToken)
    {
        client.PushReceived += OnPush;
        Volatile.Write(ref _client, client);

        await RefreshContactsAsync(cancellationToken);
        await RefreshChannelsAsync(cancellationToken);
        SetStatus(new RadioStatus(RadioState.Connected, endpoint, client.SelfInfo, client.DeviceInfo, now));
        // Connected first, so the website shows the radio online while a long backlog downloads.
        var synced = await SyncMessagesAsync(cancellationToken);
        Volatile.Write(ref _synced, true);
        LogAttached(endpoint, synced);
    }

    private bool _synced;

    /// <summary>True once the messages queued on the radio at connect time have been fetched and stored.</summary>
    internal bool IsSynced => Volatile.Read(ref _synced) && Status.IsConnected;

    internal void Detach(MeshCoreClient client)
    {
        Volatile.Write(ref _synced, false);
        client.PushReceived -= OnPush;
        Interlocked.CompareExchange(ref _client, null, client);
    }

    internal void SetStatus(RadioStatus status)
    {
        lock (_statusLock)
        {
            if (_status == status)
            {
                return;
            }
            _status = status;
        }
        StatusChanged?.Invoke(this, status);
    }

    // ---- Pushes -------------------------------------------------------------

    private void OnPush(object? sender, MeshFrameEventArgs e) => _ = HandlePushAsync(e.Frame);

    private async Task HandlePushAsync(MeshFrame frame)
    {
        try
        {
            switch (frame)
            {
                case MessagesWaitingPush:
                    await SyncMessagesAsync();
                    break;
                case SendConfirmedPush ack:
                    if (await store.MarkDeliveredAsync(ack) is { } row)
                    {
                        Raise(row, isNew: false);
                    }
                    break;
                case AdvertPush advert:
                    await store.MarkHeardAsync(advert.PublicKey);
                    ContactsChanged?.Invoke(this, EventArgs.Empty);
                    break;
                case PathUpdatedPush:
                    await RefreshContactsAsync();
                    break;
                case NewAdvertPush n:
                    // Only sent when the radio is set to add contacts manually: offer it in the "heard nodes" list.
                    LogNewNode(n.Contact.Name, n.Contact.Type);
                    Discover(n.Contact, DateTimeOffset.UtcNow);
                    break;
                case ContactDeletedPush or ContactsFullPush:
                    // The radio overwrote its oldest contact (or is full): resync so the list matches.
                    await RefreshContactsAsync();
                    break;
            }
        }
        catch (MeshCoreException ex)
        {
            LogPushFailed(ex, frame.Code);
        }
#pragma warning disable CA1031 // Background push handling must never crash the app.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogPushFailed(ex, frame.Code);
        }
    }

    private void Raise(MessageEntity row, bool isNew) => MessageChanged?.Invoke(this, new MessageEventArgs(row, isNew));

    public void Dispose() => _syncLock.Dispose();

    [LoggerMessage(Level = LogLevel.Information, Message = "Radio ready on {Endpoint}; {Count} queued message(s) fetched")]
    private partial void LogAttached(string endpoint, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Kind} message from {From}: {Text}")]
    private partial void LogReceived(MessageKind kind, string? from, string text);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ignored queued frame 0x{Code:X2} (not a text message)")]
    private partial void LogIgnoredFrame(byte code);

    [LoggerMessage(Level = LogLevel.Information, Message = "Heard new node '{Name}' ({Type}); not in contacts")]
    private partial void LogNewNode(string name, AdvertType type);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Handling push 0x{Code:X2} failed")]
    private partial void LogPushFailed(Exception ex, byte code);
}
