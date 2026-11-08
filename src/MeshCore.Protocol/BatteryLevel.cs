namespace MeshCore.Protocol;

/// <summary>
/// Estimates how full a single-cell Li-ion/LiPo battery is from its voltage (the only thing the radio reports).
/// Uses a typical resting-voltage curve rather than a straight line, because a cell spends most of its charge
/// between about 3.7 and 4.0 V. It's an estimate: voltage sags while transmitting and reads high while charging.
/// </summary>
public static class BatteryLevel
{
    // (millivolts, percent), from full to empty. Typical 1S Li-ion open-circuit curve.
    private static readonly (int Mv, int Percent)[] Curve =
    [
        (4200, 100), (4150, 95), (4110, 90), (4080, 85), (4020, 80), (3980, 75), (3950, 70), (3910, 65),
        (3870, 60), (3850, 55), (3840, 50), (3820, 45), (3800, 40), (3790, 35), (3770, 30), (3750, 25),
        (3730, 20), (3710, 15), (3690, 10), (3610, 5), (3270, 0),
    ];

    /// <summary>0–100, or null when there's no reading (0 mV: no battery or not measured).</summary>
    public static int? Percent(int millivolts)
    {
        if (millivolts <= 0)
        {
            return null;
        }
        if (millivolts >= Curve[0].Mv)
        {
            return 100;
        }
        if (millivolts <= Curve[^1].Mv)
        {
            return 0;
        }
        for (var i = 1; i < Curve.Length; i++)
        {
            var (lowMv, lowPct) = Curve[i];
            if (millivolts >= lowMv)
            {
                var (highMv, highPct) = Curve[i - 1];
                return (int)Math.Round(lowPct + (double)(millivolts - lowMv) / (highMv - lowMv) * (highPct - lowPct), MidpointRounding.AwayFromZero);
            }
        }
        return 0;
    }
}
