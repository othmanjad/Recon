using System.Data;
using Microsoft.Data.SqlClient;
using Recon.Data;
using Recon.Domain.Configuration;
using Recon.Domain.Money;
using Recon.Engine.Sql;

namespace Recon.Engine.Fees;

/// <summary>
/// Calculates a run's fees and writes the netting (Phase 5).
///
/// <para>
/// The flow the design specifies (§12): per-transaction calculation →
/// Revenue / Cost → netting → compared against the counterparty's fee report
/// <b>as an ordinary reconciliation definition</b>. That last part is why
/// there is no special comparison code here: the calculated fees become a
/// dataset, and matching one dataset against another is what the engine
/// already does.
/// </para>
///
/// <para>
/// Applicability is configuration, not an assumption:
/// <c>cfg.FeeApplicability</c> per dataset and transaction type, because it is
/// confirmed that some reconciliation reports carry fees and some do not.
/// </para>
/// </summary>
public sealed class FeeRunner(SqlConnection connection)
{
    private readonly SqlConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    public async Task<FeeRunResult> CalculateAsync(
        ReconciliationDefinition definition,
        long runId,
        long stagingRunId,
        DateOnly businessDate,
        DateOnly windowFrom,
        DateOnly windowTo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var schedules = await LoadSchedulesAsync(
            definition.CounterpartyId, businessDate, cancellationToken).ConfigureAwait(false);

        if (schedules.Count == 0)
        {
            return new FeeRunResult { Applicable = false, Netting = FeeCalculator.Net([]) };
        }

        var applicability = await LoadApplicabilityAsync(
            definition.Left.DatasetId, cancellationToken).ConfigureAwait(false);

        // Fees are charged on the platform's own side of the reconciliation —
        // the CliQ session here — because that is what the counterparty
        // invoices against.
        var dataset = definition.Left;
        var currencies = await new ConfigurationRepository(_connection)
            .LoadCurrenciesAsync(cancellationToken).ConfigureAwait(false);

        var fees = new List<TransactionFee>();
        var skipped = 0L;

        await foreach (var row in ReadTransactionsAsync(
            dataset, stagingRunId, windowFrom, windowTo, cancellationToken).ConfigureAwait(false))
        {
            // A dataset or transaction type marked not fee-bearing is skipped
            // rather than charged zero: zero is a fee, absence is not.
            if (!IsApplicable(applicability, row.TransactionType))
            {
                skipped++;
                continue;
            }

            var currency = currencies.TryGetValue(row.CurrencyCode ?? "JOD", out var c)
                ? c
                : throw new InvalidOperationException(
                    $"staged row {row.StagingId} names currency '{row.CurrencyCode}', " +
                    "which is not in cfg.Currency — its minor units are unknown, so its fee " +
                    "cannot be scaled.");

            foreach (var schedule in schedules)
            {
                if (!Matches(schedule, row))
                {
                    continue;
                }

                var tier = schedule.TierFor(row.AmountMinor);

                if (tier is null)
                {
                    // A gap in the tiers is a configuration error that would
                    // otherwise silently under-charge.
                    throw new InvalidOperationException(
                        $"fee schedule {schedule.Code} has no tier covering " +
                        $"{currency.Format(row.AmountMinor)} {currency.Code}. " +
                        "A gap in the bands means transactions are silently not charged.");
                }

                // Rounded HERE, once, per transaction.
                var fee = FeeCalculator.Calculate(schedule, tier, row.AmountMinor, currency.MinorUnits);

                fees.Add(new TransactionFee
                {
                    StagingId = row.StagingId,
                    FeeScheduleId = schedule.FeeScheduleId,
                    FeeTierId = tier.FeeTierId,
                    BaseAmountMinor = row.AmountMinor,
                    FeeAmountMinor = fee,
                    CurrencyCode = currency.Code,
                    Party = schedule.Party,
                    Direction = row.Direction ?? "Inward",
                });
            }
        }

        await PersistAsync(runId, businessDate, fees, cancellationToken).ConfigureAwait(false);

        var netting = FeeCalculator.Net(fees);

        await PersistSummaryAsync(runId, businessDate, netting,
            fees.FirstOrDefault()?.CurrencyCode ?? "JOD", cancellationToken).ConfigureAwait(false);

        return new FeeRunResult
        {
            Applicable = true,
            TransactionCount = fees.Count,
            SkippedNotApplicable = skipped,
            Netting = netting,
        };
    }

