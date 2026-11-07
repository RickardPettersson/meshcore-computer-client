using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshCore.Client.Tests;
using MeshCore.Client.Transport;
using MeshCore.Host.Radio;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshCore.Host.Tests;

/// <summary>
/// Hosts the real app in memory with its own temp data folder (DB, keys, users) and a scripted
/// radio instead of the network. Set <see cref="Radio"/> to null to simulate "radio not connected".
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "mcc-tests", Guid.NewGuid().ToString("N"));

    internal FakeRadio? Radio { get; init; } = DefaultRadio();

    /// <summary>Use the built-in radio simulator instead of <see cref="Radio"/> (no chatter during tests).</summary>
    internal bool UseSimulator { get; init; }

    internal static FakeRadio DefaultRadio() => new FakeRadio().WithHandshake("Base", maxChannels: 4)
        .On(0x04, [0x02, 1, 0, 0, 0], FakeRadio.Contact(10, "Alice"), [0x04, 0, 0, 0, 0])
        .On(0x1F, cmd => [cmd[1] == 0 ? FakeRadio.ChannelInfo(0, "Public") : FakeRadio.ChannelInfo(cmd[1], "")])
        .On(0x0A, [0x0A])
        .On(0x14, FakeRadio.Concat([0x0C, 0xAC, 0x0F], FakeRadio.U32(12), FakeRadio.U32(3000)))
        .On(0x02, FakeRadio.Concat([0x06, 0x00], FakeRadio.U32(0xACE), FakeRadio.U32(8000)))
        .On(0x03, FakeRadio.Ok);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("MeshCore:DataDirectory", _dataDir);
        builder.UseSetting("MeshCore:Connection", "192.0.2.1:5000");
        builder.UseSetting("MeshCore:ConsoleChat", "false");
        builder.UseSetting("MeshCore:ReconnectDelays:0", "00:00:00.200");
        builder.UseSetting("Web:SeparatePorts", "false"); // TestServer has no ports
        builder.UseSetting("Web:Https:Enabled", "false"); // no certificates needed in memory
        builder.UseSetting("Web:ApiBaseUrl", "http://localhost");
        builder.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<RadioConnectionService>();
            services.AddSingleton(sp =>
            {
                var service = ActivatorUtilities.CreateInstance<RadioConnectionService>(sp);
                service.TransportFactory = _ => UseSimulator
                    ? new SimulatedRadio(TimeSpan.FromHours(1), TimeProvider.System)
                    : Radio ?? (ITransport)new UnreachableTransport();
                return service;
            });
            services.PostConfigure<RadioOptions>(o => o.ReconnectDelays = [TimeSpan.FromMilliseconds(200)]);
            // The website's API client calls the in-memory server instead of the network.
            services.AddHttpClient<Web.MeshApiClient>()
                .ConfigurePrimaryHttpMessageHandler(sp => ((TestServer)sp.GetRequiredService<IServer>()).CreateHandler());
        });
    }

    /// <summary>Waits until the scripted radio is connected and synced (or fails the test).</summary>
    public async Task WaitForRadioAsync()
    {
        var radio = Services.GetRequiredService<MeshRadio>();
        var until = DateTime.UtcNow.AddSeconds(5);
        // Connected is reported before the queued messages are fetched, so wait for the sync too.
        while (!radio.IsSynced)
        {
            Assert.True(DateTime.UtcNow < until, "The scripted radio never connected and synced.");
            await Task.Delay(20);
        }
    }

    public async Task<HttpClient> AdminClientAsync(string username = "admin", string password = "correct horse battery")
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/setup", new { username, password });
        response.EnsureSuccessStatusCode();
        await AuthorizeAsync(client, response);
        return client;
    }

    public async Task<HttpClient> SignInAsync(string username, string password)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/token", new { username, password });
        response.EnsureSuccessStatusCode();
        await AuthorizeAsync(client, response);
        return client;
    }

    public static async Task AuthorizeAsync(HttpClient client, HttpResponseMessage tokenResponse)
    {
        var token = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>(Json);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.GetProperty("accessToken").GetString());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
            // SQLite may still hold the file briefly on Windows; it's in %TEMP% anyway.
        }
    }

    private sealed class UnreachableTransport : ITransport
    {
        public string Description => "unreachable";

        public System.Threading.Channels.ChannelReader<byte[]> Frames { get; } = System.Threading.Channels.Channel.CreateUnbounded<byte[]>().Reader;

        public Task OpenAsync(CancellationToken cancellationToken = default) =>
            throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostUnreachable);

        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
