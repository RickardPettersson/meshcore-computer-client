using System.Globalization;

namespace MeshCore.Protocol.Models;

/// <summary>One sensor reading from a CayenneLPP payload.</summary>
/// <param name="Channel">LPP channel. MeshCore uses 1 for the node itself.</param>
/// <param name="Values">One value for most types; 3 for accelerometer/gyro/colour/GPS.</param>
public sealed record LppReading(byte Channel, byte Type, string Name, IReadOnlyList<double> Values, string Unit)
{
    public override string ToString() =>
        $"{Name}: {string.Join(", ", Values.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)))}{(Unit.Length > 0 ? " " + Unit : "")}";
}

/// <summary>
/// Decodes CayenneLPP (channel, type, big-endian value), as MeshCore sends in telemetry responses.
/// Types and scales from <c>src/helpers/sensors/LPPDataHelpers.h</c>. Stops at the first unknown type.
/// </summary>
public static class CayenneLpp
{
    private sealed record TypeInfo(string Name, int Size, double Divisor, bool Signed, string Unit, int Axes = 1);

    private static readonly Dictionary<byte, TypeInfo> Types = new()
    {
        [0] = new("Digital input", 1, 1, false, ""),
        [1] = new("Digital output", 1, 1, false, ""),
        [2] = new("Analog input", 2, 100, true, ""),
        [3] = new("Analog output", 2, 100, true, ""),
        [100] = new("Generic sensor", 4, 1, false, ""),
        [101] = new("Illuminance", 2, 1, false, "lux"),
        [102] = new("Presence", 1, 1, false, ""),
        [103] = new("Temperature", 2, 10, true, "°C"),
        [104] = new("Humidity", 1, 2, false, "%"),
        [113] = new("Accelerometer", 6, 1000, true, "G", 3),
        [115] = new("Pressure", 2, 10, false, "hPa"),
        [116] = new("Voltage", 2, 100, false, "V"),
        [117] = new("Current", 2, 1000, false, "A"),
        [118] = new("Frequency", 4, 1, false, "Hz"),
        [120] = new("Percentage", 1, 1, false, "%"),
        [121] = new("Altitude", 2, 1, true, "m"),
        [125] = new("Concentration", 2, 1, false, "ppm"),
        [128] = new("Power", 2, 1, false, "W"),
        [130] = new("Distance", 4, 1000, false, "m"),
        [131] = new("Energy", 4, 1000, false, "kWh"),
        [132] = new("Direction", 2, 1, false, "°"),
        [133] = new("Unix time", 4, 1, false, ""),
        [134] = new("Gyrometer", 6, 100, true, "°/s", 3),
        [135] = new("Colour", 3, 1, false, "RGB", 3),
        [142] = new("Switch", 1, 1, false, ""),
    };

    private const byte Gps = 136;

    public static IReadOnlyList<LppReading> Decode(ReadOnlySpan<byte> data)
    {
        var readings = new List<LppReading>();
        var i = 0;
        while (i + 2 <= data.Length)
        {
            var channel = data[i];
            var type = data[i + 1];
            i += 2;

            if (type == Gps)
            {
                if (i + 9 > data.Length)
                {
                    break;
                }
                readings.Add(new LppReading(channel, type, "GPS",
                    [ReadInt(data.Slice(i, 3), true) / 10000.0, ReadInt(data.Slice(i + 3, 3), true) / 10000.0, ReadInt(data.Slice(i + 6, 3), true) / 100.0],
                    "lat, lon, m"));
                i += 9;
                continue;
            }

            if (channel == 0 && type == 0 && data[i..].IndexOfAnyExcept((byte)0) < 0)
            {
                break; // zero padding at the end
            }
            if (!Types.TryGetValue(type, out var info) || i + info.Size > data.Length)
            {
                break;
            }

            var axisSize = info.Size / info.Axes;
            var values = new double[info.Axes];
            for (var axis = 0; axis < info.Axes; axis++)
            {
                values[axis] = ReadInt(data.Slice(i + axis * axisSize, axisSize), info.Signed) / info.Divisor;
            }
            readings.Add(new LppReading(channel, type, info.Name, values, info.Unit));
            i += info.Size;
        }
        return readings;
    }

    private static long ReadInt(ReadOnlySpan<byte> bytes, bool signed)
    {
        long value = 0;
        foreach (var b in bytes)
        {
            value = (value << 8) | b;
        }
        if (signed)
        {
            var bits = bytes.Length * 8;
            if ((value & (1L << (bits - 1))) != 0)
            {
                value -= 1L << bits;
            }
        }
        return value;
    }
}