    private async Task<List<FeeSchedule>> LoadSchedulesAsync(
        int counterpartyId, DateOnly businessDate, CancellationToken cancellationToken)
    {
        var headers = await Db.QueryAsync(
            _connection,
            """
            SELECT FeeScheduleId, CounterpartyId, Code, Name, Direction, TransactionType,
                   FeeParty, CurrencyCode, RoundingMode, EffectiveFrom, EffectiveTo, IsActive
            FROM cfg.FeeSchedule
            WHERE CounterpartyId = @cp
              AND IsActive = 1
              AND EffectiveFrom <= @date
              AND (EffectiveTo IS NULL OR EffectiveTo >= @date)
            ORDER BY FeeScheduleId;
            """,
            r => new FeeSchedule
            {
                FeeScheduleId = r.GetInt32(0),
                CounterpartyId = r.GetInt32(1),
                Code = r.GetString(2),
                Name = r.GetString(3),
                Direction = r.GetString(4),
                TransactionType = r.GetNullableString("TransactionType"),
                Party = Db.ParseEnum<FeeParty>(r.GetString(6)),
                CurrencyCode = r.GetString(7),
                Rounding = Db.ParseEnum<Rounding>(r.GetString(8)),
                EffectiveFrom = DateOnly.FromDateTime(r.GetDateTime(9)),
                EffectiveTo = r.IsDBNull(10) ? null : DateOnly.FromDateTime(r.GetDateTime(10)),
                IsActive = r.GetBoolean(11),
                Tiers = [],
            },
            c => c.With("@cp", counterpartyId)
                  .With("@date", businessDate.ToDateTime(TimeOnly.MinValue)),
            cancellationToken).ConfigureAwait(false);

        var result = new List<FeeSchedule>();

        foreach (var header in headers)
        {
            var tiers = await Db.QueryAsync(
                _connection,
                """
                SELECT FeeTierId, AmountFromMinor, AmountToMinor, CalculationType,
                       FixedAmountMinor, Percentage, MinFeeMinor, MaxFeeMinor
                FROM cfg.FeeTier WHERE FeeScheduleId = @id ORDER BY AmountFromMinor;
                """,
                r => new FeeTier
                {
                    FeeTierId = r.GetInt32(0),
                    AmountFromMinor = r.GetInt64(1),
                    AmountToMinor = r.IsDBNull(2) ? null : r.GetInt64(2),
                    Calculation = Db.ParseEnum<FeeCalculation>(r.GetString(3)),
                    FixedAmountMinor = r.GetInt64(4),
                    Percentage = r.GetDecimal(5),
                    MinFeeMinor = r.IsDBNull(6) ? null : r.GetInt64(6),
                    MaxFeeMinor = r.IsDBNull(7) ? null : r.GetInt64(7),
                },
                c => c.With("@id", header.FeeScheduleId),
                cancellationToken).ConfigureAwait(false);

            result.Add(header with { Tiers = tiers });
        }

        return result;
    }

    private Task<List<(string? TransactionType, bool Applicable)>> LoadApplicabilityAsync(
        int datasetId, CancellationToken cancellationToken) =>
        Db.QueryAsync(
            _connection,
            """
            SELECT TransactionType, IsFeeApplicable
            FROM cfg.FeeApplicability WHERE DatasetId = @ds;
            """,
            r => (r.GetNullableString("TransactionType"), r.GetBoolean(1)),
            c => c.With("@ds", datasetId),
            cancellationToken);

    /// <summary>
    /// A specific transaction type wins over the dataset-wide default. With no
    /// rows at all the answer is "applicable": a counterparty with a fee
    /// schedule and no applicability rows means every transaction is charged.
    /// </summary>
    private static bool IsApplicable(
        List<(string? TransactionType, bool Applicable)> rules, string? transactionType)
    {
        if (rules.Count == 0)
        {
            return true;
        }

        var specific = rules.FirstOrDefault(r =>
            r.TransactionType is not null
            && string.Equals(r.TransactionType, transactionType, StringComparison.Ordinal));

        if (specific.TransactionType is not null)
        {
            return specific.Applicable;
        }

        var fallback = rules.FirstOrDefault(r => r.TransactionType is null);
        return fallback.TransactionType is null && rules.Any(r => r.TransactionType is null)
            ? fallback.Applicable
            : true;
    }

    private static bool Matches(FeeSchedule schedule, FeeRow row) =>
        string.Equals(schedule.Direction, row.Direction, StringComparison.Ordinal)
        && (schedule.TransactionType is null
            || string.Equals(schedule.TransactionType, row.TransactionType, StringComparison.Ordinal));

