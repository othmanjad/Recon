using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Recon.Data;
using Recon.Domain.Configuration;
using Recon.Engine;
using Recon.Engine.Parsing;
using Recon.Engine.Staging;
using Xunit;
using Xunit.Abstractions;

namespace Recon.IntegrationTests;

/// <summary>
/// The Phase 1 performance spike, which the design makes mandatory rather than
/// optional (§16): "a synthetic 2M-row file ingests and stages within budget;
/// one pass runs within budget."
///
/// <para>
/// Budgets from the design: bulk load ≤ 2 minutes, each pass ≤ 60 seconds. If
/// the load budget is missed, the escalation path is already written down —
/// daily partitions, a persisted composite partition column, heap load and
/// <c>SWITCH PARTITION</c> — and it is deferred because it is significantly
/// more complex and may not be needed. This test is how that decision gets
/// made with a number rather than a guess.
/// </para>
///
/// <para>
/// Skipped unless <c>RECON_PERF_ROWS</c> is set, because a 2M-row run takes
/// minutes and does not belong in the fast suite. Set it to 2000000 for the
/// real thing, or something smaller to smoke-test the harness. The measured
/// rate is reported either way, so a smaller run still says whether the full
/// one is plausible.
/// </para>
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class PerformanceSpikeTests(SqlServerFixture sql, ITestOutputHelper output)
{
    private static readonly DateOnly BusinessDate = new(2026, 9, 13);

    [SkippableFact]
    public async Task SyntheticVolumeIngestsAndReconcilesWithinBudget()
    {
        Skip.IfNot(sql.Available, sql.SkipReason);

        var rows = Environment.GetEnvironmentVariable("RECON_PERF_ROWS");
        Skip.If(string.IsNullOrWhiteSpace(rows),
            "RECON_PERF_ROWS is not set. Set it to 2000000 for the Phase 1 spike.");

        var rowCount = int.Parse(rows!, CultureInfo.InvariantCulture);

        using var connection = await sql.OpenAsync().ConfigureAwait(false);
        var ids = await TestConfiguration.CreateAsync(connection).ConfigureAwait(false);

        var config = new ConfigurationRepository(connection);
        var definition = await config.LoadDefinitionAsync(ids.DefinitionId).ConfigureAwait(false);
        var settings = await config.LoadSettingsAsync().ConfigureAwait(false);

        var batchSize = int.Parse(settings["BulkCopyBatchSize"], CultureInfo.InvariantCulture);
        var loadBudget = TimeSpan.FromSeconds(
            int.Parse(settings["StagingLoadBudgetSeconds"], CultureInfo.InvariantCulture));

        output.WriteLine($"rows per side       {rowCount:N0}");
        output.WriteLine($"bulk copy batch     {batchSize:N0}");
        output.WriteLine($"load budget         {loadBudget.TotalSeconds:N0}s");
        output.WriteLine(string.Empty);

        // ---- the indexes a real activation would create ----------------
        // A6: one nonclustered index per (dataset, field) appearing in a pass-1
        // or pass-2 condition, capped at 4 per dataset. Without them the spike
        // measures an unindexed scan and tells us nothing about production.
        await CreateMatchingIndexesAsync(connection, ids).ConfigureAwait(false);

        // ---- the run, created before the load ------------------------
        // Rows are staged under a run id, so the run has to exist first. The
        // earlier version invented an id and hit the self-referencing foreign
        // key on ops.ReconRun.SourceRunId.
        var runs = new RunRepository(connection);
        var run = await runs.CreateRunAsync(
            definition, BusinessDate, "PERF", RunType.Sandbox, "spike").ConfigureAwait(false);

        // ---- stage both sides -----------------------------------------
        var leftLoad = await StageAsync(connection, ids.LeftDatasetId, run.RunId, rowCount, batchSize,
            offset: 0).ConfigureAwait(false);

        output.WriteLine($"left  staged {leftLoad.RowsWritten:N0} rows in " +
                         $"{leftLoad.Elapsed.TotalSeconds:N1}s ({leftLoad.RowsPerSecond:N0} rows/s)");

        var rightLoad = await StageAsync(connection, ids.RightDatasetId, run.RunId, rowCount, batchSize,
            // 1% of the right side is shifted so it cannot match on reference,
            // which forces later passes to do real work rather than finding
            // everything in pass 1.
            offset: rowCount / 100).ConfigureAwait(false);

        output.WriteLine($"right staged {rightLoad.RowsWritten:N0} rows in " +
                         $"{rightLoad.Elapsed.TotalSeconds:N1}s ({rightLoad.RowsPerSecond:N0} rows/s)");
        output.WriteLine(string.Empty);

        // ---- reconcile -------------------------------------------------
        var stopwatch = Stopwatch.StartNew();
        var outcome = await new ReconciliationRunner(connection, runs)
            .ExecuteAsync(definition, run).ConfigureAwait(false);
        stopwatch.Stop();

        output.WriteLine($"run status  {outcome.Status}");
        output.WriteLine($"run elapsed {stopwatch.Elapsed.TotalSeconds:N1}s");
        output.WriteLine($"matched     {outcome.Counts.Matched:N0}");
        output.WriteLine($"unmatched   {outcome.Counts.Unmatched:N0}");
        output.WriteLine(string.Empty);

        var steps = await Db.QueryAsync(connection,
            """
            SELECT StepName, ISNULL(CAST(MatchRuleId AS VARCHAR(10)), '-') AS RuleId,
                   DATEDIFF(MILLISECOND, StartedAt, CompletedAt) AS Ms,
                   ISNULL(RowsProcessed, 0) AS Rows_
            FROM ops.ReconRunStep WHERE RunId = @run ORDER BY RunStepId;
            """,
            r => (Step: r.GetString(0), Rule: r.GetString(1), Ms: r.GetInt32(2), Rows: r.GetInt64(3)),
            c => c.With("@run", run.RunId)).ConfigureAwait(false);

        output.WriteLine("step timings");
        foreach (var step in steps)
        {
            output.WriteLine($"  {step.Step,-14} rule {step.Rule,-4} " +
                             $"{step.Ms / 1000.0,8:N1}s  {step.Rows,12:N0} rows");
        }

        output.WriteLine(string.Empty);
        output.WriteLine("match distribution");
        foreach (var (sequence, code, share) in outcome.Distribution())
        {
            output.WriteLine($"  {sequence}  {code,-16} {share,6:N2}%");
        }

        // ---- the budgets ----------------------------------------------
        // Reported before asserting, so a failure comes with its numbers.
        Assert.True(leftLoad.Elapsed <= loadBudget,
            $"left load took {leftLoad.Elapsed.TotalSeconds:N1}s " +
            $"({leftLoad.RowsPerSecond:N0} rows/s) against a {loadBudget.TotalSeconds:N0}s budget. " +
            "Raise cfg.PlatformSetting.StagingLoadBudgetSeconds if this box is slower than " +
            "production; if production itself misses the budget, the design's escalation path " +
            "(daily partitions + a persisted composite partition column + heap load + " +
            "SWITCH PARTITION) is the next step, not a code tweak.");

        Assert.True(rightLoad.Elapsed <= loadBudget,
            $"right load took {rightLoad.Elapsed.TotalSeconds:N1}s against a " +
            $"{loadBudget.TotalSeconds:N0}s budget.");

        var passBudget = TimeSpan.FromSeconds(60);
        foreach (var step in steps.Where(s => s.Step == "Match"))
        {
            Assert.True(TimeSpan.FromMilliseconds(step.Ms) <= passBudget,
                $"pass on rule {step.Rule} took {step.Ms / 1000.0:N1}s against a 60s budget.");
        }

        Assert.Equal(RunStatus.Completed, outcome.Status);

        // The spike is worthless if it reconciled nothing. The right side is
        // offset by 1%, so ~99% of each side must match on the reference and
        // the remainder must be genuinely unmatched.
        Assert.Equal(rowCount, outcome.Counts.LeftRows);
        Assert.Equal(rowCount, outcome.Counts.RightRows);
        Assert.True(outcome.Counts.Matched > rowCount * 1.9,
            $"only {outcome.Counts.Matched:N0} of {rowCount * 2:N0} rows matched; " +
            "the spike measured a reconciliation of nothing.");
        Assert.True(outcome.Counts.Unmatched > 0,
            "nothing was unmatched, so the later passes and classification did no work.");
    }

    /// <summary>
    /// The indexes the activation service would generate, written here because
    /// activation itself is Phase 3 work and the spike cannot wait for it.
    /// </summary>
    private static async Task CreateMatchingIndexesAsync(
        SqlConnection connection, TestConfiguration.Ids ids)
    {
        foreach (var (datasetId, name) in new[]
                 {
                     (ids.LeftDatasetId, "IX_Staging_Perf_L_Text1"),
                     (ids.RightDatasetId, "IX_Staging_Perf_R_Text1"),
                 })
        {
            // Filtered to the dataset, keyed as A6 specifies: (DatasetId,
            // slot, TxDate) with StagingId included.
            await Db.ExecuteAsync(connection,
                $"""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = '{name}')
                CREATE NONCLUSTERED INDEX {name}
                    ON stg.StagingTransaction (DatasetId, Text1, TxDate)
                    INCLUDE (StagingId, Num1)
                    ON ps_ByMonth(TxDate);
                """).ConfigureAwait(false);

            await Db.ExecuteAsync(connection,
                $"""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = '{name}_Norm')
                CREATE NONCLUSTERED INDEX {name}_Norm
                    ON stg.StagingTransaction (DatasetId, Text21, TxDate)
                    INCLUDE (StagingId, Num1)
                    ON ps_ByMonth(TxDate);
                """).ConfigureAwait(false);

            _ = datasetId;
        }
    }

    /// <summary>
    /// Generates and stages synthetic rows through the production loader and
    /// the production parser, so what is measured is the real path.
    /// </summary>
    private static async Task<StagingLoadResult> StageAsync(
        SqlConnection connection,
        int datasetId,
        long loadRunId,
        int rowCount,
        int batchSize,
        int offset)
    {
        var loader = new StagingBulkLoader(
            connection, new StagingLoadOptions { BatchSize = batchSize, TimeoutSeconds = 1800 });

        return await loader.LoadAsync(Records()).ConfigureAwait(false);

        IEnumerable<StagingRecord> Records()
        {
            var record = new StagingRecord();

            for (var i = 0; i < rowCount; i++)
            {
                // The reference carries the offset; everything else is derived
                // FROM the reference, so the same transaction has the same date
                // and amount on both sides.
                //
                // The earlier version derived the date from the row index
                // instead, so the offset shifted dates as well as references
                // and almost nothing fell inside the ±1-day matching window:
                // the spike measured a load and a reconciliation of nothing.
                var key = i + offset;
                var reference = "E2E-" + key.ToString("D10", CultureInfo.InvariantCulture);

                // A real session is one business date, with a small tail of
                // late arrivals from the day before — which is why the
                // matching window straddles the date at all (finding C5).
                var txDate = (key % 50 == 0) ? BusinessDate.AddDays(-1) : BusinessDate;

                record.Reset();
                record.DatasetId = datasetId;
                record.LoadRunId = loadRunId;
                record.TxDate = txDate;
                record.Text[0] = reference;
                record.Text[4] = "JOD";
                record.Text[5] = (key % 2 == 0) ? "Inward" : "Outward";
                record.Text[6] = "ACSC";
                record.Text[20] = Transforms.Normalize(reference);
                // Unique per transaction. A repeating amount lets the
                // composite pass (amount + date) pair unrelated rows, so the
                // 1% that cannot match on reference would appear matched and
                // the later passes would look cheaper than they are.
                record.Num[0] = 1_000L + key;
                record.Date[0] = txDate.ToDateTime(new TimeOnly(12, 0));

                yield return record;
            }
        }
    }
}
