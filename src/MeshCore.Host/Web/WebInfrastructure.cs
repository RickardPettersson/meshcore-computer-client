using System.Net;
using System.Security.Claims;
using MeshCore.Host.Api;
using MeshCore.Host.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace MeshCore.Host.Web;

/// <summary>
/// Base for all website controllers: signed-in users only (unless overridden), antiforgery on every
/// unsafe request, forced password changes, and friendly handling of API errors.
/// </summary>
[Authorize(AuthPolicies.SignedIn)]
[AutoValidateAntiforgeryToken]
[ApiExplorerSettings(IgnoreApi = true)]
[ServiceFilter<ForcePasswordChangeFilter>]
[ServiceFilter<ApiErrorFilter>]
public abstract class WebController : Controller
{
    protected const string SuccessKey = "Success";
    protected const string ErrorKey = "Error";

    protected void Flash(string message) => TempData[SuccessKey] = message;

    protected void FlashError(string message) => TempData[ErrorKey] = message;

    protected bool IsAdmin => User.IsInRole(nameof(UserRole.Admin));

    /// <summary>Back to where the user came from (same site only), or <paramref name="fallback"/>.</summary>
    protected IActionResult RedirectBack(string fallback)
    {
        var referer = Request.Headers.Referer.ToString();
        return Uri.TryCreate(referer, UriKind.Absolute, out var uri) && uri.Host == Request.Host.Host
            ? LocalRedirect(uri.PathAndQuery)
            : LocalRedirect(fallback);
    }
}

/// <summary>Signs the browser in with an encrypted cookie that carries the user's API token.</summary>
public static class WebSignIn
{
    public static Task SignInAsync(HttpContext http, TokenResponse token, bool persistent)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(token);
        var identity = new ClaimsIdentity(TokenClaims.From(token.AccessToken),
            CookieAuthenticationDefaults.AuthenticationScheme, MeshClaims.Username, MeshClaims.Role);
        var properties = new AuthenticationProperties
        {
            // The cookie lives exactly as long as the token inside it.
            ExpiresUtc = token.ExpiresAt,
            IsPersistent = persistent,
            AllowRefresh = false,
        };
        properties.StoreTokens([new AuthenticationToken { Name = ApiTokenHandler.TokenName, Value = token.AccessToken }]);
        return http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), properties);
    }

    /// <summary>Re-issues the cookie with a new token (after a password change), keeping "remember me".</summary>
    public static async Task RefreshAsync(HttpContext http, TokenResponse token)
    {
        ArgumentNullException.ThrowIfNull(http);
        var current = await http.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        await SignInAsync(http, token, current.Properties?.IsPersistent ?? false);
    }

    public static Task SignOutAsync(HttpContext http) => http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
}

/// <summary>Remembers once setup is done, so pages don't ask the API on every request.</summary>
public sealed class SetupGate(IServiceScopeFactory scopes)
{
    private volatile bool _done;

    public async Task<bool> IsSetupRequiredAsync(CancellationToken ct)
    {
        if (_done)
        {
            return false;
        }
        // The anonymous API call needs no token, so a fresh client is fine here.
        using var scope = scopes.CreateScope();
        var required = await scope.ServiceProvider.GetRequiredService<MeshApiClient>().IsSetupRequiredAsync(ct);
        _done = !required;
        return required;
    }

    public void MarkDone() => _done = true;
}


/// <summary>Users with a temporary password can only reach the change-password page (and sign out).</summary>
public sealed class ForcePasswordChangeFilter : IAsyncActionFilter
{
    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var pending = context.HttpContext.User.FindFirst(MeshClaims.MustChangePassword)?.Value == "true";
        var action = context.RouteData.Values["action"] as string;
        var allowed = Equals(context.RouteData.Values["controller"], "Account") && action is "ChangePassword" or "Logout" or "Login" or "Setup" or "Status" or "Error" or "Certificate" or "CertificateFile";
        if (pending && !allowed)
        {
            context.Result = new RedirectToActionResult("ChangePassword", "Account", null);
            return Task.CompletedTask;
        }
        return next();
    }
}

/// <summary>
/// Turns API failures into something sensible for a browser: an expired or revoked session goes back
/// to sign-in, "forbidden" to the access-denied page, and other errors become a message on the page.
/// </summary>
public sealed class ApiErrorFilter(ITempDataDictionaryFactory tempData) : IAsyncExceptionFilter
{
    public async Task OnExceptionAsync(ExceptionContext context)
    {
        if (context.Exception is not ApiException api)
        {
            return;
        }

        var http = context.HttpContext;
        context.ExceptionHandled = true;
        switch (api.Status)
        {
            case HttpStatusCode.Unauthorized:
                await WebSignIn.SignOutAsync(http);
                tempData.GetTempData(http)["Error"] = "Your session has ended. Please sign in again.";
                context.Result = new RedirectToActionResult("Login", "Account", new { returnUrl = http.Request.Path + http.Request.QueryString });
                break;
            case HttpStatusCode.Forbidden:
                context.Result = new RedirectToActionResult("AccessDenied", "Account", null);
                break;
            default:
                if (HttpMethods.IsGet(http.Request.Method))
                {
                    context.Result = new ViewResult
                    {
                        ViewName = "ApiError",
                        StatusCode = (int)api.Status,
                        ViewData = new ViewDataDictionary<ApiException>(
                            new Microsoft.AspNetCore.Mvc.ModelBinding.EmptyModelMetadataProvider(), context.ModelState) { Model = api },
                    };
                }
                else
                {
                    tempData.GetTempData(http)["Error"] = api.Message;
                    var referer = http.Request.Headers.Referer.ToString();
                    context.Result = Uri.TryCreate(referer, UriKind.Absolute, out var uri) && uri.Host == http.Request.Host.Host
                        ? new LocalRedirectResult(uri.PathAndQuery)
                        : new RedirectToActionResult("Index", "Home", null);
                }
                break;
        }
    }
}
