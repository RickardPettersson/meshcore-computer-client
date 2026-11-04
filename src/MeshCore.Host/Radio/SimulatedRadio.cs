using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using MeshCore.Client.Transport;
using MeshCore.Protocol;
using Microsoft.Extensions.Logging;

namespace MeshCore.Host.Radio;

/// <summary>
/// <c>MeshCore:Connection = "simulator"</c>: a pretend companion radio for demos and UI work without
/// hardware (and without transmitting anything). It speaks the real protocol, has a few contacts and
/// channels, receives a message now and then, ACKs direct messages, and sometimes replies.
/// </summary>
public sealed record SimulatorEndpoint(TimeSpan MessageInterval) : MeshCoreEndpoint
{
    public const string Name = "simulator";

    public override ITransport CreateTransport(ILoggerFactory? loggerFactory = null) => new SimulatedRadio(MessageInterval, TimeProvider.System);

    public override string ToString() => Name;
}

public sealed class SimulatedRadio(TimeSpan messageInterval, TimeProvider time) : ITransport
{
    private sealed record Node(string Name, byte Seed, AdvertType Type);

    private static readonly Node[] Nodes =
    [
        new("Alice", 0x10, AdvertType.Chat),
        new("Bob", 0x30, AdvertType.Chat),
        new("Hilltop RPT", 0x50, AdvertType.Repeater),
    ];

    private static readonly string[] Chatter =
    [
        "Anyone on frequency tonight?", "Testing from the hill, 3 hops", "Good morning mesh!", "Battery at 80%, all good",
        "Heading out on the trail, will check in later", "Signal looks great here", "Who's running the new repeater?",
    ];

    private readonly Channel<byte[]> _toApp = Channel.CreateUnbounded<byte[]>();
    private readonly ConcurrentQueue<byte[]> _queue = new();
    private readonly ConcurrentDictionary<byte, (string Name, byte[] Secret)> _channels = new()
    {
        [0] = ("Public", Convert.FromHexString("8b3387e9c5cdea6ac9e5edbaa115cd72")),
        [1] = ("Simulator test", Enumerable.Repeat((byte)0x42, 16).ToArray()),
    };
    private readonly CancellationTokenSource _stop = new();
    private uint _ack = 0x1000;
    private int _tick;

    public string Description => SimulatorEndpoint.Name;

    public ChannelReader<byte[]> Frames => _toApp.Reader;

