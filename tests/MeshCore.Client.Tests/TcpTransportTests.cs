using System.Net;
using System.Net.Sockets;
using MeshCore.Client.Transport;
using MeshCore.Protocol.Framing;
using static MeshCore.Client.Tests.FakeRadio;

namespace MeshCore.Client.Tests;

public class TcpTransportTests
{
    /// <summary>A loopback listener that behaves like the WiFi firmware's single-client server.</summary>
    private sealed class FakeWifiRadio : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public FakeWifiRadio() => _listener.Start();

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public async Task<TcpClient> AcceptAsync() => await _listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));

        public ValueTask DisposeAsync()
        {
            _listener.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static async Task<byte[]> ReadFrameAsync(NetworkStream stream)
    {
        var decoder = new FrameDecoder(FrameMarker.ToRadio);
        var buf = new byte[256];
        while (true)
        {
            var n = await stream.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            if (decoder.Decode(buf.AsSpan(0, n)) is [var frame, ..])
            {
                return frame;
            }
        }
    }

    private static MeshCoreClientOptions Options() => new()
    {
        CommandTimeout = TimeSpan.FromSeconds(2),
        HandshakeAttempts = 1,
        SyncDeviceClock = false,
    };

    [Fact]
    public async Task Client_handshakes_over_tcp_with_serial_framing()
    {
        await using var radio = new FakeWifiRadio();
        var transport = new TcpTransport(new TcpTransportOptions { Host = "127.0.0.1", Port = radio.Port });
        await using var client = new MeshCoreClient(transport, Options());

        var server = Task.Run(async () =>
        {
            using var conn = await radio.AcceptAsync();
            var stream = conn.GetStream();

            var appStart = await ReadFrameAsync(stream);
            Assert.Equal(0x01, appStart[0]);
            // Reply split across two writes, like a TCP segment boundary in the middle of a frame.
            var reply = FrameEncoder.Encode(SelfInfo("Heltec V4"), FrameMarker.FromRadio);
            await stream.WriteAsync(reply.AsMemory(0, 10));
            await stream.FlushAsync();
            await Task.Delay(50);
            await stream.WriteAsync(reply.AsMemory(10));

            Assert.Equal(0x16, (await ReadFrameAsync(stream))[0]);
            await stream.WriteAsync(FrameEncoder.Encode(DeviceInfo(40), FrameMarker.FromRadio));
            await Task.Delay(500);
        });

        await client.ConnectAsync();

        Assert.Equal("Heltec V4", client.SelfInfo?.Name);
        Assert.Equal(40, client.DeviceInfo?.MaxChannels);
        Assert.Equal($"127.0.0.1:{radio.Port}", client.TransportDescription);
        await server;
    }

    [Fact]
    public async Task Radio_closing_the_socket_raises_disconnected()
    {
        // The firmware drops the current client when another one connects.
        await using var radio = new FakeWifiRadio();
        await using var transport = new TcpTransport(new TcpTransportOptions { Host = "127.0.0.1", Port = radio.Port });
        await transport.OpenAsync();

        using (await radio.AcceptAsync())
        {
        }

        var ex = await Assert.ThrowsAsync<EndOfStreamException>(async () =>
        {
            await foreach (var _ in transport.Frames.ReadAllAsync().WithCancellation(new CancellationTokenSource(5000).Token))
            {
            }
        });
        Assert.Contains("closed the connection", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refused_connection_throws_socket_exception()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        await using var transport = new TcpTransport(new TcpTransportOptions { Host = "127.0.0.1", Port = port });

        await Assert.ThrowsAsync<SocketException>(() => transport.OpenAsync());
    }

    [Fact]
    public async Task Unreachable_host_times_out_with_a_clear_message()
    {
        // 192.0.2.0/24 is TEST-NET-1: guaranteed not routable.
        await using var transport = new TcpTransport(new TcpTransportOptions
        {
            Host = "192.0.2.1",
            ConnectTimeout = TimeSpan.FromMilliseconds(200),
        });

        try
        {
            await transport.OpenAsync();
            Assert.Fail("Expected the connect to fail.");
        }
        catch (MeshCoreTimeoutException ex)
        {
            Assert.Contains("192.0.2.1:5000", ex.Message, StringComparison.Ordinal);
        }
        catch (SocketException)
        {
            // Some networks reject TEST-NET immediately (no route); that's fine too.
        }
    }
}

public class MeshCoreEndpointTests
{
    [Theory]
    [InlineData("192.168.1.212:5000", "192.168.1.212", 5000)]
    [InlineData("192.168.1.212", "192.168.1.212", 5000)]
    [InlineData("tcp://192.168.1.212:4403", "192.168.1.212", 4403)]
    [InlineData("tcp://meshcore.local", "meshcore.local", 5000)]
    [InlineData("meshcore.local:5000", "meshcore.local", 5000)]
    [InlineData("[fe80::1]:5000", "fe80::1", 5000)]
    public void Parses_tcp_addresses(string text, string host, int port)
    {
        var endpoint = Assert.IsType<TcpEndpoint>(MeshCoreEndpoint.Parse(text));

        Assert.Equal(host, endpoint.Host);
        Assert.Equal(port, endpoint.Port);
    }

    [Theory]
    [InlineData("COM11", "COM11")]
    [InlineData("com3", "com3")]
    [InlineData("/dev/ttyACM0", "/dev/ttyACM0")]
    [InlineData("serial://COM5", "COM5")]
    public void Parses_serial_ports(string text, string port)
    {
        Assert.Equal(port, Assert.IsType<SerialEndpoint>(MeshCoreEndpoint.Parse(text)).PortName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("meshcore.local")] // ambiguous without tcp:// or a port
    [InlineData("host:notaport")]
    [InlineData("serial://")]
    public void Rejects_unclear_input(string text)
    {
        Assert.False(MeshCoreEndpoint.TryParse(text, out _));
        Assert.Throws<FormatException>(() => MeshCoreEndpoint.Parse(text));
    }

    [Fact]
    public void Round_trips_through_ToString()
    {
        Assert.Equal("tcp://192.168.1.212:5000", MeshCoreEndpoint.Parse("192.168.1.212").ToString());
        Assert.Equal("tcp://[fe80::1]:5000", MeshCoreEndpoint.Parse("[fe80::1]:5000").ToString());
        Assert.Equal("serial://COM11", MeshCoreEndpoint.Parse("COM11").ToString());
    }
}
