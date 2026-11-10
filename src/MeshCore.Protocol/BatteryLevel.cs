namespace MeshCore.Protocol;

/// <summary>
/// Estimates how full a battery is from its voltage (the only thing the radio reports), the same way as the MeshCore
/// apps: a straight line from 3.0 V (0%) to 4.2 V (100%), the default for single-cell Li-ion/LiPo. Using the same scale
/// means this app and the phone app show the same number for a device. It's an estimate: voltage sags while
/// transmitting and reads high while charging.
/// </summary>
public static class BatteryLevel
{
    /// <summary>0% at this voltage and below.</summary>
    public const int EmptyMillivolts = 3000;

    /// <summary>100% at this voltage and above.</summary>
    public const int FullMillivolts = 4200;

    /// <summary>0–100, or null when there's no reading (0 mV: no battery or not measured).</summary>
    public static int? Percent(int millivolts)
    {
        if (millivolts <= 0)
        {
            return null;
        }
        if (millivolts <= EmptyMillivolts)
        {
            return 0;
        }
        if (millivolts >= FullMillivolts)
        {
            return 100;
        }
        return (int)Math.Round((millivolts - EmptyMillivolts) * 100.0 / (FullMillivolts - EmptyMillivolts), MidpointRounding.AwayFromZero);
    }
}
