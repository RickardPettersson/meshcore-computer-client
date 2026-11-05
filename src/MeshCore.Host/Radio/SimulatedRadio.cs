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
    private readonly ConcurrentDictionary<byte, (string Name, byte Flags)> _contactEdits = new();
    private uint _ack = 0x1000;
    private int _tick;

    // The simulated node's own settings, changeable through the protocol like a real radio's.
    private string _name = "Simulator";
    private int _lat = 59_329_323, _lon = 18_068_581;
    private sbyte _txPower = 14;
    private uint _freqKhz = 869_618, _bwHz = 62_500;
    private byte _sf = 8, _cr = 5;
    private byte _manualAdd, _telemetryModes, _locPolicy, _multiAcks;
    private uint _rxDelay, _airtime = 1000;
    private readonly HashSet<byte> _adminLogins = [];

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
                Reply(Concat([5, (byte)AdvertType.Chat, unchecked((byte)_txPower), 22], Key(0x01), I32(_lat), I32(_lon),
                    [_multiAcks, _locPolicy, _telemetryModes, _manualAdd], U32(_freqKhz), U32(_bwHz), [_sf, _cr], Text(_name)));
                break;
            case CommandCode.SetAdvertName:
                _name = Encoding.UTF8.GetString(cmd[1..]).TrimEnd('\0');
                Reply([0]);
                break;
            case CommandCode.SetAdvertLatLon:
                _lat = BinaryPrimitives.ReadInt32LittleEndian(cmd[1..]);
                _lon = BinaryPrimitives.ReadInt32LittleEndian(cmd[5..]);
                Reply([0]);
                break;
            case CommandCode.SetRadioTxPower:
                var power = unchecked((sbyte)cmd[1]);
                if (power is < -9 or > 22)
                {
                    Reply([1, (byte)ErrorCode.IllegalArgument]);
                    break;
                }
                _txPower = power;
                Reply([0]);
                break;
            case CommandCode.SetRadioParams:
                (_freqKhz, _bwHz, _sf, _cr) = (BinaryPrimitives.ReadUInt32LittleEndian(cmd[1..]), BinaryPrimitives.ReadUInt32LittleEndian(cmd[5..]), cmd[9], cmd[10]);
                Reply([0]);
                break;
            case CommandCode.GetTuningParams:
                Reply(Concat([23], U32(_rxDelay), U32(_airtime)));
                break;
            case CommandCode.SetTuningParams:
                (_rxDelay, _airtime) = (BinaryPrimitives.ReadUInt32LittleEndian(cmd[1..]), BinaryPrimitives.ReadUInt32LittleEndian(cmd[5..]));
                Reply([0]);
                break;
            case CommandCode.SetOtherParams:
                _manualAdd = cmd[1];
                if (cmd.Length >= 5)
                {
                    (_telemetryModes, _locPolicy, _multiAcks) = (cmd[2], cmd[3], cmd[4]);
                }
                Reply([0]);
                break;
            case CommandCode.GetContactByKey or CommandCode.AddUpdateContact or CommandCode.ShareContact
                or CommandCode.SendLogin or CommandCode.SendStatusRequest or CommandCode.Logout:
                HandleContactCommand(cmd);
                break;
            case CommandCode.SendTelemetryRequest:
                var telemetryNode = NodeByKey(cmd.Slice(4, 32));
                Reply(Concat([6, 0], U32(Interlocked.Increment(ref _ack)), U32(4000)));
                if (telemetryNode is not null)
                {
                    // LPP: ch1 voltage 4.10 V, ch1 temperature 21.5 °C (big-endian values)
                    _ = PushLaterAsync(Concat([0x8B, 0], Key(telemetryNode.Seed)[..6], [1, 116, 0x01, 0x9A, 1, 103, 0x00, 0xD7]));
                }
                break;
            case CommandCode.SendTracePath:
                var tag = cmd.Slice(1, 4).ToArray();
                var hops = cmd[10..].ToArray();
                Reply(Concat([6, 0], tag, U32(3000)));
                var snrs = hops.Select(_ => (byte)(sbyte)Random.Shared.Next(-20, 40)).ToArray();
                _ = PushLaterAsync(Concat([0x89, 0, (byte)hops.Length, cmd[9]], tag, cmd.Slice(5, 4).ToArray(), hops, snrs, [(byte)(sbyte)24]));
                break;
            case CommandCode.ExportContact:
                // A stand-in advert: type byte, key, timestamp, 64-byte signature, then the name.
                var exported = cmd.Length > 1 ? cmd.Slice(1, 32).ToArray() : Key(0x01);
                Reply(Concat([11, 0x11], exported, U32(Now), new byte[64], Text(cmd.Length > 1 ? "contact" : _name)));
                break;
            case CommandCode.ImportContact:
                Reply(cmd.Length > 99 ? [0] : [1, (byte)ErrorCode.IllegalArgument]);
                break;
            case CommandCode.ExportPrivateKey or CommandCode.ImportPrivateKey:
                Reply([15]); // RESP_CODE_DISABLED, like most release builds
                break;
            case CommandCode.DeviceQuery:
                Reply(Concat([13, 14, 175, 8], U32(0), Fixed("04 Nov 2026", 12), Fixed("Simulated radio", 40), Fixed("v1.18.0-sim", 20), [0, 0]));
                break;
            case CommandCode.GetContacts:
                Reply(Concat([2], U32((uint)Nodes.Length)));
                foreach (var n in Nodes)
                {
                    Reply(ContactFrame(3, n));
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
                var recipient = cmd.Slice(7, 6).ToArray();
                var to = Nodes.FirstOrDefault(n => Key(n.Seed).AsSpan(0, 6).SequenceEqual(recipient));
                var text = Encoding.UTF8.GetString(cmd[13..]);
                if (cmd[1] == (byte)TextType.CliData)
                {
                    Reply(Concat([6, 0], U32(0), U32(4000))); // CLI: no ACK, like the firmware
                    _ = CliReplyAsync(to, text);
                    break;
                }
                var ack = Interlocked.Increment(ref _ack);
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
                or CommandCode.RemoveContact or CommandCode.ResetPath:
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

    private void HandleContactCommand(ReadOnlySpan<byte> cmd)
    {
        var node = NodeByKey(cmd.Slice(1, 32));
        if (node is null)
        {
            Reply([1, (byte)ErrorCode.NotFound]);
            return;
        }

        switch ((CommandCode)cmd[0])
        {
            case CommandCode.GetContactByKey:
                Reply(ContactFrame(3, node));
                break;
            case CommandCode.AddUpdateContact:
                // [34] flags, [100..131] name: same layout as a contact frame.
                _contactEdits[node.Seed] = (Encoding.UTF8.GetString(cmd.Slice(100, 32)).TrimEnd('\0'), cmd[34]);
                Reply([0]);
                break;
            case CommandCode.ShareContact or CommandCode.Logout:
                Reply([0]);
                break;
            case CommandCode.SendLogin:
                var password = Encoding.UTF8.GetString(cmd[33..]);
                Reply(Concat([6, 0], Key(node.Seed)[..4], U32(3000)));
                // Any password gets admin; blank gets guest; "wrong" is refused.
                var login = password == "wrong"
                    ? Concat([0x86, 0], Key(node.Seed)[..6])
                    : Concat([0x85, password.Length > 0 ? (byte)1 : (byte)0], Key(node.Seed)[..6], U32(Now), [0, 1]);
                if (password.Length > 0 && password != "wrong")
                {
                    _adminLogins.Add(node.Seed);
                }
                _ = PushLaterAsync(login);
                break;
            case CommandCode.SendStatusRequest:
                Reply(Concat([6, 0], U32(Interlocked.Increment(ref _ack)), U32(3000)));
                _ = PushLaterAsync(Concat([0x87, 0], Key(node.Seed)[..6],
                    U16(4120), U16(0), U16(unchecked((ushort)-118)), U16(unchecked((ushort)-92)), U32(15_234), U32(8_120), U32(1_820), U32(372_000),
                    U32(5_000), U32(3_120), U32(11_000), U32(4_234), U16(2), U16(unchecked((ushort)(sbyte)26)), U16(17), U16(410), U32(9_000), U32(3)));
                break;
        }
    }

    private async Task CliReplyAsync(Node? to, string command)
    {
        if (to is null)
        {
            return;
        }
        await Task.Delay(TimeSpan.FromMilliseconds(1200), time, _stop.Token);
        var reply = !_adminLogins.Contains(to.Seed) ? "Error: not logged in as admin"
            : command.Trim() switch
            {
                "ver" => "v1.18.0-sim (Build: 04-Nov-2026)",
                "clock" => $"{time.GetUtcNow():HH:mm} - {time.GetUtcNow():d/M/yyyy} UTC",
                "get name" => $"> {to.Name}",
                "neighbors" => "Alice:-6.5dB, Bob:3.25dB",
                _ => $"Unknown command: {command.Trim()}",
            };
        Enqueue(Concat([0x10, 0x28, 0, 0], Key(to.Seed)[..6], [0xFF, (byte)TextType.CliData], U32(Now), Text(reply)));
    }

    private async Task PushLaterAsync(byte[] frame)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(800), time, _stop.Token);
        Reply(frame);
    }

    private static Node? NodeByKey(ReadOnlySpan<byte> key)
    {
        foreach (var n in Nodes)
        {
            if (Key(n.Seed).AsSpan().SequenceEqual(key))
            {
                return n;
            }
        }
        return null;
    }

    private byte[] ContactFrame(byte code, Node n)
    {
        var (name, flags) = _contactEdits.TryGetValue(n.Seed, out var e) ? e : (n.Name, (byte)0);
        // The repeater is a direct neighbour (0 hops), so it can be traced; the others flood.
        var pathLen = n.Type == AdvertType.Repeater ? (byte)0 : (byte)0xFF;
        return Concat([code], Key(n.Seed), [(byte)n.Type, flags, pathLen], new byte[64],
            Fixed(name, 32), U32(Now), I32(59_330_000 + n.Seed * 100), I32(18_070_000), U32(Now));
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
