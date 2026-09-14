using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Recon.Domain.Configuration;

namespace Recon.Data;

/// <summary>
/// Creates and advances runs: the application lock, the checkpoints, the
/// definition snapshot, and supersession.
/// </summary>
public sealed class RunRepository(SqlConnection connection)
{
    private readonly SqlConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    private static readonly JsonSerializerOptions SnapshotOptions = new()
    {
        WriteIndented = false,
        // A snapshot is read by the compiler, not by a person, and it must
        // round-trip exactly. Converters that "helpfully" reshape enums or
        // dates would make the snapshot a different artifact from the config.
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>
    /// Takes the run-level application lock (review item B2).
    ///
    /// <para>
    /// Nothing otherwise prevents the scheduler and a manual trigger executing
    /// the same definition for the same business date at once — both would
    /// stage the same data and both would write results. The second attempt is
    /// refused here and recorded as <see cref="RunStatus.Rejected"/> rather
    /// than left to race.
    /// </para>
    ///
    /// <para>
    /// The lock is session-scoped, so it is held for as long as the connection
    /// lives and released if the process dies — which is the behaviour a
    /// long-running batch needs.
    /// </para>
    /// </summary>
    public async Task<bool> TryAcquireRunLockAsync(
        int definitionId,
        DateOnly businessDate,
        string? sessionRef,
        int timeoutMilliseconds = 0,
        CancellationToken cancellationToken = default)
    {
        var resource = $"recon:def={definitionId}:date={businessDate:yyyy-MM-dd}:session={sessionRef ?? "-"}";

        using var command = Db.Command(_connection, "sp_getapplock");
        command.CommandType = CommandType.StoredProcedure;
        command.Parameters.AddWithValue("@Resource", resource);
        command.Parameters.AddWithValue("@LockMode", "Exclusive");
        command.Parameters.AddWithValue("@LockOwner", "Session");
        command.Parameters.AddWithValue("@LockTimeout", timeoutMilliseconds);

        var result = command.Parameters.Add("@Result", SqlDbType.Int);
        result.Direction = ParameterDirection.ReturnValue;

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // 0 = granted immediately, 1 = granted after waiting. Negative values
        // are timeout, cancel, deadlock or a parameter error.
        return (int)result.Value >= 0;
    }

    public async Task ReleaseRunLockAsync(
        int definitionId,
        DateOnly businessDate,
        string? sessionRef,
        CancellationToken cancellationToken = default)
    {
        var resource = $"recon:def={definitionId}:date={businessDate:yyyy-MM-dd}:session={sessionRef ?? "-"}";

        using var command = Db.Command(_connection, "sp_releaseapplock");
        command.CommandType = CommandType.StoredProcedure;
        command.Parameters.AddWithValue("@Resource", resource);
        command.Parameters.AddWithValue("@LockOwner", "Session");

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException)
        {
            // Releasing a lock we no longer hold is not a failure worth
            // propagating out of a finally block.
        }
    }

