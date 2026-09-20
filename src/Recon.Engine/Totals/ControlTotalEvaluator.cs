using System.Globalization;
using System.Text;
using Recon.Domain.Configuration;
using Recon.Engine.Sql;

namespace Recon.Engine.Totals;

/// <summary>
/// Evaluates a control-total check: two aggregates, compared.
///
/// <para>
/// Row-level matching is not enough. Each run must prove its net position, and
/// "a run with fully matched rows but a non-zero net difference is a failed
/// run" (§11). The scope decides what is summed: one run, all sessions of a
/// business date, or a period of N days.
/// </para>
///
/// <para>
/// <b>The current-run rule is load-bearing.</b> A period-scoped aggregate must
/// read only runs with <c>IsCurrent = 1</c> and <c>RunType &lt;&gt; 'Sandbox'</c>,
/// or a superseded rerun is counted twice and a sandbox experiment lands in a
/// regulatory figure.
/// </para>
/// </summary>
public static class ControlTotalEvaluator
{
    public static CompiledStatement Compile(
        ControlTotalDefinition check,
        ReconciliationDefinition definition,
        long runId,
        long stagingRunId,
        DateOnly businessDate,
        DateOnly windowFrom,
        DateOnly windowTo)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(definition);

        var p = new SqlParameterBag();
        var sql = new StringBuilder();

        var run = p.Add("@RunId", runId);
        var checkId = p.Add("@ControlTotalId", check.ControlTotalId);
        var tolerance = p.Add("@Tolerance", check.ToleranceMinor);

        var a = CompileSide(check, check.SourceTypeA, check.SourceA, definition,
                            runId, stagingRunId, businessDate, windowFrom, windowTo, p);
        var b = CompileSide(check, check.SourceTypeB, check.SourceB, definition,
                            runId, stagingRunId, businessDate, windowFrom, windowTo, p);

        sql.AppendLine("SET NOCOUNT ON;");
        sql.AppendLine();
        sql.AppendLine("DECLARE @a BIGINT = ISNULL((" + a + "), 0);");
        sql.AppendLine("DECLARE @b BIGINT = ISNULL((" + b + "), 0);");
        sql.AppendLine();
        // Idempotent, so a resumed run re-evaluates rather than colliding with
        // the UNIQUE constraint on (RunId, ControlTotalId).
        sql.AppendLine(CultureInfo.InvariantCulture, $"DELETE ops.ControlTotalResult WHERE RunId = {run} AND ControlTotalId = {checkId};");
        sql.AppendLine();
        sql.AppendLine("INSERT ops.ControlTotalResult (RunId, ControlTotalId, ValueA, ValueB, IsBalanced)");
        sql.AppendLine(CultureInfo.InvariantCulture, $"VALUES ({run}, {checkId}, @a, @b,");
        sql.AppendLine(CultureInfo.InvariantCulture, $"        CASE WHEN ABS(@a - @b) <= {tolerance} THEN 1 ELSE 0 END);");
        sql.AppendLine();
        sql.AppendLine("SELECT @a AS ValueA, @b AS ValueB,");
        sql.AppendLine(CultureInfo.InvariantCulture, $"       CASE WHEN ABS(@a - @b) <= {tolerance} THEN 1 ELSE 0 END AS IsBalanced;");

