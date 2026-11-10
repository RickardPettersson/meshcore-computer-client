using System.Globalization;
using System.Text;
using MeshCore.Host.Api;
using MeshCore.Protocol;
using Microsoft.AspNetCore.Html;

namespace MeshCore.Host.Web;

/// <summary>
/// Draws a battery history as inline SVG on the server: no chart library and no inline script (the site's CSP forbids
/// both). Volts on the left axis, the matching estimated percentage on the right, a red marker for every check that got
/// no answer, and a break in the line wherever the device didn't answer or a check is missing.
/// Colours come from CSS classes in site.css, so it follows the light/dark theme.
/// </summary>
public static class BatteryChart
{
    private const double Width = 800, Height = 280, Left = 80, Right = 728, Top = 14, Bottom = 230, MissY = 256;

    public static IHtmlContent Render(IReadOnlyList<BatteryReadingResponse> readings, DateTimeOffset since, DateTimeOffset now, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(readings);
        var answered = readings.Where(r => r.Millivolts is not null).ToList();
        var (minMv, maxMv) = answered.Count == 0
            ? (BatteryLevel.EmptyMillivolts, 4300)
            : (Math.Min(BatteryLevel.EmptyMillivolts, answered.Min(r => r.Millivolts!.Value) - 50), Math.Max(4300, answered.Max(r => r.Millivolts!.Value) + 50));
        minMv = minMv / 100 * 100;
        maxMv = (maxMv + 99) / 100 * 100;
        var span = Math.Max(1, (now - since).TotalSeconds);

        double X(DateTimeOffset at) => Left + Math.Clamp((at - since).TotalSeconds / span, 0, 1) * (Right - Left);
        double Y(int mv) => Bottom - (double)(mv - minMv) / (maxMv - minMv) * (Bottom - Top);
        static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture, $"<svg class=\"app-bchart\" viewBox=\"0 0 {Width} {Height}\" role=\"img\" aria-labelledby=\"bchart-title\">");
        svg.Append(CultureInfo.InvariantCulture,
            $"<title id=\"bchart-title\">Battery voltage, {answered.Count} reading(s), {readings.Count - answered.Count} without an answer</title>");

        // Grid: every 0.1 V, labelled with volts (left) and the estimated percentage (right).
        var step = maxMv - minMv > 1200 ? 200 : 100;
        for (var mv = minMv; mv <= maxMv; mv += step)
        {
            var y = F(Y(mv));
            svg.Append(CultureInfo.InvariantCulture, $"<line class=\"grid\" x1=\"{Left}\" x2=\"{Right}\" y1=\"{y}\" y2=\"{y}\"/>");
            svg.Append(CultureInfo.InvariantCulture, $"<text class=\"axis\" x=\"{Left - 6}\" y=\"{y}\" text-anchor=\"end\" dominant-baseline=\"middle\">{mv / 1000.0:0.0} V</text>");
            if (mv <= 4200 && BatteryLevel.Percent(mv) is { } pct) // above 4.2 V is "full" (charging), no need to repeat 100%
            {
                svg.Append(CultureInfo.InvariantCulture, $"<text class=\"axis\" x=\"{Right + 6}\" y=\"{y}\" dominant-baseline=\"middle\">{pct}%</text>");
            }
        }
        svg.Append(CultureInfo.InvariantCulture, $"<line class=\"frame\" x1=\"{Left}\" x2=\"{Right}\" y1=\"{Bottom}\" y2=\"{Bottom}\"/>");

        // The line, broken at missed checks and at gaps longer than 2.5 intervals (the app or radio was off).
        var segment = new List<BatteryReadingResponse>();
        void Flush()
        {
            if (segment.Count > 1)
            {
                svg.Append("<polyline class=\"line\" points=\"")
                    .AppendJoin(' ', segment.Select(r => $"{F(X(r.At))},{F(Y(r.Millivolts!.Value))}"))
                    .Append("\"/>");
            }
            segment.Clear();
        }
        BatteryReadingResponse? previous = null;
        foreach (var r in readings)
        {
            if (r.Millivolts is null || (previous is not null && r.At - previous.At > interval * 2.5))
            {
                Flush();
            }
            if (r.Millivolts is not null)
            {
                segment.Add(r);
            }
            previous = r;
        }
        Flush();

