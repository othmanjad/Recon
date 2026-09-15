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
public sealed class AccountController(AuditService audit) : Controller
{
    private readonly AuditService _audit = audit ?? throw new ArgumentNullException(nameof(audit));

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

        await _audit.RecordAsync(
            HttpContext.User, "Session", userName.Trim(), AuditAction.Login,
            notes: "Portal sign-in").ConfigureAwait(false);

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
