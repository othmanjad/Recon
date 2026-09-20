using System.Globalization;
using System.Text;
using Recon.Domain.Configuration;
using Recon.Engine.Sql;

namespace Recon.Engine.Totals;

/// <summary>
/// Persists each run's totals to <c>ops.RunAggregate</c>.
///
/// <para>
/// Review blocker C1, part 2. Operations must be able to return to any past
/// reconciliation, take a total and compare it to a summary. Reading that from
/// staging means rescanning 2M rows and stops working entirely once staging is
/// archived — which is what makes a three-month staging retention safe (E3) and
/// the largest cost in the system controllable.
/// </para>
///
/// <para>
/// Written per dataset, per side, per grouping key, per match status, so
/// "run 4471, matched inward total" is a primary-key read.
/// </para>
/// </summary>
public static class RunAggregateBuilder
{
    public static CompiledStatement Compile(
        ReconciliationDefinition definition,
        long runId,
        long stagingRunId,
        DateOnly businessDate,
        DateOnly windowFrom,
        DateOnly windowTo)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var p = new SqlParameterBag();
        var sql = new StringBuilder();

        var run = p.Add("@RunId", runId);
        var def = p.Add("@DefinitionId", definition.DefinitionId);
        var bd = p.Add("@BusinessDate", businessDate.ToDateTime(TimeOnly.MinValue));

        sql.AppendLine("SET NOCOUNT ON;");
        // Idempotent: a resumed run rewrites its own aggregates rather than
        // doubling them, which the UNIQUE constraint would refuse anyway.
        sql.AppendLine(CultureInfo.InvariantCulture, $"DELETE ops.RunAggregate WHERE RunId = {run};");

        foreach (var side in new[] { Side.Left, Side.Right })
        {
            var dataset = definition.DatasetFor(side);
            var range = SqlQueryBuilder.StagingRange("S", dataset.DatasetId, stagingRunId,
                windowFrom, windowTo, p);

            var sideName = p.Add(side.ToString());
            var datasetId = p.Add(dataset.DatasetId);

            /* The coalesce is inside the SUM, not around it, and it is not
               decoration: AmountMinorSum is NOT NULL, and SUM over a group
               whose amounts are all NULL returns NULL — which killed a real
               run with "Cannot insert the value NULL into column
               'AmountMinorSum'" after an amount column that mapped to nothing.
               A row with no amount contributes nothing to the total; it is
               still counted, because COUNT_BIG(*) counts rows and not
               amounts. */
            var amount = dataset.FieldWithRole(FieldRole.Amount);
            var amountSum = amount is null
                ? "CAST(0 AS BIGINT)"
                : $"SUM(CAST(ISNULL(S.{SqlQueryBuilder.ResolveSlot(dataset, amount.FieldCode, requireMatchable: false)}, 0) AS BIGINT))";

            var currency = dataset.FieldWithRole(FieldRole.Currency);
            var currencyColumn = currency is null
                ? "CAST(" + p.Add(dataset.DefaultCurrency ?? "JOD") + " AS CHAR(3))"
                : $"CAST(S.{SqlQueryBuilder.ResolveSlot(dataset, currency.FieldCode, requireMatchable: false)} AS CHAR(3))";

            // The Direction-role field is what makes "inward total" meaningful
            // for any counterparty without naming its columns (§6).
            var direction = dataset.FieldWithRole(FieldRole.Direction);

            sql.AppendLine();
            sql.AppendLine(CultureInfo.InvariantCulture, $"/* {side}: {dataset.Code} — whole dataset, by status */");
            sql.AppendLine("INSERT ops.RunAggregate");
            sql.AppendLine("    (RunId, DefinitionId, BusinessDate, DatasetId, Side,");
            sql.AppendLine("     GroupKey, MatchStatus, RowCnt, AmountMinorSum, CurrencyCode)");
            sql.AppendLine("SELECT");
            sql.AppendLine(CultureInfo.InvariantCulture, $"    {run}, {def}, {bd}, {datasetId}, {sideName},");
            sql.AppendLine("    N'*', S.MatchStatus, COUNT_BIG(*), " + amountSum + ", " + currencyColumn);
            sql.AppendLine("FROM stg.StagingTransaction AS S");
            sql.AppendLine("WHERE " + range);
            sql.AppendLine("GROUP BY S.MatchStatus, " + currencyColumn + ";");

            sql.AppendLine();
            sql.AppendLine(CultureInfo.InvariantCulture, $"/* {side}: {dataset.Code} — whole dataset, all statuses */");
            sql.AppendLine("INSERT ops.RunAggregate");
            sql.AppendLine("    (RunId, DefinitionId, BusinessDate, DatasetId, Side,");
            sql.AppendLine("     GroupKey, MatchStatus, RowCnt, AmountMinorSum, CurrencyCode)");
            sql.AppendLine("SELECT");
            sql.AppendLine(CultureInfo.InvariantCulture, $"    {run}, {def}, {bd}, {datasetId}, {sideName},");
            sql.AppendLine("    N'*', N'*', COUNT_BIG(*), " + amountSum + ", " + currencyColumn);
            sql.AppendLine("FROM stg.StagingTransaction AS S");
            sql.AppendLine("WHERE " + range);
            sql.AppendLine("GROUP BY " + currencyColumn + ";");

            if (direction is not null)
            {
                var directionSlot = SqlQueryBuilder.ResolveSlot(
                    dataset, direction.FieldCode, requireMatchable: false);

                sql.AppendLine();
                sql.AppendLine(CultureInfo.InvariantCulture, $"/* {side}: {dataset.Code} — by direction and status */");
                sql.AppendLine("INSERT ops.RunAggregate");
                sql.AppendLine("    (RunId, DefinitionId, BusinessDate, DatasetId, Side,");
                sql.AppendLine("     GroupKey, MatchStatus, RowCnt, AmountMinorSum, CurrencyCode)");
                sql.AppendLine("SELECT");
                sql.AppendLine(CultureInfo.InvariantCulture, $"    {run}, {def}, {bd}, {datasetId}, {sideName},");
                sql.AppendLine(CultureInfo.InvariantCulture, $"    N'Direction=' + ISNULL(CAST(S.{directionSlot} AS NVARCHAR(100)), N'(none)'),");
                sql.AppendLine("    S.MatchStatus, COUNT_BIG(*), " + amountSum + ", " + currencyColumn);
                sql.AppendLine("FROM stg.StagingTransaction AS S");
                sql.AppendLine("WHERE " + range);
                sql.AppendLine(CultureInfo.InvariantCulture,
                    $"GROUP BY S.{directionSlot}, S.MatchStatus, {currencyColumn};");
            }
        }

        return new CompiledStatement(sql.ToString(), p);
    }
}
