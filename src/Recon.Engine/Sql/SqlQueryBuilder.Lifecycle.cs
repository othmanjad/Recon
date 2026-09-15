using System.Globalization;
using System.Text;
using Recon.Domain.Configuration;

namespace Recon.Engine.Sql;

/// <summary>
/// The statements that bracket matching: exclusions and duplicate detection
/// before pass 1, then the single staging update, classification, run
/// aggregates and control totals after the last pass.
/// </summary>
public static class SqlQueryBuilderLifecycle
{
    /// <summary>
    /// Re-opens the staged rows a previous run already judged.
    ///
    /// <para>
    /// Only a run that reads another run's rows needs this — a Rematch or a
    /// Sandbox dry-run. Those rows carry the source run's verdict in
    /// staging's four cache columns, and every statement before pass 1
    /// filters on <c>MatchStatus = 'Unmatched'</c>: without the reset, a
    /// replay excluded nothing, found no duplicates and matched nothing,
    /// while the stale cache made the run's aggregates look like a complete
    /// success. A dry-run reporting a perfect match rate for rules that
    /// matched nothing is worse than no dry-run at all.
    /// </para>
    ///
    /// <para>
    /// This does not lose the earlier run's results. <c>ops.MatchResult</c>
    /// is partitioned per run and never rewritten, and
    /// <c>ops.RunAggregate</c> holds that run's totals; staging's cache is
    /// explicitly the most recent run's, which after this is ours.
    /// </para>
    ///
    /// <para>
    /// The predicate touches only rows that actually carry a verdict, so a
    /// replay over a freshly staged day writes nothing.
    /// </para>
    /// </summary>
    public static CompiledStatement CompileWorkingSetReset(
        Dataset dataset,
        long runId,
        long stagingRunId,
        DateOnly windowFrom,
        DateOnly windowTo)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        var p = new SqlParameterBag();
        var sql = new StringBuilder();

        var range = SqlQueryBuilder.StagingRange("S", dataset.DatasetId, stagingRunId,
            windowFrom, windowTo, p);

        var run = p.Add("@RunId", runId);

        sql.AppendLine("SET NOCOUNT ON;");
        sql.AppendLine();
        sql.AppendLine("UPDATE S");
        sql.AppendLine("SET MatchStatus = 'Unmatched',");
        sql.AppendLine("    ExceptionCode = NULL,");
        sql.AppendLine("    MatchedWithId = NULL,");
        sql.AppendLine("    MatchedByRuleId = NULL,");
        sql.AppendLine(CultureInfo.InvariantCulture, $"    ResultRunId = {run}");
        sql.AppendLine("FROM stg.StagingTransaction AS S");
        sql.AppendLine("WHERE " + range);
        sql.AppendLine("  AND (S.MatchStatus <> 'Unmatched'");
        sql.AppendLine("       OR S.ExceptionCode IS NOT NULL");
        sql.AppendLine("       OR S.MatchedWithId IS NOT NULL");
        sql.AppendLine("       OR S.MatchedByRuleId IS NOT NULL);");
        sql.AppendLine();
        sql.AppendLine("SELECT CAST(@@ROWCOUNT AS BIGINT) AS Reopened_;");

