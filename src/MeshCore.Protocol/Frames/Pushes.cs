using MeshCore.Protocol.Models;

namespace MeshCore.Protocol.Frames;

/// <summary><c>PUSH_CODE_ADVERT</c>: a known contact re-advertised.</summary>
public sealed record AdvertPush(PublicKey PublicKey) : MeshFrame((byte)PushCode.Advert);

/// <summary><c>PUSH_CODE_PATH_UPDATED</c>: the route to a contact changed.</summary>
public sealed record PathUpdatedPush(PublicKey PublicKey) : MeshFrame((byte)PushCode.PathUpdated);

/// <summary><c>PUSH_CODE_SEND_CONFIRMED</c>: a direct message was acknowledged.</summary>
public sealed record SendConfirmedPush(uint AckCode, TimeSpan RoundTrip) : MeshFrame((byte)PushCode.SendConfirmed);

/// <summary><c>PUSH_CODE_MSG_WAITING</c>: call <c>CMD_SYNC_NEXT_MESSAGE</c> until the queue is empty.</summary>
public sealed record MessagesWaitingPush() : MeshFrame((byte)PushCode.MessagesWaiting);

/// <summary><c>PUSH_CODE_NEW_ADVERT</c>: an advert from a node that isn't a contact yet
/// (sent when the radio is set to add contacts manually).</summary>
public sealed record NewAdvertPush(Contact Contact) : MeshFrame((byte)PushCode.NewAdvert);
