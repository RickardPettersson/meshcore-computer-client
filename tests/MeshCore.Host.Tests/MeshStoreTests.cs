using MeshCore.Data;
using MeshCore.Protocol;
using MeshCore.Protocol.Frames;
using MeshCore.Protocol.Models;
using Microsoft.Extensions.Time.Testing;
using static MeshCore.Client.Tests.FakeRadio;

namespace MeshCore.Host.Tests;

public class MeshStoreTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_762_000_000);

    private static Contact ContactFrame(byte seed, string name) => Assert.IsType<Contact>(FrameParser.Parse(Contact(seed, name)));

    private static async Task<(MeshStore Store, TestDb Db, FakeTimeProvider Time)> CreateAsync()
    {
        var db = await TestDb.CreateAsync();
        var time = new FakeTimeProvider(T0);
        return (new MeshStore(db, time), db, time);
    }

    [Fact]
    public async Task Full_contact_sync_keeps_removed_contacts_for_history()
    {
        var (store, db, _) = await CreateAsync();
        await using var _d = db;

        await store.SaveContactsAsync([ContactFrame(10, "Alice"), ContactFrame(50, "Bob")], fullList: true);
        await store.SaveContactsAsync([ContactFrame(10, "Alice v2")], fullList: true);

        var onRadio = await store.GetContactsAsync();
        var all = await store.GetContactsAsync(includeRemoved: true);
        Assert.Equal(["Alice v2"], onRadio.Select(c => c.Name));
        Assert.Equal(2, all.Count);
        Assert.False(all.Single(c => c.Name == "Bob").IsOnRadio);
        Assert.Equal(new PublicKey(Key(10)).Prefix.Hex, onRadio[0].KeyPrefix);
    }

    [Fact]
    public async Task FindContact_by_name_or_key_prefix()
    {
        var (store, db, _) = await CreateAsync();
        await using var _d = db;
        await store.SaveContactsAsync([ContactFrame(10, "Alice"), ContactFrame(50, "Bob")], fullList: true);

        Assert.Equal("Alice", (await store.FindContactAsync("alice"))?.Name);
        Assert.Equal("Bob", (await store.FindContactAsync(new PublicKey(Key(50)).Hex[..8]))?.Name);
        Assert.Null(await store.FindContactAsync("Carol"));
    }

    [Fact]
    public async Task Incoming_direct_message_gets_the_contact_name()
    {
        var (store, db, _) = await CreateAsync();
        await using var _d = db;
        await store.SaveContactsAsync([ContactFrame(10, "Alice")], fullList: true);
        var frame = FrameParser.Parse(Concat([0x10, 0x14, 0, 0], Key(10)[..6], [0x02, 0x00], U32(1_762_000_000), "hej"u8.ToArray()));

        var row = await store.SaveIncomingAsync(frame);

        Assert.NotNull(row);
        Assert.Equal(MessageKind.Direct, row.Kind);
        Assert.Equal("Alice", row.SenderName);
        Assert.Equal("hej", row.Text);
        Assert.Equal(5.0, row.Snr);
        Assert.Equal(2, row.Hops);
        Assert.Equal(T0, row.ReceivedAt);
    }

    [Fact]
    public async Task Incoming_channel_message_splits_sender_name()
    {
        var (store, db, _) = await CreateAsync();
        await using var _d = db;

        var row = await store.SaveIncomingAsync(FrameParser.Parse(ChannelMessage(0, "Bob: anyone on?")));

        Assert.Equal(MessageKind.Channel, row?.Kind);
        Assert.Equal("Bob", row?.SenderName);
        Assert.Equal("anyone on?", row?.Text);
        Assert.Null(row?.Hops);
    }

    [Fact]
    public async Task Non_message_frames_are_not_stored()
    {
        var (store, db, _) = await CreateAsync();
        await using var _d = db;

        Assert.Null(await store.SaveIncomingAsync(new OkResponse()));
        Assert.Empty(await store.GetMessagesAsync());
    }

    [Fact]
    public async Task Outgoing_direct_message_lifecycle_with_ack()
    {
        var (store, db, time) = await CreateAsync();
        await using var _d = db;

        var row = await store.SaveOutgoingAsync(MessageKind.Direct, "0a0b0c0d0e0f", null, "me", "hello", "user-1");
        Assert.Equal(DeliveryStatus.Sending, row.Status);

        await store.MarkSentAsync(row.Id, new MessageSent(false, 0xCAFE, TimeSpan.FromSeconds(10)));
        time.Advance(TimeSpan.FromSeconds(3));
        var delivered = await store.MarkDeliveredAsync(new SendConfirmedPush(0xCAFE, TimeSpan.FromMilliseconds(2400)));

        Assert.Equal(row.Id, delivered?.Id);
        Assert.Equal(DeliveryStatus.Delivered, delivered?.Status);
        Assert.Equal(2400, delivered?.RoundTripMs);
        Assert.Equal("user-1", delivered?.SentByUserId);
        Assert.Null(await store.MarkDeliveredAsync(new SendConfirmedPush(0xBEEF, TimeSpan.Zero)));
    }

    [Fact]
    public async Task Unacknowledged_messages_expire_and_a_late_ack_still_counts()
    {
        var (store, db, time) = await CreateAsync();
        await using var _d = db;
        var row = await store.SaveOutgoingAsync(MessageKind.Direct, "0a0b0c0d0e0f", null, "me", "hello", null);
        await store.MarkSentAsync(row.Id, new MessageSent(true, 7, TimeSpan.FromSeconds(10)));

        Assert.Empty(await store.ExpireUnconfirmedAsync());
        time.Advance(TimeSpan.FromSeconds(11));
        var expired = await store.ExpireUnconfirmedAsync();
        var late = await store.MarkDeliveredAsync(new SendConfirmedPush(7, TimeSpan.FromSeconds(12)));

        Assert.Equal(DeliveryStatus.NotConfirmed, Assert.Single(expired).Status);
        Assert.Equal(DeliveryStatus.Delivered, late?.Status);
    }

    [Fact]
    public async Task Channel_sync_keeps_only_configured_slots()
    {
        var (store, db, _) = await CreateAsync();
        await using var _d = db;
        ChannelInfo Ch(byte i, string name) => Assert.IsType<ChannelInfo>(FrameParser.Parse(ChannelInfo(i, name)));

        await store.SaveChannelsAsync([Ch(0, "Public"), Ch(1, "Hiking"), Ch(2, "")]);
        await store.SaveChannelsAsync([Ch(0, "Public"), Ch(1, ""), Ch(2, "Family")]);

        Assert.Equal(["Public", "Family"], (await store.GetChannelsAsync()).Select(c => c.Name));
    }

    [Fact]
    public async Task Messages_page_newest_first_per_conversation()
    {
        var (store, db, _) = await CreateAsync();
        await using var _d = db;
        for (var i = 0; i < 5; i++)
        {
            await store.SaveIncomingAsync(FrameParser.Parse(ChannelMessage(0, $"A: {i}")));
        }
        await store.SaveIncomingAsync(FrameParser.Parse(ChannelMessage(1, "B: other channel")));

        var page1 = await store.GetMessagesAsync(channelIndex: 0, take: 3);
        var page2 = await store.GetMessagesAsync(channelIndex: 0, beforeId: page1[^1].Id, take: 3);

        Assert.Equal(["4", "3", "2"], page1.Select(m => m.Text));
        Assert.Equal(["1", "0"], page2.Select(m => m.Text));
    }
}
