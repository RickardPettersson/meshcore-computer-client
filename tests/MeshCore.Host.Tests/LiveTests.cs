using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MeshCore.Client;
using MeshCore.Client.Tests;
using MeshCore.Data;
using MeshCore.Host.Radio;
using MeshCore.Protocol;
using MeshCore.Protocol.Frames;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using static MeshCore.Client.Tests.FakeRadio;

namespace MeshCore.Host.Tests;

public class UnreadTests
{
    [Fact]
    public async Task Unread_counts_are_per_user_and_markers_only_move_forward()
    {
        await using var db = await TestDb.CreateAsync();
        var store = new MeshStore(db);
        for (var i = 0; i < 3; i++)
        {
            await store.SaveIncomingAsync(FrameParser.Parse(ChannelMessage(0, $"Bob: {i}")));
        }
        await store.SaveIncomingAsync(FrameParser.Parse(Concat([0x10, 0, 0, 0], Key(10)[..6], [0xFF, 0], U32(1), "dm"u8.ToArray())));
        var mine = await store.SaveOutgoingAsync(MessageKind.Channel, null, 0, "me", "outgoing doesn't count", "u1");
        var dmKey = ConversationKey.Direct(Convert.ToHexStringLower(Key(10)[..6]));

        Assert.Equal(3, (await store.GetUnreadCountsAsync("u1"))[ConversationKey.Channel(0)]);
        Assert.Equal(1, (await store.GetUnreadCountsAsync("u1"))[dmKey]);

        await store.MarkReadAsync("u1", ConversationKey.Channel(0), 2);
        await store.MarkReadAsync("u1", ConversationKey.Channel(0), 1); // ignored: never moves back

        Assert.Equal(1, (await store.GetUnreadCountsAsync("u1"))[ConversationKey.Channel(0)]);
        Assert.Equal(3, (await store.GetUnreadCountsAsync("u2"))[ConversationKey.Channel(0)]); // other users unaffected

        await store.MarkReadAsync("u1", ConversationKey.Channel(0), mine.Id);
        Assert.False((await store.GetUnreadCountsAsync("u1")).ContainsKey(ConversationKey.Channel(0)));
    }

    [Fact]
    public async Task Messages_after_an_id_come_back_oldest_first()
    {
        await using var db = await TestDb.CreateAsync();
        var store = new MeshStore(db);
        for (var i = 0; i < 4; i++)
        {
            await store.SaveIncomingAsync(FrameParser.Parse(ChannelMessage(0, $"Bob: {i}")));
        }

        var after = await store.GetMessagesAfterAsync(null, 0, afterId: 2);

        Assert.Equal(["2", "3"], after.Select(m => m.Text));
    }

    [Theory]
    [InlineData("c:3", MessageKind.Channel, null, (byte)3)]
    [InlineData("d:0A0B0C0D0E0F", MessageKind.Direct, "0a0b0c0d0e0f", null)]
    public void Conversation_keys_round_trip(string key, MessageKind kind, string? prefix, byte? channel)
    {
        Assert.True(ConversationKey.TryParse(key, out var k, out var p, out var c));
        Assert.Equal((kind, prefix, channel), (k, p, c));
    }

    [Theory]
    [InlineData("x:1")]
    [InlineData("c:abc")]
    [InlineData("d:short")]
    [InlineData(null)]
    public void Bad_conversation_keys_are_rejected(string? key) => Assert.False(ConversationKey.TryParse(key, out _, out _, out _));

