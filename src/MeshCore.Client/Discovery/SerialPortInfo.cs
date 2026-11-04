namespace MeshCore.Client.Discovery;

/// <summary>How likely a port is to be a MeshCore companion radio, best first.</summary>
public enum PortLikelihood
{
    /// <summary>Native USB on a chip used by MeshCore boards (nRF52, ESP32-S3, RP2040).</summary>
    NativeUsb = 0,

    /// <summary>A USB-UART bridge chip found on many LoRa boards (CP210x, CH340/CH9102, FTDI).</summary>
    UsbBridge = 1,

    /// <summary>No USB IDs available (e.g. macOS); could be anything.</summary>
    Unknown = 2,

    /// <summary>USB device with IDs that don't match any known board.</summary>
    Unlikely = 3,
}

/// <summary>A serial port found on this machine.</summary>
public sealed record SerialPortInfo(string PortName, ushort? VendorId, ushort? ProductId)
{
    public PortLikelihood Likelihood => KnownUsbDevices.Classify(VendorId);

    /// <summary>Board family hint, e.g. "nRF52 (T-Echo, RAK4631, …)".</summary>
    public string? Hint => KnownUsbDevices.Describe(VendorId);

    public override string ToString() =>
        VendorId is { } vid ? $"{PortName} [{vid:X4}:{ProductId:X4}] {Hint}" : PortName;
}

/// <summary>USB vendor IDs seen on MeshCore-capable boards.</summary>
public static class KnownUsbDevices
{
    private static readonly Dictionary<ushort, (PortLikelihood Likelihood, string Description)> Vendors = new()
    {
        [0x239A] = (PortLikelihood.NativeUsb, "nRF52 / Adafruit bootloader (T-Echo, RAK4631, Xiao nRF52, T114)"),
        [0x1915] = (PortLikelihood.NativeUsb, "Nordic nRF52"),
        [0x303A] = (PortLikelihood.NativeUsb, "Espressif native USB (ESP32-S3/C3: T-Deck, Heltec V3/V4, Xiao S3)"),
        [0x2E8A] = (PortLikelihood.NativeUsb, "Raspberry Pi RP2040"),
        [0x10C4] = (PortLikelihood.UsbBridge, "Silicon Labs CP210x bridge (Heltec, T-Beam)"),
        [0x1A86] = (PortLikelihood.UsbBridge, "WCH CH340/CH9102 bridge (T-Beam Supreme, many ESP32 boards)"),
        [0x0403] = (PortLikelihood.UsbBridge, "FTDI bridge"),
    };

    public static PortLikelihood Classify(ushort? vendorId) => vendorId switch
    {
        null => PortLikelihood.Unknown,
        { } vid when Vendors.TryGetValue(vid, out var v) => v.Likelihood,
        _ => PortLikelihood.Unlikely,
    };

    public static string? Describe(ushort? vendorId) =>
        vendorId is { } vid && Vendors.TryGetValue(vid, out var v) ? v.Description : null;
}
