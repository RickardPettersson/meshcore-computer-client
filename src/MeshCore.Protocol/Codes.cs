namespace MeshCore.Protocol;

#pragma warning disable CA1028 // The wire format is byte-based, so these enums are too.

/// <summary>Commands sent from the app to the radio (<c>CMD_*</c> in <c>MyMesh.cpp</c>).</summary>
public enum CommandCode : byte
{
    AppStart = 1,
    SendTextMessage = 2,
    SendChannelTextMessage = 3,
    GetContacts = 4,
    GetDeviceTime = 5,
    SetDeviceTime = 6,
    SendSelfAdvert = 7,
    SetAdvertName = 8,
    AddUpdateContact = 9,
    SyncNextMessage = 10,
    SetRadioParams = 11,
    SetRadioTxPower = 12,
    ResetPath = 13,
    SetAdvertLatLon = 14,
    RemoveContact = 15,
    ShareContact = 16,
    ExportContact = 17,
    ImportContact = 18,
    Reboot = 19,
    GetBatteryAndStorage = 20,
    SetTuningParams = 21,
    DeviceQuery = 22,
    ExportPrivateKey = 23,
    ImportPrivateKey = 24,
    SendRawData = 25,
    SendLogin = 26,
    SendStatusRequest = 27,
    HasConnection = 28,
    Logout = 29,
    GetContactByKey = 30,
    GetChannel = 31,
    SetChannel = 32,
    SignStart = 33,
    SignData = 34,
    SignFinish = 35,
    SendTracePath = 36,
    SetDevicePin = 37,
    SetOtherParams = 38,
    SendTelemetryRequest = 39,
    GetCustomVars = 40,
    SetCustomVar = 41,
    GetAdvertPath = 42,
    GetTuningParams = 43,
    SendBinaryRequest = 50,
    FactoryReset = 51,
    SendPathDiscoveryRequest = 52,
    SetFloodScopeKey = 54,
    SendControlData = 55,
    GetStats = 56,
    SendAnonRequest = 57,
    SetAutoAddConfig = 58,
    GetAutoAddConfig = 59,
    GetAllowedRepeatFreq = 60,
    SetPathHashMode = 61,
    SendChannelData = 62,
    SetDefaultFloodScope = 63,
    GetDefaultFloodScope = 64,
    SendRawPacket = 65,
    RunCliCommand = 66,
}

/// <summary>Replies to a command (<c>RESP_CODE_*</c>). Always below 0x80.</summary>
public enum ResponseCode : byte
{
    Ok = 0,
    Error = 1,
    ContactsStart = 2,
    Contact = 3,
    EndOfContacts = 4,
    SelfInfo = 5,
    Sent = 6,
    ContactMessageReceived = 7,
    ChannelMessageReceived = 8,
    CurrentTime = 9,
    NoMoreMessages = 10,
    ExportContact = 11,
    BatteryAndStorage = 12,
    DeviceInfo = 13,
    PrivateKey = 14,
    Disabled = 15,
    ContactMessageReceivedV3 = 16,
    ChannelMessageReceivedV3 = 17,
    ChannelInfo = 18,
    SignStart = 19,
    Signature = 20,
    CustomVars = 21,
    AdvertPath = 22,
    TuningParams = 23,
    Stats = 24,
    AutoAddConfig = 25,
    AllowedRepeatFreq = 26,
    ChannelDataReceived = 27,
    DefaultFloodScope = 28,
    CliReply = 29,
}

/// <summary>Unsolicited notifications from the radio (<c>PUSH_CODE_*</c>). Always 0x80 or above.</summary>
public enum PushCode : byte
{
    Advert = 0x80,
    PathUpdated = 0x81,
    SendConfirmed = 0x82,
    MessagesWaiting = 0x83,
    RawData = 0x84,
    LoginSuccess = 0x85,
    LoginFail = 0x86,
    StatusResponse = 0x87,
    LogRxData = 0x88,
    TraceData = 0x89,
    NewAdvert = 0x8A,
    TelemetryResponse = 0x8B,
    BinaryResponse = 0x8C,
    PathDiscoveryResponse = 0x8D,
    ControlData = 0x8E,
    ContactDeleted = 0x8F,
    ContactsFull = 0x90,
}

/// <summary>Error codes carried by <see cref="ResponseCode.Error"/> (<c>ERR_CODE_*</c>).</summary>
public enum ErrorCode : byte
{
    Unknown = 0,
    UnsupportedCommand = 1,
    NotFound = 2,
    TableFull = 3,
    BadState = 4,
    FileIoError = 5,
    IllegalArgument = 6,
}

/// <summary>Node type announced in adverts (<c>ADV_TYPE_*</c>).</summary>
public enum AdvertType : byte
{
    None = 0,
    Chat = 1,
    Repeater = 2,
    Room = 3,
    Sensor = 4,
}

/// <summary>Text message kind (<c>TXT_TYPE_*</c>).</summary>
public enum TextType : byte
{
    Plain = 0,
    CliData = 1,
    SignedPlain = 2,
    CliCommand = 3,
}

/// <summary>Per-contact flags (<c>ContactInfo.flags</c>).</summary>
[Flags]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "Mirrors the firmware field name.")]
public enum ContactFlags : byte
{
    None = 0,
    Favourite = 0x01,
    TelemetryBaseAllowed = 0x02,
    TelemetryLocationAllowed = 0x04,
    TelemetryEnvironmentAllowed = 0x08,
    RemoteCliAllowed = 0x10,
}

#pragma warning restore CA1028
