using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Recon.Data;
using Recon.Domain.Configuration;
using Recon.Web.Services;

namespace Recon.Web.Controllers;

// CA1848 asks for LoggerMessage delegates. An upload logs one line per file;
// the allocation the rule guards against does not arise, and the message
// belongs beside the hash it reports.
#pragma warning disable CA1848

[Authorize]
public sealed class RunsController(
    PortalQueries queries,
    ConfigurationRepository config,
    Microsoft.Data.SqlClient.SqlConnection connection,
    AccessService access,
    AuditService audit,
    SessionRunner sessions,
    ILogger<RunsController> logger) : Controller
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
    /// Triggers a run over data that is already staged, or that the
    /// definition's own acquisition will stage.
    ///
    /// <para>
    /// The application lock is what makes this safe alongside the scheduler
    /// (review item B2): a second attempt for the same definition and business
    /// date is refused and recorded as <c>Rejected</c> rather than staging the
    /// same data twice. The whole sequence lives in
    /// <see cref="SessionRunner"/>, which the scheduler and the CLI also use.
    /// </para>
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Trigger(
        int definitionId,
        DateOnly businessDate,
        string? sessionRef,
        string runType = "Manual",
        CancellationToken cancellationToken = default)
    {
        var definition = await config.LoadDefinitionAsync(definitionId, cancellationToken)
            .ConfigureAwait(false);

        await access.RequireAsync(User, definition.CounterpartyId, AccessLevel.Operate, cancellationToken)
            .ConfigureAwait(false);

        var outcome = await sessions.RunAsync(
            User, definitionId, businessDate, sessionRef,
            Db.ParseEnum<RunType>(runType), access.UserName(User),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return Land(outcome, definition.Code);
    }

    /// <summary>
    /// The whole pipeline from the browser: two files in, a reconciled run
    /// out.
    ///
    /// <para>
    /// This is what "all control from the portal" means in the one place it
    /// matters most. Before it, an operator holding two files from a partner
    /// needed somebody with a shell — the manual trigger assumed the rows were
    /// already staged, and the files were handed to a command line.
    /// </para>
    ///
    /// <para>
    /// The uploads are written to the storage root with their SHA-256 and
    /// parsed from there, never held in memory: at two million rows a day a
    /// file that fits in a request buffer is not the file this platform is for.
    /// The original stays on disk as the byte-identical record the design
    /// requires, and the database keeps the path and the hash.
    /// </para>
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestFormLimits(MultipartBodyLengthLimit = 4L * 1024 * 1024 * 1024)]
    [RequestSizeLimit(4L * 1024 * 1024 * 1024)]
    public async Task<IActionResult> Upload(
        int definitionId,
        DateOnly businessDate,
        string? sessionRef,
        string runType,
        IFormFile? leftFile,
        IFormFile? rightFile,
        CancellationToken cancellationToken)
    {
        var definition = await config.LoadDefinitionAsync(definitionId, cancellationToken)
            .ConfigureAwait(false);

        await access.RequireAsync(User, definition.CounterpartyId, AccessLevel.Operate, cancellationToken)
            .ConfigureAwait(false);

        if (leftFile is null && rightFile is null)
        {
            TempData["Error"] =
                "Choose at least one file. A run with neither is the plain trigger, which reads " +
                "whatever is already staged.";

            return RedirectToAction(nameof(Index));
        }

        // Kestrel's default request limit is 30MB, and a session file is far
        // larger than that. The attributes above raise it for this action
        // only; the feature is disabled outright so the framework does not
        // buffer the body while deciding.
        var limits = HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limits is { IsReadOnly: false })
        {
            limits.MaxRequestBodySize = null;
        }

        var directory = sessions.StorageFor(businessDate, definition.Code);
        StoredFile? left = null;
        StoredFile? right = null;

        try
        {
            if (leftFile is not null)
            {
                left = await Save(leftFile, definition.Left.Code).ConfigureAwait(false);
            }

            if (rightFile is not null)
            {
                right = await Save(rightFile, definition.Right.Code).ConfigureAwait(false);
            }
        }
        catch (IOException ex)
        {
            TempData["Error"] = "The upload could not be stored: " + ex.Message;
            return RedirectToAction(nameof(Index));
        }

        var outcome = await sessions.RunAsync(
            User, definitionId, businessDate, sessionRef,
            Db.ParseEnum<RunType>(string.IsNullOrWhiteSpace(runType) ? "Manual" : runType),
            access.UserName(User), left, right,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return Land(outcome, definition.Code);

        async Task<StoredFile> Save(IFormFile upload, string datasetCode)
        {
            await using var stream = upload.OpenReadStream();

            // Prefixed with the dataset so two files with the same name from
            // the two sides cannot overwrite each other.
            var stored = await SessionRunner.SaveAsync(
                stream, directory, datasetCode + "_" + Path.GetFileName(upload.FileName),
                cancellationToken).ConfigureAwait(false);

            await audit.RecordAsync(
                User, "SourceFile", stored.Sha256[..16], AuditAction.Create,
                after: new { stored.Path, stored.Sha256, stored.Bytes, datasetCode },
                notes: $"Uploaded {upload.FileName} for {datasetCode}",
                cancellationToken: cancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                "{User} uploaded {Bytes} bytes for {Dataset}; sha256 {Hash}.",
                access.UserName(User), stored.Bytes, datasetCode, stored.Sha256);

            return stored;
        }
    }

    /// <summary>
    /// Turns a session outcome into the page the operator should be looking
    /// at: the run itself when there is one, the list when the attempt was
    /// refused before a run existed.
    /// </summary>
    private RedirectToActionResult Land(SessionOutcome outcome, string definitionCode)
    {
        if (outcome.Refusal is { } refusal)
        {
            TempData["Error"] = refusal;
            return RedirectToAction(nameof(Index));
        }

        var staged = outcome.Staged.Count == 0
            ? string.Empty
            : " " + string.Join(", ", outcome.Staged.Select(s =>
                s.AlreadyStaged
                    ? $"{s.Dataset} was already staged"
                    : $"{s.Dataset}: {s.RowsWritten:N0} rows staged" +
                      (s.Errors.Count > 0 ? $", {s.Errors.Count:N0} rejected" : string.Empty)))
            + ".";

        // A file that was fetched rather than uploaded, and a file whose
        // content had already been received, are both things the operator
        // should read off the same message rather than go looking for. An
        // upload needs no line: the operator just chose it.
        var fetched = outcome.Acquired
            .Where(a => a.State is Recon.Engine.Providers.AcquisitionState.Acquired
                            or Recon.Engine.Providers.AcquisitionState.Duplicate)
            .Select(a => a.Message)
            .ToList();

        var acquired = fetched.Count == 0 ? string.Empty : " " + string.Join(" ", fetched);

        var result = outcome.Outcome!;

        TempData[result.Status == RunStatus.Completed ? "Ok" : "Error"] =
            result.Status == RunStatus.Completed
                ? $"Run {outcome.RunId} of {definitionCode} completed:{acquired}{staged} " +
                  $"{result.Counts.Matched:N0} matched, {result.Counts.Unmatched:N0} unmatched, " +
                  $"{result.Counts.Ambiguous:N0} ambiguous."
                : $"Run {outcome.RunId} of {definitionCode} failed:{acquired}{staged} {result.Error}";

        return RedirectToAction(nameof(Detail), new { id = outcome.RunId });
    }
}
#pragma warning restore CA1848
