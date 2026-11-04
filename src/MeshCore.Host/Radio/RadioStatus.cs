using MeshCore.Protocol.Frames;

namespace MeshCore.Host.Radio;

public enum RadioState
{
    /// <summary>Not started yet, or stopped.</summary>
    Stopped,

    Connecting,

    Connected,

    /// <summary>The WiFi radio closed our connection; another client is probably using it.</summary>
    InUseElsewhere,

    /// <summary>Connecting failed; waiting before trying again.</summary>
    WaitingToRetry,
}

/// <summary>Snapshot of the radio link, for the UI and API.</summary>
public sealed record RadioStatus(
    RadioState State,
    string Endpoint,
    SelfInfo? Self = null,
    DeviceInfo? Device = null,
    DateTimeOffset? Since = null,
    string? LastError = null,
    DateTimeOffset? NextAttempt = null)
{
    public bool IsConnected => State == RadioState.Connected;
}
