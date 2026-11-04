using MeshCore.Client.Discovery;
using MeshCore.Client.Transport;
using MeshCore.Protocol;
using MeshCore.Protocol.Frames;
using Microsoft.Extensions.Time.Testing;
using static MeshCore.Client.Tests.FakeRadio;

namespace MeshCore.Client.Tests;

public class MeshCoreClientTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_762_000_000);

    private static MeshCoreClientOptions FastOptions() => new()
    {
        CommandTimeout = TimeSpan.FromMilliseconds(300),
        ContactsTimeout = TimeSpan.FromMilliseconds(300),
        TimeProvider = new FakeTimeProvider(Now),
    };

    private static async Task<(MeshCoreClient Client, FakeRadio Radio)> ConnectedAsync(Action<FakeRadio>? setup = null)
    {
        var radio = new FakeRadio().WithHandshake();
        setup?.Invoke(radio);
        var client = new MeshCoreClient(radio, FastOptions());
        await client.ConnectAsync();
        return (client, radio);
    }

    [Fact]
    public async Task Connect_runs_the_handshake_in_order()
    {
        var (client, radio) = await ConnectedAsync();
        await using var _ = client;

        Assert.True(client.IsConnected);
        Assert.Equal("Fake Node", client.SelfInfo?.Name);
        Assert.Equal("Fake Board", client.DeviceInfo?.Model);
        Assert.Equal(8, client.DeviceInfo?.MaxChannels);

        var sent = radio.Received.Select(f => f[0]).ToArray();
        Assert.Equal(new byte[] { 0x01, 0x16, 0x06 }, sent);
        Assert.Equal(U32(1_762_000_000), radio.Received.Last()[1..5]);
    }

    [Fact]
    public async Task Connect_tolerates_a_radio_clock_that_is_ahead()
    {
        var (client, _) = await ConnectedAsync(r => r.On(0x06, Error(6)));
        await using var _c = client;

        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task Connect_retries_app_start_while_the_board_boots()
    {
        var calls = 0;
        var radio = new FakeRadio().WithHandshake()
            .On(0x01, _ => ++calls < 2 ? [] : [SelfInfo("Late Riser")]);
        await using var client = new MeshCoreClient(radio, FastOptions());

        await client.ConnectAsync();

        Assert.Equal(2, calls);
        Assert.Equal("Late Riser", client.SelfInfo?.Name);
    }

    [Fact]
    public async Task Connect_times_out_when_nothing_answers()
    {
        var radio = new FakeRadio().On(0x01, _ => []);
        await using var client = new MeshCoreClient(radio, FastOptions());

        await Assert.ThrowsAsync<MeshCoreTimeoutException>(() => client.ConnectAsync());
    }

    [Fact]
    public async Task GetContacts_collects_the_whole_list()
    {
        var (client, _) = await ConnectedAsync(r => r.On(0x04,
            [0x02, 2, 0, 0, 0], Contact(10, "Alice"), Contact(50, "Bob"), [0x04, 2, 0, 0, 0]));
        await using var _c = client;

        var contacts = await client.GetContactsAsync();

        Assert.Equal(["Alice", "Bob"], contacts.Select(c => c.Name));
    }

    [Fact]
    public async Task Pushes_in_the_middle_of_a_reply_go_to_the_event()
    {
        var (client, radio) = await ConnectedAsync(r => r.On(0x04,
            [0x02, 1, 0, 0, 0], [0x83], Contact(10, "Alice"), [0x04, 0, 0, 0, 0]));
        await using var _c = client;
        var pushed = new TaskCompletionSource<MeshFrame>();
        client.PushReceived += (_, e) => pushed.TrySetResult(e.Frame);

        var contacts = await client.GetContactsAsync();

        Assert.Single(contacts);
        Assert.IsType<MessagesWaitingPush>(await pushed.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Push_handlers_can_call_back_into_the_client()
    {
        var (client, radio) = await ConnectedAsync(r => r
            .On(0x0A, Sequence(ChannelMessage(0, "Alice: hi"), [0x0A])));
        await using var _c = client;
        var drained = new TaskCompletionSource<IReadOnlyList<MeshFrame>>();
        client.PushReceived += async (_, e) =>
        {
            if (e.Frame is MessagesWaitingPush)
            {
                drained.TrySetResult(await client.DrainMessagesAsync());
            }
        };

        radio.Push([0x83]);

        var messages = await drained.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var message = Assert.IsType<ChannelMessage>(Assert.Single(messages));
        Assert.Equal("Alice", message.SenderName);
    }

    [Fact]
    public async Task Error_replies_become_command_exceptions()
    {
        var (client, _) = await ConnectedAsync(r => r.On(0x1F, Error(2)));
        await using var _c = client;

        var ex = await Assert.ThrowsAsync<MeshCoreCommandException>(() => client.GetChannelAsync(9));

        Assert.Equal(CommandCode.GetChannel, ex.Command);
        Assert.Equal(ErrorCode.NotFound, ex.Error);
    }

    [Fact]
    public async Task GetChannels_skips_empty_slots_and_stops_at_not_found()
    {
        var (client, _) = await ConnectedAsync(r => r.On(0x1F, cmd => cmd[1] switch
        {
            0 => [ChannelInfo(0, "Public")],
            1 => [ChannelInfo(1, "")],
            2 => [ChannelInfo(2, "Hiking")],
            _ => [Error(2)],
        }));
        await using var _c = client;

        var channels = await client.GetChannelsAsync();

        Assert.Equal(["Public", "Hiking"], channels.Select(c => c.Name));
    }

    [Fact]
    public async Task Stray_replies_from_an_earlier_command_are_ignored()
    {
        // A late CURR_TIME (from a timed-out command) arrives before the real BATT reply.
        var (client, _) = await ConnectedAsync(r => r.On(0x14, Concat([0x09], U32(1)), Concat([0x0C, 0xAC, 0x0F], U32(1), U32(2))));
        await using var _c = client;

        var battery = await client.GetBatteryAndStorageAsync();

        Assert.Equal(4012, battery.BatteryMillivolts);
    }

    [Fact]
    public async Task Commands_run_one_at_a_time()
    {
        var (client, radio) = await ConnectedAsync(r => r.On(0x1F, cmd => [ChannelInfo(cmd[1], $"ch{cmd[1]}")]));
        await using var _c = client;

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => client.GetChannelAsync((byte)i)));

        Assert.Equal(Enumerable.Range(0, 8).Select(i => $"ch{i}"), results.Select(r => r.Name));
    }

    [Fact]
    public async Task Unplugging_fails_the_pending_command_and_raises_disconnected()
    {
        var (client, radio) = await ConnectedAsync(r => r.On(0x14, _ => []));
        await using var _c = client;
        var disconnected = new TaskCompletionSource<Exception?>();
        client.Disconnected += (_, e) => disconnected.TrySetResult(e.Error);

        var pending = client.GetBatteryAndStorageAsync();
        radio.Unplug();

        await Assert.ThrowsAsync<MeshCoreDisconnectedException>(() => pending);
        Assert.IsType<IOException>(await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(client.IsConnected);
        await Assert.ThrowsAsync<MeshCoreDisconnectedException>(() => client.GetDeviceTimeAsync());
    }

    [Fact]
    public async Task SendTextMessage_returns_the_expected_ack()
    {
        var (client, radio) = await ConnectedAsync(r => r.On(0x02, Concat([0x06, 0x00], U32(0xCAFE), U32(5000))));
        await using var _c = client;
        var key = new Protocol.Models.PublicKey(Key(10));

        var sent = await client.SendTextMessageAsync(key.Prefix, "hello");

        Assert.Equal(0xCAFEu, sent.ExpectedAck);
        Assert.Equal(Key(10)[..6], radio.Received.Last()[7..13]);
    }

    [Fact]
    public async Task Dispose_closes_the_transport()
    {
        var (client, radio) = await ConnectedAsync();

        await client.DisposeAsync();

        Assert.True(radio.Disposed);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Locator_tries_likely_ports_first_and_skips_silent_ones()
    {
        var silent = new FakeRadio().On(0x01, _ => []);
        var radio = new FakeRadio().WithHandshake("Found Me");
        var transports = new Dictionary<string, ITransport> { ["COM11"] = silent, ["COM3"] = radio };
        var locator = new CompanionLocator(FastOptions(), transportFactory: p => transports[p.PortName]);
        var ports = CompanionLocator.Candidates(
        [
            new SerialPortInfo("COM1", 0x8086, 0x0001),   // unrelated USB device: never probed
            new SerialPortInfo("COM3", 0x10C4, 0xEA60),   // CP210x bridge
            new SerialPortInfo("COM11", 0x239A, 0x8029),  // nRF52 native USB: tried first
        ]);

        var found = await locator.FindAsync(ports);

        Assert.Equal(["COM11", "COM3"], ports.Select(p => p.PortName));
        Assert.NotNull(found);
        await using var _ = found.Client;
        Assert.Equal("COM3", found.Port.PortName);
        Assert.Equal("Found Me", found.Client.SelfInfo?.Name);
        Assert.True(silent.Opened);
        Assert.True(silent.Disposed);
    }

    private static Func<byte[], IEnumerable<byte[]>> Sequence(params byte[][] replies)
    {
        var i = 0;
        return _ => [replies[Math.Min(i++, replies.Length - 1)]];
    }
}
