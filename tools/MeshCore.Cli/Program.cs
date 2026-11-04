using System.Globalization;
using MeshCore.Client;
using MeshCore.Client.Discovery;
using MeshCore.Client.Transport;
using MeshCore.Protocol.Frames;
using MeshCore.Protocol.Models;
using Microsoft.Extensions.Logging;

const string Usage = """
    meshcore-cli — talk to a MeshCore companion radio over WiFi (TCP) or USB serial

    Usage: meshcore-cli <command> [args] [--connect <address>] [--verbose]

    Address:
      192.168.1.212               WiFi companion, default port 5000
      192.168.1.212:5000          WiFi companion, explicit port (also tcp://host:port)
      COM11, /dev/ttyACM0         USB companion
      (none)                      $MESHCORE_CONNECT if set, else probe USB serial ports

    Commands:
      ports                       List serial ports, most likely radios first
      info                        Node, device, radio and battery details
      contacts                    List contacts
      channels                    List configured channels
      messages                    Fetch queued messages (removes them from the radio)
      listen                      Stay connected; print messages and events (Ctrl+C to stop)
      send <name|key> <text>      Send a direct message to a contact
      send-channel <idx> <text>   Send to a channel
      advert [--flood]            Broadcast our advert

    """;

// Coordinates, frequencies and voltages read best with '.' decimals; times still use local time zone.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var argList = args.ToList();
var verbose = TakeFlag(argList, "--verbose");
var flood = TakeFlag(argList, "--flood");
var address = TakeOption(argList, "--connect") ?? TakeOption(argList, "--port") ?? Environment.GetEnvironmentVariable("MESHCORE_CONNECT");
MeshCoreEndpoint? endpoint = null;
if (address is not null && !MeshCoreEndpoint.TryParse(address, out endpoint))
{
    Console.Error.WriteLine(new FormatException($"'{address}' is not a radio address.").Message);
    return 1;
}
if (argList.Count == 0 || argList[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(Usage);
    return 0;
}

var command = argList[0];
var rest = argList.Skip(1).ToList();

using var loggerFactory = LoggerFactory.Create(b => b
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
    .SetMinimumLevel(verbose ? LogLevel.Trace : LogLevel.Warning));

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

if (command == "ports")
{
    var ports = SerialPortDiscovery.GetPorts();
    if (ports.Count == 0)
    {
        Console.WriteLine("No serial ports found.");
    }
    foreach (var p in ports)
    {
        Console.WriteLine($"{p.PortName,-14} {Ids(p),-11} {p.Likelihood,-10} {p.Hint}");
    }
    return 0;
}

await using var client = await ConnectAsync();
if (client is null)
{
    Console.Error.WriteLine(endpoint is null
        ? "No MeshCore companion radio found on USB. For a WiFi radio use --connect <ip>[:port]."
        : $"Could not connect to {endpoint}.");
    return 2;
}

try
{
    switch (command)
    {
        case "info":
            await PrintInfoAsync();
            break;
        case "contacts":
            await PrintContactsAsync();
            break;
        case "channels":
            await PrintChannelsAsync();
            break;
        case "messages":
            await DrainAsync();
            break;
        case "listen":
            await ListenAsync();
            break;
        case "send" when rest.Count >= 2:
            await SendDirectAsync(rest[0], string.Join(' ', rest.Skip(1)));
            break;
        case "send-channel" when rest.Count >= 2 && byte.TryParse(rest[0], CultureInfo.InvariantCulture, out var idx):
            await client.SendChannelTextMessageAsync(idx, string.Join(' ', rest.Skip(1)), cts.Token);
            Console.WriteLine($"Sent to channel {idx}.");
            break;
        case "advert":
            await client.SendAdvertAsync(flood, cts.Token);
            Console.WriteLine(flood ? "Flood advert sent." : "Zero-hop advert sent.");
            break;
        default:
            Console.Error.WriteLine($"Unknown or incomplete command '{command}'.\n");
            Console.WriteLine(Usage);
            return 1;
    }
}
catch (MeshCoreException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 3;
}
catch (OperationCanceledException)
{
}
return 0;

// ---------------------------------------------------------------------------

async Task<MeshCoreClient?> ConnectAsync()
{
    if (endpoint is not null)
    {
        var c = new MeshCoreClient(endpoint.CreateTransport(loggerFactory), null, loggerFactory.CreateLogger<MeshCoreClient>());
        try
        {
            await c.ConnectAsync(cts.Token);
            Console.WriteLine($"Connected to {endpoint}");
            return c;
        }
        catch (Exception ex) when (ex is MeshCoreException or IOException or System.Net.Sockets.SocketException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"{endpoint}: {ex.Message}");
            await c.DisposeAsync();
            return null;
        }
    }

    var candidates = CompanionLocator.Candidates(SerialPortDiscovery.GetPorts());
    var found = await new CompanionLocator(loggerFactory: loggerFactory).FindAsync(candidates, cts.Token);
    if (found is not null)
    {
        Console.WriteLine($"Connected on {found.Port}");
    }
    return found?.Client;
}

