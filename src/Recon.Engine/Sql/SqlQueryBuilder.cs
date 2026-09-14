using System.Text;
using System.Text.Json;
using Recon.Domain.Conditions;
using Recon.Domain.Configuration;

namespace Recon.Engine.Sql;

/// <summary>
/// <b>The single class that emits SQL.</b> Nothing else in the solution builds
/// a statement, and that is deliberate (§9.4): one place to review, one place
/// to audit, one place where the registry-only rule is enforced.
///
/// <para>
/// Two invariants hold for every method here:
/// </para>
/// <list type="number">
/// <item>
/// A column name reaching the SQL text comes from
/// <see cref="DatasetField.StorageSlot"/> — resolved through the dataset's
/// field registry and then validated against the slot whitelist. A field code
/// that is not in the registry, or is in it but not matchable, throws before
/// any text is produced.
/// </item>
/// <item>
/// A value reaching the SQL text does not: values go to
/// <see cref="SqlParameterBag"/> and appear as <c>@p0</c>, <c>@p1</c>. There is
/// no code path in this class that interpolates a user value.
/// </item>
/// </list>
/// </summary>
public static class SqlQueryBuilder
{
    /// <summary>
    /// The physical slot columns of <c>stg.StagingTransaction</c>. A resolved
    /// slot is checked against this set before it is written into SQL — a
    /// belt-and-braces guard so that even a corrupted registry row cannot put
    /// arbitrary text into a statement.
    /// </summary>
    private static readonly HashSet<string> SlotWhitelist = BuildSlotWhitelist();

