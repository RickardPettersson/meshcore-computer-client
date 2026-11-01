namespace MeshCore.Protocol.Framing;

/// <summary>
/// Incremental decoder that turns a raw serial byte stream into frame payloads.
/// Feed it whatever chunks the port delivers; it handles frames split across reads,
/// several frames in one read, and stray bytes (e.g. boot log text) between frames.
/// </summary>
/// <remarks>Not thread-safe: use one instance per read loop.</remarks>
public sealed class FrameDecoder
{
    private enum State { Idle, AwaitLengthLow, AwaitLengthHigh, Payload }

    private readonly byte _marker;
    private readonly int _maxFrameSize;
    private State _state = State.Idle;
    private int _expected;
    private int _received;
    private byte[] _buffer = [];

    /// <param name="marker">Start byte to look for; defaults to <see cref="FrameMarker.FromRadio"/>.</param>
    /// <param name="maxFrameSize">Frames that declare a longer length are skipped.</param>
    public FrameDecoder(byte marker = FrameMarker.FromRadio, int maxFrameSize = ProtocolLimits.MaxFrameSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFrameSize, 1);
        _marker = marker;
        _maxFrameSize = maxFrameSize;
    }

    /// <summary>Bytes discarded while searching for a start marker.</summary>
    public long DiscardedBytes { get; private set; }

    /// <summary>Frames dropped because their declared length exceeded the maximum.</summary>
    public long OversizedFrames { get; private set; }

    /// <summary>Consumes <paramref name="data"/> and returns every frame payload it completed.</summary>
    public IReadOnlyList<byte[]> Decode(ReadOnlySpan<byte> data)
    {
        List<byte[]>? frames = null;

        for (var i = 0; i < data.Length; i++)
        {
            var b = data[i];
            switch (_state)
            {
                case State.Idle:
                    if (b == _marker)
                    {
                        _state = State.AwaitLengthLow;
                    }
                    else
                    {
                        DiscardedBytes++;
                    }
                    break;

                case State.AwaitLengthLow:
                    _expected = b;
                    _state = State.AwaitLengthHigh;
                    break;

                case State.AwaitLengthHigh:
                    _expected |= b << 8;
                    if (_expected == 0)
                    {
                        // The firmware ignores zero-length frames too.
                        _state = State.Idle;
                    }
                    else if (_expected > _maxFrameSize)
                    {
                        // Most likely a false marker inside garbage; resync on the next marker.
                        OversizedFrames++;
                        _state = State.Idle;
                    }
                    else
                    {
                        _buffer = new byte[_expected];
                        _received = 0;
                        _state = State.Payload;
                    }
                    break;

                case State.Payload:
                    var take = Math.Min(_expected - _received, data.Length - i);
                    data.Slice(i, take).CopyTo(_buffer.AsSpan(_received));
                    _received += take;
                    i += take - 1;
                    if (_received == _expected)
                    {
                        (frames ??= []).Add(_buffer);
                        _buffer = [];
                        _state = State.Idle;
                    }
                    break;
            }
        }

        return frames ?? (IReadOnlyList<byte[]>)[];
    }

    /// <summary>Drops any partially received frame, e.g. after the port is reopened.</summary>
    public void Reset()
    {
        _state = State.Idle;
        _buffer = [];
        _expected = 0;
        _received = 0;
    }
}
