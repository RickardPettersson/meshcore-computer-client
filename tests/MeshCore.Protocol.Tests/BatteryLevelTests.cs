namespace MeshCore.Protocol.Tests;

public class BatteryLevelTests
{
    [Theory]
    [InlineData(4250, 100)] // charging / above full
    [InlineData(4200, 100)]
    [InlineData(4050, 83)]  // between 4.02 V (80%) and 4.08 V (85%); halves round up
    [InlineData(4012, 79)]
    [InlineData(3840, 50)]
    [InlineData(3700, 13)]
    [InlineData(3270, 0)]
    [InlineData(3000, 0)]   // below empty
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
