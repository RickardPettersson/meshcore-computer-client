namespace MeshCore.Protocol;

/// <summary>A frame could not be decoded.</summary>
public class ProtocolException : Exception
{
    public ProtocolException()
    {
    }

    public ProtocolException(string message) : base(message)
    {
    }

    public ProtocolException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
