using System.Globalization;
using MeshCore.Client;
using MeshCore.Data;

namespace MeshCore.Host.Radio;

/// <summary>
/// Optional interactive chat in the console window, for trying the radio without the website.
/// Enabled when the app runs in a terminal (<c>MeshCore:ConsoleChat</c>, default true when interactive).
/// </summary>
public sealed class ConsoleChatService(MeshRadio radio, IHostApplicationLifetime lifetime) : BackgroundService
{
    private const string Help = """
        Console chat — type a message to send it to the current channel, or:
          /dm <name|key> <text>   direct message a contact
          /ch <n> [text]          switch to channel n (and send text)
          /contacts               list contacts
          /channels               list channels
          /history [n]            last n messages here (default 20)
          /status                 radio connection
          /sync                   fetch queued messages now
          /quit                   stop the app
        """;

    private byte _channel;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _names = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the host finish starting (and its startup logs print) before taking over stdin.
        await Task.Yield();
        radio.MessageChanged += OnMessage;
        radio.StatusChanged += (_, s) => Print($"[radio] {Describe(s)}");
        radio.ContactsChanged += (_, _) => _ = ReloadNamesAsync();
        await ReloadNamesAsync();
        Print(Help);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var line = await Task.Run(Console.ReadLine, stoppingToken).WaitAsync(stoppingToken);
                if (line is null)
                {
                    return; // stdin closed
                }
                if (line.Trim().Length > 0)
                {
                    await HandleAsync(line.Trim(), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            radio.MessageChanged -= OnMessage;
        }
    }

    private async Task HandleAsync(string line, CancellationToken ct)
    {
        try
        {
            var (cmd, rest) = Split(line);
            switch (cmd)
            {
                case "/help":
                    Print(Help);
                    break;
                case "/quit":
                    lifetime.StopApplication();
                    break;
                case "/status":
                    Print(Describe(radio.Status));
                    break;
                case "/sync":
                    Print($"{await radio.SyncMessagesAsync(ct)} new message(s).");
                    break;
                case "/contacts":
                    var contacts = await radio.Store.GetContactsAsync(cancellationToken: ct);
                    Print(contacts.Count == 0 ? "No contacts yet. Nodes appear after they advertise." : string.Join(Environment.NewLine,
                        contacts.Select(c => $"  {c.Type,-9} {c.Name,-32} {c.KeyPrefix}  {(c.OutPathLength == 0xFF ? "flood" : $"{c.OutPathLength & 0x3F} hop(s)")}")));
                    break;
                case "/channels":
                    var channels = await radio.Store.GetChannelsAsync(ct);
                    Print(string.Join(Environment.NewLine, channels.Select(c => $"  [{c.Index}] {c.Name}{(c.Index == _channel ? "  ← current" : "")}")));
                    break;
                case "/history":
                    var take = int.TryParse(rest, CultureInfo.InvariantCulture, out var n) ? n : 20;
                    var history = await radio.Store.GetMessagesAsync(channelIndex: _channel, take: take, cancellationToken: ct);
                    foreach (var m in history.Reverse())
                    {
                        Print(Format(m));
                    }
                    break;
                case "/ch":
                    var (idx, text) = Split(rest);
                    if (!byte.TryParse(idx, CultureInfo.InvariantCulture, out _channel))
                    {
                        Print("Usage: /ch <n> [text]");
                        break;
                    }
                    Print($"Current channel: {_channel}");
                    if (text.Length > 0)
                    {
                        await radio.SendChannelAsync(_channel, text, cancellationToken: ct);
                    }
                    break;
                case "/dm":
                    var (to, body) = Split(rest);
                    var contact = to.Length > 0 ? await radio.Store.FindContactAsync(to, ct) : null;
                    if (contact is null || body.Length == 0)
                    {
                        Print(to.Length == 0 || body.Length == 0 ? "Usage: /dm <name|key> <text>" : $"No single contact matches '{to}'. Try /contacts.");
                        break;
                    }
                    await radio.SendDirectAsync(contact.PublicKey, body, cancellationToken: ct);
                    break;
                default:
                    if (cmd.StartsWith('/'))
                    {
                        Print($"Unknown command {cmd}. Type /help.");
                    }
                    else
                    {
                        await radio.SendChannelAsync(_channel, line, cancellationToken: ct);
                    }
                    break;
            }
        }
        catch (ArgumentException ex)
        {
            Print($"! {ex.Message.Replace($" (Parameter '{ex.ParamName}')", "", StringComparison.Ordinal)}");
        }
        catch (MeshCoreException ex)
        {
            Print($"! {ex.Message}");
        }
    }

    private void OnMessage(object? sender, MessageEventArgs e)
    {
        var m = e.Message;
        // Outgoing: print once it has a final-ish status, not on every intermediate step.
        if (m.Direction == MessageDirection.Outgoing && m.Status == DeliveryStatus.Sending)
        {
            return;
        }
        if (m.Direction == MessageDirection.Outgoing && !e.IsNew && m.Status != DeliveryStatus.Sent)
        {
            Print($"  ↳ #{m.Id} {m.Status}{(m.RoundTripMs is { } rt ? $" ({rt} ms)" : "")}{(m.Error is { } err ? $": {err}" : "")}");
            return;
        }
        Print(Format(m));
    }

    private string Format(MessageEntity m)
    {
        var at = m.ReceivedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var where = m.Kind == MessageKind.Channel ? $"#{m.ChannelIndex}" : "DM";
        var who = m.Direction == MessageDirection.Outgoing
            ? $"me → {(m.Kind == MessageKind.Direct ? NameFor(m.ContactKeyPrefix) : $"#{m.ChannelIndex}")}"
            : m.SenderName ?? m.ContactKeyPrefix ?? "?";
        var signal = m.Direction == MessageDirection.Incoming
            ? $"  ({(m.Hops is { } h ? $"{h} hop(s)" : "direct")}{(m.Snr is { } s ? string.Create(CultureInfo.InvariantCulture, $", SNR {s:0.0} dB") : "")})"
            : $"  [{m.Status}]";
        return $"{at} [{where}] {who}: {m.Text}{signal}";
    }

    private string NameFor(string? prefix) =>
        prefix is null ? "?" : _names.TryGetValue(prefix, out var name) ? name : prefix;

    private async Task ReloadNamesAsync()
    {
        foreach (var c in await radio.Store.GetContactsAsync(includeRemoved: true))
        {
            _names[c.KeyPrefix] = c.Name;
        }
    }

    private static string Describe(RadioStatus s) => s.State switch
    {
        RadioState.Connected => $"connected to '{s.Self?.Name}' ({s.Device?.Model}) on {s.Endpoint}",
        RadioState.InUseElsewhere => $"another app is using the radio; retrying {Until(s.NextAttempt)}",
        RadioState.WaitingToRetry => $"not connected ({s.LastError}); retrying {Until(s.NextAttempt)}",
        _ => $"{s.State.ToString().ToLowerInvariant()} {s.Endpoint}",
    };

    private static string Until(DateTimeOffset? at) =>
        at is { } t ? $"at {t.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)}" : "soon";

    private static (string Head, string Tail) Split(string text)
    {
        var space = text.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? (text, "") : (text[..space], text[(space + 1)..].Trim());
    }

    private static void Print(string text) => Console.WriteLine(text);
}