    private static HashSet<string> BuildSlotWhitelist()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i <= 30; i++) { set.Add("Text" + i); }
        for (var i = 1; i <= 15; i++) { set.Add("Num" + i); }
        for (var i = 1; i <= 5; i++) { set.Add("Dec" + i); }
        for (var i = 1; i <= 8; i++) { set.Add("Date" + i); }
        for (var i = 1; i <= 5; i++) { set.Add("Flag" + i); }
        return set;
    }

    /// <summary>
    /// Resolves a field code to its physical column, enforcing both registry
    /// membership and the matchable flag. Every other method goes through this.
    /// </summary>
    public static string ResolveSlot(
        Dataset dataset,
        string fieldCode,
        bool useNormalized = false,
        bool requireMatchable = true)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        if (!dataset.TryGetField(fieldCode, out var field))
        {
            throw new FieldNotInRegistryException(fieldCode, dataset.Code);
        }

        if (requireMatchable && !field.IsMatchable)
        {
            throw new FieldNotMatchableException(fieldCode, dataset.Code);
        }

        var slot = field.SlotFor(useNormalized);

        if (!SlotWhitelist.Contains(slot))
        {
            // Unreachable through the portal; reachable if a registry row were
            // tampered with directly in the database.
            throw new SqlCompilationException(
                $"Field {fieldCode} of dataset {dataset.Code} names storage slot '{slot}', " +
                "which is not a column of stg.StagingTransaction.");
        }

        return slot;
    }

    /// <summary>Qualified slot reference, e.g. <c>L.Text1</c>.</summary>
    public static string Column(string alias, string slot) => alias + "." + slot;

    // =================================================================
    // Condition trees  (rule filters, exclusions, classifications,
    //                   source filters, report filters — all of them)
    // =================================================================

    /// <summary>
    /// Compiles a condition tree to a SQL boolean expression.
    ///
    /// The tree must already have passed
    /// <see cref="ConditionValidator"/>; this method validates again anyway,
    /// because "the caller validated it" is not a property the compiler can
    /// verify and this is the last gate before text becomes a statement.
    /// </summary>
    public static string CompileCondition(
        ConditionNode? node,
        Dataset dataset,
        string alias,
        SqlParameterBag parameters)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(parameters);

        if (node is null)
        {
            return "1 = 1";
        }

        ConditionValidator.Validate(node, dataset).ThrowIfInvalid("filter");

        return CompileNode(node, dataset, alias, parameters, 1);
    }

    private static string CompileNode(
        ConditionNode node,
        Dataset dataset,
        string alias,
        SqlParameterBag parameters,
        int depth)
    {
        if (depth > ConditionValidator.MaxDepth)
        {
            throw new SqlCompilationException(
                $"condition tree nested deeper than {ConditionValidator.MaxDepth} levels");
        }

        if (node.IsGroup)
        {
            var joiner = node.Op == "or" ? " OR " : " AND ";
            var parts = node.Items!
                .Select(child => CompileNode(child, dataset, alias, parameters, depth + 1))
                .ToList();

            return "(" + string.Join(joiner, parts) + ")";
        }

        var field = dataset.GetField(node.Field!);
        if (!field.IsMatchable)
        {
            throw new FieldNotMatchableException(field.FieldCode, dataset.Code);
        }

        var column = Column(alias, ResolveSlot(dataset, field.FieldCode));

        if (!ConditionOperators.TryParse(node.Cmp, out var op))
        {
            throw new SqlCompilationException($"unknown comparator '{node.Cmp}'");
        }

        switch (op)
        {
            case ConditionOperator.IsNull:
                return $"{column} IS NULL";

            case ConditionOperator.IsNotNull:
                return $"{column} IS NOT NULL";

            case ConditionOperator.In:
            {
                var names = node.Value!.Value.EnumerateArray()
                    .Select(v => parameters.AddJson(v, field.DataType))
                    .ToList();

                if (names.Count == 0)
                {
                    throw new SqlCompilationException($"'in' on {field.FieldCode} has no values");
                }

                return $"{column} IN ({string.Join(", ", names)})";
            }

            case ConditionOperator.Ne:
            {
                // NULL <> 'x' is UNKNOWN, so a plain <> silently drops rows
                // whose value is absent. For a reconciliation filter that is
                // almost never what Operations means by "not equal to RJCT":
                // a row with no status has not been rejected.
                var p = parameters.AddJson(node.Value!.Value, field.DataType);
                return $"({column} IS NULL OR {column} <> {p})";
            }

            default:
            {
                var p = parameters.AddJson(node.Value!.Value, field.DataType);
                return $"{column} {op.SqlOperator()} {p}";
            }
        }
    }

    // =================================================================
    // Staging range
    // =================================================================

    /// <summary>
    /// The WHERE clause selecting one dataset's staged rows for a run.
    ///
    /// <para>
    /// <c>LoadRunId</c>, not the executing run id: a Rematch reuses another
    /// run's staged rows, so the run that loaded the data and the run doing the
    /// matching are different. The caller passes
    /// <c>ops.ReconRun.StagingRunId</c>, which the schema computes as
    /// <c>ISNULL(SourceRunId, RunId)</c> — the rule review finding 1 added
    /// because its absence made every staging query return zero rows on a
    /// Rematch.
    /// </para>
    ///
    /// <para>
    /// <c>DatasetId</c> leads the clustered key and <c>TxDate</c> is the
    /// partition column, so this predicate is a partition-eliminated index
    /// seek rather than a scan.
    /// </para>
    /// </summary>
    public static string StagingRange(
        string alias,
        int datasetId,
        long stagingRunId,
        DateOnly windowFrom,
        DateOnly windowTo,
        SqlParameterBag parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var ds = parameters.Add(datasetId);
        var run = parameters.Add(stagingRunId);
        var from = parameters.Add(windowFrom.ToDateTime(TimeOnly.MinValue));
        var to = parameters.Add(windowTo.ToDateTime(TimeOnly.MinValue));

        return $"{alias}.DatasetId = {ds} AND {alias}.LoadRunId = {run} " +
               $"AND {alias}.TxDate >= {from} AND {alias}.TxDate <= {to}";
    }

    // =================================================================
    // Match passes
    // =================================================================

    /// <summary>
    /// Compiles one pass of a rule set into the statement that populates
    /// <c>ops.MatchResult</c>.
    ///
    /// <para>Three properties of the generated SQL are load-bearing:</para>
    /// <list type="bullet">
    /// <item>
    /// <b>It writes only to MatchResult.</b> Staging is untouched until the end
    /// of the run (blocker A3), so a failed pass is retried by deleting its
    /// result rows and nothing is left half-updated.
    /// </item>
    /// <item>
    /// <b>"Still unmatched" is an anti-join</b> against this run's existing
    /// results, not a status column read.
    /// </item>
    /// <item>
    /// <b>Candidates are materialised and counted</b> before anything is
    /// written (finding A4). A composite pass on 2M rows can produce
    /// many-to-many candidate sets; counting per side first is what stops the
    /// explosion and is what makes <see cref="OnMultipleMatch"/> meaningful.
    /// The compiler never <c>UPDATE ... FROM</c> a join.
    /// </item>
    /// </list>
    /// </summary>
    public static CompiledStatement CompileRowPass(PassContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var rule = ctx.Rule;
        if (rule.Mode != MatchMode.Row)
        {
            throw new SqlCompilationException(
                $"rule {rule.RuleCode} is {rule.Mode} mode; use CompileAggregatePass");
        }

        if (rule.Conditions.Count == 0)
        {
            throw new SqlCompilationException(
                $"rule {rule.RuleCode} has no conditions, so it would match every row against every row");
        }

        var p = new SqlParameterBag();
        var sql = new StringBuilder();

        var runId = p.Add("@RunId", ctx.RunId);
        var ruleId = p.Add("@MatchRuleId", rule.MatchRuleId);
        var businessDate = p.Add("@BusinessDate", ctx.BusinessDate.ToDateTime(TimeOnly.MinValue));

        var leftRange = StagingRange("L", ctx.Definition.Left.DatasetId, ctx.StagingRunId,
            ctx.WindowFrom, ctx.WindowTo, p);
        var rightRange = StagingRange("R", ctx.Definition.Right.DatasetId, ctx.StagingRunId,
            ctx.WindowFrom, ctx.WindowTo, p);

        var leftFilter = CompileCondition(rule.LeftFilter, ctx.Definition.Left, "L", p);
        var rightFilter = CompileCondition(rule.RightFilter, ctx.Definition.Right, "R", p);

        var joinPredicate = CompileJoin(rule, ctx.Definition, p);

        sql.AppendLine("SET NOCOUNT ON;");
        sql.AppendLine();
        sql.AppendLine("/* Pass " + rule.Sequence + " — " + Comment(rule.RuleCode) + " */");
        sql.AppendLine("DROP TABLE IF EXISTS #candidate;");
        sql.AppendLine();
        sql.AppendLine("SELECT");
        sql.AppendLine("    L.StagingId  AS LeftStagingId,");
        sql.AppendLine("    R.StagingId  AS RightStagingId,");
        sql.AppendLine("    L.TxDate     AS LeftTxDate,");
        // The amount difference is computed on the integer minor-unit slots, or
        // is NULL when either side has no Amount-role field.
        sql.AppendLine("    " + AmountDifferenceExpression(ctx.Definition) + " AS AmountDiffMinor,");
        sql.AppendLine("    COUNT(*) OVER (PARTITION BY L.StagingId) AS LeftCandidates,");
        sql.AppendLine("    COUNT(*) OVER (PARTITION BY R.StagingId) AS RightCandidates");
        sql.AppendLine("INTO #candidate");
        sql.AppendLine("FROM stg.StagingTransaction AS L");
        sql.AppendLine("JOIN stg.StagingTransaction AS R");
        sql.AppendLine("    ON " + joinPredicate);
        sql.AppendLine("WHERE " + leftRange);
        sql.AppendLine("  AND " + rightRange);
        sql.AppendLine("  AND " + leftFilter);
        sql.AppendLine("  AND " + rightFilter);
        // Anti-join: rows already matched by an earlier pass of THIS run are
        // out of the working set. Supported by IX_MatchResult_RunLeft/Right.
        sql.AppendLine("  AND NOT EXISTS (SELECT 1 FROM ops.MatchResult AS M");
        sql.AppendLine($"                  WHERE M.RunId = {runId} AND M.LeftStagingId = L.StagingId)");
        sql.AppendLine("  AND NOT EXISTS (SELECT 1 FROM ops.MatchResult AS M");
        sql.AppendLine($"                  WHERE M.RunId = {runId} AND M.RightStagingId = R.StagingId);");
        sql.AppendLine();

        AppendResolution(sql, rule, runId, ruleId, businessDate);

        sql.AppendLine();
        sql.AppendLine("SELECT");
        sql.AppendLine("    SUM(CASE WHEN LeftCandidates = 1 AND RightCandidates = 1 THEN 1 ELSE 0 END) AS Unique_,");
        sql.AppendLine("    SUM(CASE WHEN LeftCandidates > 1 OR  RightCandidates > 1 THEN 1 ELSE 0 END) AS Ambiguous_,");
        sql.AppendLine("    COUNT(*) AS Candidates_");
        sql.AppendLine("FROM #candidate;");
        sql.AppendLine();
        sql.AppendLine("DROP TABLE IF EXISTS #candidate;");

        return new CompiledStatement(sql.ToString(), p);
    }

    /// <summary>
    /// Resolution per <see cref="OnMultipleMatch"/>. A rule matching several
    /// candidates must never silently pick one (§9.5): at 2M rows a day
    /// duplicate references are certain, and a silent choice is a wrong number
    /// nobody can trace.
    /// </summary>
    private static void AppendResolution(
        StringBuilder sql,
        MatchRule rule,
        string runId,
        string ruleId,
        string businessDate)
    {
        if (rule.OnMultipleMatch == OnMultipleMatch.Fail)
        {
            sql.AppendLine("IF EXISTS (SELECT 1 FROM #candidate WHERE LeftCandidates > 1 OR RightCandidates > 1)");
            sql.AppendLine("BEGIN");
            sql.AppendLine("    THROW 52000, 'Pass produced multiple candidates and the rule is configured to fail.', 1;");
            sql.AppendLine("END");
            sql.AppendLine();
        }

        sql.AppendLine("INSERT ops.MatchResult");
        sql.AppendLine("    (RunId, BusinessDate, LeftStagingId, RightStagingId, MatchRuleId,");
        sql.AppendLine("     MatchStatus, AmountDiffMinor, CandidateCount)");
        sql.AppendLine("SELECT");
        sql.AppendLine($"    {runId}, {businessDate}, c.LeftStagingId, c.RightStagingId, {ruleId},");

        switch (rule.OnMultipleMatch)
        {
            case OnMultipleMatch.MarkAmbiguous:
                // An amount difference on an otherwise unique match is its own
                // outcome, not a match: it becomes AMOUNT_DIFFERENCE at
                // classification time (the OFF-US requirement in §10).
                sql.AppendLine("    CASE WHEN c.LeftCandidates > 1 OR c.RightCandidates > 1 THEN 'Ambiguous'");
                sql.AppendLine("         WHEN c.AmountDiffMinor IS NOT NULL AND c.AmountDiffMinor <> 0 THEN 'AmountDifference'");
                sql.AppendLine("         ELSE 'Matched' END,");
                sql.AppendLine("    c.AmountDiffMinor,");
                sql.AppendLine("    CASE WHEN c.LeftCandidates > c.RightCandidates THEN c.LeftCandidates");
                sql.AppendLine("         ELSE c.RightCandidates END");
                sql.AppendLine("FROM #candidate AS c;");
                break;

            case OnMultipleMatch.TakeEarliest:
                sql.AppendLine("    CASE WHEN c.AmountDiffMinor IS NOT NULL AND c.AmountDiffMinor <> 0");
                sql.AppendLine("         THEN 'AmountDifference' ELSE 'Matched' END,");
                sql.AppendLine("    c.AmountDiffMinor,");
                sql.AppendLine("    c.LeftCandidates");
                sql.AppendLine("FROM (");
                sql.AppendLine("    SELECT *, ROW_NUMBER() OVER (PARTITION BY LeftStagingId");
                sql.AppendLine("                                 ORDER BY LeftTxDate, RightStagingId) AS rn");
                sql.AppendLine("    FROM #candidate");
                sql.AppendLine(") AS c");
                sql.AppendLine("WHERE c.rn = 1;");
                break;

            case OnMultipleMatch.Fail:
                sql.AppendLine("    CASE WHEN c.AmountDiffMinor IS NOT NULL AND c.AmountDiffMinor <> 0");
                sql.AppendLine("         THEN 'AmountDifference' ELSE 'Matched' END,");
                sql.AppendLine("    c.AmountDiffMinor,");
                sql.AppendLine("    1");
                sql.AppendLine("FROM #candidate AS c;");
                break;

            default:
                throw new SqlCompilationException($"unhandled OnMultipleMatch {rule.OnMultipleMatch}");
        }
    }

    /// <summary>The join predicate: one clause per field pair the user chose.</summary>
    private static string CompileJoin(
        MatchRule rule,
        ReconciliationDefinition definition,
        SqlParameterBag parameters)
    {
        var clauses = new List<string>();

        foreach (var c in rule.Conditions.OrderBy(c => c.Sequence))
        {
            var leftSlot = ResolveSlot(definition.Left, c.LeftFieldCode, c.UseNormalized);
            var rightSlot = ResolveSlot(definition.Right, c.RightFieldCode, c.UseNormalized);

            var l = Column("L", leftSlot);
            var r = Column("R", rightSlot);

            clauses.Add(c.Comparison switch
            {
                ComparisonType.Exact or ComparisonType.NumericExact or ComparisonType.DateExact
                    when c.Comparison != ComparisonType.DateExact => $"{l} = {r}",

                // Same calendar date, not the same instant.
                ComparisonType.DateExact => $"CAST({l} AS DATE) = CAST({r} AS DATE)",

                ComparisonType.NumericTolerance => NumericTolerance(l, r, c, parameters),
                ComparisonType.DateWithin => DateWithin(l, r, c, parameters),

                // Non-sargable by nature. Permitted, because a late pass over a
                // small remainder is a legitimate use; the UI warns and
                // ActivationProblems() blocks them in pass 1.
                ComparisonType.StartsWith => $"{l} LIKE {r} + N'%'",
                ComparisonType.EndsWith => $"{l} LIKE N'%' + {r}",
                ComparisonType.Contains => $"CHARINDEX({r}, {l}) > 0",

                _ => throw new SqlCompilationException(
                    $"unhandled comparison {c.Comparison} on {c.LeftFieldCode}"),
            });
        }

        return string.Join("\n       AND ", clauses);
    }

    private static string NumericTolerance(
        string l, string r, MatchCondition c, SqlParameterBag parameters)
    {
        if (c.ToleranceValue is null)
        {
            throw new SqlCompilationException(
                $"{c.LeftFieldCode}: NumericTolerance needs a tolerance value");
        }

        // Written as a BETWEEN on the left column so the optimizer can seek a
        // range rather than evaluate ABS(l - r) per row.
        var tol = parameters.Add(c.ToleranceValue.Value);
        return $"{l} BETWEEN {r} - {tol} AND {r} + {tol}";
    }

    private static string DateWithin(
        string l, string r, MatchCondition c, SqlParameterBag parameters)
    {
        if (c.ToleranceValue is null || c.ToleranceUnit is null)
        {
            throw new SqlCompilationException(
                $"{c.LeftFieldCode}: DateWithin needs a tolerance value and unit");
        }

        // DATEADD's unit is part of the statement's shape, not a value, so it
        // is chosen from a closed enum rather than parameterized.
        var unit = c.ToleranceUnit switch
        {
            ToleranceUnit.Minute => "MINUTE",
            ToleranceUnit.Hour => "HOUR",
            ToleranceUnit.Day => "DAY",
            _ => throw new SqlCompilationException(
                $"{c.LeftFieldCode}: DateWithin cannot use tolerance unit {c.ToleranceUnit}"),
        };

        var tol = parameters.Add(c.ToleranceValue.Value);
        return $"{l} BETWEEN DATEADD({unit}, -{tol}, {r}) AND DATEADD({unit}, {tol}, {r})";
    }

    /// <summary>
    /// The difference between the two sides' Amount-role fields, in minor
    /// units. NULL when either side has no such field — a definition cannot be
    /// activated without one, but a sandbox run on a half-built dataset can
    /// still execute.
    /// </summary>
    private static string AmountDifferenceExpression(ReconciliationDefinition definition)
    {
        var left = definition.Left.FieldWithRole(FieldRole.Amount);
        var right = definition.Right.FieldWithRole(FieldRole.Amount);

        if (left is null || right is null)
        {
            return "CAST(NULL AS BIGINT)";
        }

        var l = Column("L", ResolveSlot(definition.Left, left.FieldCode, requireMatchable: false));
        var r = Column("R", ResolveSlot(definition.Right, right.FieldCode, requireMatchable: false));

        // Both are BIGINT minor-unit slots: integer arithmetic, no rounding,
        // no phantom differences.
        return $"({l} - {r})";
    }

    /// <summary>
    /// Sanitises text destined for a SQL comment. Rule codes come from the
    /// database rather than a request, but a comment is still text being
    /// concatenated, and <c>*/</c> inside one would end it early.
    /// </summary>
    private static string Comment(string text) =>
        text.Replace("*/", string.Empty, StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
}

/// <summary>Everything a pass needs to know about the run it belongs to.</summary>
public sealed record PassContext
{
    public required ReconciliationDefinition Definition { get; init; }
    public required MatchRule Rule { get; init; }
    public required long RunId { get; init; }

    /// <summary>
    /// The run whose staged rows to read: <c>ISNULL(SourceRunId, RunId)</c>.
    /// Differs from <see cref="RunId"/> on a Rematch.
    /// </summary>
    public required long StagingRunId { get; init; }

    public required DateOnly BusinessDate { get; init; }
    public required DateOnly WindowFrom { get; init; }
    public required DateOnly WindowTo { get; init; }
}

/// <summary>
/// A statement and its parameters. The text is also persisted to
/// <c>ops.ReconRunStep.GeneratedSql</c> so that "what SQL matched this row in
/// June" is answerable from the run itself (§9.4).
/// </summary>
public sealed record CompiledStatement(string Sql, SqlParameterBag Parameters);
