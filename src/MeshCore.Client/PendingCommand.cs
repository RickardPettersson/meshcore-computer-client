using MeshCore.Protocol;
using MeshCore.Protocol.Frames;

namespace MeshCore.Client;

/// <summary>
/// The command currently waiting for its reply. The read loop offers it every non-push frame;
/// it decides whether the frame belongs to it and when it's complete.
/// </summary>
internal abstract class PendingCommand
{
    public TaskCompletionSource<MeshFrame> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CommandCode Command { get; set; }

    /// <summary>Returns false if the frame isn't part of this command's reply.</summary>
    public bool Offer(MeshFrame frame)
    {
        switch (frame)
        {
            case ErrorResponse err:
                Fail(new MeshCoreCommandException(Command, err.Error));
                return true;
            case DisabledResponse:
                Fail(new MeshCoreCommandException(Command, ErrorCode.Unknown, disabled: true));
                return true;
            default:
                return Accept(frame);
        }
    }

    public void Fail(Exception error) => Completion.TrySetException(error);

    protected abstract bool Accept(MeshFrame frame);

    protected void Complete(MeshFrame frame) => Completion.TrySetResult(frame);
}

/// <summary>Completes on the first frame of type <typeparamref name="T"/>; ignores stray frames
/// (e.g. a late reply to an earlier command that timed out).</summary>
internal sealed class SingleResponseCommand<T> : PendingCommand where T : MeshFrame
{
    protected override bool Accept(MeshFrame frame)
    {
        if (frame is not T)
        {
            return false;
        }
        Complete(frame);
        return true;
    }
}

/// <summary>Completes on whatever comes back (used by sync-next-message, whose reply type varies).</summary>
internal sealed class AnyResponseCommand : PendingCommand
{
    protected override bool Accept(MeshFrame frame)
    {
        Complete(frame);
        return true;
    }
}

/// <summary>Collects <c>CONTACTS_START</c>, N × <c>CONTACT</c>, <c>END_OF_CONTACTS</c>.</summary>
internal sealed class ContactListCommand : PendingCommand
{
    private readonly List<Contact> _contacts = [];
    private bool _started;

    public IReadOnlyList<Contact> Contacts => _contacts;

    protected override bool Accept(MeshFrame frame)
    {
        switch (frame)
        {
            case ContactsStart start:
                _started = true;
                _contacts.Capacity = (int)Math.Min(start.TotalCount, 1000);
                return true;
            case Contact contact when _started:
                _contacts.Add(contact);
                return true;
            case EndOfContacts end when _started:
                Complete(end);
                return true;
            default:
                return false;
        }
    }
}
