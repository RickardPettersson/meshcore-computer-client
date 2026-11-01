namespace MeshCore.Protocol.Framing;

/// <summary>
/// Start bytes used by the MeshCore companion serial (USB/UART) transport.
/// Each frame on the wire is: marker, length LSB, length MSB, payload.
/// </summary>
public static class FrameMarker
{
    /// <summary><c>'&lt;'</c> — frames sent from the app to the radio.</summary>
    public const byte ToRadio = (byte)'<';

    /// <summary><c>'&gt;'</c> — frames sent from the radio to the app.</summary>
    public const byte FromRadio = (byte)'>';

    /// <summary>Size of the marker + 16-bit little-endian length header.</summary>
    public const int HeaderSize = 3;
}
