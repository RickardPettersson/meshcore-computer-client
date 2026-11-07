#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
// Takes the README screenshots (docs/screenshots/v<version>-*.png) with headless Edge, at exact sizes, in dark mode.
//
// 1. Start the simulator with its own data folder (never your real radio: keys, names and messages would show):
//      dotnet run --project src/MeshCore.Host -- --MeshCore:Connection=simulator --MeshCore:DataDirectory=data-shots
//    create the Admin at http://localhost:5080, send a message in Public and to Alice, and let the nodes chat for a minute.
//    For the capture, restart it with --MeshCore:SimulatorMessageInterval=01:00:00 so no new messages pop up.
// 2. dotnet run build/take-screenshots.cs -- http://localhost:5080 <user> <password> docs/screenshots v1.2.0
// 3. Look at every image, then point the README's Screenshots section at the new files.
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var (baseUrl, user, password, outDir, prefix) = (args[0].TrimEnd('/'), args[1], args[2], args[3], args[4]);
Directory.CreateDirectory(outDir);

// 1. Sign in like a browser and keep the auth cookie.
var cookies = new CookieContainer();
using var http = new HttpClient(new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false }) { BaseAddress = new Uri(baseUrl) };
var login = await http.GetStringAsync("/Account/Login");
var token = Regex.Match(login, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
var signIn = await http.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
{
    ["Username"] = user, ["Password"] = password, ["__RequestVerificationToken"] = token,
}));
var auth = cookies.GetCookies(new Uri(baseUrl)).FirstOrDefault(c => c.Name == "MeshCore.Auth")
    ?? throw new InvalidOperationException($"Sign-in failed: HTTP {(int)signIn.StatusCode}");
Console.WriteLine("signed in");

// 2. Headless Edge with remote debugging.
var profile = Path.Combine(Path.GetTempPath(), "mcc-shots-" + Guid.NewGuid().ToString("N"));
var edge = Process.Start(new ProcessStartInfo(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    $"--headless=new --remote-debugging-port=9333 --user-data-dir=\"{profile}\" --hide-scrollbars --force-color-profile=srgb --no-first-run about:blank")
    { UseShellExecute = false })!;
try
{
    string? wsUrl = null;
    for (var i = 0; i < 50 && wsUrl is null; i++)
    {
        await Task.Delay(200);
        try
        {
            var list = JsonNode.Parse(await new HttpClient().GetStringAsync("http://127.0.0.1:9333/json/list"))!.AsArray();
            wsUrl = list.FirstOrDefault(t => (string?)t!["type"] == "page")?["webSocketDebuggerUrl"]?.GetValue<string>();
        }
        catch (HttpRequestException) { }
    }
    using var ws = new ClientWebSocket();
    await ws.ConnectAsync(new Uri(wsUrl ?? throw new InvalidOperationException("Edge didn't start")), default);
    var id = 0;
    async Task<JsonNode?> Cdp(string method, object? parameters = null)
    {
        var myId = ++id;
        var msg = JsonSerializer.Serialize(new { id = myId, method, @params = parameters ?? new { } });
        await ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, default);
        var buffer = new byte[1 << 20];
        while (true)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do { r = await ws.ReceiveAsync(buffer, default); ms.Write(buffer, 0, r.Count); } while (!r.EndOfMessage);
            var node = JsonNode.Parse(ms.ToArray());
            if ((int?)node?["id"] == myId)
            {
                if (node!["error"] is { } e) { throw new InvalidOperationException($"{method}: {e}"); }
                return node["result"];
            }
        }
    }

    await Cdp("Network.enable");
    await Cdp("Network.setCookie", new { name = auth.Name, value = auth.Value, url = baseUrl, httpOnly = true });
    await Cdp("Emulation.setEmulatedMedia", new { features = new[] { new { name = "prefers-color-scheme", value = "dark" } } });

    // name, path, width, height, scale, mobile, scroll to bottom
    (string Name, string Path, int W, int H, double Scale, bool Mobile, bool Bottom)[] shots =
    [
        ("dashboard", "/", 1280, 800, 1, false, false),
        ("chat-desktop", "/Messages/Channel/0", 1280, 800, 1, false, true),
        ("contacts", "/Contacts", 1280, 800, 1, false, false),
        ("device", "/Device", 1280, 800, 1, false, false),
        ("chat-phone", "/Messages/Channel/0", 390, 844, 2, true, true),
        ("direct-phone", "/Messages/Direct/101112131415161718191a1b1c1d1e1f202122232425262728292a2b2c2d2e2f", 390, 844, 2, true, true),
        ("chats-phone", "/Messages", 390, 844, 2, true, false),
    ];
    foreach (var s in shots)
    {
        await Cdp("Emulation.setDeviceMetricsOverride", new { width = s.W, height = s.H, deviceScaleFactor = s.Scale, mobile = s.Mobile });
        await Cdp("Emulation.setTouchEmulationEnabled", new { enabled = s.Mobile });
        await Cdp("Page.navigate", new { url = baseUrl + s.Path });
        await Task.Delay(2500); // page load + live connection + local times
        // Live "new message" toasts would cover the page.
        await Cdp("Runtime.evaluate", new { expression = "document.head.insertAdjacentHTML('beforeend', '<style>.toast-container,.toast{display:none!important}</style>')" });
        if (s.Bottom) { await Cdp("Runtime.evaluate", new { expression = "window.scrollTo(0, document.body.scrollHeight)" }); await Task.Delay(700); }
        var shot = await Cdp("Page.captureScreenshot", new { format = "png" });
        var file = Path.Combine(outDir, $"{prefix}-{s.Name}.png");
        await File.WriteAllBytesAsync(file, Convert.FromBase64String(shot!["data"]!.GetValue<string>()));
        Console.WriteLine($"{file} ({new FileInfo(file).Length / 1024} KB)");
    }
}
finally
{
    try { edge.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    await Task.Delay(500);
    try { Directory.Delete(profile, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
}
