namespace MeshCore.Client;

public sealed class MeshCoreClientOptions
{
    /// <summary>Name sent in <c>CMD_APP_START</c>; shows up in the radio's debug log.</summary>
    public string AppName { get; set; } = "MeshCoreComputerClient";

    /// <summary>How long to wait for a reply to a single command.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long to wait for a whole contact list (hundreds of frames on a full radio).</summary>
    public TimeSpan ContactsTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// <c>CMD_APP_START</c> attempts during connect. Some boards reset when the port opens
    /// and need a moment before they answer.
    /// </summary>
    public int HandshakeAttempts { get; set; } = 3;

    /// <summary>Set the radio clock from this computer on connect (the radio has no RTC).</summary>
    public bool SyncDeviceClock { get; set; } = true;

    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}
