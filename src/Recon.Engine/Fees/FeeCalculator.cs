using Recon.Domain.Money;

namespace Recon.Engine.Fees;

/// <summary>
/// Calculates the fee on one transaction.
///
/// <para>
/// <b>Rounded per transaction, then summed — never the other way round.</b>
/// The design names summing first and rounding last as the most common cause
/// of netting discrepancies that never close (§7.1), because it produces a
/// different figure from the counterparty's and the difference is a few fils
/// that nobody can attribute to a row.
/// </para>
///
/// <para>
/// The calculation is transaction-level for the same reason the module
/// exists: an aggregate-only figure is one you cannot defend. Transaction-level
/// lets you answer "why is our netting 4.120 JOD below theirs" by pointing at
/// rows (§12).
/// </para>
/// </summary>
public static class FeeCalculator
{
    public static long Calculate(FeeSchedule schedule, FeeTier tier, long baseAmountMinor, int minorUnits)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(tier);

        if (baseAmountMinor < 0)
        {
            // A negative base is a reversal, and a reversal's fee treatment is
            // a business question rather than an arithmetic one. Failing is
            // better than inventing a sign convention.
            throw new ArgumentOutOfRangeException(
                nameof(baseAmountMinor), baseAmountMinor,
                "a negative base amount needs a stated fee treatment for reversals");
        }

        var fee = tier.Calculation switch
        {
            FeeCalculation.Fixed => tier.FixedAmountMinor,

            FeeCalculation.Percentage =>
                Percentage(baseAmountMinor, tier.Percentage, schedule.Rounding, minorUnits),

            FeeCalculation.FixedPlusPercentage =>
                tier.FixedAmountMinor
                + Percentage(baseAmountMinor, tier.Percentage, schedule.Rounding, minorUnits),

            _ => throw new ArgumentOutOfRangeException(nameof(tier), tier.Calculation, null),
        };

        // Caps apply AFTER the calculation and after rounding, which is the
        // order a tariff document means by "minimum 0.100, maximum 5.000".
        if (tier.MinFeeMinor is { } min && fee < min)
        {
            fee = min;
        }

        if (tier.MaxFeeMinor is { } max && fee > max)
        {
            fee = max;
        }

        return fee;
    }

    /// <summary>
    /// A percentage of an integer minor-unit amount, rounded once, here.
    ///
    /// <para>
    /// The intermediate is <see cref="decimal"/> and never
    /// <see cref="double"/>: a percentage of a large amount in binary floating
    /// point is off by an amount that shows up as a netting difference.
    /// </para>
    /// </summary>
    private static long Percentage(
        long baseAmountMinor, decimal percentage, Rounding rounding, int minorUnits)
    {
        var raw = baseAmountMinor * percentage / 100m;

        return rounding switch
        {
            Rounding.HalfUp => (long)decimal.Round(raw, 0, MidpointRounding.AwayFromZero),
            Rounding.HalfEven => (long)decimal.Round(raw, 0, MidpointRounding.ToEven),
            Rounding.Truncate => (long)decimal.Truncate(raw),
            _ => throw new ArgumentOutOfRangeException(nameof(rounding), rounding, null),
        };
    }

    /// <summary>
    /// Netting: revenue minus cost, both summed from per-transaction figures
    /// that were each rounded individually.
    /// </summary>
    public static NettingResult Net(IEnumerable<TransactionFee> fees)
    {
        ArgumentNullException.ThrowIfNull(fees);

        long revenue = 0, cost = 0, inwardCount = 0, outwardCount = 0;
        long inwardAmount = 0, outwardAmount = 0;

        foreach (var fee in fees)
        {
            if (fee.Party == FeeParty.Revenue)
            {
                revenue += fee.FeeAmountMinor;
            }
            else
            {
                cost += fee.FeeAmountMinor;
            }

            if (string.Equals(fee.Direction, "Inward", StringComparison.Ordinal))
            {
                inwardCount++;
                inwardAmount += fee.BaseAmountMinor;
            }
            else
            {
                outwardCount++;
                outwardAmount += fee.BaseAmountMinor;
            }
        }

        return new NettingResult
        {
            RevenueMinor = revenue,
            CostMinor = cost,
            InwardCount = inwardCount,
            OutwardCount = outwardCount,
            InwardAmountMinor = inwardAmount,
            OutwardAmountMinor = outwardAmount,
        };
    }
}

public sealed record TransactionFee
{
    public required long StagingId { get; init; }
    public required int FeeScheduleId { get; init; }
    public int? FeeTierId { get; init; }
    public required long BaseAmountMinor { get; init; }
    public required long FeeAmountMinor { get; init; }
    public required string CurrencyCode { get; init; }
    public required FeeParty Party { get; init; }
    public required string Direction { get; init; }
}

public sealed record NettingResult
{
    public required long RevenueMinor { get; init; }
    public required long CostMinor { get; init; }
    public required long InwardCount { get; init; }
    public required long OutwardCount { get; init; }
    public required long InwardAmountMinor { get; init; }
    public required long OutwardAmountMinor { get; init; }

    public long NettingMinor => RevenueMinor - CostMinor;
}
