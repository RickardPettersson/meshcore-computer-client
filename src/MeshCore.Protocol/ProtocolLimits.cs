namespace MeshCore.Protocol;

/// <summary>Size constants from the MeshCore firmware. Sources and layouts: <c>docs/PROTOCOL.md</c>.</summary>
public static class ProtocolLimits
{
    /// <summary><c>MAX_FRAME_SIZE</c> in <c>BaseSerialInterface.h</c>.</summary>
    public const int MaxFrameSize = 176;

    /// <summary><c>PUB_KEY_SIZE</c> in <c>MeshCore.h</c>.</summary>
    public const int PublicKeySize = 32;

    /// <summary>Length of the public-key prefix used to address contacts in messages.</summary>
    public const int PublicKeyPrefixSize = 6;

    /// <summary><c>MAX_PATH_SIZE</c> in <c>MeshCore.h</c>.</summary>
    public const int MaxPathSize = 64;

    /// <summary>Fixed width of name fields (contacts, channels).</summary>
    public const int NameSize = 32;

    /// <summary>Group channel shared secret length (128-bit).</summary>
    public const int ChannelSecretSize = 16;

    /// <summary><c>MAX_TEXT_LEN</c> in <c>BaseChatMesh.h</c> (10 × 16-byte cipher blocks). Longer direct
    /// messages are rejected by the radio.</summary>
    public const int MaxTextBytes = 160;

    /// <summary>
    /// Text budget for a channel message. The radio prepends <c>"&lt;node name&gt;: "</c> and silently
    /// truncates the result to <see cref="MaxTextBytes"/> (<c>BaseChatMesh::sendGroupMessage</c>).
    /// </summary>
    public static int MaxChannelTextBytes(string nodeName) =>
        Math.Max(0, MaxTextBytes - System.Text.Encoding.UTF8.GetByteCount(nodeName ?? "") - 2);

    /// <summary>Protocol version this client announces in <c>CMD_DEVICE_QUERY</c> / <c>CMD_APP_START</c>.
    /// Version 3+ makes the radio send V3 message frames that include SNR.</summary>
    public const byte AppProtocolVersion = 3;
}
