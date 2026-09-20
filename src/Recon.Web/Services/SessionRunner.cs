using Microsoft.Data.SqlClient;
using Recon.Data;
using Recon.Domain.Configuration;
using Recon.Engine;
using Recon.Engine.Providers;
using Recon.Engine.Staging;

namespace Recon.Web.Services;

/// <summary>
/// One session, end to end: take the lock, create the run, stage each side,
/// execute the passes, release the lock.
///
/// <para>
/// The portal needed this because "run a reconciliation" was only ever
/// available to somebody with a shell: the manual trigger assumed the rows
/// were already staged, and the two files were handed to the CLI. An operator
/// with two files from a partner and a browser could not start a run, which
/// makes "all control from the portal" untrue in the one place it matters
/// most.
/// </para>
///
/// <para>
/// The staging itself is <see cref="FileStager"/>, the same class the CLI and
/// the scheduler use. This orchestrates; it does not parse.
/// </para>
/// </summary>
public sealed class SessionRunner(
    SqlConnection connection,
    ConfigurationRepository config,
    RunRepository runs,
    PlatformConfiguration platform,
    AuditService audit)
{
    private readonly SqlConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    private readonly ConfigurationRepository _config =
        config ?? throw new ArgumentNullException(nameof(config));

    private readonly RunRepository _runs = runs ?? throw new ArgumentNullException(nameof(runs));

    private readonly PlatformConfiguration _platform =
        platform ?? throw new ArgumentNullException(nameof(platform));

    private readonly AuditService _audit = audit ?? throw new ArgumentNullException(nameof(audit));

    /// <summary>
    /// Where an uploaded file is kept. The original file is stored on the
    /// filesystem and never as a database blob: at 2M rows a day the blob
    /// path leads straight to an unmanageable database, and the design is
    /// explicit about it (§13). What the database keeps is the path, the hash
    /// and the metadata.
    /// </summary>
    public string StorageFor(DateOnly businessDate, string definitionCode)
    {
        var directory = Path.Combine(
            _platform.StorageRoot,
            definitionCode,
            businessDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));

        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// Saves an uploaded file under the storage root and returns its path and
    /// SHA-256.
    ///
    /// <para>
    /// The hash is computed while the bytes are written rather than by reading
    /// the file back: at 2M rows the file is large enough that a second pass
    /// is a real cost, and the point of the hash is to identify the bytes that
    /// were actually stored.
    /// </para>
    /// </summary>
    public static async Task<StoredFile> SaveAsync(
        Stream source, string directory, string fileName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        // The name comes from a browser upload, so only its file part is used:
        // a name carrying a path would otherwise choose where to write.
        var safe = Path.GetFileName(fileName);

        if (string.IsNullOrWhiteSpace(safe))
        {
            safe = "upload.dat";
        }

        var path = Path.Combine(directory, safe);

        using var hash = System.Security.Cryptography.SHA256.Create();
        await using (var file = File.Create(path))
        await using (var crypto = new System.Security.Cryptography.CryptoStream(
            file, hash, System.Security.Cryptography.CryptoStreamMode.Write, leaveOpen: true))
        {
            await source.CopyToAsync(crypto, cancellationToken).ConfigureAwait(false);
            await crypto.FlushFinalBlockAsync(cancellationToken).ConfigureAwait(false);
        }

        return new StoredFile(
            path,
            Convert.ToHexString(hash.Hash!).ToLowerInvariant(),
            new FileInfo(path).Length);
    }

    /// <summary>
    /// Runs one session. The files are optional in three different ways, and
    /// the difference matters:
    ///
    /// <list type="bullet">
    /// <item>an upload supplies them, and they are recorded and staged;</item>
    /// <item>a Rematch supplies neither and reads the source run's rows;</item>
    /// <item>a plain trigger or a scheduled run supplies neither, and each
    /// side's own acquisition is asked for the day's file.</item>
    /// </list>
    ///
    /// <para>
    /// Acquisition happens after the lock and before the run row exists, so a
    /// day whose file never arrived is recorded as <c>Rejected</c> with the
    /// reason rather than as a run that reconciled nothing. An empty
    /// reconciliation and a missing file look identical on a dashboard and are
    /// not the same event.
    /// </para>
    /// </summary>
    public async Task<SessionOutcome> RunAsync(
        System.Security.Claims.ClaimsPrincipal? user,
        int definitionId,
        DateOnly businessDate,
        string? sessionRef,
        RunType runType,
        string triggeredBy,
        StoredFile? leftFile = null,
        StoredFile? rightFile = null,
        long? sourceRunId = null,
        CancellationToken cancellationToken = default)
    {
        var definition = await _config.LoadDefinitionAsync(definitionId, cancellationToken)
            .ConfigureAwait(false);

        var problems = definition.ActivationProblems();

        // A sandbox run against a half-built definition is the point of
        // sandbox runs; any other kind is not.
        if (problems.Count > 0 && runType != RunType.Sandbox)
        {
            return SessionOutcome.Refused(
                "This definition cannot be activated: " + string.Join("; ", problems));
        }

        // B2: the scheduler and a manual trigger must not execute the same
        // definition and date at once. The second attempt is recorded as
        // Rejected rather than left to race.
        if (!await _runs.TryAcquireRunLockAsync(definitionId, businessDate, sessionRef,
                cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            await _runs.RecordRejectedAsync(
                definitionId, definition.Version, businessDate, sessionRef, runType,
                triggeredBy, "another run holds the lock", cancellationToken).ConfigureAwait(false);

            return SessionOutcome.Refused(
                "Another run holds the lock for this definition and business date. " +
                "The attempt is recorded as Rejected.");
        }

        try
        {
            var files = new SourceFileRepository(_connection);
            var acquired = new List<AcquisitionOutcome>();
            var inputs = new Dictionary<Side, (string Path, long SourceFileId)>();

            foreach (var (side, upload) in new[]
                     {
                         (Side.Left, leftFile),
                         (Side.Right, rightFile),
                     })
            {
                var dataset = definition.DatasetFor(side);

                if (upload is not null)
                {
                    // An upload is an arrival too: the same ops.SourceFile row
                    // an acquired file gets, so the audit trail does not
                    // depend on how the bytes got here.
                    var record = await files.RecordAsync(
                        dataset.DatasetId, Path.GetFileName(upload.Path), upload.Path,
                        upload.Sha256, upload.Bytes, businessDate, sessionRef, cancellationToken)
                        .ConfigureAwait(false);

                    inputs[side] = (upload.Path, record.SourceFileId);

                    acquired.Add(record.IsDuplicate
                        ? AcquisitionOutcome.Duplicate(
                            dataset.Code, upload.Path, upload.Sha256, record.SourceFileId, record.Status)
                        : AcquisitionOutcome.Uploaded(
                            dataset.Code, upload.Path, upload.Sha256, record.SourceFileId, upload.Bytes));

                    continue;
                }

                // A Rematch reads the source run's staged rows; asking a
                // folder for a file it already consumed would be wrong.
                if (sourceRunId is not null)
                {
                    continue;
                }

                AcquisitionOutcome outcome;

                try
                {
                    outcome = await new FolderAcquisition(_connection)
                        .AcquireAsync(dataset, businessDate, sessionRef,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException
                                              or IOException or UnauthorizedAccessException)
                {
                    // A method with no provider, a folder that is not there,
                    // two files that both match: all of them mean the day's
                    // file is not established, and none of them is a run.
                    await _runs.RecordRejectedAsync(
                        definitionId, definition.Version, businessDate, sessionRef, runType,
                        triggeredBy, ex.Message, cancellationToken).ConfigureAwait(false);

                    return SessionOutcome.Refused(ex.Message);
                }

                acquired.Add(outcome);

                if (outcome.State == AcquisitionState.NotFound)
                {
                    await _runs.RecordRejectedAsync(
                        definitionId, definition.Version, businessDate, sessionRef, runType,
                        triggeredBy, outcome.Message, cancellationToken).ConfigureAwait(false);

                    return SessionOutcome.Refused(
                        outcome.Message +
                        " The attempt is recorded as Rejected: a file that never arrived is not a " +
                        "run that matched nothing.");
                }

                if (outcome.Path is not null && outcome.SourceFileId is { } id)
                {
                    inputs[side] = (outcome.Path, id);
                }
            }

            var run = await _runs.CreateRunAsync(
                definition, businessDate, sessionRef, runType, triggeredBy, sourceRunId,
                cancellationToken).ConfigureAwait(false);

            await _audit.RecordAsync(
                user, "ReconRun", run.RunId, AuditAction.Execute,
                after: new { definitionId, businessDate, sessionRef, runType = runType.ToString() },
                notes: $"{runType} run of {definition.Code} from the portal",
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var staged = new List<StageOutcome>();
            var stager = new FileStager(_connection);

            // Rows are staged UNDER a run id, so the load follows the run's
            // creation rather than preceding it.
            foreach (var side in new[] { Side.Left, Side.Right })
            {
                if (!inputs.TryGetValue(side, out var input))
                {
                    continue;
                }

                StageOutcome outcome;

                try
                {
                    outcome = await stager.StageAsync(
                        definition, side, run.RunId, businessDate, input.Path, _runs,
                        input.SourceFileId, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // The file arrived and could not be read. Recording that
                    // against the file is the difference between "no statement
                    // yesterday" and "yesterday's statement was unreadable".
                    await files.RecordParsedAsync(
                        input.SourceFileId, businessDate, 0, 0, failed: true,
                        CancellationToken.None).ConfigureAwait(false);

                    throw;
                }

                await files.RecordParsedAsync(
                    input.SourceFileId, businessDate, outcome.RowsWritten, outcome.Errors.Count,
                    failed: false, cancellationToken).ConfigureAwait(false);

                staged.Add(outcome);
            }

            var result = await new ReconciliationRunner(_connection, _runs)
                .ExecuteAsync(definition, run, cancellationToken).ConfigureAwait(false);

            return new SessionOutcome
            {
                RunId = run.RunId,
                Staged = staged,
                Acquired = acquired,
                Outcome = result,
                Refusal = null,
            };
        }
        catch (InvalidOperationException ex)
        {
            // A parse-error ceiling, a missing file format, an unknown
            // currency: configuration problems the operator can act on, and
            // the run already carries the failed step.
            return SessionOutcome.Refused(ex.Message);
        }
        catch (SqlException ex)
        {
            // The server refused a statement. ReconciliationRunner has already
            // marked the run and the step failed with this same message, so
            // the run page tells the whole story — but only if the operator is
            // sent there. Letting it out of here produced an unhandled
            // exception page with a stack trace, which is the developer's view
            // of a problem the operator is the one holding.
            return SessionOutcome.Refused(
                "The run failed in the database and is recorded as Failed: " + ex.Message);
        }
        finally
        {
            await _runs.ReleaseRunLockAsync(definitionId, businessDate, sessionRef, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}

public sealed record StoredFile(string Path, string Sha256, long Bytes);

public sealed record SessionOutcome
{
    public long? RunId { get; init; }
    public IReadOnlyList<StageOutcome> Staged { get; init; } = [];

    /// <summary>
    /// What each side's file did: acquired from its folder, uploaded, already
    /// received, or nothing to fetch. Empty for a Rematch, which reads the
    /// source run's rows.
    /// </summary>
    public IReadOnlyList<AcquisitionOutcome> Acquired { get; init; } = [];
    public RunOutcome? Outcome { get; init; }

    /// <summary>
    /// Why the session did not run at all. Distinct from a failed run: a
    /// refusal means nothing was executed, and the run either does not exist
    /// or exists as Rejected.
    /// </summary>
    public string? Refusal { get; init; }

    public bool Ran => Refusal is null && Outcome is not null;

    public static SessionOutcome Refused(string reason) => new() { Refusal = reason };
}