    /// <summary>
    /// Streams the staged rows. A day is 2M rows and each needs one fee, so
    /// this must not materialise the set.
    /// </summary>
    private async IAsyncEnumerable<FeeRow> ReadTransactionsAsync(
        Dataset dataset,
        long stagingRunId,
        DateOnly windowFrom,
        DateOnly windowTo,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var amount = dataset.FieldWithRole(FieldRole.Amount)
            ?? throw new InvalidOperationException(
                $"dataset {dataset.Code} has no Amount-role field, so fees cannot be calculated");

        var direction = dataset.FieldWithRole(FieldRole.Direction);
        var currency = dataset.FieldWithRole(FieldRole.Currency);
        var txnType = dataset.FieldWithRole(FieldRole.TransactionType);

        var p = new SqlParameterBag();
        var range = SqlQueryBuilder.StagingRange("S", dataset.DatasetId, stagingRunId,
            windowFrom, windowTo, p);

        string Column(DatasetField? field, string fallback) => field is null
            ? fallback
            : "S." + SqlQueryBuilder.ResolveSlot(dataset, field.FieldCode, requireMatchable: false);

        var sql = $"""
            SELECT S.StagingId,
                   {Column(amount, "CAST(0 AS BIGINT)")} AS AmountMinor,
                   {Column(direction, "CAST('Inward' AS NVARCHAR(300))")} AS Direction,
                   {Column(currency, "CAST(NULL AS NVARCHAR(300))")} AS CurrencyCode,
                   {Column(txnType, "CAST(NULL AS NVARCHAR(300))")} AS TransactionType
            FROM stg.StagingTransaction AS S
            WHERE {range} AND S.MatchStatus NOT IN ('Excluded','Duplicate');
            """;

        using var command = Db.Command(_connection, sql, timeoutSeconds: 1800);
        foreach (var parameter in p.Parameters)
        {
            command.Parameters.Add(new SqlParameter(parameter.ParameterName, parameter.SqlDbType)
            {
                Value = parameter.Value,
                Size = parameter.Size,
            });
        }

        using var reader = await command
            .ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return new FeeRow
            {
                StagingId = reader.GetInt64(0),
                AmountMinor = reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                Direction = reader.IsDBNull(2) ? null : reader.GetString(2),
                CurrencyCode = reader.IsDBNull(3) ? null : reader.GetString(3),
                TransactionType = reader.IsDBNull(4) ? null : reader.GetString(4),
            };
        }
    }

    private async Task PersistAsync(
        long runId, DateOnly businessDate, List<TransactionFee> fees, CancellationToken cancellationToken)
    {
        // Idempotent: a resumed run replaces its own fees rather than doubling
        // them.
        await Db.ExecuteAsync(
            _connection,
            "DELETE ops.TransactionFee WHERE RunId = @run;",
            c => c.With("@run", runId),
            cancellationToken).ConfigureAwait(false);

        if (fees.Count == 0)
        {
            return;
        }

        // Bulk-copied rather than inserted row by row: at 2M transactions this
        // is the difference between seconds and hours.
        var table = new DataTable();
        table.Columns.Add("RunId", typeof(long));
        table.Columns.Add("BusinessDate", typeof(DateTime));
        table.Columns.Add("StagingId", typeof(long));
        table.Columns.Add("FeeScheduleId", typeof(int));
        table.Columns.Add("FeeTierId", typeof(int));
        table.Columns.Add("BaseAmountMinor", typeof(long));
        table.Columns.Add("FeeAmountMinor", typeof(long));
        table.Columns.Add("CurrencyCode", typeof(string));
        table.Columns.Add("FeeParty", typeof(string));
        table.Columns.Add("Direction", typeof(string));

        foreach (var fee in fees)
        {
            table.Rows.Add(
                runId, businessDate.ToDateTime(TimeOnly.MinValue), fee.StagingId,
                fee.FeeScheduleId, fee.FeeTierId, fee.BaseAmountMinor, fee.FeeAmountMinor,
                fee.CurrencyCode, fee.Party.ToString(), fee.Direction);
        }

        using var bulk = new SqlBulkCopy(_connection)
        {
            DestinationTableName = "ops.TransactionFee",
            BatchSize = 50_000,
            BulkCopyTimeout = 600,
        };

        foreach (DataColumn column in table.Columns)
        {
            bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        }

        await bulk.WriteToServerAsync(table, cancellationToken).ConfigureAwait(false);
    }

    private Task<int> PersistSummaryAsync(
        long runId,
        DateOnly businessDate,
        NettingResult netting,
        string currencyCode,
        CancellationToken cancellationToken) =>
        Db.ExecuteAsync(
            _connection,
            """
            DELETE ops.InterchangeSummary WHERE RunId = @run;

            INSERT ops.InterchangeSummary
                (RunId, BusinessDate, CurrencyCode, InwardCnt, OutwardCnt,
                 InwardAmountMinor, OutwardAmountMinor, RevenueMinor, CostMinor)
            VALUES (@run, @date, @currency, @inCnt, @outCnt,
                    @inAmt, @outAmt, @revenue, @cost);
            """,
            c => c.With("@run", runId)
                  .With("@date", businessDate.ToDateTime(TimeOnly.MinValue))
                  .With("@currency", currencyCode)
                  .With("@inCnt", netting.InwardCount)
                  .With("@outCnt", netting.OutwardCount)
                  .With("@inAmt", netting.InwardAmountMinor)
                  .With("@outAmt", netting.OutwardAmountMinor)
                  .With("@revenue", netting.RevenueMinor)
                  .With("@cost", netting.CostMinor),
            cancellationToken);

    private sealed record FeeRow
    {
        public required long StagingId { get; init; }
        public required long AmountMinor { get; init; }
        public string? Direction { get; init; }
        public string? CurrencyCode { get; init; }
        public string? TransactionType { get; init; }
    }
}

public sealed record FeeRunResult
{
    public required bool Applicable { get; init; }
    public int TransactionCount { get; init; }
    public long SkippedNotApplicable { get; init; }
    public required NettingResult Netting { get; init; }
}
