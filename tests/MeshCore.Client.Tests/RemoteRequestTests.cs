using MeshCore.Protocol;
using MeshCore.Protocol.Models;
using static MeshCore.Client.Tests.FakeRadio;

namespace MeshCore.Client.Tests;

public class RemoteRequestTests
{
    private static readonly PublicKey Repeater = new(Key(0x50));

    private static async Task<(MeshCoreClient, FakeRadio)> ConnectedAsync(Action<FakeRadio> setup)
    {
        var radio = new FakeRadio().WithHandshake();
        setup(radio);
        var client = new MeshCoreClient(radio, new MeshCoreClientOptions
        {
            CommandTimeout = TimeSpan.FromSeconds(1),
            MinRemoteReplyWait = TimeSpan.FromMilliseconds(300),
        });
        await client.ConnectAsync();
        return (client, radio);
    }

    private static byte[] Sent(uint estimateMs = 0) => Concat([0x06, 0x00], U32(1), U32(estimateMs));

    [Fact]
    public async Task Login_waits_for_the_servers_answer()
    {
        var (client, _) = await ConnectedAsync(r => r.On(0x1A, Sent(), Concat([0x85, 0x01], Key(0x50)[..6], U32(1), [0, 1])));
        await using var _c = client;

        var result = await client.LoginAsync(Repeater, "secret");

        Assert.True(result.Success);
        Assert.True(result.IsAdmin);
    }

    [Fact]
    public async Task An_answer_that_beats_the_sent_reply_is_not_missed()
    {
        // The push arrives before RESP_CODE_SENT: the waiter must already be registered.
        var (client, _) = await ConnectedAsync(r => r.On(0x1A, Concat([0x86, 0x00], Key(0x50)[..6]), Sent()));
        await using var _c = client;

        var result = await client.LoginAsync(Repeater, "wrong");

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Answers_from_other_nodes_are_ignored()
    {
        var (client, radio) = await ConnectedAsync(r => r.On(0x1B, Sent(),
            Concat([0x87, 0], Key(0x10)[..6], new byte[56]),        // someone else's status
            Concat([0x87, 0], Key(0x50)[..6], U16(4000), new byte[54])));
        await using var _c = client;

        var status = await client.RequestStatusAsync(Repeater);

        Assert.Equal(4000, status.Stats?.BatteryMillivolts);
    }

    [Fact]
    public async Task No_answer_times_out_with_a_helpful_message()
    {
        var (client, _) = await ConnectedAsync(r => r.On(0x27, Sent()));
        await using var _c = client;

        var ex = await Assert.ThrowsAsync<MeshCoreTimeoutException>(() => client.RequestTelemetryAsync(Repeater));

        Assert.Contains("No answer from the remote node", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trace_matches_on_its_random_tag()
    {
        var (client, radio) = await ConnectedAsync(r => r.On(0x24, cmd =>
            [Sent(), Concat([0x89, 0, 1, 0], cmd[1..5], U32(0), [0x50], [20], [24])]));
        await using var _c = client;

        var trace = await client.TraceAsync(new byte[] { 0x50 }, 1);

        Assert.Equal(["50"], trace.HopHashes);
        Assert.Equal([5.0], trace.HopSnrs);
    }

    [Fact]
    public async Task Disconnecting_fails_pending_remote_requests()
    {
        var (client, radio) = await ConnectedAsync(r => r.On(0x1B, Sent(60_000)));
        await using var _c = client;

        var pending = client.RequestStatusAsync(Repeater);
        await Task.Delay(100);
        radio.Unplug();

        await Assert.ThrowsAsync<MeshCoreDisconnectedException>(() => pending);
    }

    [Fact]
    public async Task Cli_commands_go_out_as_text_type_1()
    {
        var (client, radio) = await ConnectedAsync(r => r.On(0x02, Sent()));
        await using var _c = client;

        await client.SendCliCommandAsync(Repeater.Prefix, "ver");

        var cmd = radio.Received.Last(f => f[0] == 0x02);
        Assert.Equal((byte)TextType.CliData, cmd[1]);
        Assert.Equal("ver", System.Text.Encoding.UTF8.GetString(cmd[13..]));
    }

    private static byte[] U16(ushort v) => [(byte)v, (byte)(v >> 8)];
}

public class CompanionCliClientTests
{
    [Fact]
    public async Task Run_cli_command_returns_the_reply_text()
    {
        var radio = new FakeRadio().WithHandshake().On(0x42, cmd => [FakeRadio.Concat([0x1D], System.Text.Encoding.UTF8.GetBytes("> connected"))]);
        await using var client = new MeshCoreClient(radio, new MeshCoreClientOptions { CommandTimeout = TimeSpan.FromSeconds(1) });
        await client.ConnectAsync();

        Assert.Equal("> connected", await client.RunCliCommandAsync("get wifi.status"));
        Assert.Equal("get wifi.status", System.Text.Encoding.UTF8.GetString(radio.Received.Last()[1..]));
    }
}