    [Fact]
    public async Task Api_reports_unread_per_signed_in_user()
    {
        var queue = new ConcurrentQueue<byte[]>([ChannelMessage(0, "Bob: one"), ChannelMessage(0, "Bob: two")]);
        var radio = ApiFactory.DefaultRadio().On(0x0A, _ => [queue.TryDequeue(out var m) ? m : [0x0A]]);
        using var app = new ApiFactory { Radio = radio };
        var admin = await app.AdminClientAsync();
        await app.WaitForRadioAsync();

        var before = await admin.GetFromJsonAsync<JsonElement>("/api/v1/conversations", ApiFactory.Json);
        var publicChat = before.EnumerateArray().Single(c => c.GetProperty("key").GetString() == "c:0");
        var newest = publicChat.GetProperty("lastMessage").GetProperty("id").GetInt64();
        var marked = await admin.PostAsJsonAsync("/api/v1/conversations/read", new { key = "c:0", messageId = newest });
        var after = await admin.GetFromJsonAsync<JsonElement>("/api/v1/conversations", ApiFactory.Json);

        Assert.Equal(2, publicChat.GetProperty("unread").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, marked.StatusCode);
        Assert.Equal(0, after.EnumerateArray().Single(c => c.GetProperty("key").GetString() == "c:0").GetProperty("unread").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/conversations/read", new { key = "zz", messageId = 1 })).StatusCode);
    }
}

public class HubTests
{
    [Fact]
    public async Task Api_clients_get_new_messages_and_delivery_updates_live()
    {
        var queue = new ConcurrentQueue<byte[]>();
        var radio = ApiFactory.DefaultRadio().On(0x0A, _ => [queue.TryDequeue(out var m) ? m : [0x0A]]);
        using var app = new ApiFactory { Radio = radio };
        var admin = await app.AdminClientAsync();
        await app.WaitForRadioAsync();
        var token = admin.DefaultRequestHeaders.Authorization!.Parameter!;

        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(app.Server.BaseAddress, "/api/v1/hub"), o =>
            {
                o.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        var received = new ConcurrentQueue<JsonElement>();
        hub.On<JsonElement>("message", m => received.Enqueue(m));
        await hub.StartAsync();

        // An incoming message arrives on the radio...
        queue.Enqueue(ChannelMessage(0, "Bob: live!"));
        radio.Push([0x83]);
        // ...and we send a DM that the recipient then acknowledges.
        var alice = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/contacts", ApiFactory.Json))[0].GetProperty("publicKey").GetString();
        await admin.PostAsJsonAsync("/api/v1/messages/direct", new { contact = alice, text = "hi" });
        radio.Push(Concat([0x82], U32(0xACE), U32(900)));

        await WaitForAsync(() => received.Any(m => m.GetProperty("status").GetString() == "Delivered"));
        Assert.Contains(received, m => m.GetProperty("text").GetString() == "live!" && m.GetProperty("senderName").GetString() == "Bob");
        Assert.Contains(received, m => m.GetProperty("text").GetString() == "hi" && m.GetProperty("status").GetString() == "Sent");
        Assert.Contains(received, m => m.GetProperty("roundTripMs").ValueKind == JsonValueKind.Number && m.GetProperty("roundTripMs").GetInt32() == 900);
    }

    [Fact]
    public async Task The_hub_requires_a_signed_in_user()
    {
        using var app = new ApiFactory();
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(app.Server.BaseAddress, "/api/v1/hub"), o =>
            {
                o.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            })
            .Build();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => hub.StartAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    internal static async Task WaitForAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "Timed out waiting for a live update.");
            await Task.Delay(20);
        }
    }
}

