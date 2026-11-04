using MeshCore.Host.Api;
using Microsoft.AspNetCore.Mvc;

namespace MeshCore.Host.Web;

/// <summary>The little radio status pill in the navigation (via the API, like everything else).</summary>
public sealed class RadioStatusViewComponent(MeshApiClient api) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return Content("");
        }
        // Shown in both the sidebar and the mobile top bar: ask the API once per request.
        const string key = "RadioStatus";
        if (!HttpContext.Items.TryGetValue(key, out var cached))
        {
            try
            {
                cached = await api.DeviceAsync(HttpContext.RequestAborted);
            }
            catch (ApiException)
            {
                cached = null;
            }
            HttpContext.Items[key] = cached;
        }
        return View(cached as DeviceResponse);
    }
}
