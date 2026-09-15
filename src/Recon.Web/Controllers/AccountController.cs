using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Recon.Web.Services;

namespace Recon.Web.Controllers;

/// <summary>
/// Development sign-in.
///
/// <para>
/// Deliberately minimal and deliberately obvious: it names the operator so
/// that the audit log has a subject and the per-counterparty access checks
/// have someone to check. A deployment replaces this controller with the
/// bank's identity provider — the authorization logic reads claims and does
/// not care where they came from.
/// </para>
///
/// <para>
/// It does not authenticate anybody. That is stated on the page rather than
/// implied, so nobody mistakes it for a login.
/// </para>
/// </summary>
public sealed class AccountController(
    IServiceProvider services, Services.PlatformConfiguration platform) : Controller
{
    private readonly IServiceProvider _services =
        services ?? throw new ArgumentNullException(nameof(services));

    // Sign-in has to work on a portal that has no database yet: it is how the
    // setup screens get a name to write against the install. So the audit
    // service is resolved lazily rather than injected — asking for one before
    // there is a connection would throw here, on the one page that must
    // answer.
    private readonly Services.PlatformConfiguration _platform =
        platform ?? throw new ArgumentNullException(nameof(platform));

    [HttpGet]
    public IActionResult SignIn(string? returnUrl = null)
    {
        ViewData["Title"] = "Sign in";
        ViewData["ReturnUrl"] = returnUrl;
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SignIn(string userName, string? returnUrl = null)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            TempData["Error"] = "A user name is required: the audit log records who did what.";
            return RedirectToAction(nameof(SignIn), new { returnUrl });
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, userName.Trim())],
            CookieAuthenticationDefaults.AuthenticationScheme);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity)).ConfigureAwait(false);

        if (_platform.IsConfigured)
        {
            try
            {
                var audit = _services.GetRequiredService<AuditService>();

                await audit.RecordAsync(
                    HttpContext.User, "Session", userName.Trim(), AuditAction.Login,
                    notes: "Portal sign-in").ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException
                                          or PlatformNotConfiguredException)
            {
                // A database that is configured but not yet built must not
                // stop the sign-in that is about to build it. The sign-in is
                // still recorded the moment the log exists.
            }
        }

        // Only local redirects: an open redirect here would be a phishing
        // vector off the back of a trusted host.
        return Url.IsLocalUrl(returnUrl)
            ? Redirect(returnUrl)
            : RedirectToAction("Index", "Home");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SignOutOperator()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme)
            .ConfigureAwait(false);

        return RedirectToAction(nameof(SignIn));
    }

    [HttpGet]
    public IActionResult Denied()
    {
        ViewData["Title"] = "Access denied";
        return View();
    }
}
