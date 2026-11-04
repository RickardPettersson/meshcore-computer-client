using MeshCore.Host.Api;
using Microsoft.AspNetCore.Mvc;

namespace MeshCore.Host.Web;

/// <summary>Total unread messages for the signed-in user, next to "Chats" in the navigation. Kept live by live.js.</summary>
public sealed class UnreadBadgeViewComponent(MeshApiClient api) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        // Shown in both the sidebar and the tab bar: ask the API once per request.
        const string key = "UnreadTotal";
        if (!HttpContext.Items.TryGetValue(key, out var cached))
        {
            try
            {
                cached = (await api.ConversationsAsync(HttpContext.RequestAborted)).Sum(c => c.Unread);
            }
            catch (ApiException)
            {
                cached = 0;
            }
            HttpContext.Items[key] = cached;
        }
        return View((int)cached!);
    }
}
