using System.Collections.Concurrent;
using MeshCore.Client;
using MeshCore.Data;
using MeshCore.Protocol;
using MeshCore.Protocol.Frames;
using MeshCore.Protocol.Models;

namespace MeshCore.Host.Radio;

/// <summary>Result of a trace: the route out and back with the SNR each hop heard, and the round trip.</summary>
public sealed record TraceResult(IReadOnlyList<string> HopHashes, IReadOnlyList<double> HopSnrs, double FinalSnr, TimeSpan RoundTrip);

public sealed partial class MeshRadio
{
    private const int MaxDiscovered = 50;
    private readonly ConcurrentDictionary<string, (Contact Contact, DateTimeOffset HeardAt)> _discovered = new(StringComparer.Ordinal);

    /// <summary>Nodes heard advertising that aren't contacts yet (only when the radio adds contacts manually).</summary>
    public IReadOnlyList<(Contact Contact, DateTimeOffset HeardAt)> Discovered =>
        [.. _discovered.Values.OrderByDescending(d => d.HeardAt)];

    // ---- Node settings ---------------------------------------------------------

    public async Task SetNodeNameAsync(string name, CancellationToken cancellationToken = default)
    {
        await RequireClient().SetAdvertNameAsync(name, cancellationToken);
        PublishSelf();
    }

    public async Task SetLocationAsync(double latitude, double longitude, CancellationToken cancellationToken = default)
    {
        await RequireClient().SetLocationAsync(latitude, longitude, cancellationToken);
        PublishSelf();
    }

    public async Task SetRadioParamsAsync(uint frequencyKhz, uint bandwidthHz, byte spreadingFactor, byte codingRate, CancellationToken cancellationToken = default)
    {
        await RequireClient().SetRadioParamsAsync(frequencyKhz, bandwidthHz, spreadingFactor, codingRate, cancellationToken);
        PublishSelf();
    }

    public async Task SetTxPowerAsync(sbyte dbm, CancellationToken cancellationToken = default)
    {
        await RequireClient().SetTxPowerAsync(dbm, cancellationToken);
        PublishSelf();
    }

    public Task<TuningParams> GetTuningParamsAsync(CancellationToken cancellationToken = default) => RequireClient().GetTuningParamsAsync(cancellationToken);

    public Task SetTuningParamsAsync(double rxDelayBase, double airtimeFactor, CancellationToken cancellationToken = default) =>
        RequireClient().SetTuningParamsAsync(rxDelayBase, airtimeFactor, cancellationToken);

    public async Task SetOtherParamsAsync(bool manualAddContacts, byte telemetryModes, byte advertLocationPolicy, byte multiAcks, CancellationToken cancellationToken = default)
    {
        await RequireClient().SetOtherParamsAsync(manualAddContacts, telemetryModes, advertLocationPolicy, multiAcks, cancellationToken);
        PublishSelf();
    }

    /// <summary>Pushes the refreshed self info out through <see cref="StatusChanged"/>.</summary>
    private void PublishSelf()
    {
        if (Volatile.Read(ref _client) is { } client)
        {
            SetStatus(Status with { Self = client.SelfInfo });
        }
    }

    // ---- Contacts ----------------------------------------------------------------

    public async Task SetFavouriteAsync(PublicKey key, bool favourite, CancellationToken cancellationToken = default) =>
        await UpdateContactAsync(key, c => c with { Flags = favourite ? c.Flags | ContactFlags.Favourite : c.Flags & ~ContactFlags.Favourite }, cancellationToken);

    /// <summary>Renames a contact on this radio only (the node keeps advertising its own name).</summary>
    public async Task RenameContactAsync(PublicKey key, string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await UpdateContactAsync(key, c => c with { Name = name.Trim() }, cancellationToken);
    }

    private async Task UpdateContactAsync(PublicKey key, Func<ContactData, ContactData> change, CancellationToken cancellationToken)
    {
        var client = RequireClient();
        var current = await client.GetContactAsync(key, cancellationToken);
        await client.AddOrUpdateContactAsync(change(ContactData.From(current)), cancellationToken);
        await RefreshContactsAsync(cancellationToken);
    }

    public Task ShareContactAsync(PublicKey key, CancellationToken cancellationToken = default) => RequireClient().ShareContactAsync(key, cancellationToken);

    /// <summary>A contact's (or, with null, our own) signed advert as hex, for sharing outside the mesh.</summary>
    public async Task<string> ExportContactAsync(PublicKey? key = null, CancellationToken cancellationToken = default) =>
        Convert.ToHexStringLower(await RequireClient().ExportContactAsync(key, cancellationToken));

    public async Task ImportContactAsync(string advertHex, CancellationToken cancellationToken = default)
    {
        byte[] advert;
        try
        {
            advert = Convert.FromHexString(advertHex.Trim());
        }
        catch (FormatException)
        {
            throw new ArgumentException("That isn't an exported contact: expected hex text.", nameof(advertHex));
        }
        await RequireClient().ImportContactAsync(advert, cancellationToken);
        await RefreshContactsAsync(cancellationToken);
    }

