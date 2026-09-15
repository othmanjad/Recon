using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Recon.Data;
using Recon.Web.Models;
using Recon.Web.Services;

namespace Recon.Web.Controllers;

/// <summary>
/// The exception workspace: filter, assign, comment, close with a reason,
/// and see aging (design §13).
/// </summary>
[Authorize]
public sealed class ExceptionsController(
    PortalQueries queries,
    Microsoft.Data.SqlClient.SqlConnection connection,
    AccessService access,
    AuditService audit) : Controller
{
    public async Task<IActionResult> Index(
        string? code, string? status, int? definitionId,
        int? minAge, int? maxAge, string? search)
    {
        ViewData["Title"] = "Exceptions";

        var filter = new ExceptionFilter
        {
            ExceptionCode = code,
            Status = status,
            DefinitionId = definitionId,
            MinAgeDays = minAge,
            MaxAgeDays = maxAge,
            Search = search,
        };

        var model = await queries.ExceptionsAsync(User, filter).ConfigureAwait(false);
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Assign(long id, string? assignTo)
    {
        var counterpartyId = await CounterpartyOfAsync(id).ConfigureAwait(false);
        await access.RequireAsync(User, counterpartyId, AccessLevel.Operate).ConfigureAwait(false);

        var before = await SnapshotAsync(id).ConfigureAwait(false);

        await Db.ExecuteAsync(
            connection,
            """
            UPDATE ops.ReconException
            SET AssignedTo = @to,
                Status = CASE WHEN Status = 'Open' AND @to IS NOT NULL THEN 'InProgress' ELSE Status END
            WHERE ExceptionId = @id;
            """,
            c => c.With("@id", id).With("@to", string.IsNullOrWhiteSpace(assignTo) ? null : assignTo.Trim()))
            .ConfigureAwait(false);

        await audit.RecordAsync(
            User, "ReconException", id, AuditAction.Update, before,
            after: new { AssignedTo = assignTo },
            notes: "Assignment changed").ConfigureAwait(false);

        TempData["Ok"] = string.IsNullOrWhiteSpace(assignTo)
            ? $"Exception {id} unassigned."
            : $"Exception {id} assigned to {assignTo}.";

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Closes an exception with a reason.
    ///
    /// <para>
    /// The reason is required. An exception closed without one is
    /// indistinguishable from one that was never investigated, and the open
    /// items list is only trustworthy if every closure says why.
    /// </para>
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Close(
        long id, string resolutionCode, string? note, string status = "Resolved")
    {
        if (string.IsNullOrWhiteSpace(resolutionCode))
        {
            TempData["Error"] =
                "A resolution code is required. An exception closed without a reason is " +
                "indistinguishable from one nobody looked at.";

            return RedirectToAction(nameof(Index));
        }

        var counterpartyId = await CounterpartyOfAsync(id).ConfigureAwait(false);
        await access.RequireAsync(User, counterpartyId, AccessLevel.Operate).ConfigureAwait(false);

        var before = await SnapshotAsync(id).ConfigureAwait(false);

        await Db.ExecuteAsync(
            connection,
            """
            UPDATE ops.ReconException
            SET Status = @status,
                ResolutionCode = @code,
                ResolutionNote = @note,
                ClosedAt = SYSDATETIME(),
                ClosedBy = @by
            WHERE ExceptionId = @id;
            """,
            c => c.With("@id", id)
                  .With("@status", status)
                  .With("@code", resolutionCode.Trim())
                  .With("@note", string.IsNullOrWhiteSpace(note) ? null : note.Trim())
                  .With("@by", access.UserName(User)))
            .ConfigureAwait(false);

        await audit.RecordAsync(
            User, "ReconException", id, AuditAction.Close, before,
            after: new { Status = status, ResolutionCode = resolutionCode, Note = note })
            .ConfigureAwait(false);

        TempData["Ok"] = $"Exception {id} closed as {status} ({resolutionCode}).";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reopen(long id, string? note)
    {
        var counterpartyId = await CounterpartyOfAsync(id).ConfigureAwait(false);
        await access.RequireAsync(User, counterpartyId, AccessLevel.Operate).ConfigureAwait(false);

        var before = await SnapshotAsync(id).ConfigureAwait(false);

        // ClosedAt is cleared, or the CHECK constraint that requires a closed
        // exception to carry one would be satisfied by a reopened row.
        await Db.ExecuteAsync(
            connection,
            """
            UPDATE ops.ReconException
            SET Status = 'Open', ClosedAt = NULL, ClosedBy = NULL,
                ResolutionCode = NULL, ResolutionNote = @note
            WHERE ExceptionId = @id;
            """,
            c => c.With("@id", id)
                  .With("@note", string.IsNullOrWhiteSpace(note) ? null : note.Trim()))
            .ConfigureAwait(false);

        await audit.RecordAsync(User, "ReconException", id, AuditAction.Reopen, before,
            after: new { Status = "Open", Note = note }).ConfigureAwait(false);

        TempData["Ok"] = $"Exception {id} reopened.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<int> CounterpartyOfAsync(long exceptionId)
    {
        var id = await Db.ScalarAsync<int?>(
            connection,
            """
            SELECT d.CounterpartyId
            FROM ops.ReconException AS e
            JOIN cfg.ReconciliationDefinition AS d ON d.DefinitionId = e.DefinitionId
            WHERE e.ExceptionId = @id;
            """,
            c => c.With("@id", exceptionId)).ConfigureAwait(false);

        return id ?? throw new InvalidOperationException($"exception {exceptionId} was not found");
    }

    /// <summary>
    /// The row before a change, for the audit entry. Writing "who changed
    /// this" without "from what" is half a record.
    /// </summary>
    private Task<List<object>> SnapshotAsync(long exceptionId) =>
        Db.QueryAsync<object>(
            connection,
            """
            SELECT Status, AssignedTo, ResolutionCode, ResolutionNote
            FROM ops.ReconException WHERE ExceptionId = @id;
            """,
            r => new
            {
                Status = r.GetString(0),
                AssignedTo = r.IsDBNull(1) ? null : r.GetString(1),
                ResolutionCode = r.IsDBNull(2) ? null : r.GetString(2),
                ResolutionNote = r.IsDBNull(3) ? null : r.GetString(3),
            },
            c => c.With("@id", exceptionId));
}
