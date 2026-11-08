namespace MeshCore.Host.Tests;

public class BatteryPageTests
{
    [Fact]
    public async Task Device_page_and_dashboard_show_percentage_and_volts()
    {
        using var app = new ApiFactory();
        var browser = new Browser(app);
        await browser.SetupAsync();
        await app.WaitForRadioAsync();

        var device = await browser.PageAsync("/Device");
        var dashboard = await browser.PageAsync("/");

        Assert.Matches("<a class=\"fw-semibold\"[^>]*>79%</a>", device);
        Assert.Contains("(4.012 V)", device, StringComparison.Ordinal);
        Assert.Contains("value=\"79\"", device, StringComparison.Ordinal);            // the level bar
        Assert.Contains("<div class=\"display-6\">79%</div>", dashboard, StringComparison.Ordinal);
        Assert.Contains("4.01 V", dashboard, StringComparison.Ordinal);
    }
}
