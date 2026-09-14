namespace Recon.Domain.Money;

/// <summary>
/// Conversion between a source decimal amount and the integer minor-unit
/// amount everything downstream uses.
///
/// The design is unambiguous about this (§7.1): never <c>float</c> or
/// <c>double</c> at any stage; store <c>DECIMAL(18,3)</c> for display and
/// derive an integer for every comparison, join and sum. Decimal comparison
/// across two systems produces phantom differences; integer comparison
/// cannot.
///
/// The scale is per currency and comes from <c>cfg.Currency.MinorUnits</c> —
/// JOD has 3 (fils), USD and EUR have 2. Hard-coding 3 was review finding C4.
/// </summary>
public static class MinorUnits
{
    /// <summary>Largest scale the schema permits (cfg.Currency CHECK).</summary>
    public const int MaxScale = 4;

    private static readonly long[] Powers = [1L, 10L, 100L, 1_000L, 10_000L];

    public static long PowerOfTen(int scale)
    {
        if (scale is < 0 or > MaxScale)
        {
            throw new ArgumentOutOfRangeException(
                nameof(scale), scale, $"Currency scale must be 0..{MaxScale}.");
        }

        return Powers[scale];
    }

    /// <summary>
    /// Scales a source amount to integer minor units.
    ///
    /// Rounding is <see cref="MidpointRounding.AwayFromZero"/> — "half up" —
    /// matching <c>cfg.FeeSchedule.RoundingMode</c>'s default. That default is
    /// a placeholder: the counterparty's own rule is still an open question
    /// (review item C8) and fee development must not start without it, or
    /// netting will never tie out. <see cref="ToMinor(decimal,int,Rounding)"/>
    /// takes the mode explicitly for that reason.
    /// </summary>
    public static long ToMinor(decimal amount, int scale) =>
        ToMinor(amount, scale, Rounding.HalfUp);

    public static long ToMinor(decimal amount, int scale, Rounding rounding)
    {
        var shifted = amount * PowerOfTen(scale);

        var rounded = rounding switch
        {
            Rounding.HalfUp => decimal.Round(shifted, 0, MidpointRounding.AwayFromZero),
            Rounding.HalfEven => decimal.Round(shifted, 0, MidpointRounding.ToEven),
            Rounding.Truncate => decimal.Truncate(shifted),
            _ => throw new ArgumentOutOfRangeException(nameof(rounding), rounding, null),
        };

        // A value beyond BIGINT would be silently wrong in the database, so it
        // is an error here rather than a surprise in a control total.
        if (rounded > long.MaxValue || rounded < long.MinValue)
        {
            throw new OverflowException(
                $"Amount {amount} at scale {scale} does not fit in a 64-bit minor-unit value.");
        }

        return (long)rounded;
    }

    /// <summary>
    /// Back to a decimal, for display and for the <c>Dec*</c> slots only.
    /// Nothing in the engine does arithmetic on the result.
    /// </summary>
    public static decimal ToDecimal(long minor, int scale) =>
        (decimal)minor / PowerOfTen(scale);

    /// <summary>
    /// Formats minor units without ever dividing: the digits are split and a
    /// group separator inserted. Used by reports and the CLI.
    /// </summary>
    public static string Format(long minor, int scale)
    {
        var negative = minor < 0;
        var digits = Math.Abs(minor).ToString(System.Globalization.CultureInfo.InvariantCulture);

        while (digits.Length <= scale)
        {
            digits = "0" + digits;
        }

        var whole = scale > 0 ? digits[..^scale] : digits;
        var fraction = scale > 0 ? digits[^scale..] : string.Empty;

        var grouped = new System.Text.StringBuilder();
        for (var i = 0; i < whole.Length; i++)
        {
            if (i > 0 && (whole.Length - i) % 3 == 0)
            {
                grouped.Append(',');
            }

            grouped.Append(whole[i]);
        }

        var result = grouped.ToString();
        if (scale > 0)
        {
            result += "." + fraction;
        }

        return negative ? "-" + result : result;
    }
}

/// <summary>
/// Mirrors <c>cfg.FeeSchedule.RoundingMode</c>. Fee rounding happens PER
/// TRANSACTION and is then summed: summing first and rounding last yields a
/// different netting figure than the counterparty's, which the design names as
/// the most common cause of netting discrepancies that never close (§7.1).
/// </summary>
public enum Rounding
{
    HalfUp,
    HalfEven,
    Truncate,
}