async Task PrintInfoAsync()
{
    var self = client.SelfInfo!;
    var dev = client.DeviceInfo!;
    var battery = await client.GetBatteryAndStorageAsync(cts.Token);
    var clock = await client.GetDeviceTimeAsync(cts.Token);

    Console.WriteLine($"""
        Node        {self.Name} ({self.Type})
        Public key  {self.PublicKey}
        Location    {self.Latitude:0.000000}, {self.Longitude:0.000000}
        Radio       {self.FrequencyMhz:0.000} MHz, BW {self.BandwidthKhz:0.0} kHz, SF{self.SpreadingFactor}, CR{self.CodingRate}, TX {self.TxPowerDbm}/{self.MaxTxPowerDbm} dBm
        Model       {dev.Model}
        Firmware    {dev.FirmwareVersion} ({dev.FirmwareBuildDate}, protocol v{dev.FirmwareVersionCode})
        Capacity    {dev.MaxContacts} contacts, {dev.MaxChannels} channels
        Battery     {battery.BatteryVolts:0.000} V
        Storage     {(battery.StorageTotalKb is { } total ? $"{battery.StorageUsedKb} / {total} KB" : "n/a")}
        Clock       {clock.ToLocalTime():yyyy-MM-dd HH:mm:ss}
        """);
}

