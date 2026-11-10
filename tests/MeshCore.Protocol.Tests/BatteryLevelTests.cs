namespace MeshCore.Protocol.Tests;

public class BatteryLevelTests
{
    [Theory]
    [InlineData(4250, 100)] // charging / above full
    [InlineData(4200, 100)]
    [InlineData(4050, 88)]  // 87.5: halves round up, as in the MeshCore app
    [InlineData(4012, 84)]
    [InlineData(3600, 50)]
    [InlineData(3520, 43)]  // what the MeshCore Android app shows for 3.52 V
    [InlineData(3006, 1)]   // 0.5 rounds up
    [InlineData(3000, 0)]
    [InlineData(2800, 0)]   // below empty
    public void Voltage_maps_to_an_estimated_percentage(int millivolts, int percent) =>
        Assert.Equal(percent, BatteryLevel.Percent(millivolts));

    [Fact]
    public void No_reading_means_no_percentage() => Assert.Null(BatteryLevel.Percent(0));

    [Fact]
    public void More_voltage_never_means_less_charge()
    {
        var previous = 0;
        for (var mv = 3000; mv <= 4300; mv += 5)
        {
            var pct = BatteryLevel.Percent(mv)!.Value;
            Assert.InRange(pct, previous, 100);
            previous = pct;
        }
    }
}
