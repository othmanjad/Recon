using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Recon.Data;
using Recon.Domain.Configuration;
using Recon.Engine;
using Recon.Web.Services;

namespace Recon.Web.Controllers;

[Authorize]
public sealed class RunsController(
    PortalQueries queries,
    ConfigurationRepository config,
    RunRepository runs,
    Microsoft.Data.SqlClient.SqlConnection connection,
    AccessService access,
    AuditService audit) : Controller
{
    public async Task<IActionResult> Index(int? definitionId, string? status)
    {
        ViewData["Title"] = "Runs";

        var grants = await access.GrantsAsync(User).ConfigureAwait(false);
        var model = await queries.DashboardAsync(User, definitionId, status).ConfigureAwait(false);

        ViewData["CanOperate"] = grants.Values.Any(l => l >= AccessLevel.Operate);
        return View(model);
    }

    /// <summary>
    /// One run in full: every stage with its timing and the SQL it ran, the
    /// control totals, the durable aggregates and any parse errors.
    ///
    /// The persisted SQL is the point (§9.4): "what rule matched this in June"
    /// is answerable from the run itself rather than from configuration that
    /// has since been edited.
    /// </summary>
    public async Task<IActionResult> Detail(long id)
    {
        var model = await queries.RunDetailAsync(User, id).ConfigureAwait(false);

        if (model is null)
        {
            return NotFound();
        }

        ViewData["Title"] = $"Run {id}";
        return View(model);
    }

    /// <summary>The generated SQL for one step, as plain text.</summary>
    public async Task<IActionResult> StepSql(long id, long stepId)
    {
        var model = await queries.RunDetailAsync(User, id).ConfigureAwait(false);

        if (model is null || model.Steps.All(s => s.RunStepId != stepId))
        {
            return NotFound();
        }

        var sql = await Db.ScalarAsync<string>(
            connection,
            "SELECT GeneratedSql FROM ops.ReconRunStep WHERE RunStepId = @id;",
            c => c.With("@id", stepId)).ConfigureAwait(false);

        return Content(sql ?? "-- this step recorded no generated SQL", "text/plain");
    }

    /// <summary>
    /// Triggers a run manually.
    ///
    /// <para>
    /// The application lock is what makes this safe alongside the scheduler
    /// (review item B2): a second attempt for the same definition and business
    /// date is refused and recorded as <c>Rejected</c> rather than staging the
    /// same data twice.
    /// </para>
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Trigger(
        int definitionId, DateOnly businessDate, string? sessionRef, string runType = "Manual")
    {
        var definition = await config.LoadDefinitionAsync(definitionId).ConfigureAwait(false);

        await access.RequireAsync(User, definition.CounterpartyId, AccessLevel.Operate)
            .ConfigureAwait(false);

        var problems = definition.ActivationProblems();
        if (problems.Count > 0)
        {
            TempData["Error"] = "This definition cannot be activated: " + string.Join("; ", problems);
            return RedirectToAction(nameof(Index));
        }

        if (!await runs.TryAcquireRunLockAsync(definitionId, businessDate, sessionRef)
                .ConfigureAwait(false))
        {
            await runs.RecordRejectedAsync(
                definitionId, definition.Version, businessDate, sessionRef,
                Db.ParseEnum<RunType>(runType), access.UserName(User),
                "another run holds the lock").ConfigureAwait(false);

            TempData["Error"] =
                "Another run holds the lock for this definition and business date. " +
                "The attempt is recorded as Rejected.";

            return RedirectToAction(nameof(Index));
        }

        try
        {
            var run = await runs.CreateRunAsync(
                definition, businessDate, sessionRef, Db.ParseEnum<RunType>(runType),
                access.UserName(User)).ConfigureAwait(false);

            await audit.RecordAsync(
                User, "ReconRun", run.RunId, AuditAction.Execute,
                after: new { definitionId, businessDate, sessionRef, runType },
                notes: $"Manual trigger of {definition.Code}").ConfigureAwait(false);

            var outcome = await new ReconciliationRunner(connection, runs)
                .ExecuteAsync(definition, run).ConfigureAwait(false);

            TempData[outcome.Status == RunStatus.Completed ? "Ok" : "Error"] =
                outcome.Status == RunStatus.Completed
                    ? $"Run {run.RunId} completed: {outcome.Counts.Matched:N0} matched, " +
                      $"{outcome.Counts.Unmatched:N0} unmatched."
                    : $"Run {run.RunId} failed: {outcome.Error}";

            return RedirectToAction(nameof(Detail), new { id = run.RunId });
        }
        finally
        {
            await runs.ReleaseRunLockAsync(definitionId, businessDate, sessionRef)
                .ConfigureAwait(false);
        }
    }
}
