using System.Net;
using MeshCore.Host.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace MeshCore.Host.Web.Controllers;

/// <summary>First-run setup, sign-in, sign-out and password changes.</summary>
public sealed class AccountController(MeshApiClient api, SetupGate gate) : WebController
{
    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> Setup(CancellationToken ct) =>
        await gate.IsSetupRequiredAsync(ct) ? View(new SetupViewModel()) : RedirectToAction(nameof(Login));

    [HttpPost, AllowAnonymous, EnableRateLimiting(AuthPolicies.AuthRateLimit)]
    public async Task<IActionResult> Setup(SetupViewModel model, CancellationToken ct)
    {
        if (!await gate.IsSetupRequiredAsync(ct))
        {
            return RedirectToAction(nameof(Login));
        }
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        try
        {
            var token = await api.SetupAsync(model.Username, model.Password, ct);
            gate.MarkDone();
            await WebSignIn.SignInAsync(HttpContext, token, persistent: false);
            Flash($"Welcome, {token.User.Username}! You're the Admin. Add other users under Settings → Users.");
            return RedirectToAction("Index", "Home");
        }
        catch (ApiException ex) when (ex.Status is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
        {
            ModelState.AddModelError("", ex.Message);
            return View(model);
        }
    }

    [HttpGet, AllowAnonymous]
    public IActionResult Login(string? returnUrl) =>
        User.Identity?.IsAuthenticated == true ? LocalRedirect(SafeReturn(returnUrl)) : View(new LoginViewModel { ReturnUrl = returnUrl });

    [HttpPost, AllowAnonymous, EnableRateLimiting(AuthPolicies.AuthRateLimit)]
    public async Task<IActionResult> Login(LoginViewModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        try
        {
            var token = await api.SignInAsync(model.Username, model.Password, ct);
            await WebSignIn.SignInAsync(HttpContext, token, model.RememberMe);
            return token.User.MustChangePassword
                ? RedirectToAction(nameof(ChangePassword))
                : LocalRedirect(SafeReturn(model.ReturnUrl));
        }
        catch (ApiException ex) when (ex.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Locked or HttpStatusCode.TooManyRequests or HttpStatusCode.BadRequest)
        {
            ModelState.AddModelError("", ex.Status switch
            {
                HttpStatusCode.Locked => ex.Message,
                HttpStatusCode.TooManyRequests => "Too many attempts. Wait a minute and try again.",
                _ => "Wrong username or password.",
            });
            model.Password = "";
            return View(model);
        }
    }

    [HttpPost]
    public async Task<IActionResult> Logout()
    {
        await WebSignIn.SignOutAsync(HttpContext);
        return RedirectToAction(nameof(Login));
    }

    [HttpGet]
    public IActionResult ChangePassword() =>
        View(new ChangePasswordViewModel { Forced = User.FindFirst(MeshClaims.MustChangePassword)?.Value == "true" });

    [HttpPost, EnableRateLimiting(AuthPolicies.AuthRateLimit)]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        try
        {
            var token = await api.ChangePasswordAsync(model.CurrentPassword, model.NewPassword, ct);
            await WebSignIn.RefreshAsync(HttpContext, token);
            Flash("Your password has been changed.");
            return RedirectToAction("Index", "Home");
        }
        catch (ApiException ex) when (ex.Status == HttpStatusCode.BadRequest)
        {
            ModelState.AddModelError("", ex.Message);
            return View(model);
        }
    }

    [HttpGet, AllowAnonymous]
    public IActionResult AccessDenied() => View();

    /// <summary>How to make phones trust this site's HTTPS certificate. Reachable over plain HTTP and before setup.</summary>
    [HttpGet, AllowAnonymous]
    public IActionResult Certificate([FromServices] Hosting.HttpsState https) => View(https);

    /// <summary>The local certificate authority (public part only), for installing on phones and other computers.</summary>
    [HttpGet, AllowAnonymous]
    public IActionResult CertificateFile([FromServices] Hosting.HttpsState https) =>
        https.Certificates?.AuthorityDer is { } der ? File(der, "application/x-x509-ca-cert", "meshcore-ca.crt") : NotFound();

    /// <summary>Friendly page for 404 and other status codes (re-executed by the status-code middleware).</summary>
    [AllowAnonymous, IgnoreAntiforgeryToken]
    public IActionResult Status(int id) => View(id);

    [AllowAnonymous, IgnoreAntiforgeryToken]
    public IActionResult Error() => View("Status", 500);

    private string SafeReturn(string? returnUrl) => Url.IsLocalUrl(returnUrl) ? returnUrl! : Url.Action("Index", "Home")!;
}