        // Dots for each answer (when there aren't too many), red markers for each missed check.
        if (answered.Count <= 200)
        {
            foreach (var r in answered)
            {
                svg.Append(CultureInfo.InvariantCulture,
                    $"<circle class=\"dot\" cx=\"{F(X(r.At))}\" cy=\"{F(Y(r.Millivolts!.Value))}\" r=\"3\"><title>{r.Volts:0.000} V · {r.Percent}%</title></circle>");
            }
        }
        foreach (var r in readings.Where(r => r.Millivolts is null))
        {
            var x = F(X(r.At));
            svg.Append(CultureInfo.InvariantCulture,
                $"<line class=\"miss-line\" x1=\"{x}\" x2=\"{x}\" y1=\"{Top}\" y2=\"{Bottom}\"/><rect class=\"miss\" x=\"{F(X(r.At) - 3)}\" y=\"{MissY - 6}\" width=\"6\" height=\"12\" rx=\"1\"><title>No answer ({System.Net.WebUtility.HtmlEncode(r.Note ?? "")})</title></rect>");
        }
        svg.Append(CultureInfo.InvariantCulture, $"<text class=\"axis\" x=\"{Left - 6}\" y=\"{MissY}\" text-anchor=\"end\" dominant-baseline=\"middle\">no answer</text>");
        svg.Append("</svg>");
        return new HtmlString(svg.ToString());
    }

    /// <summary>
    /// The temperatures the device reported with its battery, on the same time axis as <see cref="Render"/>.
    /// The line breaks where a check has no temperature or a check is missing.
    /// </summary>
    public static IHtmlContent RenderTemperature(IReadOnlyList<BatteryReadingResponse> readings, DateTimeOffset since, DateTimeOffset now, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(readings);
        const double TBottom = 150;
        var withTemp = readings.Where(r => r.TemperatureC is not null).ToList();
        if (withTemp.Count == 0)
        {
            return HtmlString.Empty;
        }
        // Whole 5 °C steps around the readings, at least 10 °C tall so small changes don't look dramatic.
        var min = Math.Floor(withTemp.Min(r => r.TemperatureC!.Value) / 5) * 5;
        var max = Math.Ceiling(withTemp.Max(r => r.TemperatureC!.Value) / 5) * 5;
        if (max - min < 10)
        {
            max = min + 10;
        }
        var span = Math.Max(1, (now - since).TotalSeconds);

        double X(DateTimeOffset at) => Left + Math.Clamp((at - since).TotalSeconds / span, 0, 1) * (Right - Left);
        double Y(double c) => TBottom - (c - min) / (max - min) * (TBottom - Top);
        static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture, $"<svg class=\"app-bchart app-tchart\" viewBox=\"0 0 {Width} {TBottom + 10}\" role=\"img\" aria-labelledby=\"tchart-title\">");
        svg.Append(CultureInfo.InvariantCulture,
            $"<title id=\"tchart-title\">Temperature, {withTemp.Count} reading(s), {withTemp.Min(r => r.TemperatureC):0.0} to {withTemp.Max(r => r.TemperatureC):0.0} °C</title>");
        var step = max - min > 30 ? 10 : 5;
        for (var c = min; c <= max; c += step)
        {
            var y = F(Y(c));
            svg.Append(CultureInfo.InvariantCulture, $"<line class=\"grid\" x1=\"{Left}\" x2=\"{Right}\" y1=\"{y}\" y2=\"{y}\"/>");
            svg.Append(CultureInfo.InvariantCulture, $"<text class=\"axis\" x=\"{Left - 6}\" y=\"{y}\" text-anchor=\"end\" dominant-baseline=\"middle\">{c:0} °C</text>");
        }
        svg.Append(CultureInfo.InvariantCulture, $"<line class=\"frame\" x1=\"{Left}\" x2=\"{Right}\" y1=\"{TBottom}\" y2=\"{TBottom}\"/>");

        var segment = new List<BatteryReadingResponse>();
        void Flush()
        {
            if (segment.Count > 1)
            {
                svg.Append("<polyline class=\"tline\" points=\"")
                    .AppendJoin(' ', segment.Select(r => $"{F(X(r.At))},{F(Y(r.TemperatureC!.Value))}"))
                    .Append("\"/>");
            }
            segment.Clear();
        }
        BatteryReadingResponse? previous = null;
        foreach (var r in readings)
        {
            if (r.TemperatureC is null || (previous is not null && r.At - previous.At > interval * 2.5))
            {
                Flush();
            }
            if (r.TemperatureC is not null)
            {
                segment.Add(r);
            }
            previous = r;
        }
        Flush();
        if (withTemp.Count <= 200)
        {
            foreach (var r in withTemp)
            {
                svg.Append(CultureInfo.InvariantCulture,
                    $"<circle class=\"tdot\" cx=\"{F(X(r.At))}\" cy=\"{F(Y(r.TemperatureC!.Value))}\" r=\"3\"><title>{r.TemperatureC:0.0} °C</title></circle>");
            }
        }
        svg.Append("</svg>");
        return new HtmlString(svg.ToString());
    }
}
