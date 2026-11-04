using MeshCore.Protocol.Frames;
using MeshCore.Protocol.Models;
using Microsoft.EntityFrameworkCore;

namespace MeshCore.Data;

/// <summary>
/// Persists what the radio reports. Safe to use from singletons: every call opens its own context.
/// </summary>
public sealed class MeshStore(IDbContextFactory<MeshDbContext> contextFactory, TimeProvider? timeProvider = null)
{
    private readonly IDbContextFactory<MeshDbContext> _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Creates the database or upgrades it to the latest migration.</summary>
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---- Contacts -----------------------------------------------------------

    /// <summary>
    /// Saves contacts from the radio. With <paramref name="fullList"/>, contacts not in the list are
    /// marked as no longer on the radio (kept for message history).
    /// </summary>
    public async Task SaveContactsAsync(IEnumerable<Contact> contacts, bool fullList, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contacts);
        var now = _time.GetUtcNow();
        var incoming = contacts.GroupBy(c => c.PublicKey.Hex).ToDictionary(g => g.Key, g => g.Last());

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var keys = incoming.Keys.ToList();
        var existing = fullList
            ? await db.Contacts.ToDictionaryAsync(c => c.PublicKey, cancellationToken).ConfigureAwait(false)
            : await db.Contacts.Where(c => keys.Contains(c.PublicKey)).ToDictionaryAsync(c => c.PublicKey, cancellationToken).ConfigureAwait(false);

        foreach (var (key, contact) in incoming)
        {
            if (!existing.TryGetValue(key, out var row))
            {
                row = new ContactEntity { PublicKey = key, KeyPrefix = contact.PublicKey.Prefix.Hex, Name = contact.Name, FirstSeen = now };
                db.Contacts.Add(row);
            }
            Apply(row, contact, now);
        }