        return new CompiledStatement(sql.ToString(), p);
    }

    private static string CompileSide(
        ControlTotalDefinition check,
        ControlTotalSource sourceType,
        AggregateSpec spec,
        ReconciliationDefinition definition,
        long runId,
        long stagingRunId,
        DateOnly businessDate,
        DateOnly windowFrom,
        DateOnly windowTo,
        SqlParameterBag p)
    {
        return sourceType switch
        {
            ControlTotalSource.Staging =>
                CompileStagingSide(spec, definition, stagingRunId, windowFrom, windowTo, p),

            ControlTotalSource.MatchResult =>
                CompileMatchResultSide(spec, runId, p),

            ControlTotalSource.RunAggregate =>
                CompileRunAggregateSide(check, spec, definition, runId, businessDate, p),

            // A summary file or API is modelled as its own dataset (§11), so
            // reading it is the same staging query as any other side.
            ControlTotalSource.Dataset =>
                CompileStagingSide(spec, definition, stagingRunId, windowFrom, windowTo, p),

            _ => throw new SqlCompilationException($"unhandled control-total source {sourceType}"),
        };
    }

    private static string CompileStagingSide(
        AggregateSpec spec,
        ReconciliationDefinition definition,
        long stagingRunId,
        DateOnly windowFrom,
        DateOnly windowTo,
        SqlParameterBag p)
    {
        var dataset = ResolveDataset(spec, definition);
        var range = SqlQueryBuilder.StagingRange("S", dataset.DatasetId, stagingRunId,
            windowFrom, windowTo, p);
        var filter = SqlQueryBuilder.CompileCondition(spec.Filter, dataset, "S", p);

        var expression = spec.Function switch
        {
            "Count" => "COUNT_BIG(*)",
            "SumAmount" => SumAmount(dataset),
            _ => throw new SqlCompilationException(
                $"unknown aggregate function '{spec.Function}'"),
        };

        var statusClause = spec.MatchStatus is { } status
            ? $" AND S.MatchStatus = {p.Add(status.ToString())}"
            : string.Empty;

        return $"SELECT {expression} FROM stg.StagingTransaction AS S " +
               $"WHERE {range} AND {filter}{statusClause}";
    }

    private static string CompileMatchResultSide(
        AggregateSpec spec, long runId, SqlParameterBag p)
    {
        var run = p.Add(runId);

        var expression = spec.Function switch
        {
            "Count" => "COUNT_BIG(*)",
            "SumAmount" => "SUM(ISNULL(M.AmountDiffMinor, 0))",
            _ => throw new SqlCompilationException(
                $"unknown aggregate function '{spec.Function}'"),
        };

        var statusClause = spec.MatchStatus is { } status
            ? $" AND M.MatchStatus = {p.Add(status.ToString())}"
            : string.Empty;

        return $"SELECT {expression} FROM ops.MatchResult AS M WHERE M.RunId = {run}{statusClause}";
    }

    /// <summary>
    /// Sums persisted aggregates. This is the path that survives staging
    /// archival and the one a period-scoped check must use.
    /// </summary>
    private static string CompileRunAggregateSide(
        ControlTotalDefinition check,
        AggregateSpec spec,
        ReconciliationDefinition definition,
        long runId,
        DateOnly businessDate,
        SqlParameterBag p)
    {
        var column = spec.Function switch
        {
            "Count" => "A.RowCnt",
            "SumAmount" => "A.AmountMinorSum",
            _ => throw new SqlCompilationException(
                $"unknown aggregate function '{spec.Function}'"),
        };

        var def = p.Add(definition.DefinitionId);
        var groupKey = p.Add(spec.GroupKey ?? "*");
        var status = p.Add(spec.MatchStatus?.ToString() ?? "*");

        var scopeClause = check.Scope switch
        {
            ControlTotalScope.Run => $"A.RunId = {p.Add(runId)}",

            ControlTotalScope.BusinessDate =>
                $"A.BusinessDate = {p.Add(businessDate.ToDateTime(TimeOnly.MinValue))}",

            ControlTotalScope.Period =>
                $"A.BusinessDate > {p.Add(businessDate.AddDays(-(check.PeriodDays ?? throw new SqlCompilationException($"check {check.CheckCode} is Period-scoped with no PeriodDays"))).ToDateTime(TimeOnly.MinValue))} " +
                $"AND A.BusinessDate <= {p.Add(businessDate.ToDateTime(TimeOnly.MinValue))}",

            _ => throw new SqlCompilationException($"unhandled scope {check.Scope}"),
        };

        var sideClause = spec.Side is { } side
            ? $" AND A.Side = {p.Add(side.ToString())}"
            : string.Empty;

        var datasetClause = spec.DatasetId is { } datasetId
            ? $" AND A.DatasetId = {p.Add(datasetId)}"
            : string.Empty;

        // The current-run rule: superseded reruns and sandbox runs are excluded
        // by joining the run, not by hoping nobody re-ran the day.
        return $"SELECT ISNULL(SUM({column}), 0) FROM ops.RunAggregate AS A " +
               $"JOIN ops.ReconRun AS R ON R.RunId = A.RunId " +
               $"WHERE A.DefinitionId = {def} AND {scopeClause} " +
               $"AND A.GroupKey = {groupKey} AND A.MatchStatus = {status}" +
               $"{sideClause}{datasetClause} " +
               "AND R.IsCurrent = 1 AND R.RunType <> 'Sandbox'";
    }

    private static string SumAmount(Domain.Configuration.Dataset dataset)
    {
        var amount = dataset.FieldWithRole(FieldRole.Amount)
            ?? throw new SqlCompilationException(
                $"dataset {dataset.Code} has no Amount-role field, so it cannot be summed");

        var slot = SqlQueryBuilder.ResolveSlot(dataset, amount.FieldCode, requireMatchable: false);

        // A control total that evaluates to NULL is worse than one that
        // evaluates to zero: it compares equal to nothing, so the check
        // neither balances nor reports a difference anyone can read.
        return $"SUM(CAST(ISNULL(S.{slot}, 0) AS BIGINT))";
    }

    private static Domain.Configuration.Dataset ResolveDataset(
        AggregateSpec spec, ReconciliationDefinition definition)
    {
        if (spec.DatasetId is { } id)
        {
            if (definition.Left.DatasetId == id) { return definition.Left; }
            if (definition.Right.DatasetId == id) { return definition.Right; }

            throw new SqlCompilationException(
                $"dataset {id} is not part of definition {definition.Code}");
        }

        return spec.Side switch
        {
            Side.Left => definition.Left,
            Side.Right => definition.Right,
            _ => throw new SqlCompilationException(
                "an aggregate over staging must name a dataset or a side"),
        };
    }
}
