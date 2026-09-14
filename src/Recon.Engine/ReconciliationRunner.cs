using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Recon.Data;
using Recon.Domain.Configuration;
using Recon.Engine.Sql;
using Recon.Engine.Totals;

namespace Recon.Engine;

// CA1848 asks for LoggerMessage delegates. These two calls happen once per
// pass — a handful of times per run, not per row — so the allocation the rule
// guards against does not arise, and inlined templates keep the message next
// to the code that emits it.
#pragma warning disable CA1848

/// <summary>
/// Executes a reconciliation: exclusions, duplicates, the ordered passes,
/// classification, run aggregates and control totals.
///
/// <para>
/// The order is not arbitrary. Exclusions and duplicate detection run
/// <b>before</b> pass 1 so the working set is correct when the first pass
/// touches it; classification runs after the last pass so it sees the final
/// unmatched set; aggregates are written before control totals because a
/// period-scoped check reads them.
/// </para>
///
/// <para>
/// Every stage is a checkpoint, so <c>Resume</c> re-executes from the first
/// incomplete one rather than re-staging 2M rows (review item B3). The
/// definition compiled from is the run's snapshot, never live configuration
/// (blocker B1).
/// </para>
/// </summary>
public sealed class ReconciliationRunner(
    SqlConnection connection,
    Data.RunRepository runs,
    ILogger<ReconciliationRunner>? logger = null)
{
    private readonly SqlConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    private readonly Data.RunRepository _runs = runs ?? throw new ArgumentNullException(nameof(runs));
    private readonly ILogger _log =
        logger ?? (ILogger)Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public async Task<RunOutcome> ExecuteAsync(
        ReconciliationDefinition definition,
        Data.RunHandle run,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(run);

        var (windowFrom, windowTo) = definition.WindowFor(run.BusinessDate);
        var passes = new List<PassOutcome>();

        try
        {
            // ---- before pass 1 -------------------------------------------
            foreach (var side in new[] { Side.Left, Side.Right })
            {
                var dataset = definition.DatasetFor(side);

                await RunStepAsync(
                    run.RunId, RunStepName.Exclude, null, side,
                    () => SqlQueryBuilderLifecycle.CompileExclusions(
                        dataset, definition.Exclusions, run.StagingRunId, windowFrom, windowTo),
                    cancellationToken).ConfigureAwait(false);

                await RunStepAsync(
                    run.RunId, RunStepName.Duplicates, null, side,
                    () => SqlQueryBuilderLifecycle.CompileDuplicateDetection(
                        dataset, run.StagingRunId, windowFrom, windowTo),
                    cancellationToken).ConfigureAwait(false);
            }

            // ---- the ordered passes --------------------------------------
            foreach (var rule in definition.ActivePasses)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var step = await _runs.BeginStepAsync(
                    run.RunId, RunStepName.Match, rule.MatchRuleId,
                    side: null, cancellationToken).ConfigureAwait(false);

                if (step.AlreadyCompleted)
                {
                    _log.LogInformation("Pass {Sequence} ({Code}) already completed; skipping.",
                        rule.Sequence, rule.RuleCode);
                    continue;
                }

                // A pass being retried may have left partial results. Deleting
                // them is safe precisely because passes never touch staging.
                await _runs.DeletePassResultsAsync(run.RunId, rule.MatchRuleId, cancellationToken)
                    .ConfigureAwait(false);

                var context = new PassContext
                {
                    Definition = definition,
                    Rule = rule,
                    RunId = run.RunId,
                    StagingRunId = run.StagingRunId,
                    BusinessDate = run.BusinessDate,
                    WindowFrom = windowFrom,
                    WindowTo = windowTo,
                };

                var statement = rule.Mode == MatchMode.Aggregate
                    ? SqlQueryBuilderAggregate.CompileAggregatePass(context)
                    : SqlQueryBuilder.CompileRowPass(context);

                try
                {
                    var passCounts = await ExecutePassAsync(statement, cancellationToken)
                        .ConfigureAwait(false);

                    await _runs.CompleteStepAsync(
                        step.RunStepId, passCounts.Candidates, passCounts.Unique,
                        statement.Sql, cancellationToken).ConfigureAwait(false);

                    passes.Add(new PassOutcome(rule.Sequence, rule.RuleCode,
                        passCounts.Candidates, passCounts.Unique, passCounts.Ambiguous));

                    _log.LogInformation(
                        "Pass {Sequence} ({Code}): {Unique} matched, {Ambiguous} ambiguous.",
                        rule.Sequence, rule.RuleCode, passCounts.Unique, passCounts.Ambiguous);
                }
                catch (SqlException ex)
                {
                    await _runs.FailStepAsync(step.RunStepId, ex.Message, statement.Sql,
                        cancellationToken).ConfigureAwait(false);
                    throw;
                }
            }

            // ---- after the last pass -------------------------------------
            await RunStepAsync(
                run.RunId, RunStepName.Stage, null, null,
                () => SqlQueryBuilderLifecycle.CompileFinalizeStaging(
                    definition, run.RunId, run.StagingRunId, windowFrom, windowTo),
                cancellationToken).ConfigureAwait(false);

            foreach (var side in new[] { Side.Left, Side.Right })
            {
                await RunStepAsync(
                    run.RunId, RunStepName.Classify, null, side,
                    () => SqlQueryBuilderLifecycle.CompileClassification(
                        definition, side, run.RunId, run.StagingRunId,
                        run.BusinessDate, windowFrom, windowTo),
                    cancellationToken).ConfigureAwait(false);
            }

            await RunStepAsync(
                run.RunId, RunStepName.Aggregate, null, null,
                () => RunAggregateBuilder.Compile(
                    definition, run.RunId, run.StagingRunId,
                    run.BusinessDate, windowFrom, windowTo),
                cancellationToken).ConfigureAwait(false);

            var totals = await EvaluateControlTotalsAsync(
                definition, run, windowFrom, windowTo, cancellationToken).ConfigureAwait(false);

            var counts = await ReadCountsAsync(definition, run, windowFrom, windowTo, cancellationToken)
                .ConfigureAwait(false);

            // A non-zero net difference on a check configured to fail the run
            // IS a failed run, however many rows matched (§11).
            var breaking = totals.Where(t => !t.IsBalanced && t.FailsRun).ToList();

            if (breaking.Count > 0)
            {
                var message = "control total mismatch: " + string.Join("; ",
                    breaking.Select(t => $"{t.CheckCode} differs by {t.Difference}"));

                await _runs.FailRunAsync(run.RunId, message, cancellationToken).ConfigureAwait(false);
                return new RunOutcome(run.RunId, RunStatus.Failed, counts, passes, totals, message);
            }

            await _runs.CompleteRunAsync(run.RunId, counts, cancellationToken).ConfigureAwait(false);
            return new RunOutcome(run.RunId, RunStatus.Completed, counts, passes, totals, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _runs.FailRunAsync(run.RunId, ex.Message, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private async Task RunStepAsync(
        long runId,
        RunStepName stepName,
        int? matchRuleId,
        Side? side,
        Func<CompiledStatement> compile,
        CancellationToken cancellationToken)
    {
        var step = await _runs.BeginStepAsync(runId, stepName, matchRuleId, side, cancellationToken)
            .ConfigureAwait(false);

        if (step.AlreadyCompleted)
        {
            return;
        }

        var statement = compile();

        try
        {
            var affected = await ExecuteScalarLongAsync(statement, cancellationToken)
                .ConfigureAwait(false);

            await _runs.CompleteStepAsync(step.RunStepId, affected, null, statement.Sql,
                cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException ex)
        {
            await _runs.FailStepAsync(step.RunStepId, ex.Message, statement.Sql, cancellationToken)
                .ConfigureAwait(false);
            throw;
        }
    }

    private async Task<PassCounts> ExecutePassAsync(
        CompiledStatement statement, CancellationToken cancellationToken)
    {
        using var command = Data.Db.Command(_connection, statement.Sql);
        foreach (var p in statement.Parameters.Parameters)
        {
            command.Parameters.Add(Clone(p));
        }

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        // The pass statement ends with a one-row summary select; earlier result
        // sets belong to the INSERTs and are skipped.
        do
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            if (reader.FieldCount < 3)
            {
                continue;
            }

            var unique = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
            var ambiguous = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            var candidates = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);

            return new PassCounts(unique, ambiguous, candidates);
        }
        while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));

        return new PassCounts(0, 0, 0);
    }

    private async Task<long> ExecuteScalarLongAsync(
        CompiledStatement statement, CancellationToken cancellationToken)
    {
        using var command = Data.Db.Command(_connection, statement.Sql);
        foreach (var p in statement.Parameters.Parameters)
        {
            command.Parameters.Add(Clone(p));
        }

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result switch
        {
            null or DBNull => 0,
            long l => l,
            int i => i,
            _ => 0,
        };
    }

    private async Task<List<ControlTotalOutcome>> EvaluateControlTotalsAsync(
        ReconciliationDefinition definition,
        Data.RunHandle run,
        DateOnly windowFrom,
        DateOnly windowTo,
        CancellationToken cancellationToken)
    {
        var outcomes = new List<ControlTotalOutcome>();

        var step = await _runs.BeginStepAsync(
            run.RunId, RunStepName.ControlTotals, null, null, cancellationToken)
            .ConfigureAwait(false);

        foreach (var check in definition.ControlTotals.Where(c => c.IsActive))
        {
            var statement = ControlTotalEvaluator.Compile(
                check, definition, run.RunId, run.StagingRunId,
                run.BusinessDate, windowFrom, windowTo);

            using var command = Data.Db.Command(_connection, statement.Sql);
            foreach (var p in statement.Parameters.Parameters)
            {
                command.Parameters.Add(Clone(p));
            }

            using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            long a = 0, b = 0;
            var balanced = true;

            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                a = reader.GetInt64(0);
                b = reader.GetInt64(1);
                balanced = reader.GetInt32(2) == 1;
            }

            outcomes.Add(new ControlTotalOutcome(
                check.CheckCode, check.DisplayName, a, b, balanced, check.FailRunOnMismatch));
        }

        if (!step.AlreadyCompleted)
        {
            await _runs.CompleteStepAsync(step.RunStepId, outcomes.Count, null, null, cancellationToken)
                .ConfigureAwait(false);
        }

        return outcomes;
    }

    private async Task<Data.RunCounts> ReadCountsAsync(
        ReconciliationDefinition definition,
        Data.RunHandle run,
        DateOnly windowFrom,
        DateOnly windowTo,
        CancellationToken cancellationToken)
    {
        // Read from the aggregates just written rather than rescanning staging:
        // the same primary-key read Operations will use later.
        var rows = await Data.Db.QueryAsync(
            _connection,
            """
            SELECT Side, MatchStatus, SUM(RowCnt) AS RowCnt
            FROM ops.RunAggregate
            WHERE RunId = @run AND GroupKey = N'*'
            GROUP BY Side, MatchStatus;
            """,
            r => (Side: r.GetString(0), Status: r.GetString(1), Rows: r.GetInt64(2)),
            c => c.With("@run", run.RunId),
            cancellationToken).ConfigureAwait(false);

        long Total(string side, string status) =>
            rows.Where(x => x.Side == side && x.Status == status).Sum(x => x.Rows);

        return new Data.RunCounts
        {
            LeftRows = Total("Left", "*"),
            RightRows = Total("Right", "*"),
            Matched = Total("Left", "Matched") + Total("Right", "Matched"),
            Unmatched = Total("Left", "Unmatched") + Total("Right", "Unmatched"),
            Ambiguous = Total("Left", "Ambiguous") + Total("Right", "Ambiguous"),
        };
    }

    /// <summary>
    /// A <see cref="SqlParameter"/> belongs to one command, so it is copied
    /// rather than shared. Cheap, and it keeps
    /// <see cref="CompiledStatement"/> reusable — which the tests rely on to
    /// inspect a statement without executing it.
    /// </summary>
    private static SqlParameter Clone(SqlParameter source) =>
        new(source.ParameterName, source.SqlDbType)
        {
            Value = source.Value,
            Size = source.Size,
            Precision = source.Precision,
            Scale = source.Scale,
            Direction = source.Direction,
        };

    private sealed record PassCounts(int Unique, int Ambiguous, int Candidates);
}

public sealed record RunOutcome(
    long RunId,
    RunStatus Status,
    Data.RunCounts Counts,
    IReadOnlyList<PassOutcome> Passes,
    IReadOnlyList<ControlTotalOutcome> ControlTotals,
    string? Error)
{
    /// <summary>
    /// The share of matches each pass contributed. The design calls this a
    /// data-quality early-warning system (§9.3): a rising share in the last
    /// pass means the clean reference match is degrading upstream, and it is
    /// the <c>MatchDistributionDrift</c> alert.
    /// </summary>
    public IReadOnlyList<(int Sequence, string Code, double Share)> Distribution()
    {
        var total = Passes.Sum(p => (double)p.Matched);

        return Passes
            .Select(p => (p.Sequence, p.Code, total <= 0 ? 0 : 100 * p.Matched / total))
            .ToList();
    }
}

public sealed record PassOutcome(int Sequence, string Code, long Candidates, long Matched, long Ambiguous);

public sealed record ControlTotalOutcome(
    string CheckCode,
    string DisplayName,
    long ValueA,
    long ValueB,
    bool IsBalanced,
    bool FailsRun)
{
    public long Difference => ValueA - ValueB;
}
#pragma warning restore CA1848