        if (fullList)
        {
            foreach (var gone in existing.Values.Where(r => r.IsOnRadio && !incoming.ContainsKey(r.PublicKey)))
            {
                gone.IsOnRadio = false;
                gone.UpdatedAt = now;
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records that we heard an advert from a contact.</summary>
    public async Task MarkHeardAsync(PublicKey key, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Contacts.Where(c => c.PublicKey == key.Hex)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastHeard, _time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContactEntity>> GetContactsAsync(bool includeRemoved = false, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Contacts.AsNoTracking()
            .Where(c => includeRemoved || c.IsOnRadio)
            .OrderBy(c => c.Name)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Finds a contact by exact name (case-insensitive) or key prefix; null if none or ambiguous.</summary>
    public async Task<ContactEntity?> FindContactAsync(string nameOrKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nameOrKey);
        var contacts = await GetContactsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var key = nameOrKey.Trim().ToLowerInvariant();
        var byName = contacts.Where(c => string.Equals(c.Name, nameOrKey.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        var matches = byName.Count > 0 ? byName : contacts.Where(c => c.PublicKey.StartsWith(key, StringComparison.Ordinal)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static void Apply(ContactEntity row, Contact c, DateTimeOffset now)
    {
        row.Name = c.Name;
        row.Type = c.Type;
        row.Flags = c.Flags;
        row.OutPathLength = c.OutPathLength.Raw;
        row.OutPath = c.OutPath.ToArray();
        row.Latitude = c.Latitude;
        row.Longitude = c.Longitude;
        row.LastAdvert = c.LastAdvert;
        row.LastModified = c.LastModified;
        row.IsOnRadio = true;
        row.UpdatedAt = now;
    }

    // ---- Channels -----------------------------------------------------------

    /// <summary>Replaces the stored channel list with what the radio has configured.</summary>
    public async Task SaveChannelsAsync(IEnumerable<ChannelInfo> channels, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channels);
        var now = _time.GetUtcNow();
        var configured = channels.Where(c => !c.IsEmpty).ToDictionary(c => c.Index);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Channels.ToListAsync(cancellationToken).ConfigureAwait(false);
        db.Channels.RemoveRange(rows.Where(r => !configured.ContainsKey(r.Index)));
        foreach (var channel in configured.Values)
        {
            var row = rows.FirstOrDefault(r => r.Index == channel.Index);
            if (row is null)
            {
                db.Channels.Add(new ChannelEntity { Index = channel.Index, Name = channel.Name, UpdatedAt = now });
            }
            else if (row.Name != channel.Name)
            {
                row.Name = channel.Name;
                row.UpdatedAt = now;
            }
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ChannelEntity>> GetChannelsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Channels.AsNoTracking().OrderBy(c => c.Index).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---- Incoming messages --------------------------------------------------

    /// <summary>Stores a message popped from the radio. Returns null for frame types that aren't messages.</summary>
    public async Task<MessageEntity?> SaveIncomingAsync(MeshFrame frame, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();

        MessageEntity row;
        switch (frame)
        {
            case ContactMessage m:
                var prefix = m.SenderPrefix.Hex;
                var name = await db.Contacts.AsNoTracking().Where(c => c.KeyPrefix == prefix)
                    .Select(c => c.Name).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                row = new MessageEntity
                {
                    Kind = MessageKind.Direct,
                    ContactKeyPrefix = prefix,
                    SenderName = name,
                    Text = m.Text,
                    TextType = m.TextType,
                    SignerPrefix = m.SignerPrefix?.Hex,
                    SenderTimestamp = m.SenderTimestamp,
                    Snr = m.Snr,
                    Hops = HopsOf(m.PathLength),
                };
                break;

            case ChannelMessage m:
                row = new MessageEntity
                {
                    Kind = MessageKind.Channel,
                    ChannelIndex = m.ChannelIndex,
                    SenderName = m.SenderName,
                    Text = m.Body,
                    TextType = m.TextType,
                    SenderTimestamp = m.SenderTimestamp,
                    Snr = m.Snr,
                    Hops = HopsOf(m.PathLength),
                };
                break;

            default:
                return null;
        }

        row.Direction = MessageDirection.Incoming;
        row.ReceivedAt = now;
        row.Status = DeliveryStatus.None;
        db.Messages.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    private static int? HopsOf(PathLength path) => path.IsNone ? null : path.HopCount;

    // ---- Outgoing messages --------------------------------------------------

    /// <summary>Stores an outgoing message as <see cref="DeliveryStatus.Sending"/> before it goes to the radio.</summary>
    public async Task<MessageEntity> SaveOutgoingAsync(
        MessageKind kind, string? contactKeyPrefix, byte? channelIndex, string senderName, string text, string? userId,
        CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        var row = new MessageEntity
        {
            Kind = kind,
            Direction = MessageDirection.Outgoing,
            ContactKeyPrefix = contactKeyPrefix,
            ChannelIndex = channelIndex,
            SenderName = senderName,
            Text = text,
            SenderTimestamp = now,
            ReceivedAt = now,
            Status = DeliveryStatus.Sending,
            SentByUserId = userId,
        };

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.Messages.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    /// <summary>The radio accepted the message. For direct messages, start waiting for the ACK.</summary>
    public Task<MessageEntity?> MarkSentAsync(long id, MessageSent? sent, CancellationToken cancellationToken = default) =>
        UpdateAsync(id, row =>
        {
            row.Status = DeliveryStatus.Sent;
            if (sent is not null)
            {
                row.ExpectedAck = sent.ExpectedAck;
                row.Flooded = sent.Flooded;
                row.AckDeadline = _time.GetUtcNow() + sent.EstimatedTimeout;
            }
        }, cancellationToken);

    public Task<MessageEntity?> MarkFailedAsync(long id, string error, CancellationToken cancellationToken = default) =>
        UpdateAsync(id, row =>
        {
            row.Status = DeliveryStatus.Failed;
            row.Error = error.Length > 256 ? error[..256] : error;
        }, cancellationToken);

    /// <summary>Matches an ACK push to the message waiting for it. Returns the message, or null if none matched.</summary>
    public async Task<MessageEntity?> MarkDeliveredAsync(SendConfirmedPush ack, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ack);
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Messages
            .Where(m => m.ExpectedAck == ack.AckCode && m.Direction == MessageDirection.Outgoing
                        && (m.Status == DeliveryStatus.Sent || m.Status == DeliveryStatus.NotConfirmed))
            .OrderByDescending(m => m.Id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        row.Status = DeliveryStatus.Delivered;
        row.RoundTripMs = (int)ack.RoundTrip.TotalMilliseconds;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    /// <summary>Marks direct messages whose ACK deadline passed as <see cref="DeliveryStatus.NotConfirmed"/>.</summary>
    public async Task<IReadOnlyList<MessageEntity>> ExpireUnconfirmedAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var overdue = await db.Messages
            .Where(m => m.Status == DeliveryStatus.Sent && m.AckDeadline != null && m.AckDeadline < now)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in overdue)
        {
            row.Status = DeliveryStatus.NotConfirmed;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return overdue;
    }

    private async Task<MessageEntity?> UpdateAsync(long id, Action<MessageEntity> change, CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Messages.FindAsync([id], cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }
        change(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    // ---- Queries ------------------------------------------------------------

    /// <summary>Newest-first page of a conversation. Pass the oldest Id you have as <paramref name="beforeId"/> to page back.</summary>
    public async Task<IReadOnlyList<MessageEntity>> GetMessagesAsync(
        string? contactKeyPrefix = null, byte? channelIndex = null, long? beforeId = null, int take = 50,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.Messages.AsNoTracking();
        if (contactKeyPrefix is not null)
        {
            query = query.Where(m => m.Kind == MessageKind.Direct && m.ContactKeyPrefix == contactKeyPrefix);
        }
        if (channelIndex is not null)
        {
            query = query.Where(m => m.Kind == MessageKind.Channel && m.ChannelIndex == channelIndex);
        }
        if (beforeId is not null)
        {
            query = query.Where(m => m.Id < beforeId);
        }
        return await query.OrderByDescending(m => m.Id).Take(Math.Clamp(take, 1, 500)).ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
