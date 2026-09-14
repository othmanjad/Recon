using Recon.Domain.Money;

namespace Recon.Engine.Fees;

/// <summary>
/// A row of <c>cfg.FeeSchedule</c> with its tiers.
///
/// <para>
/// Effective dating is mandatory, not a nicety: a tariff change must never
/// retroactively alter last month's figures (design §12). A schedule is
/// selected by the transaction's date, never by "the latest".
/// </para>
/// </summary>
public sealed record FeeSchedule
{
    public required int FeeScheduleId { get; init; }
    public required int CounterpartyId { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }

    /// <summary>Inward | Outward</summary>
    public required string Direction { get; init; }

    public string? TransactionType { get; init; }

    /// <summary>Revenue (paid to us) | Cost (paid by us).</summary>
    public required FeeParty Party { get; init; }

    public required string CurrencyCode { get; init; }

    /// <summary>
    /// STILL OPEN (review item C8). <c>HalfUp</c> is the schema default and a
    /// guess: the counterparty's own stated rule must be obtained before
    /// Phase 5 goes live, or netting will never tie out. The engine honours
    /// whatever is configured rather than assuming.
    /// </summary>
    public required Rounding Rounding { get; init; }

    public required DateOnly EffectiveFrom { get; init; }
    public DateOnly? EffectiveTo { get; init; }
    public bool IsActive { get; init; } = true;

    public required IReadOnlyList<FeeTier> Tiers { get; init; }

    public bool CoversDate(DateOnly date) =>
        IsActive && date >= EffectiveFrom && (EffectiveTo is null || date <= EffectiveTo);

    /// <summary>
    /// The tier whose band contains the amount. Bands are in minor units and
    /// the top tier may be open-ended.
    /// </summary>
    public FeeTier? TierFor(long amountMinor) =>
        Tiers.FirstOrDefault(t =>
            amountMinor >= t.AmountFromMinor
            && (t.AmountToMinor is null || amountMinor <= t.AmountToMinor));
}

public sealed record FeeTier
{
    public required int FeeTierId { get; init; }
    public required long AmountFromMinor { get; init; }
    public long? AmountToMinor { get; init; }
    public required FeeCalculation Calculation { get; init; }
    public long FixedAmountMinor { get; init; }
    public decimal Percentage { get; init; }
    public long? MinFeeMinor { get; init; }
    public long? MaxFeeMinor { get; init; }
}

public enum FeeCalculation { Fixed, Percentage, FixedPlusPercentage }

public enum FeeParty { Revenue, Cost }
