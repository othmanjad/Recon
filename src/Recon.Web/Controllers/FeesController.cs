using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Recon.Data;
using Recon.Domain.Money;
using Recon.Engine.Fees;
using Recon.Web.Services;

namespace Recon.Web.Controllers;

/// <summary>
/// Fee schedules, tiers, applicability and interchange netting (Phase 5).
///
/// <para>
/// Two properties of the design are visible on this screen rather than buried
/// in the engine. First, every amount is a minor-unit integer: a tier band of
/// "1.000 JOD" is stored as 1000, and the form says so, because a schedule
/// entered in major units under-charges by three orders of magnitude and
/// nothing would fail. Second, effective dating is mandatory (§12) — a tariff
/// change must never retroactively alter last month's figures, so a rate
/// change is a new schedule with a later <c>EffectiveFrom</c>, not an edit.
/// </para>
/// </summary>
[Authorize]
public sealed class FeesController(
    PortalQueries queries,
    ConfigurationRepository config,
    Microsoft.Data.SqlClient.SqlConnection connection,
    AccessService access,
    AuditService audit) : Controller
{
    public async Task<IActionResult> Index(int? id, int? feeScheduleId)
    {
        ViewData["Title"] = "Fees & interchange";

        var grants = await access.GrantsAsync(User).ConfigureAwait(false);
        var counterparties = await queries.CounterpartiesAsync(User).ConfigureAwait(false);
        var selectedId = id ?? counterparties.FirstOrDefault()?.CounterpartyId;

        var selected = selectedId is { } cp
            ? counterparties.FirstOrDefault(c => c.CounterpartyId == cp)
            : null;

        ViewData["Counterparties"] = counterparties;
        ViewData["Selected"] = selected;
        ViewData["Currencies"] = (await config.LoadCurrenciesAsync().ConfigureAwait(false))
            .Values.OrderBy(c => c.Code, StringComparer.Ordinal).ToList();

        if (selected is not null)
        {
            ViewData["Schedules"] = await SchedulesAsync(selected.CounterpartyId).ConfigureAwait(false);

            ViewData["Datasets"] = (await queries.DatasetsAsync(User).ConfigureAwait(false))
                .Where(d => d.CounterpartyId == selected.CounterpartyId).ToList();

            ViewData["Applicability"] = await ApplicabilityAsync(selected.CounterpartyId)
                .ConfigureAwait(false);

            ViewData["Summaries"] = await SummariesAsync(selected.CounterpartyId).ConfigureAwait(false);
            ViewData["EditingScheduleId"] = feeScheduleId;
        }
        else
        {
            ViewData["Schedules"] = new List<FeeScheduleRow>();
            ViewData["Datasets"] = new List<Models.DatasetRow>();
            ViewData["Applicability"] = new List<ApplicabilityRow>();
            ViewData["Summaries"] = new List<InterchangeRow>();
        }

        ViewData["CanConfigure"] = selected is not null
            && grants.TryGetValue(selected.CounterpartyId, out var level)
            && level >= AccessLevel.Configure;

        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSchedule(
        int counterpartyId,
        int? feeScheduleId,
        string code,
        string name,
        string direction,
        string? transactionType,
        string feeParty,
        string currencyCode,
        string roundingMode,
        DateOnly effectiveFrom,
        DateOnly? effectiveTo,
        bool isActive)
    {
        await access.RequireAsync(User, counterpartyId, AccessLevel.Configure).ConfigureAwait(false);

        try
        {
            if (feeScheduleId is { } existing)
            {
                await Db.ExecuteAsync(
                    connection,
                    """
                    UPDATE cfg.FeeSchedule
                    SET Code = @code, Name = @name, Direction = @direction,
                        TransactionType = @txnType, FeeParty = @party, CurrencyCode = @currency,
                        RoundingMode = @rounding, EffectiveFrom = @from, EffectiveTo = @to,
                        IsActive = @active
                    WHERE FeeScheduleId = @id;
                    """,
                    Bind(existing)).ConfigureAwait(false);

                await audit.RecordAsync(User, "FeeSchedule", existing, AuditAction.Update,
                    after: new { code, direction, feeParty, effectiveFrom, effectiveTo })
                    .ConfigureAwait(false);

                TempData["Ok"] = $"Fee schedule {code} saved.";
            }
            else
            {
                var id = await Db.ScalarAsync<int>(
                    connection,
                    """
                    INSERT cfg.FeeSchedule
                        (CounterpartyId, Code, Name, Direction, TransactionType, FeeParty,
                         CurrencyCode, RoundingMode, EffectiveFrom, EffectiveTo, IsActive)
                    VALUES (@cp, @code, @name, @direction, @txnType, @party,
                            @currency, @rounding, @from, @to, @active);
                    SELECT CAST(SCOPE_IDENTITY() AS INT);
                    """,
                    Bind(null)).ConfigureAwait(false);

                await audit.RecordAsync(User, "FeeSchedule", id, AuditAction.Create,
                    after: new { code, direction, feeParty, effectiveFrom }).ConfigureAwait(false);

                TempData["Ok"] =
                    $"Fee schedule {code} created. It charges nothing until it has tiers covering " +
                    "the amounts it will see.";
            }
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            TempData["Error"] = "The database refused this schedule: " + ex.Message;
        }

        return RedirectToAction(nameof(Index), new { id = counterpartyId });

        Action<Microsoft.Data.SqlClient.SqlCommand> Bind(int? id) => command =>
        {
            if (id is { } value)
            {
                command.With("@id", value);
            }

            command.With("@cp", counterpartyId)
                   .With("@code", code?.Trim())
                   .With("@name", name?.Trim())
                   .With("@direction", direction)
                   .With("@txnType", string.IsNullOrWhiteSpace(transactionType) ? null : transactionType.Trim())
                   .With("@party", feeParty)
                   .With("@currency", currencyCode)
                   .With("@rounding", roundingMode)
                   .With("@from", effectiveFrom.ToDateTime(TimeOnly.MinValue))
                   .With("@to", effectiveTo?.ToDateTime(TimeOnly.MinValue))
                   .With("@active", isActive);
        };
    }

    /// <summary>
    /// Adds or edits one tier.
    ///
    /// <para>
    /// A gap between tiers is refused here rather than at run time. The engine
    /// throws on a transaction no tier covers — deliberately, because the
    /// alternative is charging nothing and nobody noticing — but a run that
    /// dies at 3am is a worse way to learn about it than a message on the
    /// screen where the band was typed.
    /// </para>
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveTier(
        int counterpartyId,
        int feeScheduleId,
        int? feeTierId,
        long amountFromMinor,
        long? amountToMinor,
        string calculationType,
        long fixedAmountMinor,
        decimal percentage,
        long? minFeeMinor,
        long? maxFeeMinor)
    {
        await access.RequireAsync(User, counterpartyId, AccessLevel.Configure).ConfigureAwait(false);

        try
        {
            if (feeTierId is { } existing)
            {
                await Db.ExecuteAsync(
                    connection,
                    """
                    UPDATE cfg.FeeTier
                    SET AmountFromMinor = @from, AmountToMinor = @to,
                        CalculationType = @calc, FixedAmountMinor = @fixed,
                        Percentage = @pct, MinFeeMinor = @min, MaxFeeMinor = @max
                    WHERE FeeTierId = @id;
                    """,
                    Bind(existing)).ConfigureAwait(false);

                await audit.RecordAsync(User, "FeeTier", existing, AuditAction.Update,
                    after: new { amountFromMinor, amountToMinor, calculationType }).ConfigureAwait(false);
            }
            else
            {
                var id = await Db.ScalarAsync<int>(
                    connection,
                    """
                    INSERT cfg.FeeTier
                        (FeeScheduleId, AmountFromMinor, AmountToMinor, CalculationType,
                         FixedAmountMinor, Percentage, MinFeeMinor, MaxFeeMinor)
                    VALUES (@schedule, @from, @to, @calc, @fixed, @pct, @min, @max);
                    SELECT CAST(SCOPE_IDENTITY() AS INT);
                    """,
                    Bind(null)).ConfigureAwait(false);

                await audit.RecordAsync(User, "FeeTier", id, AuditAction.Create,
                    after: new { feeScheduleId, amountFromMinor, amountToMinor, calculationType })
                    .ConfigureAwait(false);
            }

            var gaps = await GapsAsync(feeScheduleId).ConfigureAwait(false);

            TempData[gaps.Count > 0 ? "Error" : "Ok"] = gaps.Count > 0
                ? "Tier saved, but the bands have a gap: " + string.Join("; ", gaps) +
                  ". A transaction landing in a gap fails the run rather than being charged zero."
                : "Tier saved; the bands are continuous.";
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            TempData["Error"] = "The database refused this tier: " + ex.Message;
        }

        return RedirectToAction(nameof(Index), new { id = counterpartyId, feeScheduleId });

        Action<Microsoft.Data.SqlClient.SqlCommand> Bind(int? id) => command =>
        {
            if (id is { } value)
            {
                command.With("@id", value);
            }

            command.With("@schedule", feeScheduleId)
                   .With("@from", amountFromMinor)
                   .With("@to", amountToMinor)
                   .With("@calc", calculationType)
                   .With("@fixed", fixedAmountMinor)
                   .With("@pct", percentage)
                   .With("@min", minFeeMinor)
                   .With("@max", maxFeeMinor);
        };
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteTier(int counterpartyId, int feeScheduleId, int feeTierId)
    {
        await access.RequireAsync(User, counterpartyId, AccessLevel.Configure).ConfigureAwait(false);

        // A tier already charged against is kept: ops.TransactionFee names the
        // tier that produced each fee, and deleting it would make a past
        // invoice unexplainable.
        var used = await Db.ScalarAsync<int>(
            connection,
            """
            SELECT CASE WHEN EXISTS (SELECT 1 FROM ops.TransactionFee WHERE FeeTierId = @id)
                        THEN 1 ELSE 0 END;
            """,
            c => c.With("@id", feeTierId)).ConfigureAwait(false);

        if (used > 0)
        {
            TempData["Error"] =
                "This tier has charged fees, so it cannot be deleted: every stored fee names the " +
                "tier that produced it. Change the schedule's effective dates instead.";

            return RedirectToAction(nameof(Index), new { id = counterpartyId, feeScheduleId });
        }

        await Db.ExecuteAsync(
            connection, "DELETE cfg.FeeTier WHERE FeeTierId = @id;",
            c => c.With("@id", feeTierId)).ConfigureAwait(false);

        await audit.RecordAsync(User, "FeeTier", feeTierId, AuditAction.Delete).ConfigureAwait(false);

        TempData["Ok"] = "Tier removed.";
        return RedirectToAction(nameof(Index), new { id = counterpartyId, feeScheduleId });
    }

    /// <summary>
    /// Whether fees apply for a dataset and transaction type — configuration,
    /// not an assumption, because it is confirmed that some reconciliation
    /// reports carry fees and some do not.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveApplicability(
        int counterpartyId,
        int datasetId,
        string? transactionType,
        bool isFeeApplicable,
        int? feeScheduleId)
    {
        await access.RequireAsync(User, counterpartyId, AccessLevel.Configure).ConfigureAwait(false);

        if (isFeeApplicable && feeScheduleId is null)
        {
            TempData["Error"] = "A rule saying fees apply must name the schedule that applies them.";
            return RedirectToAction(nameof(Index), new { id = counterpartyId });
        }

        try
        {
            var type = string.IsNullOrWhiteSpace(transactionType) ? null : transactionType.Trim();

            // The two filtered unique indexes make one ruling per
            // (dataset, type) — so this replaces rather than appends, or the
            // save would fail on the second edit of the same rule.
            await Db.ExecuteAsync(
                connection,
                """
                DELETE cfg.FeeApplicability
                WHERE DatasetId = @ds
                  AND ((@type IS NULL AND TransactionType IS NULL) OR TransactionType = @type);

                INSERT cfg.FeeApplicability (DatasetId, TransactionType, IsFeeApplicable, FeeScheduleId)
                VALUES (@ds, @type, @applicable, @schedule);
                """,
                c => c.With("@ds", datasetId)
                      .With("@type", type)
                      .With("@applicable", isFeeApplicable)
                      .With("@schedule", isFeeApplicable ? feeScheduleId : null)).ConfigureAwait(false);

            await audit.RecordAsync(User, "FeeApplicability", $"{datasetId}/{type ?? "*"}",
                AuditAction.Update, after: new { datasetId, type, isFeeApplicable, feeScheduleId })
                .ConfigureAwait(false);

            TempData["Ok"] = "Applicability saved.";
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            TempData["Error"] = "The database refused this rule: " + ex.Message;
        }

        return RedirectToAction(nameof(Index), new { id = counterpartyId });
    }

    /// <summary>
    /// Calculates one run's fees and stores the netting.
    ///
    /// <para>
    /// Separate from the run itself on purpose: the fee calculation reads the
    /// run's staged rows, so it can be re-done after a tariff correction
    /// without re-reconciling 2M transactions. It is idempotent — it replaces
    /// its own rows rather than doubling them.
    /// </para>
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Calculate(int counterpartyId, long runId)
    {
        await access.RequireAsync(User, counterpartyId, AccessLevel.Operate).ConfigureAwait(false);

        var run = await Db.QueryAsync(
            connection,
            """
            SELECT r.DefinitionId, r.BusinessDate, r.StagingRunId, d.CounterpartyId
            FROM ops.ReconRun AS r
            JOIN cfg.ReconciliationDefinition AS d ON d.DefinitionId = r.DefinitionId
            WHERE r.RunId = @run;
            """,
            r => (
                DefinitionId: r.GetInt32(0),
                BusinessDate: DateOnly.FromDateTime(r.GetDateTime(1)),
                StagingRunId: r.GetInt64(2),
                CounterpartyId: r.GetInt32(3)),
            c => c.With("@run", runId)).ConfigureAwait(false);

        if (run.Count == 0 || run[0].CounterpartyId != counterpartyId)
        {
            TempData["Error"] = $"Run {runId} does not belong to this counterparty.";
            return RedirectToAction(nameof(Index), new { id = counterpartyId });
        }

        var definition = await config.LoadDefinitionAsync(run[0].DefinitionId).ConfigureAwait(false);
        var (from, to) = definition.WindowFor(run[0].BusinessDate);

        try
        {
            var result = await new FeeRunner(connection).CalculateAsync(
                definition, runId, run[0].StagingRunId, run[0].BusinessDate, from, to)
                .ConfigureAwait(false);

            await audit.RecordAsync(User, "InterchangeSummary", runId, AuditAction.Execute,
                after: new { result.Applicable, result.TransactionCount, result.Netting.NettingMinor },
                notes: $"Fee calculation for run {runId}").ConfigureAwait(false);

            var scale = Fmt.Scale(definition.Left.DefaultCurrency);

            TempData[result.Applicable ? "Ok" : "Error"] = result.Applicable
                ? string.Create(CultureInfo.InvariantCulture,
                    $"Run {runId}: {result.TransactionCount:N0} fees calculated, {result.SkippedNotApplicable:N0} rows not fee-bearing. Netting {MinorUnits.Format(result.Netting.NettingMinor, scale)}.")
                : $"Run {runId} has no fee schedule covering its business date, so nothing was charged.";
        }
        catch (InvalidOperationException ex)
        {
            // A tier gap or an unknown currency. The message names the actual
            // configuration problem, which is what the operator needs.
            TempData["Error"] = "Fee calculation refused to guess: " + ex.Message;
        }

        return RedirectToAction(nameof(Index), new { id = counterpartyId });
    }

    /// <summary>
    /// Records the counterparty's own stated netting figure, which is what
    /// <c>ops.InterchangeSummary.DifferenceMinor</c> compares against. Without
    /// it the netting is only our own arithmetic agreeing with itself.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveReported(
        int counterpartyId, long interchangeSummaryId, long? reportedNettingMinor)
    {
        await access.RequireAsync(User, counterpartyId, AccessLevel.Operate).ConfigureAwait(false);

        await Db.ExecuteAsync(
            connection,
            """
            UPDATE ops.InterchangeSummary
            SET ReportedNettingMinor = @reported
            WHERE InterchangeSummaryId = @id;
            """,
            c => c.With("@id", interchangeSummaryId).With("@reported", reportedNettingMinor))
            .ConfigureAwait(false);

        await audit.RecordAsync(User, "InterchangeSummary", interchangeSummaryId, AuditAction.Update,
            after: new { reportedNettingMinor }).ConfigureAwait(false);

        TempData["Ok"] = "Reported netting recorded.";
        return RedirectToAction(nameof(Index), new { id = counterpartyId });
    }

    // =================================================================
    // Reads
    // =================================================================

    private async Task<List<FeeScheduleRow>> SchedulesAsync(int counterpartyId)
    {
        var schedules = await Db.QueryAsync(
            connection,
            """
            SELECT FeeScheduleId, Code, Name, Direction, TransactionType, FeeParty,
                   CurrencyCode, RoundingMode, EffectiveFrom, EffectiveTo, IsActive
            FROM cfg.FeeSchedule WHERE CounterpartyId = @cp ORDER BY Code, EffectiveFrom;
            """,
            r => new FeeScheduleRow
            {
                FeeScheduleId = r.GetInt32(0),
                Code = r.GetString(1),
                Name = r.GetString(2),
                Direction = r.GetString(3),
                TransactionType = r.GetNullableString("TransactionType"),
                FeeParty = r.GetString(5),
                CurrencyCode = r.GetString(6),
                RoundingMode = r.GetString(7),
                EffectiveFrom = DateOnly.FromDateTime(r.GetDateTime(8)),
                EffectiveTo = r.IsDBNull(9) ? null : DateOnly.FromDateTime(r.GetDateTime(9)),
                IsActive = r.GetBoolean(10),
                Tiers = [],
                Gaps = [],
            },
            c => c.With("@cp", counterpartyId)).ConfigureAwait(false);

        var result = new List<FeeScheduleRow>();

        foreach (var schedule in schedules)
        {
            var tiers = await Db.QueryAsync(
                connection,
                """
                SELECT FeeTierId, AmountFromMinor, AmountToMinor, CalculationType,
                       FixedAmountMinor, Percentage, MinFeeMinor, MaxFeeMinor,
                       CASE WHEN EXISTS (SELECT 1 FROM ops.TransactionFee AS f
                                         WHERE f.FeeTierId = t.FeeTierId)
                            THEN 1 ELSE 0 END AS IsUsed
                FROM cfg.FeeTier AS t WHERE t.FeeScheduleId = @id ORDER BY t.AmountFromMinor;
                """,
                r => new FeeTierRow
                {
                    FeeTierId = r.GetInt32(0),
                    AmountFromMinor = r.GetInt64(1),
                    AmountToMinor = r.IsDBNull(2) ? null : r.GetInt64(2),
                    CalculationType = r.GetString(3),
                    FixedAmountMinor = r.GetInt64(4),
                    Percentage = r.GetDecimal(5),
                    MinFeeMinor = r.IsDBNull(6) ? null : r.GetInt64(6),
                    MaxFeeMinor = r.IsDBNull(7) ? null : r.GetInt64(7),
                    IsUsed = r.GetInt32(8) == 1,
                },
                c => c.With("@id", schedule.FeeScheduleId)).ConfigureAwait(false);

            result.Add(schedule with { Tiers = tiers, Gaps = Gaps(tiers) });
        }

        return result;
    }

    private async Task<List<string>> GapsAsync(int feeScheduleId)
    {
        var tiers = await Db.QueryAsync(
            connection,
            """
            SELECT FeeTierId, AmountFromMinor, AmountToMinor, CalculationType,
                   FixedAmountMinor, Percentage, MinFeeMinor, MaxFeeMinor
            FROM cfg.FeeTier WHERE FeeScheduleId = @id ORDER BY AmountFromMinor;
            """,
            r => new FeeTierRow
            {
                FeeTierId = r.GetInt32(0),
                AmountFromMinor = r.GetInt64(1),
                AmountToMinor = r.IsDBNull(2) ? null : r.GetInt64(2),
                CalculationType = r.GetString(3),
                FixedAmountMinor = r.GetInt64(4),
                Percentage = r.GetDecimal(5),
                MinFeeMinor = r.IsDBNull(6) ? null : r.GetInt64(6),
                MaxFeeMinor = r.IsDBNull(7) ? null : r.GetInt64(7),
                IsUsed = false,
            },
            c => c.With("@id", feeScheduleId)).ConfigureAwait(false);

        return Gaps(tiers);
    }

    /// <summary>
    /// The bands' holes, in minor units. Includes the hole below the first
    /// band: a lowest tier starting at 1 leaves a zero-amount transaction
    /// uncovered, and zero-amount rows do arrive.
    /// </summary>
    internal static List<string> Gaps(List<FeeTierRow> tiers)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        var gaps = new List<string>();

        if (tiers.Count == 0)
        {
            return gaps;
        }

        if (tiers[0].AmountFromMinor > 0)
        {
            gaps.Add(string.Create(CultureInfo.InvariantCulture,
                $"0 to {tiers[0].AmountFromMinor - 1} is not covered"));
        }

        for (var i = 0; i < tiers.Count - 1; i++)
        {
            if (tiers[i].AmountToMinor is not { } upper)
            {
                gaps.Add(string.Create(CultureInfo.InvariantCulture,
                    $"the open-ended band from {tiers[i].AmountFromMinor} is not the last one, so the bands after it are unreachable"));

                continue;
            }

            var next = tiers[i + 1].AmountFromMinor;

            if (next > upper + 1)
            {
                gaps.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{upper + 1} to {next - 1} is not covered"));
            }
            else if (next <= upper)
            {
                gaps.Add(string.Create(CultureInfo.InvariantCulture,
                    $"the bands ending at {upper} and starting at {next} overlap, so the fee depends on which tier is read first"));
            }
        }

        if (tiers[^1].AmountToMinor is { } last)
        {
            gaps.Add(string.Create(CultureInfo.InvariantCulture,
                $"nothing covers amounts above {last} — the top band should be open-ended"));
        }

        return gaps;
    }

    private Task<List<ApplicabilityRow>> ApplicabilityAsync(int counterpartyId) =>
        Db.QueryAsync(
            connection,
            """
            SELECT a.FeeApplicabilityId, d.DatasetId, d.Code, a.TransactionType,
                   a.IsFeeApplicable, s.Code
            FROM cfg.FeeApplicability AS a
            JOIN cfg.Dataset AS d ON d.DatasetId = a.DatasetId
            LEFT JOIN cfg.FeeSchedule AS s ON s.FeeScheduleId = a.FeeScheduleId
            WHERE d.CounterpartyId = @cp
            ORDER BY d.Code, a.TransactionType;
            """,
            r => new ApplicabilityRow(
                r.GetInt32(0), r.GetInt32(1), r.GetString(2),
                r.GetNullableString("TransactionType"), r.GetBoolean(4),
                r.IsDBNull(5) ? null : r.GetString(5)),
            c => c.With("@cp", counterpartyId));

    private Task<List<InterchangeRow>> SummariesAsync(int counterpartyId) =>
        Db.QueryAsync(
            connection,
            """
            SELECT TOP (30) s.InterchangeSummaryId, s.RunId, d.Code, s.BusinessDate, s.CurrencyCode,
                   s.InwardCnt, s.OutwardCnt, s.InwardAmountMinor, s.OutwardAmountMinor,
                   s.RevenueMinor, s.CostMinor, s.NettingMinor, s.ReportedNettingMinor,
                   s.DifferenceMinor
            FROM ops.InterchangeSummary AS s
            JOIN ops.ReconRun AS r ON r.RunId = s.RunId
            JOIN cfg.ReconciliationDefinition AS d ON d.DefinitionId = r.DefinitionId
            WHERE d.CounterpartyId = @cp
            ORDER BY s.InterchangeSummaryId DESC;
            """,
            r => new InterchangeRow
            {
                InterchangeSummaryId = r.GetInt64(0),
                RunId = r.GetInt64(1),
                DefinitionCode = r.GetString(2),
                BusinessDate = DateOnly.FromDateTime(r.GetDateTime(3)),
                CurrencyCode = r.GetString(4),
                InwardCount = r.GetInt64(5),
                OutwardCount = r.GetInt64(6),
                InwardAmountMinor = r.GetInt64(7),
                OutwardAmountMinor = r.GetInt64(8),
                RevenueMinor = r.GetInt64(9),
                CostMinor = r.GetInt64(10),
                NettingMinor = r.GetInt64(11),
                ReportedNettingMinor = r.IsDBNull(12) ? null : r.GetInt64(12),
                DifferenceMinor = r.GetInt64(13),
            },
            c => c.With("@cp", counterpartyId));
}

