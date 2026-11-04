using System.IO.Ports;
using Microsoft.Extensions.Logging;

namespace MeshCore.Client.Transport;

public sealed class SerialTransportOptions
{
    /// <summary>Port name, e.g. <c>COM11</c> or <c>/dev/ttyACM0</c>.</summary>
    public required string PortName { get; init; }

    public int BaudRate { get; init; } = 115200;

    /// <summary>
    /// Assert DTR on open. nRF52 boards (T-Echo, RAK) only send over USB CDC while DTR is set.
    /// </summary>
    public bool DtrEnable { get; init; } = true;

    /// <summary>
    /// Assert RTS on open. With both DTR and RTS asserted, the ESP32 auto-reset circuit stays idle
    /// (the same default as pyserial, which the official Python client uses).
    /// </summary>
    public bool RtsEnable { get; init; } = true;
}

/// <summary>MeshCore companion link over a USB/UART serial port (companion USB firmware).</summary>
public sealed class SerialTransport(SerialTransportOptions options, ILogger<SerialTransport>? logger = null)
    : StreamTransport(logger)
{
    private const int WriteTimeoutMs = 2000;

    private readonly SerialTransportOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private SerialPort? _port;

    public override string Description => _options.PortName;

    protected override Task<Stream> OpenStreamAsync(CancellationToken cancellationToken)
    {
        var port = new SerialPort(_options.PortName, _options.BaudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = _options.DtrEnable,
            RtsEnable = _options.RtsEnable,
            WriteTimeout = WriteTimeoutMs,
        };
        port.Open();
        port.DiscardInBuffer();
        _port = port;
        return Task.FromResult(port.BaseStream);
    }

    protected override void CloseLink()
    {
        _port?.Close();
        _port?.Dispose();
    }
}