    /// <summary>
    /// Creates a run and stores the definition snapshot.
    ///
    /// <para>
    /// The snapshot is the whole point of this method (blocker B1): the full
    /// effective definition — rules, conditions, classifications, control
    /// totals, both field registries — serialised at run start. Everything
    /// downstream compiles from the snapshot, so "what rule matched this in
    /// June" is answerable from the run itself rather than from config that has
    /// since been edited.
    /// </para>
    /// </summary>
    public async Task<RunHandle> CreateRunAsync(
        ReconciliationDefinition definition,
        DateOnly businessDate,
        string? sessionRef,
        RunType runType,
        string triggeredBy,
        long? sourceRunId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (runType == RunType.Rematch && sourceRunId is null)
        {
            throw new ArgumentException(
                "a Rematch reuses another run's staged rows and needs a SourceRunId",
                nameof(sourceRunId));
        }

        var snapshot = JsonSerializer.Serialize(definition, SnapshotOptions);

        // Supersede the current run for this slot before inserting, or the
        // filtered unique index UX_ReconRun_Current refuses the insert. The
        // prior run is not deleted or overwritten: it keeps its results and
        // stays queryable, which is what "re-runs never overwrite" means.
        long? superseded = null;

        if (runType != RunType.Sandbox)
        {
            superseded = await Db.ScalarAsync<long?>(
                _connection,
                """
                SELECT TOP (1) RunId FROM ops.ReconRun
                WHERE DefinitionId = @def AND BusinessDate = @date
                  AND ((SessionRef IS NULL AND @session IS NULL) OR SessionRef = @session)
                  AND IsCurrent = 1 AND RunType <> 'Sandbox'
                ORDER BY RunId DESC;
                """,
                c => c.With("@def", definition.DefinitionId)
                      .With("@date", businessDate.ToDateTime(TimeOnly.MinValue))
                      .With("@session", sessionRef),
                cancellationToken).ConfigureAwait(false);

            if (superseded is not null)
            {
                await Db.ExecuteAsync(
                    _connection,
                    "UPDATE ops.ReconRun SET IsCurrent = 0 WHERE RunId = @id;",
                    c => c.With("@id", superseded.Value),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        var runId = await Db.ScalarAsync<long>(
            _connection,
            """
            INSERT ops.ReconRun
                (DefinitionId, DefinitionVersion, BusinessDate, SessionRef, RunType, Status,
                 DefinitionSnapshotJson, SupersedesRunId, SourceRunId, StartedAt, TriggeredBy)
            VALUES
                (@def, @version, @date, @session, @type, 'Running',
                 @snapshot, @supersedes, @source, SYSDATETIME(), @by);
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);
            """,
            c => c.With("@def", definition.DefinitionId)
                  .With("@version", definition.Version)
                  .With("@date", businessDate.ToDateTime(TimeOnly.MinValue))
                  .With("@session", sessionRef)
                  .With("@type", runType.ToString())
                  .With("@snapshot", snapshot)
                  .With("@supersedes", superseded)
                  .With("@source", sourceRunId)
                  .With("@by", triggeredBy),
            cancellationToken).ConfigureAwait(false);

        // StagingRunId is computed by the database as ISNULL(SourceRunId,
        // RunId). It is read back rather than inferred here so that the one
        // definition of the rule stays in the schema (review finding 1).
        var stagingRunId = await Db.ScalarAsync<long>(
            _connection,
            "SELECT StagingRunId FROM ops.ReconRun WHERE RunId = @id;",
            c => c.With("@id", runId),
            cancellationToken).ConfigureAwait(false);

        return new RunHandle(runId, stagingRunId, definition.DefinitionId, businessDate, sessionRef, runType);
    }

    /// <summary>
    /// Reads a run's snapshot back as a definition. This is what a resumed or
    /// re-matched run compiles from — never live config, which may have changed
    /// since.
    /// </summary>
    public async Task<ReconciliationDefinition> LoadSnapshotAsync(
        long runId, CancellationToken cancellationToken = default)
    {
        var json = await Db.ScalarAsync<string>(
            _connection,
            "SELECT DefinitionSnapshotJson FROM ops.ReconRun WHERE RunId = @id;",
            c => c.With("@id", runId),
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(json))
        {
            throw new InvalidOperationException(
                $"run {runId} has no definition snapshot, so it cannot be reproduced. " +
                "Runs created before the snapshot existed are not replayable.");
        }

        return JsonSerializer.Deserialize<ReconciliationDefinition>(json, SnapshotOptions)
            ?? throw new InvalidOperationException($"run {runId}'s snapshot could not be read");
    }

    /// <summary>
    /// Starts a step, or returns the id of one already completed.
    ///
    /// <para>
    /// Each step is a checkpoint (review item B3): a run that dies in pass 3
    /// resumes from pass 3 rather than re-staging 2M rows. Acquire and Parse
    /// are idempotent by file hash; a pass is idempotent by
    /// <c>(RunId, MatchRuleId)</c>, which is why the step row carries the rule
    /// id.
    /// </para>
    /// </summary>
    public async Task<StepHandle> BeginStepAsync(
        long runId,
        RunStepName step,
        int? matchRuleId = null,
        Side? side = null,
        CancellationToken cancellationToken = default)
    {
        // A step's identity is (run, name, rule, side). The side is load-
        // bearing: Exclude, Duplicates and Classify each run once per dataset,
        // and without it the second side's step looked already-completed and
        // was skipped — half a reconciliation, silently.
        var existing = await Db.QueryAsync(
            _connection,
            """
            SELECT RunStepId, Status FROM ops.ReconRunStep
            WHERE RunId = @run AND StepName = @step
              AND ((MatchRuleId IS NULL AND @rule IS NULL) OR MatchRuleId = @rule)
              AND ((Side IS NULL AND @side IS NULL) OR Side = @side);
            """,
            r => (Id: r.GetInt64(0), Status: r.GetString(1)),
            c => c.With("@run", runId)
                  .With("@step", step.ToString())
                  .With("@rule", matchRuleId)
                  .With("@side", side?.ToString()),
            cancellationToken).ConfigureAwait(false);

        if (existing.Count > 0 && existing[0].Status == nameof(StepStatus.Completed))
        {
            return new StepHandle(existing[0].Id, AlreadyCompleted: true);
        }

        if (existing.Count > 0)
        {
            await Db.ExecuteAsync(
                _connection,
                """
                UPDATE ops.ReconRunStep
                SET Status = 'Running', StartedAt = SYSDATETIME(), ErrorMessage = NULL
                WHERE RunStepId = @id;
                """,
                c => c.With("@id", existing[0].Id),
                cancellationToken).ConfigureAwait(false);

            return new StepHandle(existing[0].Id, AlreadyCompleted: false);
        }

        var id = await Db.ScalarAsync<long>(
            _connection,
            """
            INSERT ops.ReconRunStep (RunId, StepName, MatchRuleId, Side, Status, StartedAt)
            VALUES (@run, @step, @rule, @side, 'Running', SYSDATETIME());
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);
            """,
            c => c.With("@run", runId)
                  .With("@step", step.ToString())
                  .With("@rule", matchRuleId)
                  .With("@side", side?.ToString()),
            cancellationToken).ConfigureAwait(false);

        return new StepHandle(id, AlreadyCompleted: false);
    }

    /// <summary>
    /// Completes a step, storing the generated SQL alongside it.
    ///
    /// <para>
    /// Persisting the SQL is a design requirement (§9.4), not diagnostics: it
    /// is how a past run's behaviour is explained to an auditor without
    /// trusting that today's configuration would generate the same statement.
    /// </para>
    /// </summary>
    public Task CompleteStepAsync(
        long runStepId,
        long? rowsProcessed = null,
        long? rowsMatched = null,
        string? generatedSql = null,
        CancellationToken cancellationToken = default) =>
        Db.ExecuteAsync(
            _connection,
            """
            UPDATE ops.ReconRunStep
            SET Status = 'Completed', CompletedAt = SYSDATETIME(),
                RowsProcessed = @processed, RowsMatched = @matched,
                GeneratedSql = COALESCE(@sql, GeneratedSql)
            WHERE RunStepId = @id;
            """,
            c => c.With("@id", runStepId)
                  .With("@processed", rowsProcessed)
                  .With("@matched", rowsMatched)
                  .With("@sql", generatedSql),
            cancellationToken);

    public Task FailStepAsync(
        long runStepId, string error, string? generatedSql = null,
        CancellationToken cancellationToken = default) =>
        Db.ExecuteAsync(
            _connection,
            """
            UPDATE ops.ReconRunStep
            SET Status = 'Failed', CompletedAt = SYSDATETIME(),
                ErrorMessage = @error, GeneratedSql = COALESCE(@sql, GeneratedSql)
            WHERE RunStepId = @id;
            """,
            c => c.With("@id", runStepId).With("@error", Truncate(error)).With("@sql", generatedSql),
            cancellationToken);

    public Task CompleteRunAsync(
        long runId, RunCounts counts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(counts);

        return Db.ExecuteAsync(
            _connection,
            """
            UPDATE ops.ReconRun
            SET Status = 'Completed', CompletedAt = SYSDATETIME(),
                LeftRowCnt = @left, RightRowCnt = @right,
                MatchedCnt = @matched, UnmatchedCnt = @unmatched, AmbiguousCnt = @ambiguous
            WHERE RunId = @id;
            """,
            c => c.With("@id", runId)
                  .With("@left", counts.LeftRows)
                  .With("@right", counts.RightRows)
                  .With("@matched", counts.Matched)
                  .With("@unmatched", counts.Unmatched)
                  .With("@ambiguous", counts.Ambiguous),
            cancellationToken);
    }

    public Task FailRunAsync(
        long runId, string error, CancellationToken cancellationToken = default) =>
        Db.ExecuteAsync(
            _connection,
            """
            UPDATE ops.ReconRun
            SET Status = 'Failed', CompletedAt = SYSDATETIME(), ErrorMessage = @error
            WHERE RunId = @id;
            """,
            c => c.With("@id", runId).With("@error", error),
            cancellationToken);

    /// <summary>
    /// Records a run that could not start because another holds the lock. A
    /// rejected attempt is visible on the dashboard rather than silently
    /// absent — Operations needs to know the manual trigger did nothing.
    /// </summary>
    public Task RecordRejectedAsync(
        int definitionId,
        int definitionVersion,
        DateOnly businessDate,
        string? sessionRef,
        RunType runType,
        string triggeredBy,
        string reason,
        CancellationToken cancellationToken = default) =>
        Db.ExecuteAsync(
            _connection,
            """
            INSERT ops.ReconRun
                (DefinitionId, DefinitionVersion, BusinessDate, SessionRef, RunType,
                 Status, IsCurrent, StartedAt, CompletedAt, ErrorMessage, TriggeredBy)
            VALUES
                (@def, @version, @date, @session, @type,
                 'Rejected', 0, SYSDATETIME(), SYSDATETIME(), @reason, @by);
            """,
            c => c.With("@def", definitionId)
                  .With("@version", definitionVersion)
                  .With("@date", businessDate.ToDateTime(TimeOnly.MinValue))
                  .With("@session", sessionRef)
                  .With("@type", runType.ToString())
                  .With("@reason", reason)
                  .With("@by", triggeredBy),
            cancellationToken);

    /// <summary>
    /// Deletes a pass's results so it can be retried. This is what makes a
    /// failed pass safe to re-run: staging was never touched, so removing the
    /// pass's <c>MatchResult</c> rows restores the exact pre-pass state.
    /// </summary>
    public Task DeletePassResultsAsync(
        long runId, int matchRuleId, CancellationToken cancellationToken = default) =>
        Db.ExecuteAsync(
            _connection,
            "DELETE ops.MatchResult WHERE RunId = @run AND MatchRuleId = @rule;",
            c => c.With("@run", runId).With("@rule", matchRuleId),
            cancellationToken);

    private static string Truncate(string value) =>
        value.Length <= 3900 ? value : value[..3900];
}

public sealed record RunHandle(
    long RunId,
    long StagingRunId,
    int DefinitionId,
    DateOnly BusinessDate,
    string? SessionRef,
    RunType RunType);

public sealed record StepHandle(long RunStepId, bool AlreadyCompleted);

public sealed record RunCounts
{
    public long LeftRows { get; init; }
    public long RightRows { get; init; }
    public long Matched { get; init; }
    public long Unmatched { get; init; }
    public long Ambiguous { get; init; }
}