    public Task OpenAsync(CancellationToken cancellationToken = default)
    {
        _ = ChatterLoopAsync();
        return Task.CompletedTask;
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        var cmd = payload.Span;
        switch ((CommandCode)cmd[0])
        {
            case CommandCode.AppStart:
                Reply(Concat([5, (byte)AdvertType.Chat, 14, 22], Key(0x01), I32(59_329_323), I32(18_068_581), [0, 0, 0, 0],
                    U32(869_618), U32(62_500), [8, 5], Text("Simulator")));
                break;
            case CommandCode.DeviceQuery:
                Reply(Concat([13, 14, 175, 8], U32(0), Fixed("04 Nov 2026", 12), Fixed("Simulated radio", 40), Fixed("v1.18.0-sim", 20), [0, 0]));
                break;
            case CommandCode.GetContacts:
                Reply(Concat([2], U32((uint)Nodes.Length)));
                foreach (var n in Nodes)
                {
                    Reply(Concat([3], Key(n.Seed), [(byte)n.Type, 0, n.Type == AdvertType.Repeater ? (byte)1 : (byte)0xFF], new byte[64],
                        Fixed(n.Name, 32), U32(Now), I32(59_330_000 + n.Seed * 100), I32(18_070_000), U32(Now)));
                }
                Reply(Concat([4], U32(Now)));
                break;
            case CommandCode.GetChannel:
                var idx = cmd[1];
                if (idx >= 8)
                {
                    Reply([1, (byte)ErrorCode.NotFound]);
                }
                else
                {
                    var (name, secret) = _channels.TryGetValue(idx, out var ch) ? ch : ("", new byte[16]);
                    Reply(Concat([18, idx], Fixed(name, 32), secret));
                }
                break;
            case CommandCode.SetChannel:
                var name2 = Encoding.UTF8.GetString(cmd.Slice(2, 32)).TrimEnd('\0');
                if (name2.Length == 0)
                {
                    _channels.TryRemove(cmd[1], out _);
                }
                else
                {
                    _channels[cmd[1]] = (name2, cmd.Slice(34, 16).ToArray());
                }
                Reply([0]);
                break;
            case CommandCode.SyncNextMessage:
                Reply(_queue.TryDequeue(out var next) ? next : [10]);
                break;
            case CommandCode.SendTextMessage:
                var ack = Interlocked.Increment(ref _ack);
                var recipient = cmd.Slice(7, 6).ToArray();
                var to = Nodes.FirstOrDefault(n => Key(n.Seed).AsSpan(0, 6).SequenceEqual(recipient));
                var text = Encoding.UTF8.GetString(cmd[13..]);
                Reply(Concat([6, 0], U32(ack), U32(6000)));
                _ = DeliverAsync(ack, to, text);
                break;
            case CommandCode.GetBatteryAndStorage:
                Reply(Concat([12], U16(4050), U32(24), U32(3169)));
                break;
            case CommandCode.GetDeviceTime:
                Reply(Concat([9], U32(Now)));
                break;
            case CommandCode.SendChannelTextMessage or CommandCode.SetDeviceTime or CommandCode.SendSelfAdvert
                or CommandCode.RemoveContact or CommandCode.ResetPath or CommandCode.SetAdvertName:
                Reply([0]);
                break;
            case CommandCode.Reboot:
                _toApp.Writer.TryComplete(new EndOfStreamException("simulator rebooted"));
                break;
            default:
                Reply([1, (byte)ErrorCode.UnsupportedCommand]);
                break;
        }
        return ValueTask.CompletedTask;
    }

    private async Task DeliverAsync(uint ack, Node? to, string text)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(1500), time, _stop.Token);
        Reply(Concat([0x82], U32(ack), U32(1500)));
        if (to is { Type: AdvertType.Chat } && Random.Shared.Next(2) == 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), time, _stop.Token);
            Enqueue(DirectMessage(to, $"Got it: \"{(text.Length > 40 ? text[..40] + "…" : text)}\""));
        }
    }

    private async Task ChatterLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(messageInterval, time);
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                var from = Nodes[Random.Shared.Next(2)];
                var line = Chatter[_tick++ % Chatter.Length];
                Enqueue(_tick % 4 == 0
                    ? DirectMessage(from, line)
                    : Concat([0x11, (byte)(sbyte)Random.Shared.Next(-40, 40), 0, 0, 0, (byte)Random.Shared.Next(0, 4), 0], U32(Now), Text($"{from.Name}: {line}")));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Enqueue(byte[] message)
    {
        _queue.Enqueue(message);
        Reply([0x83]); // PUSH_CODE_MSG_WAITING
    }

    private byte[] DirectMessage(Node from, string text) =>
        Concat([0x10, (byte)(sbyte)Random.Shared.Next(-20, 40), 0, 0], Key(from.Seed)[..6], [0xFF, 0], U32(Now), Text(text));

    private void Reply(byte[] frame) => _toApp.Writer.TryWrite(frame);

    private uint Now => (uint)time.GetUtcNow().ToUnixTimeSeconds();

    public ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _toApp.Writer.TryComplete();
        _stop.Dispose();
        return ValueTask.CompletedTask;
    }

    private static byte[] Key(byte seed) => [.. Enumerable.Range(0, 32).Select(i => (byte)(seed + i))];

    private static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);

    private static byte[] Fixed(string s, int size)
    {
        var b = new byte[size];
        Encoding.UTF8.GetBytes(s).CopyTo(b, 0);
        return b;
    }

    private static byte[] U16(ushort v)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        return b;
    }

    private static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }

    private static byte[] I32(int v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, v);
        return b;
    }

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];
}