public class LiveWebTests
{
    [Fact]
    public async Task Chat_page_sends_with_fetch_and_gets_json_back()
    {
        using var app = new ApiFactory();
        var browser = new Browser(app);
        await browser.SetupAsync();
        await app.WaitForRadioAsync();

        var response = await browser.SubmitWithHeaderAsync("/Messages/Channel/0", "/Messages/Send", ("X-Requested-With", "fetch"), ("channel", "0"), ("text", "via fetch"));
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("via fetch", json.GetProperty("text").GetString());
        Assert.Equal("Sent", json.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Fetch_send_errors_come_back_as_json()
    {
        using var app = new ApiFactory();
        var browser = new Browser(app);
        await browser.SetupAsync();
        await app.WaitForRadioAsync();

        var response = await browser.SubmitWithHeaderAsync("/Messages/Channel/0", "/Messages/Send", ("X-Requested-With", "fetch"), ("channel", "0"), ("text", new string('x', 200)));
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("limit", json.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Opening_a_chat_marks_it_read_and_updates_returns_newer_messages()
    {
        var queue = new ConcurrentQueue<byte[]>([ChannelMessage(0, "Bob: unread one")]);
        var radio = ApiFactory.DefaultRadio().On(0x0A, _ => [queue.TryDequeue(out var m) ? m : [0x0A]]);
        using var app = new ApiFactory { Radio = radio };
        var browser = new Browser(app);
        await browser.SetupAsync();
        await app.WaitForRadioAsync();

        var dashboard = await browser.PageAsync("/");
        Assert.Contains("data-unread-total=\"1\"", dashboard, StringComparison.Ordinal);

        var chat = await browser.PageAsync("/Messages/Channel/0");
        Assert.Contains("data-conversation=\"c:0\"", chat, StringComparison.Ordinal);
        Assert.Contains("data-unread-total=\"0\"", chat, StringComparison.Ordinal);

        var updates = await browser.GetAsync("/Messages/Updates?key=c:0&after=0");
        var list = await updates.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);
        Assert.Equal("unread one", Assert.Single(list.EnumerateArray()).GetProperty("text").GetString());
    }

    [Fact]
    public async Task Pages_include_the_live_script_and_pwa_manifest()
    {
        using var app = new ApiFactory();
        var browser = new Browser(app);
        await browser.SetupAsync();

        var page = await browser.PageAsync("/");
        var manifest = await browser.GetAsync("/manifest.webmanifest");

        Assert.Contains("data-hub=\"/hubs/mesh\"", page, StringComparison.Ordinal);
        Assert.Contains("/js/live.js", page, StringComparison.Ordinal);
        Assert.Contains("rel=\"manifest\"", page, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/sw.js")).StatusCode);
    }
}

public class SimulatorTests
{
    [Fact]
    public async Task The_simulator_speaks_the_real_protocol()
    {
        await using var client = new MeshCoreClient(new SimulatedRadio(TimeSpan.FromMilliseconds(100), TimeProvider.System),
            new MeshCoreClientOptions { CommandTimeout = TimeSpan.FromSeconds(2) });
        var pushes = new ConcurrentQueue<MeshFrame>();
        client.PushReceived += (_, e) => pushes.Enqueue(e.Frame);

        await client.ConnectAsync();
        var contacts = await client.GetContactsAsync();
        var channels = await client.GetChannelsAsync();
        var sent = await client.SendTextMessageAsync(contacts[0].PublicKey.Prefix, "hello sim");

        Assert.Equal("Simulator", client.SelfInfo?.Name);
        Assert.Equal(["Alice", "Bob", "Hilltop RPT"], contacts.Select(c => c.Name));
        Assert.Equal(["Public", "Simulator test"], channels.Select(c => c.Name));
        await HubTests.WaitForAsync(() => pushes.OfType<SendConfirmedPush>().Any(a => a.AckCode == sent.ExpectedAck));
        await HubTests.WaitForAsync(() => pushes.OfType<MessagesWaitingPush>().Any());
        Assert.NotEmpty(await client.DrainMessagesAsync());
    }
}

public class ChannelSenderTests
{
    [Fact]
    public async Task Channel_senders_who_are_contacts_link_to_a_private_chat()
    {
        using var app = new ApiFactory();
        var browser = new Browser(app);
        await browser.SetupAsync();
        await app.WaitForRadioAsync();
        var store = app.Services.GetRequiredService<MeshStore>();
        await store.SaveIncomingAsync(FrameParser.Parse(ChannelMessage(0, "Alice: anyone around?")));
        await store.SaveIncomingAsync(FrameParser.Parse(ChannelMessage(0, "Mallory: hi all")));

        var chat = await browser.PageAsync("/Messages/Channel/0");
        var alice = Convert.ToHexStringLower(Key(10));

        Assert.Contains($"title=\"Send Alice a private message\" href=\"/Messages/Direct/{alice}\"", chat, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("title=\"Mallory isn't in your contacts yet. Tap to see how to message them privately.\" href=\"/Messages/Sender?name=Mallory&amp;channel=0\"", chat, StringComparison.Ordinal);
        Assert.Contains("data-sender-contacts=\"{&quot;Alice&quot;:", chat, StringComparison.Ordinal); // for live messages (chat.js)
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync($"/Messages/Direct/{alice}")).StatusCode);
    }

    [Fact]
    public async Task Unknown_sender_page_explains_how_to_reach_them_and_contacts_go_straight_to_chat()
    {
        using var app = new ApiFactory { Radio = ApiFactory.DefaultRadio().On(0x07, Ok) };
        var browser = new Browser(app);
        await browser.SetupAsync();
        await app.WaitForRadioAsync();

        var page = await browser.PageAsync("/Messages/Sender?name=Mallory&channel=0");
        var alice = await browser.GetAsync("/Messages/Sender?name=Alice&channel=0");
        var advert = await browser.SubmitAsync("/Messages/Sender?name=Mallory&channel=0", "/Device/Advert",
            ("flood", "true"), ("returnUrl", "/Messages/Sender?name=Mallory&channel=0"));
        var offsite = await browser.SubmitAsync("/Device", "/Device/Advert", ("flood", "false"), ("returnUrl", "https://example.com/"));

        Assert.Contains("Mallory isn't in your contacts yet", page, StringComparison.Ordinal);
        Assert.Contains("Ask them to send an advert.", page, StringComparison.Ordinal);
        Assert.Contains("action=\"/Device/Advert\"", page, StringComparison.Ordinal);
        Assert.Contains("href=\"/Messages/Channel/0\"", page, StringComparison.Ordinal);                  // back to the channel
        Assert.Equal(HttpStatusCode.Redirect, alice.StatusCode);
        Assert.Equal($"/Messages/Direct/{Convert.ToHexStringLower(Key(10))}", alice.Headers.Location?.OriginalString, ignoreCase: true);
        Assert.Equal("/Messages/Sender?name=Mallory&channel=0", advert.Headers.Location?.OriginalString);
        Assert.Equal("/Device", offsite.Headers.Location?.OriginalString);                                   // never off-site
    }

    [Fact]
    public void Senders_are_matched_by_exact_unique_name()
    {
        static Host.Api.ContactResponse C(string key, string name) =>
            new(key, key[..12], name, AdvertType.Chat, false, null, 0, 0, DateTimeOffset.UnixEpoch, null, true);

        var senders = Web.ChatViewModel.SendersFrom([C("aa".PadRight(64, '1'), "Alice"), C("bb".PadRight(64, '2'), " Twin "), C("cc".PadRight(64, '3'), "Twin"), C("dd".PadRight(64, '4'), "")]);
        var model = new Web.ChatViewModel("c:0", "Public", null, null, 0, [], new Dictionary<string, string>(), 150, null, SenderContacts: senders);

        Assert.Equal(["Alice"], senders.Keys);                   // "Twin" is ambiguous, "" is skipped
        Assert.Equal("aa".PadRight(64, '1'), model.SenderKey("Alice "));
        Assert.Null(model.SenderKey("alice"));                     // names are case-sensitive on the mesh
        Assert.Null(model.SenderKey("Twin"));
        Assert.Null(model.SenderKey(null));
    }
}