        return new CompiledStatement(sql.ToString(), p);
    }

    /// <summary>
    /// Rows that must never match leave the working set before pass 1 and are
    /// reported separately (finding C6). They never generate exceptions: a
    /// rejected transaction is not a break.
    ///
    /// <para>
    /// This is one of only two statements in the engine that write
    /// <c>MatchStatus</c> on staging, and it runs before any pass — so the
    /// invariant that passes never touch staging (A3) still holds.
    /// </para>
    /// </summary>
    public static CompiledStatement CompileExclusions(
        Dataset dataset,
        IReadOnlyList<ExclusionRule> rules,
        long stagingRunId,
        DateOnly windowFrom,
        DateOnly windowTo)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(rules);

        var active = rules.Where(r => r.IsActive && r.DatasetId == dataset.DatasetId).ToList();
        var p = new SqlParameterBag();
        var sql = new StringBuilder();

        sql.AppendLine("SET NOCOUNT ON;");

        if (active.Count == 0)
        {
            sql.AppendLine("SELECT CAST(0 AS BIGINT) AS Excluded_;");
            return new CompiledStatement(sql.ToString(), p);
        }

        var range = SqlQueryBuilder.StagingRange("S", dataset.DatasetId, stagingRunId,
            windowFrom, windowTo, p);

        // One statement per rule so that each reason code is attributable; the
        // alternative is a single CASE that loses which rule fired.
        foreach (var rule in active)
        {
            var condition = SqlQueryBuilder.CompileCondition(rule.Condition, dataset, "S", p);
            var reason = p.Add(rule.ReasonCode);

            sql.AppendLine();
            sql.AppendLine("UPDATE S SET MatchStatus = 'Excluded', ExceptionCode = " + reason);
            sql.AppendLine("FROM stg.StagingTransaction AS S");
            sql.AppendLine("WHERE " + range);
            sql.AppendLine("  AND S.MatchStatus = 'Unmatched'");
            sql.AppendLine("  AND " + condition + ";");
        }

        sql.AppendLine();
        sql.AppendLine("SELECT COUNT_BIG(*) AS Excluded_");
        sql.AppendLine("FROM stg.StagingTransaction AS S");
        sql.AppendLine("WHERE " + range + " AND S.MatchStatus = 'Excluded';");

        return new CompiledStatement(sql.ToString(), p);
    }

    /// <summary>
    /// Flags second and later occurrences of the dataset's declared key
    /// (finding C3).
    ///
    /// <para>
    /// Without this, a file containing the same reference twice matches one row
    /// and orphans the other — or produces an Ambiguous on the far side — with
    /// no signal that the SOURCE was the problem. The first occurrence stays in
    /// the working set; the rest are <c>Duplicate</c> and never enter matching.
    /// </para>
    /// </summary>
    public static CompiledStatement CompileDuplicateDetection(
        Dataset dataset,
        long stagingRunId,
        DateOnly windowFrom,
        DateOnly windowTo)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        var key = dataset.DuplicateKey();
        var p = new SqlParameterBag();
        var sql = new StringBuilder();

        sql.AppendLine("SET NOCOUNT ON;");

        if (key.Count == 0)
        {
            // No declared key is a legitimate configuration, not an error: a
            // summary dataset has no per-transaction reference.
            sql.AppendLine("SELECT CAST(0 AS BIGINT) AS Duplicates_;");
            return new CompiledStatement(sql.ToString(), p);
        }

        var range = SqlQueryBuilder.StagingRange("S", dataset.DatasetId, stagingRunId,
            windowFrom, windowTo, p);

        var keyColumns = string.Join(", ",
            key.Select(f => "S." + SqlQueryBuilder.ResolveSlot(dataset, f.FieldCode, requireMatchable: false)));

        var reason = p.Add("DUPLICATE_IN_SOURCE");

        sql.AppendLine();
        sql.AppendLine("WITH ranked AS (");
        sql.AppendLine("    SELECT S.StagingId, S.TxDate,");
        sql.AppendLine("           ROW_NUMBER() OVER (PARTITION BY " + keyColumns);
        // Earliest row number wins, so the duplicate flagged is the later
        // arrival in the file rather than an arbitrary one.
        sql.AppendLine("                              ORDER BY S.RawRowNumber, S.StagingId) AS rn");
        sql.AppendLine("    FROM stg.StagingTransaction AS S");
        sql.AppendLine("    WHERE " + range);
        sql.AppendLine("      AND S.MatchStatus = 'Unmatched'");
        sql.AppendLine(")");
        sql.AppendLine("UPDATE S SET MatchStatus = 'Duplicate', ExceptionCode = " + reason);
        sql.AppendLine("FROM stg.StagingTransaction AS S");
        sql.AppendLine("JOIN ranked AS r ON r.StagingId = S.StagingId AND r.TxDate = S.TxDate");
        sql.AppendLine("WHERE r.rn > 1;");
        sql.AppendLine();
        sql.AppendLine("SELECT COUNT_BIG(*) AS Duplicates_");
        sql.AppendLine("FROM stg.StagingTransaction AS S");
        sql.AppendLine("WHERE " + range + " AND S.MatchStatus = 'Duplicate';");

        return new CompiledStatement(sql.ToString(), p);
    }

    /// <summary>
    /// The single set-based update that stamps the run's outcome onto staging,
    /// once, at the end (blocker A3).
    ///
    /// <para>
    /// v0.1 updated <c>MatchStatus</c> in every pass: 2M rows × 4 passes is 8M
    /// row writes plus filtered-index churn, and a failed pass left staging
    /// half-updated. Now the passes write only to <c>ops.MatchResult</c> and
    /// this runs once.
    /// </para>
    ///
    /// <para>
    /// <c>ResultRunId</c> records WHICH run produced these values. Without it,
    /// a Rematch writing its outcome onto rows owned by the source run
    /// destroyed that run's row-level results while the design claimed they
    /// survived (review finding 1). The authoritative per-run record remains
    /// <c>ops.MatchResult</c>; this is a cache for fast filtering and
    /// reporting.
    /// </para>
    /// </summary>
    public static CompiledStatement CompileFinalizeStaging(
        ReconciliationDefinition definition,
        long runId,
        long stagingRunId,
        DateOnly windowFrom,
        DateOnly windowTo)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var p = new SqlParameterBag();
        var sql = new StringBuilder();
        var run = p.Add("@RunId", runId);

        sql.AppendLine("SET NOCOUNT ON;");

        foreach (var side in new[] { Side.Left, Side.Right })
        {
            var dataset = definition.DatasetFor(side);
            var range = SqlQueryBuilder.StagingRange("S", dataset.DatasetId, stagingRunId,
                windowFrom, windowTo, p);

            var idColumn = side == Side.Left ? "LeftStagingId" : "RightStagingId";
            var otherColumn = side == Side.Left ? "RightStagingId" : "LeftStagingId";

            sql.AppendLine();
            sql.AppendLine(CultureInfo.InvariantCulture, $"/* {side} side: {dataset.Code} */");
            sql.AppendLine("UPDATE S SET");
            sql.AppendLine(CultureInfo.InvariantCulture, $"    ResultRunId = {run},");
            // A row with several results (an Ambiguous pass) takes the worst
            // outcome, not whichever the engine happened to read last.
            sql.AppendLine("    MatchStatus = CASE");
            sql.AppendLine("        WHEN best.Ambiguous_ > 0 THEN 'Ambiguous'");
            sql.AppendLine("        WHEN best.Matched_ > 0 THEN 'Matched'");
            sql.AppendLine("        ELSE S.MatchStatus END,");
            sql.AppendLine("    MatchedWithId = best.CounterpartId,");
            sql.AppendLine("    MatchedByRuleId = best.MatchRuleId");
            sql.AppendLine("FROM stg.StagingTransaction AS S");
            sql.AppendLine("CROSS APPLY (");
            sql.AppendLine("    SELECT");
            sql.AppendLine("        SUM(CASE WHEN M.MatchStatus = 'Ambiguous' THEN 1 ELSE 0 END) AS Ambiguous_,");
            sql.AppendLine("        SUM(CASE WHEN M.MatchStatus IN ('Matched','AmountDifference') THEN 1 ELSE 0 END) AS Matched_,");
            sql.AppendLine(CultureInfo.InvariantCulture, $"        MIN(M.{otherColumn}) AS CounterpartId,");
            sql.AppendLine("        MIN(M.MatchRuleId) AS MatchRuleId");
            sql.AppendLine("    FROM ops.MatchResult AS M");
            sql.AppendLine(CultureInfo.InvariantCulture, $"    WHERE M.RunId = {run} AND M.{idColumn} = S.StagingId");
            sql.AppendLine(") AS best");
            sql.AppendLine("WHERE " + range);
            // Excluded and Duplicate rows never entered matching, so their
            // status must not be overwritten.
            sql.AppendLine("  AND S.MatchStatus NOT IN ('Excluded','Duplicate');");
        }

        return new CompiledStatement(sql.ToString(), p);
    }

    /// <summary>
    /// Turns each still-unmatched row into a classified exception (§10).
    ///
    /// <para>
    /// Rules are evaluated in sequence and the first match wins, so ordering is
    /// meaningful configuration. <c>KeyValuesJson</c> snapshots the row's
    /// matchable field values (finding C2) because <c>StagingId</c> points into
    /// staging, which has a far shorter retention than exceptions do — without
    /// the snapshot, late-arrival auto-close breaks as soon as staging is
    /// archived and the open-items list grows forever.
    /// </para>
    /// </summary>
    public static CompiledStatement CompileClassification(
        ReconciliationDefinition definition,
        Side side,
        long runId,
        long stagingRunId,
        DateOnly businessDate,
        DateOnly windowFrom,
        DateOnly windowTo)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var dataset = definition.DatasetFor(side);
        var rules = definition.Classifications
            .Where(c => c.IsActive && c.AppliesToSide.Covers(side))
            .OrderBy(c => c.Sequence)
            .ToList();

        var p = new SqlParameterBag();
        var sql = new StringBuilder();

        var run = p.Add("@RunId", runId);
        var def = p.Add("@DefinitionId", definition.DefinitionId);
        var bd = p.Add("@BusinessDate", businessDate.ToDateTime(TimeOnly.MinValue));
        var sideName = p.Add("@Side", side.ToString());

        sql.AppendLine("SET NOCOUNT ON;");

        if (rules.Count == 0)
        {
            sql.AppendLine("SELECT CAST(0 AS BIGINT) AS Classified_;");
            return new CompiledStatement(sql.ToString(), p);
        }

        var range = SqlQueryBuilder.StagingRange("S", dataset.DatasetId, stagingRunId,
            windowFrom, windowTo, p);

        var amount = dataset.FieldWithRole(FieldRole.Amount);
        var amountColumn = amount is null
            ? "CAST(NULL AS BIGINT)"
            : "S." + SqlQueryBuilder.ResolveSlot(dataset, amount.FieldCode, requireMatchable: false);

        var currency = dataset.FieldWithRole(FieldRole.Currency);
        var currencyColumn = currency is null
            ? (dataset.DefaultCurrency is null
                ? "CAST(NULL AS CHAR(3))"
                : "CAST(" + p.Add(dataset.DefaultCurrency) + " AS CHAR(3))")
            : "CAST(S." + SqlQueryBuilder.ResolveSlot(dataset, currency.FieldCode, requireMatchable: false) + " AS CHAR(3))";

        // The key snapshot: every matchable field, as a JSON object. This is
        // what makes late-arrival auto-close independent of staging retention.
        var matchable = dataset.Matchable.ToList();
        var keyJson = matchable.Count == 0
            ? "CAST(NULL AS NVARCHAR(MAX))"
            : "(SELECT " + string.Join(", ", matchable.Select(f =>
                  "S." + SqlQueryBuilder.ResolveSlot(dataset, f.FieldCode) +
                  " AS [" + Escape(f.FieldCode) + "]")) +
              " FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)";

        sql.AppendLine();
        sql.AppendLine("INSERT ops.ReconException");
        sql.AppendLine("    (RunId, DefinitionId, BusinessDate, StagingId, Side, ExceptionCode,");
        sql.AppendLine("     AmountMinor, CurrencyCode, KeyValuesJson)");
        sql.AppendLine("SELECT");
        sql.AppendLine(CultureInfo.InvariantCulture, $"    {run}, {def}, {bd}, S.StagingId, {sideName}, x.ExceptionCode,");
        sql.AppendLine(CultureInfo.InvariantCulture, $"    {amountColumn}, {currencyColumn}, {keyJson}");
        sql.AppendLine("FROM stg.StagingTransaction AS S");
        sql.AppendLine("CROSS APPLY (");
        sql.AppendLine("    SELECT TOP (1) v.ExceptionCode");
        sql.AppendLine("    FROM (VALUES");

        var rows = new List<string>();
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            var condition = SqlQueryBuilder.CompileCondition(rule.Condition, dataset, "S", p);
            var code = p.Add(rule.ExceptionCode);
            rows.Add($"        ({i}, {code}, CASE WHEN {condition} THEN 1 ELSE 0 END)");
        }

        sql.AppendLine(string.Join(",\n", rows));
        sql.AppendLine("    ) AS v(Seq, ExceptionCode, Applies)");
        sql.AppendLine("    WHERE v.Applies = 1");
        sql.AppendLine("    ORDER BY v.Seq");
        sql.AppendLine(") AS x");
        sql.AppendLine("WHERE " + range);
        sql.AppendLine("  AND S.MatchStatus = 'Unmatched'");
        // A row already carrying an exception from this run is not classified
        // twice; this makes the step idempotent, which Resume depends on (B3).
        sql.AppendLine("  AND NOT EXISTS (SELECT 1 FROM ops.ReconException AS E");
        sql.AppendLine(CultureInfo.InvariantCulture, $"                  WHERE E.RunId = {run} AND E.StagingId = S.StagingId);");
        sql.AppendLine();
        sql.AppendLine("SELECT COUNT_BIG(*) AS Classified_ FROM ops.ReconException");
        sql.AppendLine(CultureInfo.InvariantCulture, $"WHERE RunId = {run} AND Side = {sideName};");

        return new CompiledStatement(sql.ToString(), p);
    }

    /// <summary>
    /// A field code reaching a SQL identifier or a JSON key. Codes come from
    /// the registry and the schema constrains their characters, but this is
    /// text being concatenated rather than parameterized, so it is escaped
    /// rather than trusted.
    /// </summary>
    private static string Escape(string identifier)
    {
        foreach (var ch in identifier)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '_')
            {
                throw new SqlCompilationException(
                    $"field code '{identifier}' contains a character that cannot appear in an identifier");
            }
        }

        return identifier;
    }
}
