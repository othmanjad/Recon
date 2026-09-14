using System.Globalization;
using System.Text;
using Recon.Domain.Configuration;

namespace Recon.Engine.Sql;

public static partial class SqlQueryBuilderAggregate
{
    /// <summary>
    /// Aggregate mode (§9.2 / review blocker C1): group one side by the
    /// user-chosen fields, sum its Amount-role field and count its rows, then
    /// join the grouped result to the other side.
    ///
    /// <para>
    /// This is what makes "one summary line ↔ many transactions" an ordinary
    /// rule rather than special code — a JoPACC session summary, a settlement
    /// batch total, or a monthly fee report against detail rows.
    /// </para>
    ///
    /// <para>
    /// The grouped side writes its group key into
    /// <c>MatchResult.LeftAggregateKey</c> / <c>RightAggregateKey</c> so the
    /// break is traceable to the group that produced it, not just to a number.
    /// </para>
    /// </summary>
    public static CompiledStatement CompileAggregatePass(PassContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var rule = ctx.Rule;
        if (rule.Mode != MatchMode.Aggregate)
        {
            throw new SqlCompilationException(
                $"rule {rule.RuleCode} is {rule.Mode} mode; use CompileRowPass");
        }

        if (rule.LeftGroupByFields.Count == 0)
        {
            throw new SqlCompilationException(
                $"rule {rule.RuleCode} is Aggregate mode with no grouping, which would sum the whole dataset by accident");
        }

        if (rule.Conditions.Count == 0)
        {
            throw new SqlCompilationException(
                $"rule {rule.RuleCode} has no conditions to join the grouped result on");
        }

        var leftAmount = ctx.Definition.Left.FieldWithRole(FieldRole.Amount)
            ?? throw new SqlCompilationException(
                $"Aggregate mode needs an Amount-role field on {ctx.Definition.Left.Code}");

        var p = new SqlParameterBag();
        var sql = new StringBuilder();

        var runId = p.Add("@RunId", ctx.RunId);
        var ruleId = p.Add("@MatchRuleId", rule.MatchRuleId);
        var businessDate = p.Add("@BusinessDate", ctx.BusinessDate.ToDateTime(TimeOnly.MinValue));

        var leftRange = SqlQueryBuilder.StagingRange("L", ctx.Definition.Left.DatasetId,
            ctx.StagingRunId, ctx.WindowFrom, ctx.WindowTo, p, matchableOnly: true);
        var rightRange = SqlQueryBuilder.StagingRange("R", ctx.Definition.Right.DatasetId,
            ctx.StagingRunId, ctx.WindowFrom, ctx.WindowTo, p, matchableOnly: true);

        var leftFilter = SqlQueryBuilder.CompileCondition(rule.LeftFilter, ctx.Definition.Left, "L", p);
        var rightFilter = SqlQueryBuilder.CompileCondition(rule.RightFilter, ctx.Definition.Right, "R", p);

        // The grouping columns, resolved through the registry like everything else.
        var groupSlots = rule.LeftGroupByFields
            .Select(code => SqlQueryBuilder.ResolveSlot(ctx.Definition.Left, code))
            .ToList();

        var groupColumns = string.Join(", ", groupSlots.Select(sl => "L." + sl));
        var amountSlot = SqlQueryBuilder.ResolveSlot(
            ctx.Definition.Left, leftAmount.FieldCode, requireMatchable: false);

        // The key is a readable composite so a break names its own group.
        var keyExpression = string.Join(" + N'|' + ",
            groupSlots.Select(sl => $"ISNULL(CAST(L.{sl} AS NVARCHAR(300)), N'')"));

        sql.AppendLine("SET NOCOUNT ON;");
        sql.AppendLine();
        sql.AppendLine("/* Aggregate pass " + rule.Sequence + " */");
        sql.AppendLine("DROP TABLE IF EXISTS #grouped;");
        sql.AppendLine("DROP TABLE IF EXISTS #candidate;");
        sql.AppendLine();
        sql.AppendLine("SELECT");
        sql.AppendLine("    " + keyExpression + " AS GroupKey,");
        sql.AppendLine("    " + groupColumns + ",");
        sql.AppendLine(CultureInfo.InvariantCulture, $"    SUM(CAST(L.{amountSlot} AS BIGINT)) AS AmountMinorSum,");
        sql.AppendLine("    COUNT_BIG(*) AS RowCnt,");
        sql.AppendLine("    MIN(L.StagingId) AS AnyStagingId,");
        sql.AppendLine("    MIN(L.TxDate) AS AnyTxDate");
        sql.AppendLine("INTO #grouped");
        sql.AppendLine("FROM stg.StagingTransaction AS L");
        sql.AppendLine("WHERE " + leftRange);
        sql.AppendLine("  AND " + leftFilter);
        sql.AppendLine("  AND NOT EXISTS (SELECT 1 FROM ops.MatchResult AS M");
        sql.AppendLine(CultureInfo.InvariantCulture, $"                  WHERE M.RunId = {runId} AND M.LeftStagingId = L.StagingId)");
        sql.AppendLine("GROUP BY " + groupColumns + ";");
        sql.AppendLine();

        // Join the grouped total to the summary side. The conditions the user
        // chose compare the group's columns to the summary row's columns; the
        // Amount pair compares the SUM to the reported total.
        var joinClauses = new List<string>();
        foreach (var c in rule.Conditions.OrderBy(c => c.Sequence))
        {
            var leftField = ctx.Definition.Left.GetField(c.LeftFieldCode);
            var rightSlot = SqlQueryBuilder.ResolveSlot(
                ctx.Definition.Right, c.RightFieldCode, c.UseNormalized);

            // An Amount-role pair means "does the sum equal the reported total".
            var leftExpression = leftField.Role == FieldRole.Amount
                ? "G.AmountMinorSum"
                : "G." + SqlQueryBuilder.ResolveSlot(ctx.Definition.Left, c.LeftFieldCode, c.UseNormalized);

            var right = "R." + rightSlot;

            joinClauses.Add(c.Comparison switch
            {
                ComparisonType.Exact or ComparisonType.NumericExact => $"{leftExpression} = {right}",
                ComparisonType.NumericTolerance when c.ToleranceValue is not null =>
                    $"{leftExpression} BETWEEN {right} - {p.Add(c.ToleranceValue.Value)} AND {right} + {p.Add(c.ToleranceValue.Value)}",
                ComparisonType.DateExact => $"CAST({leftExpression} AS DATE) = CAST({right} AS DATE)",
                _ => throw new SqlCompilationException(
                    $"comparison {c.Comparison} is not supported in Aggregate mode " +
                    "(a grouped total cannot be prefix- or substring-matched)"),
            });
        }

        sql.AppendLine("SELECT");
        sql.AppendLine("    G.AnyStagingId AS LeftStagingId,");
        sql.AppendLine("    R.StagingId    AS RightStagingId,");
        sql.AppendLine("    G.GroupKey,");
        sql.AppendLine("    G.RowCnt,");
        sql.AppendLine("    COUNT(*) OVER (PARTITION BY G.GroupKey)   AS LeftCandidates,");
        sql.AppendLine("    COUNT(*) OVER (PARTITION BY R.StagingId)  AS RightCandidates");
        sql.AppendLine("INTO #candidate");
        sql.AppendLine("FROM #grouped AS G");
        sql.AppendLine("JOIN stg.StagingTransaction AS R");
        sql.AppendLine("    ON " + string.Join("\n       AND ", joinClauses));
        sql.AppendLine("WHERE " + rightRange);
        sql.AppendLine("  AND " + rightFilter);
        sql.AppendLine("  AND NOT EXISTS (SELECT 1 FROM ops.MatchResult AS M");
        sql.AppendLine(CultureInfo.InvariantCulture, $"                  WHERE M.RunId = {runId} AND M.RightStagingId = R.StagingId);");
        sql.AppendLine();

        // Every member of a matched group is recorded, not just the
        // representative row: "which transactions made up this summary line"
        // is the question the module exists to answer.
        sql.AppendLine("INSERT ops.MatchResult");
        sql.AppendLine("    (RunId, BusinessDate, LeftStagingId, RightStagingId, MatchRuleId,");
        sql.AppendLine("     MatchStatus, CandidateCount, LeftAggregateKey, RightAggregateKey)");
        sql.AppendLine("SELECT");
        sql.AppendLine(CultureInfo.InvariantCulture, $"    {runId}, {businessDate}, L.StagingId, c.RightStagingId, {ruleId},");
        sql.AppendLine("    CASE WHEN c.LeftCandidates > 1 OR c.RightCandidates > 1");
        sql.AppendLine("         THEN 'Ambiguous' ELSE 'Matched' END,");
        sql.AppendLine("    CASE WHEN c.LeftCandidates > c.RightCandidates");
        sql.AppendLine("         THEN c.LeftCandidates ELSE c.RightCandidates END,");
        sql.AppendLine("    c.GroupKey, c.GroupKey");
        sql.AppendLine("FROM #candidate AS c");
        sql.AppendLine("JOIN stg.StagingTransaction AS L");
        sql.AppendLine("    ON " + leftRange.Replace("L.LoadRunId", "L.LoadRunId", StringComparison.Ordinal));
        sql.AppendLine("   AND " + keyExpression.Replace("L.", "L.", StringComparison.Ordinal) + " = c.GroupKey;");
        sql.AppendLine();
        sql.AppendLine("SELECT");
        sql.AppendLine("    SUM(CASE WHEN LeftCandidates = 1 AND RightCandidates = 1 THEN 1 ELSE 0 END) AS Unique_,");
        sql.AppendLine("    SUM(CASE WHEN LeftCandidates > 1 OR  RightCandidates > 1 THEN 1 ELSE 0 END) AS Ambiguous_,");
        sql.AppendLine("    COUNT(*) AS Candidates_");
        sql.AppendLine("FROM #candidate;");
        sql.AppendLine();
        sql.AppendLine("DROP TABLE IF EXISTS #grouped;");
        sql.AppendLine("DROP TABLE IF EXISTS #candidate;");

        return new CompiledStatement(sql.ToString(), p);
    }
}