    /// <summary>Adds a node that was heard advertising but isn't a contact yet.</summary>
    public async Task AddDiscoveredAsync(PublicKey key, CancellationToken cancellationToken = default)
    {
        if (!_discovered.TryGetValue(key.Hex, out var found))
        {
            throw new ArgumentException("That node isn't in the list of recently heard nodes.", nameof(key));
        }
        await RequireClient().AddOrUpdateContactAsync(ContactData.From(found.Contact), cancellationToken);
        _discovered.TryRemove(key.Hex, out _);
        await RefreshContactsAsync(cancellationToken);
    }

    private void Discover(Contact contact, DateTimeOffset now)
    {
        _discovered[contact.PublicKey.Hex] = (contact, now);
        foreach (var stale in _discovered.OrderByDescending(d => d.Value.HeardAt).Skip(MaxDiscovered).Select(d => d.Key).ToList())
        {
            _discovered.TryRemove(stale, out _);
        }
        ContactsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Repeaters and room servers ---------------------------------------------

    public Task<LoginResultPush> LoginAsync(PublicKey key, string password, CancellationToken cancellationToken = default) =>
        RequireClient().LoginAsync(key, password, cancellationToken);

    public Task LogoutAsync(PublicKey key, CancellationToken cancellationToken = default) => RequireClient().LogoutAsync(key, cancellationToken);

    public Task<StatusResponsePush> RequestStatusAsync(PublicKey key, CancellationToken cancellationToken = default) =>
        RequireClient().RequestStatusAsync(key, cancellationToken);

    public Task<TelemetryResponsePush> RequestTelemetryAsync(PublicKey key, CancellationToken cancellationToken = default) =>
        RequireClient().RequestTelemetryAsync(key, cancellationToken);

    /// <summary>
    /// Traces the route to a node and back: its known out-path, the node itself, then the out-path reversed.
    /// Needs a learned route (send it a message or log in first if it floods).
    /// </summary>
    public async Task<TraceResult> TraceAsync(PublicKey key, TimeProvider time, CancellationToken cancellationToken = default)
    {
        var client = RequireClient();
        var contact = await client.GetContactAsync(key, cancellationToken);
        if (contact.OutPathLength.IsNone)
        {
            throw new ArgumentException("There's no known route to this node yet. Send it a message (or log in) so a route is learned, then trace.");
        }

        var hashSize = contact.OutPathLength.HopCount == 0 ? 1 : contact.OutPathLength.HashSize;
        var outPath = contact.OutPath.ToArray();
        var hops = Enumerable.Range(0, outPath.Length / hashSize).Select(i => outPath.AsSpan(i * hashSize, hashSize).ToArray()).ToList();
        var path = hops.SelectMany(h => h)
            .Concat(key.ToBytes().Take(hashSize))
            .Concat(Enumerable.Reverse(hops).SelectMany(h => h))
            .ToArray();

        var started = time.GetTimestamp();
        var trace = await client.TraceAsync(path, hashSize, cancellationToken);
        return new TraceResult(trace.HopHashes, trace.HopSnrs, trace.FinalSnr, time.GetElapsedTime(started));
    }

    /// <summary>Sends a CLI command to a repeater (admin login needed) and stores it in the conversation.
    /// The reply arrives later as a direct message with <see cref="TextType.CliData"/>.</summary>
    public async Task<MessageEntity> SendCliCommandAsync(PublicKey key, string command, string? userId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var client = RequireClient();
        var row = await store.SaveOutgoingAsync(MessageKind.Direct, key.Prefix.Hex, null, client.SelfInfo?.Name ?? "", command.Trim(), userId, TextType.CliData, cancellationToken);
        Raise(row, isNew: true);
        try
        {
            await client.SendCliCommandAsync(key.Prefix, command.Trim(), cancellationToken);
            row = await store.MarkSentAsync(row.Id, null, cancellationToken) ?? row; // CLI commands are never ACKed
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

    // ---- Identity -------------------------------------------------------------------

    public async Task<string> ExportPrivateKeyAsync(CancellationToken cancellationToken = default) =>
        Convert.ToHexStringLower(await RequireClient().ExportPrivateKeyAsync(cancellationToken));

    public async Task ImportPrivateKeyAsync(string privateKeyHex, CancellationToken cancellationToken = default)
    {
        byte[] key;
        try
        {
            key = Convert.FromHexString(privateKeyHex.Trim());
        }
        catch (FormatException)
        {
            throw new ArgumentException("A private key is 128 hex characters.", nameof(privateKeyHex));
        }
        await RequireClient().ImportPrivateKeyAsync(key, cancellationToken);
        PublishSelf();
        await RefreshContactsAsync(cancellationToken);
    }
}
