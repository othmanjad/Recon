using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Recon.Web.Services;

namespace Recon.Web.Controllers;

[Authorize]
public sealed class HomeController(PortalQueries queries) : Controller
{
    private readonly PortalQueries _queries = queries ?? throw new ArgumentNullException(nameof(queries));

    /// <summary>
    /// The run dashboard: status, duration, and the match distribution per
    /// pass — which the design calls a data-quality early-warning system
    /// rather than a progress bar.
    /// </summary>
    public async Task<IActionResult> Index(int? definitionId, string? status)
    {
        ViewData["Title"] = "Run dashboard";

        var model = await _queries.DashboardAsync(User, definitionId, status).ConfigureAwait(false);
        return View(model);
    }

    [AllowAnonymous]
    public IActionResult Error()
    {
        ViewData["Title"] = "Something went wrong";
        return View();
    }
}