public sealed record FeeScheduleRow
{
    public required int FeeScheduleId { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string Direction { get; init; }
    public string? TransactionType { get; init; }
    public required string FeeParty { get; init; }
    public required string CurrencyCode { get; init; }
    public required string RoundingMode { get; init; }
    public required DateOnly EffectiveFrom { get; init; }
    public DateOnly? EffectiveTo { get; init; }
    public required bool IsActive { get; init; }
    public required List<FeeTierRow> Tiers { get; init; }
    public required List<string> Gaps { get; init; }
}

public sealed record FeeTierRow
{
    public required int FeeTierId { get; init; }
    public required long AmountFromMinor { get; init; }
    public long? AmountToMinor { get; init; }
    public required string CalculationType { get; init; }
    public required long FixedAmountMinor { get; init; }
    public required decimal Percentage { get; init; }
    public long? MinFeeMinor { get; init; }
    public long? MaxFeeMinor { get; init; }
    public required bool IsUsed { get; init; }
}

public sealed record ApplicabilityRow(
    int FeeApplicabilityId,
    int DatasetId,
    string DatasetCode,
    string? TransactionType,
    bool IsFeeApplicable,
    string? ScheduleCode);

public sealed record InterchangeRow
{
    public required long InterchangeSummaryId { get; init; }
    public required long RunId { get; init; }
    public required string DefinitionCode { get; init; }
    public required DateOnly BusinessDate { get; init; }
    public required string CurrencyCode { get; init; }
    public required long InwardCount { get; init; }
    public required long OutwardCount { get; init; }
    public required long InwardAmountMinor { get; init; }
    public required long OutwardAmountMinor { get; init; }
    public required long RevenueMinor { get; init; }
    public required long CostMinor { get; init; }
    public required long NettingMinor { get; init; }
    public long? ReportedNettingMinor { get; init; }
    public required long DifferenceMinor { get; init; }

    public bool Ties => ReportedNettingMinor is not null && DifferenceMinor == 0;
}
