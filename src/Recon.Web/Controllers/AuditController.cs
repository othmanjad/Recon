using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Recon.Data;
using Recon.Web.Services;

namespace Recon.Web.Controllers;

/// <summary>
/// The audit viewer.
///
/// <para>
/// No role anywhere has <c>UPDATE</c> or <c>DELETE</c> on the <c>aud</c>
/// schema (see <c>db/03-roles.sql</c>), so this screen is read-only by grant
/// and not merely by omission — the application it records cannot rewrite the
/// record.
/// </para>
///
/// <para>
/// Exports appear here too, which is review item D3: the log captured
/// configuration changes but not reads of sensitive exports, and regulators
/// ask who downloaded which report.
/// </para>
/// </summary>
[Authorize]
public sealed class AuditController(
    PortalQueries queries,
    Microsoft.Data.SqlClient.SqlConnection connection,
    AccessService access) : Controller
{
    public async Task<IActionResult> Index(string? entityType, string? performedBy, int take = 200)
    {
        ViewData["Title"] = "Audit log";

        var grants = await access.GrantsAsync(User).ConfigureAwait(false);

        // The log is platform-wide: an entry names an entity id, not a
        // counterparty, so it cannot be filtered by grant without joining
        // every entity type it can reference. Access to it is therefore
        // all-or-nothing, and the bar is Configure somewhere.
        if (!grants.Values.Any(l => l >= AccessLevel.Configure))
        {
            ViewData["Rows"] = new List<Models.AuditRow>();
            ViewData["Denied"] = true;
            ViewData["Types"] = new List<string>();
            ViewData["Users"] = new List<string>();
            ViewData["Take"] = take;
            return View();
        }

        ViewData["Denied"] = false;
        ViewData["Rows"] = await queries
            .AuditAsync(entityType, performedBy, Math.Clamp(take, 20, 2000)).ConfigureAwait(false);

        ViewData["Types"] = await Db.QueryAsync(
            connection,
            "SELECT DISTINCT EntityType FROM aud.AuditLog ORDER BY EntityType;",
            r => r.GetString(0)).ConfigureAwait(false);

        ViewData["Users"] = await Db.QueryAsync(
            connection,
            "SELECT DISTINCT PerformedBy FROM aud.AuditLog ORDER BY PerformedBy;",
            r => r.GetString(0)).ConfigureAwait(false);

        ViewData["EntityType"] = entityType;
        ViewData["PerformedBy"] = performedBy;
        ViewData["Take"] = take;

        return View();
    }
}