async Task PrintContactsAsync()
{
    var contacts = await client.GetContactsAsync(cancellationToken: cts.Token);
    Console.WriteLine($"{contacts.Count} contact(s)");
    foreach (var c in contacts.OrderBy(c => c.Type).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
    {
        var path = c.OutPathLength.IsNone ? "flood" : $"{c.OutPathLength.HopCount} hop(s)";
        Console.WriteLine($"  {c.Type,-9} {c.Name,-32} {c.PublicKey.Prefix}  {path,-9} last advert {c.LastAdvert.ToLocalTime():yyyy-MM-dd HH:mm}");
    }
}

async Task PrintChannelsAsync()
{
    var channels = await client.GetChannelsAsync(cancellationToken: cts.Token);
    Console.WriteLine($"{channels.Count} channel(s) of {client.DeviceInfo?.MaxChannels}");
    foreach (var ch in channels)
    {
        Console.WriteLine($"  [{ch.Index}] {ch.Name}");
    }
}

async Task DrainAsync()
{
    var messages = await client.DrainMessagesAsync(cts.Token);
    Console.WriteLine($"{messages.Count} message(s)");
    var contacts = messages.OfType<ContactMessage>().Any() ? await client.GetContactsAsync(cancellationToken: cts.Token) : [];
    foreach (var m in messages)
    {
        PrintMessage(m, contacts);
    }
}

async Task ListenAsync()
{
    var contacts = await client.GetContactsAsync(cancellationToken: cts.Token);
    var drain = new SemaphoreSlim(1, 1);

    async Task DrainAndPrintAsync()
    {
        await drain.WaitAsync(cts.Token);
        try
        {
            foreach (var m in await client.DrainMessagesAsync(cts.Token))
            {
                PrintMessage(m, contacts);
            }
        }
        finally
        {
            drain.Release();
        }
    }

    client.PushReceived += (_, e) =>
    {
        switch (e.Frame)
        {
            case MessagesWaitingPush:
                _ = DrainAndPrintAsync();
                break;
            case SendConfirmedPush ack:
                Console.WriteLine($"{Stamp()} ✓ delivered (ack {ack.AckCode:X8}, {ack.RoundTrip.TotalMilliseconds:0} ms)");
                break;
            case AdvertPush advert:
                Console.WriteLine($"{Stamp()} advert from {NameOf(advert.PublicKey, contacts)}");
                break;
            case NewAdvertPush n:
                Console.WriteLine($"{Stamp()} new node heard: {n.Contact.Name} ({n.Contact.Type})");
                break;
            case PathUpdatedPush p:
                Console.WriteLine($"{Stamp()} path updated for {NameOf(p.PublicKey, contacts)}");
                break;
            default:
                if (verbose)
                {
                    Console.WriteLine($"{Stamp()} push 0x{e.Frame.Code:X2} {e.Frame.GetType().Name}");
                }
                break;
        }
    };

    var lost = new TaskCompletionSource();
    client.Disconnected += (_, e) => lost.TrySetResult();

    Console.WriteLine($"Listening on {client.TransportDescription} as '{client.SelfInfo!.Name}'. Ctrl+C to stop.");
    await DrainAndPrintAsync();
    await Task.WhenAny(lost.Task, Task.Delay(Timeout.Infinite, cts.Token)).ContinueWith(_ => { }, TaskScheduler.Default);
    if (lost.Task.IsCompleted)
    {
        Console.Error.WriteLine("Connection lost.");
    }
}

async Task SendDirectAsync(string to, string text)
{
    var contacts = await client.GetContactsAsync(cancellationToken: cts.Token);
    var matches = contacts.Where(c =>
        c.Name.Equals(to, StringComparison.OrdinalIgnoreCase) ||
        c.PublicKey.Hex.StartsWith(to.ToLowerInvariant(), StringComparison.Ordinal)).ToList();

    if (matches.Count != 1)
    {
        Console.Error.WriteLine(matches.Count == 0 ? $"No contact matches '{to}'." : $"'{to}' matches {matches.Count} contacts; use more of the key.");
        return;
    }

    var target = matches[0];
    var sent = await client.SendTextMessageAsync(target.PublicKey.Prefix, text, cancellationToken: cts.Token);
    Console.WriteLine($"Sent to {target.Name} ({(sent.Flooded ? "flood" : "direct")}), expecting ack {sent.ExpectedAck:X8} within {sent.EstimatedTimeout.TotalSeconds:0} s.");
}

void PrintMessage(MeshFrame frame, IReadOnlyList<Contact> contacts)
{
    switch (frame)
    {
        case ContactMessage m:
            var from = contacts.FirstOrDefault(c => c.PublicKey.Matches(m.SenderPrefix))?.Name ?? m.SenderPrefix.Hex;
            Console.WriteLine($"{m.SenderTimestamp.ToLocalTime():HH:mm:ss} [DM] {from}: {m.Text}{Signal(m.Snr, m.PathLength)}");
            break;
        case ChannelMessage m:
            Console.WriteLine($"{m.SenderTimestamp.ToLocalTime():HH:mm:ss} [#{m.ChannelIndex}] {m.Text}{Signal(m.Snr, m.PathLength)}");
            break;
        default:
            Console.WriteLine($"(frame 0x{frame.Code:X2} {frame.GetType().Name})");
            break;
    }
}

static string Signal(double? snr, PathLength path) =>
    $"  ({(path.IsNone ? "direct" : $"{path.HopCount} hop(s)")}{(snr is { } s ? $", SNR {s:0.0} dB" : "")})";

static string NameOf(PublicKey key, IReadOnlyList<Contact> contacts) =>
    contacts.FirstOrDefault(c => c.PublicKey == key)?.Name ?? key.Prefix.Hex;

static string Stamp() => DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

static string Ids(SerialPortInfo p) => p.VendorId is { } v ? $"{v:X4}:{p.ProductId:X4}" : "-";

static bool TakeFlag(List<string> list, string flag) => list.Remove(flag);

static string? TakeOption(List<string> list, string name)
{
    var i = list.IndexOf(name);
    if (i < 0 || i + 1 >= list.Count)
    {
        return null;
    }
    var value = list[i + 1];
    list.RemoveRange(i, 2);
    return value;
}
