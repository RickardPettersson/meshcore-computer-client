namespace MeshCore.Host.Radio;

/// <summary>The <c>MeshCore</c> configuration section.</summary>
public sealed class RadioOptions
{
    public const string SectionName = "MeshCore";

    /// <summary>
    /// Radio address: <c>192.168.1.212:5000</c> (WiFi), <c>COM11</c> (USB), or <c>auto</c> to probe USB ports.
    /// </summary>
    public string Connection { get; set; } = "auto";

    /// <summary>Name sent to the radio in <c>CMD_APP_START</c>.</summary>
    public string AppName { get; set; } = "MeshCoreComputerClient";

    /// <summary>Folder for the database (and later keys and credentials), relative to the app folder.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>Waits between reconnect attempts after errors; the last value repeats.</summary>
    public TimeSpan[] ReconnectDelays { get; set; } =
    [
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5),
    ];

    /// <summary>
    /// Wait before reconnecting when the WiFi radio closed our connection, which means another
    /// client (e.g. the phone app) took it over. Reconnecting sooner would kick that client off.
    /// </summary>
    public TimeSpan TakeoverBackoff { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>A connection that lasted this long resets the reconnect delay back to the first step.</summary>
    public TimeSpan StableConnection { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Safety net: also drain the radio's message queue this often, in case a push was missed.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(1);
}
