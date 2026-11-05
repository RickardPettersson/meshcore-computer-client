using System.Net;
using MeshCore.Host.Api;
using MeshCore.Host.Auth;
using MeshCore.Protocol;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeshCore.Host.Web.Controllers;

public sealed class HomeController(MeshApiClient api) : WebController
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var device = await api.DeviceAsync(ct);
        var battery = device.State == Radio.RadioState.Connected ? await TryAsync(() => api.BatteryAsync(ct)) : null;
        var contacts = await api.ContactsAsync(ct: ct);
        var channels = await api.ChannelsAsync(ct);
        var recent = (await api.ConversationsAsync(ct)).Where(c => c.LastMessage is not null).Take(5).ToList();
        return View(new DashboardViewModel(device, battery, contacts.Length, channels.Length, recent));
    }

    internal static async Task<T?> TryAsync<T>(Func<Task<T>> call) where T : class
    {
        try
        {
            return await call();
        }
        catch (ApiException ex) when (ex.Status is HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
        {
            return null; // Radio offline or slow: show the page without live figures.
        }
    }
}

public sealed partial class DeviceController(MeshApiClient api) : WebController
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var device = await api.DeviceAsync(ct);
        var online = device.State == Radio.RadioState.Connected;
        return View(new DevicePageViewModel(
            device,
            online ? await HomeController.TryAsync(() => api.BatteryAsync(ct)) : null,
            online ? await HomeController.TryAsync(() => api.DeviceTimeAsync(ct)) : null));
    }

    [HttpPost]
    public async Task<IActionResult> Advert(bool flood, CancellationToken ct)
    {
        await api.SendAdvertAsync(flood, ct);
        Flash(flood ? "Flood advert sent through the mesh." : "Advert sent to nearby nodes.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, Authorize(AuthPolicies.Admin)]
    public async Task<IActionResult> SyncTime(CancellationToken ct)
    {
        try
        {
            var time = await api.SyncDeviceTimeAsync(ct);
            Flash($"Radio clock set. Drift is now {time.DriftSeconds:0.0} s.");
        }
        catch (ApiException ex) when (ex.Status == HttpStatusCode.Conflict)
        {
            FlashError("The radio's clock is ahead of this computer, and it won't set its clock backwards.");
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, Authorize(AuthPolicies.Admin)]
    public async Task<IActionResult> Reboot(CancellationToken ct)
    {
        await api.RebootAsync(ct);
        Flash("Reboot sent. The radio will be back in a few seconds and the app reconnects by itself.");
        return RedirectToAction(nameof(Index));
    }
}

public sealed partial class ContactsController(MeshApiClient api) : WebController
{
    public async Task<IActionResult> Index(bool removed, CancellationToken ct)
    {
        ViewData["ShowRemoved"] = removed;
        ViewData["Discovered"] = await api.DiscoveredAsync(ct);
        return View(await api.ContactsAsync(removed, ct));
    }

    [HttpPost]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        var contacts = await api.RefreshContactsAsync(ct);
        Flash($"Contact list refreshed from the radio: {contacts.Length} contact(s).");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, Authorize(AuthPolicies.Admin)]
    public async Task<IActionResult> ResetPath(string key, CancellationToken ct)
    {
        await api.ResetPathAsync(key, ct);
        Flash("Route forgotten. The next message to this contact will flood to find a new one.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, Authorize(AuthPolicies.Admin)]
    public async Task<IActionResult> Remove(string key, string name, CancellationToken ct)
    {
        await api.RemoveContactAsync(key, ct);
        Flash($"{name} was removed from the radio. Its message history is kept.");
        return RedirectToAction(nameof(Index));
    }
}

public sealed class ChannelsController(MeshApiClient api) : WebController
{
    public async Task<IActionResult> Index(CancellationToken ct) =>
        View(new ChannelsPageViewModel(await api.ChannelsAsync(ct), (await api.DeviceAsync(ct)).Hardware?.MaxChannels, null));

    /// <summary>Shows a channel's key so it can be shared. Admin only, and only on request.</summary>
    [HttpPost, Authorize(AuthPolicies.Admin)]
    public async Task<IActionResult> Reveal(byte index, CancellationToken ct) =>
        View(nameof(Index), new ChannelsPageViewModel(await api.ChannelsAsync(ct), (await api.DeviceAsync(ct)).Hardware?.MaxChannels, await api.ChannelSecretAsync(index, ct)));

    [HttpGet, Authorize(AuthPolicies.Admin)]
    public async Task<IActionResult> Edit(byte? index, CancellationToken ct)
    {
        var channels = await api.ChannelsAsync(ct);
        if (index is { } i && channels.FirstOrDefault(c => c.Index == i) is { } existing)
        {
            return View(new ChannelEditViewModel { Index = existing.Index, Name = existing.Name });
        }

        var max = (await api.DeviceAsync(ct)).Hardware?.MaxChannels ?? 8;
        var free = Enumerable.Range(0, max).Select(n => (byte)n).Where(n => channels.All(c => c.Index != n)).Cast<byte?>().FirstOrDefault();
        if (free is null)
        {
            FlashError($"All {max} channel slots are in use. Delete one first.");
            return RedirectToAction(nameof(Index));
        }
        return View(new ChannelEditViewModel { Index = free.Value, IsNew = true });
    }

    [HttpPost, Authorize(AuthPolicies.Admin)]
    public async Task<IActionResult> Edit(ChannelEditViewModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }
        try
        {
            var secret = string.IsNullOrWhiteSpace(model.SecretHex) ? null : model.SecretHex.Trim();
            if (secret is null && !model.IsNew)
            {
                // Renaming only: keep the existing key, or everyone else in the channel would be cut off.
                secret = (await api.ChannelSecretAsync(model.Index, ct)).SecretHex;
            }
            await api.SetChannelAsync(model.Index, model.Name, secret, ct);
        }
        catch (ApiException ex) when (ex.Status is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
        {
            ModelState.AddModelError("", ex.Message);
            return View(model);
        }
        Flash(model.IsNew ? $"Channel '{model.Name}' added in slot {model.Index}." : $"Channel '{model.Name}' saved.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, Authorize(AuthPolicies.Admin)]
    public async Task<IActionResult> Delete(byte index, string name, CancellationToken ct)
    {
        await api.DeleteChannelAsync(index, ct);
        Flash($"Channel '{name}' deleted from the radio.");
        return RedirectToAction(nameof(Index));
    }
}

/// <summary>
/// Chats. Server-rendered for now: the list, a thread, and a send form (post/redirect/get).
/// Phase 6 makes this live with SignalR.
/// </summary>
public sealed class MessagesController(MeshApiClient api) : WebController
{
    public async Task<IActionResult> Index(CancellationToken ct) => View(await api.ConversationsAsync(ct));

    public async Task<IActionResult> Channel(byte id, long? before, CancellationToken ct)
    {
        var channel = (await api.ChannelsAsync(ct)).FirstOrDefault(c => c.Index == id);
        if (channel is null)
        {
            return NotFound();
        }
        var device = await api.DeviceAsync(ct);
        var messages = await api.MessagesAsync(channel: id, before: before, ct: ct);
        var key = Data.ConversationKey.Channel(id);
        await MarkNewestReadAsync(key, messages, before, ct);
        var limit = ProtocolLimits.MaxChannelTextBytes(device.Node?.Name ?? "");
        return View("Chat", new ChatViewModel(key, channel.Name, $"Channel {id}", null, id, messages.Reverse().ToList(), await UserNamesAsync(ct), limit, TempData["Draft"] as string));
    }

    public async Task<IActionResult> Direct(string id, long? before, CancellationToken ct)
    {
        var contact = await api.ContactAsync(id, ct);
        var messages = await api.MessagesAsync(contact: contact.KeyPrefix, before: before, ct: ct);
        var key = Data.ConversationKey.Direct(contact.KeyPrefix);
        await MarkNewestReadAsync(key, messages, before, ct);
        var subtitle = contact.Hops is { } h ? $"{h} hop(s)" : "route unknown (floods)";
        var cli = IsAdmin && contact.Type is AdvertType.Repeater or AdvertType.Room;
        return View("Chat", new ChatViewModel(key, contact.Name, subtitle, contact.PublicKey, null, messages.Reverse().ToList(), await UserNamesAsync(ct), ProtocolLimits.MaxTextBytes, TempData["Draft"] as string, cli));
    }

    /// <summary>
    /// Sends a message. With JavaScript (<c>X-Requested-With: fetch</c>) it answers with JSON and the page updates live;
    /// without, it's a classic post/redirect/get.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Send(string? contact, byte? channel, string text, bool cli, CancellationToken ct)
    {
        var ajax = Request.Headers.XRequestedWith == "fetch";
        try
        {
            var sent = contact is not null && cli
                ? await api.CliAsync(contact, text, ct)
                : contact is not null
                ? await api.SendDirectAsync(contact, text, ct)
                : await api.SendChannelAsync(channel ?? 0, text, ct);
            if (ajax)
            {
                return Json(sent, MeshApiClient.Json);
            }
            return contact is not null ? RedirectToAction(nameof(Direct), new { id = contact }) : RedirectToAction(nameof(Channel), new { id = channel ?? 0 });
        }
        catch (ApiException ex) when (ex.Status is HttpStatusCode.BadRequest or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout or HttpStatusCode.Conflict)
        {
            if (ajax)
            {
                return StatusCode((int)ex.Status, new { error = ex.Message });
            }
            FlashError(ex.Message);
            TempData["Draft"] = text; // Don't lose what they typed.
            return contact is not null ? RedirectToAction(nameof(Direct), new { id = contact }) : RedirectToAction(nameof(Channel), new { id = channel ?? 0 });
        }
    }

    /// <summary>Called by the chat page when new messages are seen while it's open.</summary>
    [HttpPost]
    public async Task<IActionResult> MarkRead(string key, long messageId, CancellationToken ct)
    {
        if (!Data.ConversationKey.TryParse(key, out _, out _, out _))
        {
            return BadRequest();
        }
        await api.MarkReadAsync(key, messageId, ct);
        return NoContent();
    }

    /// <summary>Messages newer than <paramref name="after"/>, for catching up after the live connection drops.</summary>
    [HttpGet]
    public async Task<IActionResult> Updates(string key, long after, CancellationToken ct)
    {
        if (!Data.ConversationKey.TryParse(key, out _, out var prefix, out var channel))
        {
            return BadRequest();
        }
        return Json(await api.MessagesAfterAsync(prefix, channel, after, ct), MeshApiClient.Json);
    }

    /// <summary>Total unread for the signed-in user (keeps the navigation badge exact).</summary>
    [HttpGet]
    public async Task<IActionResult> UnreadCount(CancellationToken ct) =>
        Json(new { total = (await api.ConversationsAsync(ct)).Sum(c => c.Unread) });

    /// <summary>The chat list, without the page around it (the live script swaps it in when something changes).</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => PartialView("_ConversationList", await api.ConversationsAsync(ct));

    private async Task MarkNewestReadAsync(string key, MessageResponse[] newestFirst, long? before, CancellationToken ct)
    {
        // Only when looking at the latest page; paging back through history doesn't change what's unread.
        if (before is null && newestFirst.Length > 0)
        {
            await api.MarkReadAsync(key, newestFirst[0].Id, ct);
        }
    }

    /// <summary>Who sent our outgoing messages. Only Admins may list users, so others see "you" / "another user".</summary>
    private async Task<IReadOnlyDictionary<string, string>> UserNamesAsync(CancellationToken ct)
    {
        if (IsAdmin)
        {
            return (await api.UsersAsync(ct)).ToDictionary(u => u.Id, u => u.Username);
        }
        var me = User.FindFirst(MeshClaims.UserId)?.Value;
        return me is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [me] = User.Identity!.Name! };
    }
}

/// <summary>Account settings for everyone, user management for Admins.</summary>
public sealed class SettingsController(MeshApiClient api) : WebController
{
    public async Task<IActionResult> Index(CancellationToken ct) => View(await api.MeAsync(ct));

    [Authorize(AuthPolicies.Admin)]
    public async Task<IActionResult> Users(CancellationToken ct) => View(await api.UsersAsync(ct));

    [HttpGet, Authorize(AuthPolicies.Admin)]
    public IActionResult CreateUser() => View(new CreateUserViewModel());

    [HttpPost, Authorize(AuthPolicies.Admin)]
    public async Task<IActionResult> CreateUser(CreateUserViewModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }
        try
        {
            var user = await api.CreateUserAsync(model.Username, model.TemporaryPassword, model.Role, ct);
            Flash($"User '{user.Username}' added. Give them the temporary password; they'll choose their own at first sign-in.");
            return RedirectToAction(nameof(Users));
        }
        catch (ApiException ex) when (ex.Status is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
        {
            ModelState.AddModelError("", ex.Message);
            return View(model);
        }
    }

    [HttpPost, Authorize(AuthPolicies.Admin)]
    public async Task<IActionResult> UpdateUser(string id, UserRole? role, bool? enabled, CancellationToken ct)
    {
        var user = await api.UpdateUserAsync(id, role, enabled, ct);
        Flash($"'{user.Username}' is now {(user.Enabled ? "enabled" : "disabled")} ({user.Role}).");
        return RedirectToAction(nameof(Users));
    }

    [HttpPost, Authorize(AuthPolicies.Admin)]
    public async Task<IActionResult> ResetPassword(string id, string temporaryPassword, CancellationToken ct)
    {
        var user = await api.ResetPasswordAsync(id, temporaryPassword, ct);
        Flash($"Temporary password set for '{user.Username}'. They've been signed out and must choose a new one.");
        return RedirectToAction(nameof(Users));
    }

    [HttpPost, Authorize(AuthPolicies.Admin)]
    public async Task<IActionResult> DeleteUser(string id, string username, CancellationToken ct)
    {
        await api.DeleteUserAsync(id, ct);
        Flash($"User '{username}' deleted.");
        return RedirectToAction(nameof(Users));
    }
}
