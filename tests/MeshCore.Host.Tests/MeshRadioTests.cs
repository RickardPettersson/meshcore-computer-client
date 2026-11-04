using System.Collections.Concurrent;
using MeshCore.Client;
using MeshCore.Client.Tests;
using MeshCore.Client.Transport;
using MeshCore.Data;
using MeshCore.Host.Radio;
using MeshCore.Protocol.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static MeshCore.Client.Tests.FakeRadio;

namespace MeshCore.Host.Tests;

public class MeshRadioTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    /// <summary>A radio with two contacts, a Public channel, and one queued channel message.</summary>
    private static FakeRadio ScriptedRadio()
    {
        var queue = new ConcurrentQueue<byte[]>([ChannelMessage(0, "Bob: queued while offline")]);
        return new FakeRadio().WithHandshake("Base")
            .On(0x04, [0x02, 2, 0, 0, 0], Contact(10, "Alice"), Contact(50, "Bob"), [0x04, 0, 0, 0, 0])
            .On(0x1F, cmd => [cmd[1] == 0 ? ChannelInfo(0, "Public") : ChannelInfo(cmd[1], "")])
            .On(0x0A, _ => [queue.TryDequeue(out var m) ? m : [0x0A]])
            .On(0x02, Concat([0x06, 0x00], U32(0xACE), U32(8000)))
            .On(0x03, Ok);
    }

    private static async Task<(MeshRadio Radio, FakeRadio Fake, MeshCoreClient Client, TestDb Db)> AttachedAsync(FakeRadio? fake = null)
    {
        fake ??= ScriptedRadio();
        var db = await TestDb.CreateAsync();
        var radio = new MeshRadio(new MeshStore(db), NullLogger<MeshRadio>.Instance);
        var client = new MeshCoreClient(fake, new MeshCoreClientOptions { CommandTimeout = TimeSpan.FromSeconds(2) });
        await client.ConnectAsync();
        await radio.AttachAsync(client, "fake", DateTimeOffset.UtcNow, CancellationToken.None);
        return (radio, fake, client, db);
    }

    [Fact]
    public async Task Attach_syncs_contacts_channels_and_queued_messages()
    {
        var (radio, _, client, db) = await AttachedAsync();
        await using var _c = client;
        await using var _d = db;

        Assert.Equal(RadioState.Connected, radio.Status.State);
        Assert.Equal("Base", radio.Status.Self?.Name);
        Assert.Equal(["Alice", "Bob"], (await radio.Store.GetContactsAsync()).Select(c => c.Name));
        Assert.Equal(["Public"], (await radio.Store.GetChannelsAsync()).Select(c => c.Name));
        var message = Assert.Single(await radio.Store.GetMessagesAsync());
        Assert.Equal("queued while offline", message.Text);
    }

    [Fact]
    public async Task Messages_waiting_push_stores_and_announces_new_messages()
    {
        var queue = new ConcurrentQueue<byte[]>();
        var fake = ScriptedRadio().On(0x0A, _ => [queue.TryDequeue(out var m) ? m : [0x0A]]);
        var (radio, _, client, db) = await AttachedAsync(fake);
        await using var _c = client;
        await using var _d = db;
        var announced = new TaskCompletionSource<MessageEntity>();
        radio.MessageChanged += (_, e) => { if (e.IsNew) { announced.TrySetResult(e.Message); } };

        queue.Enqueue(Concat([0x10, 0x10, 0, 0], Key(10)[..6], [0xFF, 0x00], U32(1_762_000_000), "ping"u8.ToArray()));
        fake.Push([0x83]);

        var row = await announced.Task.WaitAsync(Wait);
        Assert.Equal("Alice", row.SenderName);
        Assert.Equal("ping", row.Text);
        Assert.Null(row.Hops);
    }

    [Fact]
    public async Task Direct_message_is_stored_sent_and_marked_delivered_on_ack()
    {
        var (radio, fake, client, db) = await AttachedAsync();
        await using var _c = client;
        await using var _d = db;
        var delivered = new TaskCompletionSource<MessageEntity>();
        radio.MessageChanged += (_, e) => { if (e.Message.Status == DeliveryStatus.Delivered) { delivered.TrySetResult(e.Message); } };
        var alice = new PublicKey(Key(10));

        var sent = await radio.SendDirectAsync(alice.Hex, "hi Alice", userId: "admin");
        fake.Push(Concat([0x82], U32(0xACE), U32(1234)));

        Assert.Equal(DeliveryStatus.Sent, sent.Status);
        Assert.Equal(0xACEu, sent.ExpectedAck);
        Assert.Equal(alice.Prefix.Hex, sent.ContactKeyPrefix);
        Assert.Equal(alice.Prefix.ToBytes(), fake.Received.Last(f => f[0] == 0x02)[7..13]);
        var row = await delivered.Task.WaitAsync(Wait);
        Assert.Equal(sent.Id, row.Id);
        Assert.Equal(1234, row.RoundTripMs);
    }

    [Fact]
    public async Task Channel_text_limit_accounts_for_the_name_prefix_the_radio_adds()
    {
        var (radio, fake, client, db) = await AttachedAsync();
        await using var _c = client;
        await using var _d = db;
        var limit = 160 - "Base: ".Length;

        await radio.SendChannelAsync(0, new string('x', limit));
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => radio.SendChannelAsync(0, new string('x', limit + 1)));

        Assert.Contains($"limit is {limit}", ex.Message, StringComparison.Ordinal);
        Assert.Single(fake.Received, f => f[0] == 0x03);
        Assert.Single(await radio.Store.GetMessagesAsync(channelIndex: 0), m => m.Direction == MessageDirection.Outgoing);
    }

    [Fact]
    public async Task Radio_errors_mark_the_message_failed()
    {
        var fake = ScriptedRadio().On(0x03, Error(2)); // channel not found
        var (radio, _, client, db) = await AttachedAsync(fake);
        await using var _c = client;
        await using var _d = db;

        await Assert.ThrowsAsync<MeshCoreCommandException>(() => radio.SendChannelAsync(7, "into the void"));

        var row = Assert.Single(await radio.Store.GetMessagesAsync(channelIndex: 7));
        Assert.Equal(DeliveryStatus.Failed, row.Status);
        Assert.Contains("NotFound", row.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sending_without_a_radio_stores_nothing()
    {
        await using var db = await TestDb.CreateAsync();
        var radio = new MeshRadio(new MeshStore(db), NullLogger<MeshRadio>.Instance);

        await Assert.ThrowsAsync<MeshCoreDisconnectedException>(() => radio.SendChannelAsync(0, "hello"));
        Assert.Empty(await radio.Store.GetMessagesAsync());
    }
}

public class RadioConnectionServiceTests
{
    [Fact]
    public async Task Backs_off_when_another_client_takes_the_radio_then_reconnects()
    {
        await using var db = await TestDb.CreateAsync();
        var radio = new MeshRadio(new MeshStore(db), NullLogger<MeshRadio>.Instance);
        var options = Options.Create(new RadioOptions
        {
            Connection = "192.168.1.212:5000",
            TakeoverBackoff = TimeSpan.FromMilliseconds(300),
            ReconnectDelays = [TimeSpan.FromMilliseconds(50)],
            PollInterval = TimeSpan.FromMinutes(5),
        });
        var radios = new Queue<FakeRadio>([MinimalRadio(), MinimalRadio()]);
        var first = radios.Peek();
        var service = new RadioConnectionService(radio, options, NullLoggerFactory.Instance, TimeProvider.System, NullLogger<RadioConnectionService>.Instance)
        {
            TransportFactory = _ => radios.Dequeue(),
        };
        var states = new ConcurrentQueue<RadioStatus>();
        radio.StatusChanged += (_, s) => states.Enqueue(s);

        await service.StartAsync(CancellationToken.None);
        await WaitForAsync(() => radio.Status.State == RadioState.Connected);
        first.CloseByRemote();
        await WaitForAsync(() => radio.Status.State == RadioState.InUseElsewhere);
        var takenOver = radio.Status;
        await WaitForAsync(() => radio.Status.State == RadioState.Connected && radios.Count == 0);
        await service.StopAsync(CancellationToken.None);

        Assert.Contains("another app", takenOver.LastError, StringComparison.Ordinal);
        Assert.NotNull(takenOver.NextAttempt);
        Assert.Equal(RadioState.Stopped, radio.Status.State);
        Assert.Equal(
            [RadioState.Connecting, RadioState.Connected, RadioState.InUseElsewhere, RadioState.Connecting, RadioState.Connected, RadioState.Stopped],
            states.Select(s => s.State));
    }

    [Fact]
    public async Task Retries_after_connection_errors()
    {
        await using var db = await TestDb.CreateAsync();
        var radio = new MeshRadio(new MeshStore(db), NullLogger<MeshRadio>.Instance);
        var options = Options.Create(new RadioOptions
        {
            Connection = "192.168.1.212",
            ReconnectDelays = [TimeSpan.FromMilliseconds(50)],
        });
        var attempts = 0;
        var service = new RadioConnectionService(radio, options, NullLoggerFactory.Instance, TimeProvider.System, NullLogger<RadioConnectionService>.Instance)
        {
            TransportFactory = _ => ++attempts < 3 ? new FailingTransport() : MinimalRadio(),
        };

        await service.StartAsync(CancellationToken.None);
        await WaitForAsync(() => radio.Status.State == RadioState.Connected);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(3, attempts);
    }

    private static FakeRadio MinimalRadio() => new FakeRadio().WithHandshake()
        .On(0x04, [0x02, 0, 0, 0, 0], [0x04, 0, 0, 0, 0])
        .On(0x1F, Error(2))
        .On(0x0A, [0x0A]);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "Timed out waiting for the radio state.");
            await Task.Delay(20);
        }
    }

    private sealed class FailingTransport : ITransport
    {
        public string Description => "failing";

        public System.Threading.Channels.ChannelReader<byte[]> Frames { get; } = System.Threading.Channels.Channel.CreateUnbounded<byte[]>().Reader;

        public Task OpenAsync(CancellationToken cancellationToken = default) =>
            throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused);

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
