using System.Globalization;
using System.Text;
using Recon.Domain.Configuration;
using Recon.Engine.Sql;

namespace Recon.Engine.Reporting;

/// <summary>
/// Compiles a report definition into the query that streams its rows.
///
/// <para>
/// It resolves columns through the field registry and parameterizes every
/// value, exactly as <see cref="SqlQueryBuilder"/> does — a report filter is a
/// condition tree like any other, and a report is not a reason to open a
/// second path to SQL.
/// </para>
/// </summary>
public static class ReportSqlBuilder
{
    public static CompiledStatement Compile(
        ReportDefinition report,
        ReconciliationDefinition definition,
        long runId,
        long stagingRunId,
        DateOnly businessDate,
        DateOnly windowFrom,
        DateOnly windowTo)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(definition);

        return report.Scope switch
        {
            ReportScope.ControlTotals => CompileControlTotals(runId),
            ReportScope.Exceptions => CompileExceptions(report, definition, runId),
            _ => CompileTransactions(report, definition, runId, stagingRunId, windowFrom, windowTo),
        };
    }

    /// <summary>
    /// The transaction-level scopes. One row per staged row, with each column
    /// resolved to its slot.
    /// </summary>
    private static CompiledStatement CompileTransactions(
        ReportDefinition report,
        ReconciliationDefinition definition,
        long runId,
        long stagingRunId,
        DateOnly windowFrom,
        DateOnly windowTo)
    {
        var p = new SqlParameterBag();
        var sql = new StringBuilder();
        var run = p.Add("@RunId", runId);

        // A report belongs to a definition, which has two sides. Rows from
        // both are returned, tagged, because "the matched list" means both
        // halves of each pair.
        var selects = new List<string>();

        foreach (var side in new[] { Side.Left, Side.Right })
        {
            var dataset = definition.DatasetFor(side);
            var range = SqlQueryBuilder.StagingRange("S", dataset.DatasetId, stagingRunId,
                windowFrom, windowTo, p);

            var filter = SqlQueryBuilder.CompileCondition(report.Filter, dataset, "S", p);
            var columns = new List<string>
            {
                p.Add(side.ToString()) + " AS [Side]",
                p.Add(dataset.Code) + " AS [Dataset]",
            };

            foreach (var column in report.Columns.OrderBy(c => c.Sequence))
            {
                columns.Add(ColumnExpression(column, dataset, "S"));
            }

            var statusClause = report.Scope switch
            {
                // AmountDifference is a matched pair whose amounts disagree, so
                // it belongs in the matched list — it is a break about the
                // amount, not about the pairing.
                ReportScope.Matched => " AND S.MatchStatus IN ('Matched','AutoClosed')",
                ReportScope.Unmatched => " AND S.MatchStatus IN ('Unmatched','Ambiguous')",
                _ => string.Empty,
            };

            selects.Add($"""
                SELECT {string.Join(",\n                       ", columns)}
                FROM stg.StagingTransaction AS S
                WHERE {range} AND {filter}
                  AND (S.ResultRunId = {run} OR S.ResultRunId IS NULL){statusClause}
                """);
        }

        sql.AppendLine(string.Join("\nUNION ALL\n", selects));
        sql.AppendLine("ORDER BY [Side];");

        return new CompiledStatement(sql.ToString(), p);
    }

    private static string ColumnExpression(ReportColumn column, Dataset dataset, string alias)
    {
        var name = Bracket(column.Header);

        if (column.Field is { } field)
        {
            // requireMatchable: false — a report may show a field the rule
            // builder withholds. Currency is the obvious case: not a matching
            // key, but every money column needs it.
            var slot = SqlQueryBuilder.ResolveSlot(dataset, field.FieldCode, requireMatchable: false);
            return $"{alias}.{slot} AS {name}";
        }

        // A closed set, mapped here rather than interpolated: these are the
        // computed values the schema's ComputedField column documents.
        var expression = column.ComputedField switch
        {
            "StagingId" => $"{alias}.StagingId",
            "TxDate" => $"{alias}.TxDate",
            "MatchStatus" => $"{alias}.MatchStatus",
            "ExceptionCode" => $"{alias}.ExceptionCode",
            "MatchedWithId" => $"{alias}.MatchedWithId",
            "RawRowNumber" => $"{alias}.RawRowNumber",
            "MatchedByRuleCode" =>
                $"(SELECT r.RuleCode FROM cfg.MatchRule AS r WHERE r.MatchRuleId = {alias}.MatchedByRuleId)",
            _ => throw new SqlCompilationException(
                $"'{column.ComputedField}' is not a computed report column. " +
                "A report column is either a registry field or one of the documented computed values."),
        };

        return $"{expression} AS {name}";
    }

    private static CompiledStatement CompileExceptions(
        ReportDefinition report, ReconciliationDefinition definition, long runId)
    {
        var p = new SqlParameterBag();
        var run = p.Add("@RunId", runId);

        // The exception list reads ops.ReconException, which carries its own
        // key snapshot — so this report still works after staging is archived
        // (finding C2), which a join back to staging would not.
        var sql = $"""
            SELECT e.ExceptionId AS [Exception Id],
                   e.Side AS [Side],
                   e.ExceptionCode AS [Code],
                   c.DisplayName AS [Meaning],
                   e.BusinessDate AS [Business Date],
                   DATEDIFF(DAY, e.BusinessDate, CAST(SYSDATETIME() AS DATE)) AS [Age Days],
                   e.AmountMinor AS [Amount],
                   e.CurrencyCode AS [Currency],
                   e.Status AS [Status],
                   e.AssignedTo AS [Assigned To],
                   e.ResolutionCode AS [Resolution],
                   e.KeyValuesJson AS [Key Values]
            FROM ops.ReconException AS e
            LEFT JOIN cfg.ClassificationRule AS c
                   ON c.ExceptionCode = e.ExceptionCode AND c.DefinitionId = e.DefinitionId
            WHERE e.RunId = {run}
            ORDER BY e.Side, e.ExceptionCode, e.ExceptionId;
            """;

        _ = report;
        _ = definition;
        return new CompiledStatement(sql, p);
    }

    private static CompiledStatement CompileControlTotals(long runId)
    {
        var p = new SqlParameterBag();
        var run = p.Add("@RunId", runId);

        var sql = $"""
            SELECT c.CheckCode AS [Check],
                   c.DisplayName AS [Description],
                   c.Scope AS [Scope],
                   t.ValueA AS [Source A],
                   t.ValueB AS [Source B],
                   t.DifferenceMinor AS [Difference],
                   CASE WHEN t.IsBalanced = 1 THEN 'Balanced' ELSE 'MISMATCH' END AS [Result],
                   CASE WHEN c.FailRunOnMismatch = 1 THEN 'Yes' ELSE 'No' END AS [Fails Run]
            FROM ops.ControlTotalResult AS t
            JOIN cfg.ControlTotalDefinition AS c ON c.ControlTotalId = t.ControlTotalId
            WHERE t.RunId = {run}
            ORDER BY c.CheckCode;
            """;

        return new CompiledStatement(sql, p);
    }

    /// <summary>
    /// A column header becomes a SQL alias, so it is bracketed and its closing
    /// bracket escaped. Headers come from configuration rather than a request,
    /// but this is still text being concatenated into a statement.
    /// </summary>
    private static string Bracket(string header) =>
        "[" + header.Replace("]", "]]", StringComparison.Ordinal) + "]";

    public static string Describe(ReportDefinition report, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        culture ??= CultureInfo.InvariantCulture;

        return string.Format(
            culture,
            "{0} — {1} scope, {2} column(s), {3}",
            report.Code, report.Scope, report.Columns.Count, report.OutputFormat);
    }
}
