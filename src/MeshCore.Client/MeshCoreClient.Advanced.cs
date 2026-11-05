using System.Security.Cryptography;
using MeshCore.Protocol;
using MeshCore.Protocol.Frames;
using MeshCore.Protocol.Models;

namespace MeshCore.Client;

public sealed partial class MeshCoreClient
{
    private static readonly TimeSpan RemoteReplyMargin = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxRemoteWait = TimeSpan.FromSeconds(90);

    private readonly Lock _waitersLock = new();
    private readonly List<(Func<MeshFrame, bool> Match, TaskCompletionSource<MeshFrame> Completion)> _pushWaiters = [];

    // ---- Node settings ---------------------------------------------------------

    public async Task SetLocationAsync(double latitude, double longitude, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync<OkResponse>(Commands.SetAdvertLatLon(latitude, longitude), cancellationToken).ConfigureAwait(false);
        await RefreshSelfInfoAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetRadioParamsAsync(uint frequencyKhz, uint bandwidthHz, byte spreadingFactor, byte codingRate, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync<OkResponse>(Commands.SetRadioParams(frequencyKhz, bandwidthHz, spreadingFactor, codingRate), cancellationToken).ConfigureAwait(false);
        await RefreshSelfInfoAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetTxPowerAsync(sbyte dbm, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync<OkResponse>(Commands.SetTxPower(dbm), cancellationToken).ConfigureAwait(false);
        await RefreshSelfInfoAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<TuningParams> GetTuningParamsAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync<TuningParams>(Commands.GetTuningParams(), cancellationToken);

    public Task SetTuningParamsAsync(double rxDelayBase, double airtimeFactor, CancellationToken cancellationToken = default) =>
        ExecuteAsync<OkResponse>(Commands.SetTuningParams(rxDelayBase, airtimeFactor), cancellationToken);

    public async Task SetOtherParamsAsync(bool manualAddContacts, byte telemetryModes, byte advertLocationPolicy, byte multiAcks, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync<OkResponse>(Commands.SetOtherParams(manualAddContacts, telemetryModes, advertLocationPolicy, multiAcks), cancellationToken).ConfigureAwait(false);
        await RefreshSelfInfoAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---- Contacts ----------------------------------------------------------------

    public Task AddOrUpdateContactAsync(ContactData contact, CancellationToken cancellationToken = default) =>
        ExecuteAsync<OkResponse>(Commands.AddUpdateContact(contact), cancellationToken);

    public Task<Contact> GetContactAsync(PublicKey key, CancellationToken cancellationToken = default) =>
        ExecuteAsync<Contact>(Commands.GetContactByKey(key), cancellationToken);

    public Task ShareContactAsync(PublicKey key, CancellationToken cancellationToken = default) =>
        ExecuteAsync<OkResponse>(Commands.ShareContact(key), cancellationToken);

    /// <summary>A contact's signed advert, or ours when <paramref name="key"/> is null, for sharing out of band.</summary>
    public async Task<byte[]> ExportContactAsync(PublicKey? key = null, CancellationToken cancellationToken = default) =>
        (await ExecuteAsync<ExportedContact>(Commands.ExportContact(key), cancellationToken).ConfigureAwait(false)).Advert.ToArray();

    public Task ImportContactAsync(ReadOnlyMemory<byte> advert, CancellationToken cancellationToken = default) =>
        ExecuteAsync<OkResponse>(Commands.ImportContact(advert.Span), cancellationToken);

    // ---- Repeaters and room servers ---------------------------------------------

    /// <summary>Logs in and waits for the server's answer (success, admin rights, or failure).</summary>
    public Task<LoginResultPush> LoginAsync(PublicKey key, string password, CancellationToken cancellationToken = default) =>
        RemoteRequestAsync<LoginResultPush>(Commands.SendLogin(key, password), p => key.Matches(p.Sender), cancellationToken);

    public Task LogoutAsync(PublicKey key, CancellationToken cancellationToken = default) =>
        ExecuteAsync<OkResponse>(Commands.Logout(key), cancellationToken);

    public Task<StatusResponsePush> RequestStatusAsync(PublicKey key, CancellationToken cancellationToken = default) =>
        RemoteRequestAsync<StatusResponsePush>(Commands.SendStatusRequest(key), p => key.Matches(p.Sender), cancellationToken);

    public Task<TelemetryResponsePush> RequestTelemetryAsync(PublicKey key, CancellationToken cancellationToken = default) =>
        RemoteRequestAsync<TelemetryResponsePush>(Commands.SendTelemetryRequest(key), p => key.Matches(p.Sender), cancellationToken);

    /// <summary>Sends a trace along <paramref name="path"/> and waits for it to come back with per-hop SNRs.</summary>
    public Task<TraceDataPush> TraceAsync(ReadOnlyMemory<byte> path, int hashSize, CancellationToken cancellationToken = default)
    {
        var tag = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
        return RemoteRequestAsync<TraceDataPush>(Commands.SendTracePath(tag, 0, hashSize, path.Span), p => p.Tag == tag, cancellationToken);
    }

    /// <summary>Sends a CLI command to a repeater you're logged in to as admin. The reply arrives as a
    /// <see cref="ContactMessage"/> with <see cref="TextType.CliData"/>. No ACK is sent for CLI commands.</summary>
    public Task<MessageSent> SendCliCommandAsync(PublicKeyPrefix recipient, string command, CancellationToken cancellationToken = default) =>
        ExecuteAsync<MessageSent>(Commands.SendTextMessage(recipient, command, Now, textType: TextType.CliData), cancellationToken);

    // ---- Identity -------------------------------------------------------------------

    /// <summary>The node's private key. Throws <see cref="MeshCoreCommandException"/> (disabled) on builds without export.</summary>
    public async Task<byte[]> ExportPrivateKeyAsync(CancellationToken cancellationToken = default) =>
        (await ExecuteAsync<PrivateKey>(Commands.ExportPrivateKey(), cancellationToken).ConfigureAwait(false)).Key.ToArray();

    public async Task ImportPrivateKeyAsync(ReadOnlyMemory<byte> privateKey, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync<OkResponse>(Commands.ImportPrivateKey(privateKey.Span), cancellationToken).ConfigureAwait(false);
        await RefreshSelfInfoAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---- Waiting for pushes ------------------------------------------------------------

    /// <summary>
    /// Sends a request to a remote node: the radio answers <c>SENT</c> straight away, and the remote node's answer
    /// arrives later as a push. The waiter is registered before sending, so a quick answer isn't missed.
    /// </summary>
    private async Task<T> RemoteRequestAsync<T>(byte[] command, Func<T, bool> match, CancellationToken cancellationToken) where T : MeshFrame
    {
        var completion = new TaskCompletionSource<MeshFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiter = ((Func<MeshFrame, bool>)(f => f is T t && match(t)), completion);
        lock (_waitersLock)
        {
            _pushWaiters.Add(waiter);
        }

        try
        {
            var sent = await ExecuteAsync<MessageSent>(command, cancellationToken).ConfigureAwait(false);
            var floor = Math.Min(_options.MinRemoteReplyWait.Ticks, MaxRemoteWait.Ticks);
            var margin = Math.Min(RemoteReplyMargin.Ticks, floor);
            var wait = TimeSpan.FromTicks(Math.Clamp(sent.EstimatedTimeout.Ticks + margin, floor, MaxRemoteWait.Ticks));
            try
            {
                return (T)await completion.Task.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new MeshCoreTimeoutException(
                    $"No answer from the remote node within {wait.TotalSeconds:0} s. It may be out of range, or the route may have changed.");
            }
        }
        finally
        {
            lock (_waitersLock)
            {
                _pushWaiters.Remove(waiter);
            }
        }
    }

    /// <summary>Called by the read loop for every push, before the push event is raised.</summary>
    private void OfferToWaiters(MeshFrame push)
    {
        lock (_waitersLock)
        {
            for (var i = _pushWaiters.Count - 1; i >= 0; i--)
            {
                if (_pushWaiters[i].Match(push))
                {
                    _pushWaiters[i].Completion.TrySetResult(push);
                }
            }
        }
    }

    private void FailWaiters(Exception error)
    {
        lock (_waitersLock)
        {
            foreach (var (_, completion) in _pushWaiters)
            {
                completion.TrySetException(error);
            }
        }
    }
}
