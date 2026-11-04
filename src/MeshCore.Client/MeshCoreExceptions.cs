using MeshCore.Protocol;

namespace MeshCore.Client;

/// <summary>Base type for errors talking to a companion radio.</summary>
public class MeshCoreException : Exception
{
    public MeshCoreException()
    {
    }

    public MeshCoreException(string message) : base(message)
    {
    }

    public MeshCoreException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>The radio answered a command with <c>RESP_CODE_ERR</c> or <c>RESP_CODE_DISABLED</c>.</summary>
public class MeshCoreCommandException : MeshCoreException
{
    public MeshCoreCommandException()
    {
    }

    public MeshCoreCommandException(string message) : base(message)
    {
    }

    public MeshCoreCommandException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public MeshCoreCommandException(CommandCode command, ErrorCode error, bool disabled = false)
        : base(disabled ? $"{command} is disabled on this radio." : $"{command} failed: {error}.")
    {
        Command = command;
        Error = error;
        IsDisabled = disabled;
    }

    public CommandCode Command { get; }

    public ErrorCode Error { get; }

    /// <summary>The feature is compiled out or switched off on this firmware.</summary>
    public bool IsDisabled { get; }
}

/// <summary>The radio didn't answer in time.</summary>
public class MeshCoreTimeoutException : MeshCoreException
{
    public MeshCoreTimeoutException()
    {
    }

    public MeshCoreTimeoutException(string message) : base(message)
    {
    }

    public MeshCoreTimeoutException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>The link to the radio is closed or was lost.</summary>
public class MeshCoreDisconnectedException : MeshCoreException
{
    public MeshCoreDisconnectedException()
    {
    }

    public MeshCoreDisconnectedException(string message) : base(message)
    {
    }

    public MeshCoreDisconnectedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
