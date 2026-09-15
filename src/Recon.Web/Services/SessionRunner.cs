using Microsoft.Data.SqlClient;
using Recon.Data;
using Recon.Domain.Configuration;
using Recon.Engine;
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
    /// Runs one session. <paramref name="leftFile"/> and
    /// <paramref name="rightFile"/> are optional: a Rematch supplies neither
    /// and reads the source run's rows, and a run whose acquisition already
    /// staged its data supplies neither either.
    /// </summary>
    public async Task<SessionOutcome> RunAsync(
        System.Security.Claims.ClaimsPrincipal? user,
        int definitionId,
        DateOnly businessDate,
        string? sessionRef,
        RunType runType,
        string triggeredBy,
        string? leftFile = null,
        string? rightFile = null,
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
            if (leftFile is not null)
            {
                staged.Add(await stager.StageAsync(
                    definition, Side.Left, run.RunId, businessDate, leftFile, _runs, cancellationToken)
                    .ConfigureAwait(false));
            }

            if (rightFile is not null)
            {
                staged.Add(await stager.StageAsync(
                    definition, Side.Right, run.RunId, businessDate, rightFile, _runs, cancellationToken)
                    .ConfigureAwait(false));
            }

            var outcome = await new ReconciliationRunner(_connection, _runs)
                .ExecuteAsync(definition, run, cancellationToken).ConfigureAwait(false);

            return new SessionOutcome
            {
                RunId = run.RunId,
                Staged = staged,
                Outcome = outcome,
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
