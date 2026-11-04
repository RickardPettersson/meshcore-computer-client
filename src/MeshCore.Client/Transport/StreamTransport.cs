using System.Threading.Channels;
using MeshCore.Protocol.Framing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshCore.Client.Transport;

/// <summary>
/// Shared plumbing for byte-stream links (serial, TCP). These all carry the same
/// <c>'&lt;'</c>/<c>'&gt;'</c> + LE16 length framing; subclasses only open the stream.
/// </summary>
public abstract partial class StreamTransport : ITransport
{
    private const int ReadBufferSize = 1024;

    private readonly Channel<byte[]> _frames = Channel.CreateUnbounded<byte[]>(new() { SingleWriter = true });
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly ILogger _logger;
    private Stream? _stream;
    private Task? _readLoop;

    protected StreamTransport(ILogger? logger)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public abstract string Description { get; }

    public ChannelReader<byte[]> Frames => _frames.Reader;

    /// <summary>Opens the underlying link and returns its stream.</summary>
    protected abstract Task<Stream> OpenStreamAsync(CancellationToken cancellationToken);

    /// <summary>Closes the underlying link; must make a pending read return or throw.</summary>
    protected abstract void CloseLink();

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_stop.IsCancellationRequested, this);
        if (_stream is not null)
        {
            throw new InvalidOperationException("The transport is already open.");
        }

        _stream = await OpenStreamAsync(cancellationToken).ConfigureAwait(false);
        LogOpened(Description);
        var stream = _stream;
        _readLoop = Task.Run(() => ReadLoopAsync(stream), CancellationToken.None);
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        var stream = _stream ?? throw new InvalidOperationException("The transport is not open.");
        var frame = FrameEncoder.Encode(payload.Span);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(Stream stream)
    {
        var decoder = new FrameDecoder();
        var buffer = new byte[ReadBufferSize];
        Exception? failure = null;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    // TCP: the radio closed the socket (e.g. another client connected and took over).
                    failure = new EndOfStreamException($"{Description} closed the connection.");
                    break;
                }

                foreach (var frame in decoder.Decode(buffer.AsSpan(0, read)))
                {
                    _frames.Writer.TryWrite(frame);
                }
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested)
        {
            // Closing the link aborts the pending read; that's a normal shutdown.
        }
#pragma warning disable CA1031 // Any read failure means the link is down; report it through the channel.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = ex;
            LogReadFailed(ex, Description);
        }
        finally
        {
            if (decoder.DiscardedBytes > 0)
            {
                LogDiscarded(decoder.DiscardedBytes, Description);
            }
            _frames.Writer.TryComplete(failure);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            CloseLink();
        }
        catch (IOException)
        {
            // The device may already be gone.
        }

        if (_readLoop is not null)
        {
            await _readLoop.ConfigureAwait(false);
        }

        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        _writeLock.Dispose();
        _stop.Dispose();
        LogClosed(Description);
        GC.SuppressFinalize(this);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Opened {Endpoint}")]
    private partial void LogOpened(string endpoint);

    [LoggerMessage(Level = LogLevel.Information, Message = "Closed {Endpoint}")]
    private partial void LogClosed(string endpoint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Read from {Endpoint} failed; link is down")]
    private partial void LogReadFailed(Exception ex, string endpoint);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Discarded {Count} non-frame byte(s) from {Endpoint}")]
    private partial void LogDiscarded(long count, string endpoint);
}
