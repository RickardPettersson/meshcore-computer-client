using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using MeshCore.Client.Transport;

namespace MeshCore.Client.Tests;

/// <summary>
/// In-memory transport that plays the radio side. Register replies per command code;
/// a handler returns the frames to send back (or none, to simulate silence).
/// </summary>
internal sealed class FakeRadio : ITransport
{
    private readonly Channel<byte[]> _toApp = Channel.CreateUnbounded<byte[]>();
    private readonly ConcurrentDictionary<byte, Func<byte[], IEnumerable<byte[]>>> _handlers = new();

    public ConcurrentQueue<byte[]> Received { get; } = new();

    public bool Opened { get; private set; }

    public bool Disposed { get; private set; }

    public string Description => "fake";

    public ChannelReader<byte[]> Frames => _toApp.Reader;

    public FakeRadio On(byte command, Func<byte[], IEnumerable<byte[]>> handler)
    {
        _handlers[command] = handler;
        return this;
    }

    public FakeRadio On(byte command, params byte[][] replies) => On(command, _ => replies);

    /// <summary>Default handshake replies: SELF_INFO, DEVICE_INFO (8 channels), OK for set-time.</summary>
    public FakeRadio WithHandshake(string name = "Fake Node", byte maxChannels = 8) =>
        On(0x01, SelfInfo(name)).On(0x16, DeviceInfo(maxChannels)).On(0x06, Ok);

    public void Push(byte[] frame) => _toApp.Writer.TryWrite(frame);

    public void Unplug() => _toApp.Writer.TryComplete(new IOException("The device was removed."));

    public Task OpenAsync(CancellationToken cancellationToken = default)
    {
        Opened = true;
        return Task.CompletedTask;
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        var bytes = payload.ToArray();
        Received.Enqueue(bytes);
        if (_handlers.TryGetValue(bytes[0], out var handler))
        {
            foreach (var reply in handler(bytes))
            {
                _toApp.Writer.TryWrite(reply);
            }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _toApp.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    // ---- Frame builders (radio → app) ----------------------------------------

    public static readonly byte[] Ok = [0x00];

    public static byte[] Error(byte code) => [0x01, code];

    public static byte[] Key(byte seed) => [.. Enumerable.Range(0, 32).Select(i => (byte)(seed + i))];

    public static byte[] SelfInfo(string name) => Concat(
        [0x05, 0x01, 22, 22], Key(1), new byte[8], [0, 0, 0, 0], U32(869_525), U32(250_000), [11, 5], Encoding.UTF8.GetBytes(name));

    public static byte[] DeviceInfo(byte maxChannels) => Concat(
        [0x0D, 14, 175, maxChannels], U32(0), Fixed("01 Nov 2026", 12), Fixed("Fake Board", 40), Fixed("v1.18.0", 20), [0, 0]);

    public static byte[] Contact(byte seed, string name) => Concat(
        [0x03], Key(seed), [0x01, 0x00, 0xFF], new byte[64], Fixed(name, 32), U32(1), new byte[8], U32(2));

    public static byte[] ChannelInfo(byte index, string name) => Concat([0x12, index], Fixed(name, 32), name.Length == 0 ? new byte[16] : Enumerable.Repeat((byte)0x8B, 16).ToArray());

    public static byte[] ChannelMessage(byte channel, string text) => Concat([0x11, 0x08, 0, 0, channel, 0xFF, 0x00], U32(1_762_000_000), Encoding.UTF8.GetBytes(text));

    public static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }

    public static byte[] Fixed(string s, int size)
    {
        var b = new byte[size];
        Encoding.UTF8.GetBytes(s).CopyTo(b, 0);
        return b;
    }

    public static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];
}
