using Microsoft.Data.SqlClient;
using Recon.Data;
using Recon.Domain.Configuration;
using Recon.Engine;
using Recon.Engine.Staging;
using Xunit;

namespace Recon.IntegrationTests;

/// <summary>
/// End-to-end reconciliations against a live SQL Server.
///
/// <para>
/// The unit tests prove the compiler emits what it means to. Only these prove
/// the server agrees — that the generated SQL parses, that the partitioned
/// staging table accepts the rows, that the filtered indexes permit the writes,
/// and that the numbers come out right.
/// </para>
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class ReconciliationTests(SqlServerFixture sql)
{
    private static readonly DateOnly BusinessDate = new(2026, 9, 13);

    [SkippableFact]
    public async Task ACleanSessionReconcilesEndToEnd()
    {
        Skip.IfNot(sql.Available, sql.SkipReason);

        using var connection = await sql.OpenAsync().ConfigureAwait(false);
        var ids = await TestConfiguration.CreateAsync(connection).ConfigureAwait(false);

        var config = new ConfigurationRepository(connection);
        var definition = await config.LoadDefinitionAsync(ids.DefinitionId).ConfigureAwait(false);

        // The definition loaded from the database must be activatable: this is
        // the same gate the CLI and the portal apply.
        Assert.Empty(definition.ActivationProblems());

        // The run is created BEFORE the load, because rows are staged under a
        // run id: that is what LoadRunId means, and what StagingRunId resolves
        // to for anything other than a Rematch.
        var runs = new RunRepository(connection);
        var run = await runs.CreateRunAsync(
            definition, BusinessDate, "S1", RunType.Scheduled, "tests").ConfigureAwait(false);

        Assert.Equal(run.RunId, run.StagingRunId);

        // 100 pairs that match on the reference.
        await StageAsync(connection, ids.LeftDatasetId, run.RunId, Enumerable.Range(1, 100)
            .Select(i => new Row($"E2E-{i:D6}", i * 1_000L, "Inward", "ACSC")));

        await StageAsync(connection, ids.RightDatasetId, run.RunId, Enumerable.Range(1, 100)
            .Select(i => new Row($"E2E-{i:D6}", i * 1_000L, "Inward", "ACSC")));

        var runner = new ReconciliationRunner(connection, runs);
        var outcome = await runner.ExecuteAsync(definition, run).ConfigureAwait(false);

        Assert.Equal(RunStatus.Completed, outcome.Status);
        Assert.Equal(100, outcome.Counts.LeftRows);
        Assert.Equal(100, outcome.Counts.RightRows);
        Assert.Equal(0, outcome.Counts.Unmatched);

        // Pass 1 must carry everything: these references are identical, so a
        // later pass doing the work would mean pass 1 is broken.
        var distribution = outcome.Distribution();
        Assert.Equal(100d, distribution[0].Share, precision: 2);
    }

    [SkippableFact]
    public async Task UnmatchedRowsBecomeClassifiedExceptionsOnBothSides()
    {
        Skip.IfNot(sql.Available, sql.SkipReason);

        using var connection = await sql.OpenAsync().ConfigureAwait(false);
        var ids = await TestConfiguration.CreateAsync(connection).ConfigureAwait(false);

        var config = new ConfigurationRepository(connection);
        var definition = await config.LoadDefinitionAsync(ids.DefinitionId).ConfigureAwait(false);

        // Two matching pairs, one row only in CliQ, one only in OM.
        var runs = new RunRepository(connection);
        var run = await runs.CreateRunAsync(
            definition, BusinessDate, "S1", RunType.Scheduled, "tests").ConfigureAwait(false);

        await StageAsync(connection, ids.LeftDatasetId, run.RunId,
        [
            new Row("E2E-000001", 1_000L, "Inward", "ACSC"),
            new Row("E2E-000002", 2_000L, "Inward", "ACSC"),
            new Row("E2E-ORPHAN", 9_000L, "Inward", "ACSC"),
        ]);

        await StageAsync(connection, ids.RightDatasetId, run.RunId,
        [
            new Row("E2E-000001", 1_000L, "Inward", "ACSC"),
            new Row("E2E-000002", 2_000L, "Inward", "ACSC"),
            new Row("OM-ORPHAN", 7_000L, "Inward", "ACSC"),
        ]);

        var outcome = await new ReconciliationRunner(connection, runs)
            .ExecuteAsync(definition, run).ConfigureAwait(false);

        Assert.Equal(RunStatus.Completed, outcome.Status);

        var exceptions = await Db.QueryAsync(connection,
            """
            SELECT ExceptionCode, Side, AmountMinor, KeyValuesJson
            FROM ops.ReconException WHERE RunId = @run ORDER BY Side, ExceptionCode;
            """,
            r => (Code: r.GetString(0), Side: r.GetString(1),
                  Amount: r.GetInt64(2), Keys: r.GetString(3)),
            c => c.With("@run", run.RunId)).ConfigureAwait(false);

        Assert.Equal(2, exceptions.Count);

        var left = exceptions.Single(e => e.Side == "Left");
        Assert.Equal("FAILED_INWARD", left.Code);
        Assert.Equal(9_000L, left.Amount);

        var right = exceptions.Single(e => e.Side == "Right");
        Assert.Equal("MISSING_IN_CLIQ", right.Code);
        Assert.Equal(7_000L, right.Amount);

        // Finding C2: the key snapshot is what makes late-arrival auto-close
        // work after staging is archived, so it must actually be populated.
        Assert.Contains("E2E-ORPHAN", left.Keys, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ExcludedAndDuplicateRowsNeverEnterMatching()
    {
        Skip.IfNot(sql.Available, sql.SkipReason);

        using var connection = await sql.OpenAsync().ConfigureAwait(false);
        var ids = await TestConfiguration.CreateAsync(connection).ConfigureAwait(false);

        var config = new ConfigurationRepository(connection);
        var definition = await config.LoadDefinitionAsync(ids.DefinitionId).ConfigureAwait(false);

        var runs = new RunRepository(connection);
        var run = await runs.CreateRunAsync(
            definition, BusinessDate, "S1", RunType.Scheduled, "tests").ConfigureAwait(false);

        await StageAsync(connection, ids.LeftDatasetId, run.RunId,
        [
            new Row("E2E-000001", 1_000L, "Inward", "ACSC"),
            // Rejected: must be Excluded, and must NOT become an exception.
            new Row("E2E-REJECTED", 5_000L, "Inward", "RJCT"),
            // The declared key twice: the second is a source duplicate.
            new Row("E2E-000002", 2_000L, "Inward", "ACSC"),
            new Row("E2E-000002", 2_000L, "Inward", "ACSC"),
        ]);

        await StageAsync(connection, ids.RightDatasetId, run.RunId,
        [
            new Row("E2E-000001", 1_000L, "Inward", "ACSC"),
            new Row("E2E-000002", 2_000L, "Inward", "ACSC"),
        ]);

        var outcome = await new ReconciliationRunner(connection, runs)
            .ExecuteAsync(definition, run).ConfigureAwait(false);

        Assert.Equal(RunStatus.Completed, outcome.Status);

        var statuses = await Db.QueryAsync(connection,
            """
            SELECT Text1, MatchStatus, ExceptionCode
            FROM stg.StagingTransaction
            WHERE DatasetId = @ds AND LoadRunId = @run ORDER BY StagingId;
            """,
            r => (Reference: r.GetString(0), Status: r.GetString(1),
                  Code: r.IsDBNull(2) ? null : r.GetString(2)),
            c => c.With("@ds", ids.LeftDatasetId).With("@run", run.RunId)).ConfigureAwait(false);

        var rejected = statuses.Single(s => s.Reference == "E2E-REJECTED");
        Assert.Equal("Excluded", rejected.Status);
        Assert.Equal("REJECTED", rejected.Code);

        var duplicates = statuses.Where(s => s.Reference == "E2E-000002").ToList();
        Assert.Equal(2, duplicates.Count);
        // The first occurrence stays in the working set and matches; the
        // second is flagged (finding C3).
        Assert.Contains(duplicates, d => d.Status == "Matched");
        Assert.Contains(duplicates, d => d.Status == "Duplicate" && d.Code == "DUPLICATE_IN_SOURCE");

        // Neither an excluded nor a duplicate row may raise an exception: a
        // rejected transaction is not a break, and a source duplicate is a
        // data-quality report.
        var exceptionCodes = await Db.QueryAsync(connection,
            "SELECT ExceptionCode FROM ops.ReconException WHERE RunId = @run;",
            r => r.GetString(0),
            c => c.With("@run", run.RunId)).ConfigureAwait(false);

        Assert.DoesNotContain("REJECTED", exceptionCodes);
        Assert.DoesNotContain("DUPLICATE_IN_SOURCE", exceptionCodes);
    }

    [SkippableFact]
    public async Task ADuplicateReferenceOnBothSidesIsAmbiguousNotASilentChoice()
    {
        Skip.IfNot(sql.Available, sql.SkipReason);

        using var connection = await sql.OpenAsync().ConfigureAwait(false);
        var ids = await TestConfiguration.CreateAsync(connection).ConfigureAwait(false);

        // No declared duplicate key on the right side, so two OM rows can
        // legitimately share a reference — exactly the case §9.5 is about.
        var config = new ConfigurationRepository(connection);
        var definition = await config.LoadDefinitionAsync(ids.DefinitionId).ConfigureAwait(false);

        var runs = new RunRepository(connection);
        var run = await runs.CreateRunAsync(
            definition, BusinessDate, "S1", RunType.Scheduled, "tests").ConfigureAwait(false);

        await StageAsync(connection, ids.LeftDatasetId, run.RunId,
            [new Row("E2E-000001", 1_000L, "Inward", "ACSC")]);

        await StageAsync(connection, ids.RightDatasetId, run.RunId,
        [
            new Row("E2E-000001", 1_000L, "Inward", "ACSC"),
            new Row("E2E-000001", 1_000L, "Inward", "ACSC"),
        ]);

        await new ReconciliationRunner(connection, runs)
            .ExecuteAsync(definition, run).ConfigureAwait(false);

        var results = await Db.QueryAsync(connection,
            "SELECT MatchStatus, CandidateCount FROM ops.MatchResult WHERE RunId = @run;",
            r => (Status: r.GetString(0), Candidates: r.IsDBNull(1) ? 0 : r.GetInt32(1)),
            c => c.With("@run", run.RunId)).ConfigureAwait(false);

        // Two candidates, both Ambiguous. A rule must never silently pick one:
        // at 2M rows a day a silent choice is a wrong number nobody can trace.
        Assert.Equal(2, results.Count);
        Assert.All(results, x => Assert.Equal("Ambiguous", x.Status));
        Assert.All(results, x => Assert.Equal(2, x.Candidates));
    }

    [SkippableFact]
    public async Task NormalizedCompanionMatchesReferencesThatDifferOnlyInFormatting()
    {
        Skip.IfNot(sql.Available, sql.SkipReason);

        using var connection = await sql.OpenAsync().ConfigureAwait(false);
        var ids = await TestConfiguration.CreateAsync(connection).ConfigureAwait(false);

        var config = new ConfigurationRepository(connection);
        var definition = await config.LoadDefinitionAsync(ids.DefinitionId).ConfigureAwait(false);

        // Same reference, spelled differently by the two systems. Pass 1 must
        // miss it and pass 2 must catch it — which is the whole reason the
        // companion slot exists (blocker A2).
        var runs = new RunRepository(connection);
        var run = await runs.CreateRunAsync(
            definition, BusinessDate, "S1", RunType.Scheduled, "tests").ConfigureAwait(false);

        await StageAsync(connection, ids.LeftDatasetId, run.RunId,
            [new Row("E2E-2026 0913/0001", 1_000L, "Inward", "ACSC")]);

        await StageAsync(connection, ids.RightDatasetId, run.RunId,
            [new Row("e2e-20260913-0001", 1_000L, "Inward", "ACSC")]);

        var outcome = await new ReconciliationRunner(connection, runs)
            .ExecuteAsync(definition, run).ConfigureAwait(false);

        Assert.Equal(RunStatus.Completed, outcome.Status);
        Assert.Equal(0, outcome.Counts.Unmatched);

        var pass1 = outcome.Passes.Single(p => p.Code == "P1_REF");
        var pass2 = outcome.Passes.Single(p => p.Code == "P2_REF_NORM");

        Assert.Equal(0, pass1.Matched);
        Assert.Equal(1, pass2.Matched);
    }

    [SkippableFact]
    public async Task ARematchReadsTheSourceRunsRowsAndLeavesItsResultsIntact()
    {
        Skip.IfNot(sql.Available, sql.SkipReason);

        using var connection = await sql.OpenAsync().ConfigureAwait(false);
        var ids = await TestConfiguration.CreateAsync(connection).ConfigureAwait(false);

        var config = new ConfigurationRepository(connection);
        var definition = await config.LoadDefinitionAsync(ids.DefinitionId).ConfigureAwait(false);

        var runs = new RunRepository(connection);
        var first = await runs.CreateRunAsync(
            definition, BusinessDate, "S1", RunType.Scheduled, "tests").ConfigureAwait(false);

        // Rows staged under the FIRST run's id.
        await StageAsync(connection, ids.LeftDatasetId, first.RunId,
            [new Row("E2E-000001", 1_000L, "Inward", "ACSC")]);
        await StageAsync(connection, ids.RightDatasetId, first.RunId,
            [new Row("E2E-000001", 1_000L, "Inward", "ACSC")]);

        var firstOutcome = await new ReconciliationRunner(connection, runs)
            .ExecuteAsync(definition, first).ConfigureAwait(false);

        Assert.Equal(RunStatus.Completed, firstOutcome.Status);
        // One pair, so one matched row on each side.
        Assert.Equal(2, firstOutcome.Counts.Matched);

        var firstResults = await CountResultsAsync(connection, first.RunId).ConfigureAwait(false);
        Assert.True(firstResults > 0);

        // The Rematch: new rules, same staged rows. This is review finding 1 —
        // without StagingRunId it would read nothing at all.
        var rematch = await runs.CreateRunAsync(
            definition, BusinessDate, "S1", RunType.Rematch, "tests",
            sourceRunId: first.RunId).ConfigureAwait(false);

        Assert.Equal(first.RunId, rematch.StagingRunId);
        Assert.NotEqual(first.RunId, rematch.RunId);

        var rematchOutcome = await new ReconciliationRunner(connection, runs)
            .ExecuteAsync(definition, rematch).ConfigureAwait(false);

        Assert.Equal(RunStatus.Completed, rematchOutcome.Status);
        // It found the rows: the whole point.
        Assert.Equal(1, rematchOutcome.Counts.LeftRows);

        // And it MATCHED them. This is the assertion whose absence hid a real
        // defect: the rows still carried the first run's 'Matched' status in
        // staging's cache, every statement before pass 1 filters on
        // 'Unmatched', so the replay's passes saw nothing — while the stale
        // cache made the aggregates report a perfect run. A rematch that
        // matches nothing must not look like a rematch that matched
        // everything.
        Assert.Equal(2, rematchOutcome.Counts.Matched);
        Assert.Equal(1, rematchOutcome.Passes.Sum(pass => pass.Matched));
        Assert.True(await CountResultsAsync(connection, rematch.RunId).ConfigureAwait(false) > 0,
            "the rematch wrote no MatchResult rows of its own");

        // The reset ran once per side and is visible as a step, so an
        // operator can see that this run re-opened those rows.
        var resetSides = await Db.QueryAsync(connection,
            """
            SELECT Side FROM ops.ReconRunStep
            WHERE RunId = @run AND StepName = 'Reset' ORDER BY Side;
            """,
            r => r.GetString(0),
            c => c.With("@run", rematch.RunId)).ConfigureAwait(false);

        Assert.Equal(["Left", "Right"], resetSides);

        // The FIRST run recorded no reset: it staged its own rows, so there
        // was nothing to re-open and the update would have been pure cost.
        Assert.Empty(await Db.QueryAsync(connection,
            "SELECT Side FROM ops.ReconRunStep WHERE RunId = @run AND StepName = 'Reset';",
            r => r.GetString(0),
            c => c.With("@run", first.RunId)).ConfigureAwait(false));

        // And the first run's results are still there, unmodified. "Re-runs
        // never overwrite" is true at the level that matters.
        Assert.Equal(firstResults, await CountResultsAsync(connection, first.RunId).ConfigureAwait(false));

        var currency = await Db.QueryAsync(connection,
            "SELECT RunId, IsCurrent FROM ops.ReconRun WHERE RunId IN (@a, @b) ORDER BY RunId;",
            r => (RunId: r.GetInt64(0), IsCurrent: r.GetBoolean(1)),
            c => c.With("@a", first.RunId).With("@b", rematch.RunId)).ConfigureAwait(false);

        Assert.False(currency.Single(x => x.RunId == first.RunId).IsCurrent);
        Assert.True(currency.Single(x => x.RunId == rematch.RunId).IsCurrent);

        // The staging cache says which run produced it.
        var resultRunIds = await Db.QueryAsync(connection,
            """
            SELECT DISTINCT ResultRunId FROM stg.StagingTransaction
            WHERE LoadRunId = @load AND ResultRunId IS NOT NULL;
            """,
            r => r.GetInt64(0),
            c => c.With("@load", first.RunId)).ConfigureAwait(false);

        Assert.Equal([rematch.RunId], resultRunIds);
    }

    [SkippableFact]
    public async Task AControlTotalMismatchFailsTheRunEvenWhenEveryRowMatched()
    {
        Skip.IfNot(sql.Available, sql.SkipReason);

        using var connection = await sql.OpenAsync().ConfigureAwait(false);
        var ids = await TestConfiguration.CreateAsync(connection).ConfigureAwait(false);
        await TestConfiguration.AddControlTotalAsync(connection, ids, "MATCHED_TOTAL", 0, failRun: true)
            .ConfigureAwait(false);

        var config = new ConfigurationRepository(connection);
        var definition = await config.LoadDefinitionAsync(ids.DefinitionId).ConfigureAwait(false);

        // The references pair, but the amounts differ by 4.120 JOD. Every row
        // matches and the run must still fail (§11).
        var runs = new RunRepository(connection);
        var run = await runs.CreateRunAsync(
            definition, BusinessDate, "S1", RunType.Scheduled, "tests").ConfigureAwait(false);

        await StageAsync(connection, ids.LeftDatasetId, run.RunId,
            [new Row("E2E-000001", 125_500L, "Inward", "ACSC")]);
        await StageAsync(connection, ids.RightDatasetId, run.RunId,
            [new Row("E2E-000001", 121_380L, "Inward", "ACSC")]);

        var outcome = await new ReconciliationRunner(connection, runs)
            .ExecuteAsync(definition, run).ConfigureAwait(false);

        Assert.Equal(RunStatus.Failed, outcome.Status);
        Assert.Contains("MATCHED_TOTAL", outcome.Error, StringComparison.Ordinal);

        var total = outcome.ControlTotals.Single();
        Assert.False(total.IsBalanced);
        Assert.Equal(4_120L, total.Difference);

        // The unbalanced result is persisted, not just returned: the dashboard
        // reads ops.ControlTotalResult.
        var persisted = await Db.ScalarAsync<int>(connection,
            "SELECT COUNT(*) FROM ops.ControlTotalResult WHERE RunId = @run AND IsBalanced = 0;",
            c => c.With("@run", run.RunId)).ConfigureAwait(false);

        Assert.Equal(1, persisted);
    }

    [SkippableFact]
    public async Task TheRunLockRefusesASecondConcurrentRun()
    {
        Skip.IfNot(sql.Available, sql.SkipReason);

        using var first = await sql.OpenAsync().ConfigureAwait(false);
        using var second = await sql.OpenAsync().ConfigureAwait(false);

        var ids = await TestConfiguration.CreateAsync(first).ConfigureAwait(false);

        var firstRuns = new RunRepository(first);
        var secondRuns = new RunRepository(second);

        Assert.True(await firstRuns.TryAcquireRunLockAsync(ids.DefinitionId, BusinessDate, "S1")
            .ConfigureAwait(false));

        // Review item B2: the scheduler and a manual trigger must not both
        // stage the same date.
        Assert.False(await secondRuns.TryAcquireRunLockAsync(ids.DefinitionId, BusinessDate, "S1")
            .ConfigureAwait(false));

        // A different business date is a different lock.
        Assert.True(await secondRuns
            .TryAcquireRunLockAsync(ids.DefinitionId, BusinessDate.AddDays(1), "S1")
            .ConfigureAwait(false));

        await firstRuns.ReleaseRunLockAsync(ids.DefinitionId, BusinessDate, "S1").ConfigureAwait(false);

        Assert.True(await secondRuns.TryAcquireRunLockAsync(ids.DefinitionId, BusinessDate, "S1")
            .ConfigureAwait(false));
    }

    [SkippableFact]
    public async Task AResumedRunSkipsCompletedStepsAndKeepsItsResults()
    {
        Skip.IfNot(sql.Available, sql.SkipReason);

        using var connection = await sql.OpenAsync().ConfigureAwait(false);
        var ids = await TestConfiguration.CreateAsync(connection).ConfigureAwait(false);

        var config = new ConfigurationRepository(connection);
        var definition = await config.LoadDefinitionAsync(ids.DefinitionId).ConfigureAwait(false);

        var runs = new RunRepository(connection);
        var run = await runs.CreateRunAsync(
            definition, BusinessDate, "S1", RunType.Scheduled, "tests").ConfigureAwait(false);

        await StageAsync(connection, ids.LeftDatasetId, run.RunId,
            [new Row("E2E-000001", 1_000L, "Inward", "ACSC")]);
        await StageAsync(connection, ids.RightDatasetId, run.RunId,
            [new Row("E2E-000001", 1_000L, "Inward", "ACSC")]);

        var runner = new ReconciliationRunner(connection, runs);
        await runner.ExecuteAsync(definition, run).ConfigureAwait(false);

        var resultsAfterFirst = await CountResultsAsync(connection, run.RunId).ConfigureAwait(false);

        // Executing the same run again must be a no-op: every step is already
        // Completed, so B3's checkpoint logic skips them rather than doubling
        // the results.
        var second = await runner.ExecuteAsync(definition, run).ConfigureAwait(false);

        Assert.Equal(RunStatus.Completed, second.Status);
        Assert.Equal(resultsAfterFirst, await CountResultsAsync(connection, run.RunId).ConfigureAwait(false));

        var exceptions = await Db.ScalarAsync<int>(connection,
            "SELECT COUNT(*) FROM ops.ReconException WHERE RunId = @run;",
            c => c.With("@run", run.RunId)).ConfigureAwait(false);

        Assert.Equal(0, exceptions);
    }

    [SkippableFact]
    public async Task TheRunSnapshotReproducesTheDefinitionThatExecuted()
    {
        Skip.IfNot(sql.Available, sql.SkipReason);

        using var connection = await sql.OpenAsync().ConfigureAwait(false);
        var ids = await TestConfiguration.CreateAsync(connection).ConfigureAwait(false);

        var config = new ConfigurationRepository(connection);
        var definition = await config.LoadDefinitionAsync(ids.DefinitionId).ConfigureAwait(false);

        var runs = new RunRepository(connection);
        var run = await runs.CreateRunAsync(
            definition, BusinessDate, "S1", RunType.Scheduled, "tests").ConfigureAwait(false);

        // Now change the live configuration, as an operator would.
        await Db.ExecuteAsync(connection,
            "UPDATE cfg.MatchRule SET IsActive = 0 WHERE DefinitionId = @def;",
            c => c.With("@def", ids.DefinitionId)).ConfigureAwait(false);

        var live = await config.LoadDefinitionAsync(ids.DefinitionId).ConfigureAwait(false);
        var snapshot = await runs.LoadSnapshotAsync(run.RunId).ConfigureAwait(false);

        // Blocker B1: the snapshot still describes what ran, while live config
        // has moved on. Without this, "which rule matched this in June" gets a
        // wrong answer with a confident label.
        Assert.Empty(live.ActivePasses);
        Assert.Equal(3, snapshot.ActivePasses.Count());
        Assert.Equal(definition.Code, snapshot.Code);
        Assert.Equal(definition.Left.Fields.Count, snapshot.Left.Fields.Count);
        Assert.Equal("Text21", snapshot.Left.GetField("REF_PRIMARY").NormalizedSlot);
    }

    // =================================================================

    private static Task<long> CountResultsAsync(SqlConnection connection, long runId) =>
        Db.ScalarAsync<long>(connection,
            "SELECT COUNT_BIG(*) FROM ops.MatchResult WHERE RunId = @run;",
            c => c.With("@run", runId))!;

    private sealed record Row(
        string Reference, long? AmountMinor, string? Direction, string? Status, string? Currency = "JOD");

    [SkippableFact]
    public async Task ARunWhoseMappedColumnsAreAllEmptyStillCompletes()
    {
        /* Reported from a real run, as an unhandled exception page:
           "Cannot insert the value NULL into column 'AmountMinorSum', table
           'rec.ops.RunAggregate'". The amount column had mapped to nothing, so
           every staged row carried NULL, and SUM over a group of NULLs is NULL
           — which that NOT NULL column refused. The only other clue was a
           warning nobody reads: "Null value is eliminated by an aggregate".

           A row with no amount contributes nothing to the total and is still
           counted. Nothing here should fail. */
        Skip.IfNot(sql.Available, sql.SkipReason);

        using var connection = await sql.OpenAsync().ConfigureAwait(false);
        var ids = await TestConfiguration.CreateAsync(connection).ConfigureAwait(false);

        var config = new ConfigurationRepository(connection);
        var definition = await config.LoadDefinitionAsync(ids.DefinitionId).ConfigureAwait(false);

        var runs = new RunRepository(connection);
        var run = await runs.CreateRunAsync(
            definition, BusinessDate, "S1", RunType.Scheduled, "tests").ConfigureAwait(false);

        /* Nothing but the reference mapped, which is what a file whose column
           names do not match the mappings actually produces: every other slot
           NULL. Currency is the one that bit second — it is NOT NULL with a
           foreign key, and the dataset's declared currency is what stands in.
           Direction and status are NULL too, because a run that survives one
           empty column and dies on the next has not been fixed. */
        await StageAsync(connection, ids.LeftDatasetId, run.RunId, Enumerable.Range(1, 10)
            .Select(i => new Row($"E2E-{i:D6}", null, null, null, Currency: null)));

        await StageAsync(connection, ids.RightDatasetId, run.RunId, Enumerable.Range(1, 10)
            .Select(i => new Row($"E2E-{i:D6}", null, null, null, Currency: null)));

        var outcome = await new ReconciliationRunner(connection, runs)
            .ExecuteAsync(definition, run).ConfigureAwait(false);

        Assert.Equal(RunStatus.Completed, outcome.Status);

        // Ten pairs, counted as the twenty rows they are.
        Assert.Equal(20, outcome.Counts.Matched);

        // The rows are counted and the total is zero — not absent, not NULL.
        var total = await Db.ScalarAsync<long>(
            connection,
            """
            SELECT AmountMinorSum FROM ops.RunAggregate
            WHERE RunId = @run AND DatasetId = @ds AND GroupKey = N'*' AND MatchStatus = N'*';
            """,
            c => c.With("@run", run.RunId).With("@ds", ids.LeftDatasetId)).ConfigureAwait(false);

        Assert.Equal(0, total);

        var rows = await Db.ScalarAsync<long>(
            connection,
            """
            SELECT RowCnt FROM ops.RunAggregate
            WHERE RunId = @run AND DatasetId = @ds AND GroupKey = N'*' AND MatchStatus = N'*';
            """,
            c => c.With("@run", run.RunId).With("@ds", ids.LeftDatasetId)).ConfigureAwait(false);

        Assert.Equal(10, rows);

        // And the currency is the dataset's declared one rather than nothing.
        var currency = await Db.ScalarAsync<string>(
            connection,
            """
            SELECT CurrencyCode FROM ops.RunAggregate
            WHERE RunId = @run AND DatasetId = @ds AND GroupKey = N'*' AND MatchStatus = N'*';
            """,
            c => c.With("@run", run.RunId).With("@ds", ids.LeftDatasetId)).ConfigureAwait(false);

        Assert.Equal("JOD", currency);
    }

    /// <summary>
    /// Stages rows through the real bulk loader, so the load path under test is
    /// the production one.
    /// </summary>
    private static async Task StageAsync(
        SqlConnection connection, int datasetId, long loadRunId, IEnumerable<Row> rows)
    {
        var loader = new StagingBulkLoader(connection, new StagingLoadOptions { BatchSize = 1000 });

        await loader.LoadAsync(Records()).ConfigureAwait(false);

        IEnumerable<StagingRecord> Records()
        {
            var record = new StagingRecord();

            foreach (var row in rows)
            {
                record.Reset();
                record.DatasetId = datasetId;
                record.LoadRunId = loadRunId;
                record.TxDate = BusinessDate;
                record.Text[0] = row.Reference;                       // Text1
                record.Text[4] = row.Currency;                        // Text5
                record.Text[5] = row.Direction;                       // Text6
                record.Text[6] = row.Status;                          // Text7
                record.Text[20] = Engine.Parsing.Transforms.Normalize(row.Reference); // Text21
                record.Num[0] = row.AmountMinor;                      // Num1
                record.Date[0] = BusinessDate.ToDateTime(new TimeOnly(12, 0));

                yield return record;
            }
        }
    }
}
