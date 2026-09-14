using Recon.Domain.Money;
using Xunit;

namespace Recon.UnitTests;

/// <summary>
/// Money is the one place the design says "never" about an implementation
/// detail: never float, never decimal comparison, never sum-then-round. These
/// tests pin all three.
/// </summary>
public class MoneyTests
{
    [Theory]
    [InlineData("125.500", 3, 125_500L)]
    [InlineData("0.001", 3, 1L)]
    [InlineData("1000", 3, 1_000_000L)]
    [InlineData("12.34", 2, 1_234L)]
    [InlineData("-4.120", 3, -4_120L)]
    [InlineData("0", 3, 0L)]
    public void ScalesToMinorUnits(string amount, int scale, long expected) =>
        Assert.Equal(expected, MinorUnits.ToMinor(decimal.Parse(amount,
            System.Globalization.CultureInfo.InvariantCulture), scale));

    [Fact]
    public void ScaleComesFromTheCurrencyNotAConstant()
    {
        // Review finding C4: AmountFils hard-coded 3 decimals. The same amount
        // is a different integer in JOD and USD, and getting this wrong means
        // every control total in a USD reconciliation is out by a factor of 10.
        Assert.Equal(12_340L, Fixtures.Jod.ToMinor(12.34m));
        Assert.Equal(1_234L, Fixtures.Usd.ToMinor(12.34m));
    }

    [Theory]
    [InlineData(Rounding.HalfUp, "0.0005", 3, 1L)]
    [InlineData(Rounding.HalfEven, "0.0005", 3, 0L)]
    [InlineData(Rounding.Truncate, "0.0009", 3, 0L)]
    [InlineData(Rounding.HalfUp, "0.0015", 3, 2L)]
    [InlineData(Rounding.HalfEven, "0.0015", 3, 2L)]
    public void RoundingModeIsHonoured(Rounding mode, string amount, int scale, long expected) =>
        Assert.Equal(expected, MinorUnits.ToMinor(
            decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), scale, mode));

    [Fact]
    public void RoundingPerTransactionDiffersFromRoundingTheSum()
    {
        // The design names this as the most common cause of netting
        // discrepancies that never close (§7.1). This test exists to show the
        // two answers are genuinely different, so nobody "simplifies" the fee
        // calculation into a single rounded total later.
        decimal[] fees = [0.0004m, 0.0004m, 0.0004m, 0.0004m, 0.0004m];

        var perTransaction = fees.Sum(f => MinorUnits.ToMinor(f, 3));
        var sumThenRound = MinorUnits.ToMinor(fees.Sum(), 3);

        Assert.Equal(0L, perTransaction);
        Assert.Equal(2L, sumThenRound);
        Assert.NotEqual(perTransaction, sumThenRound);
    }

    [Fact]
    public void OverflowIsAnErrorRatherThanASilentlyWrongTotal() =>
        Assert.Throws<OverflowException>(
            () => MinorUnits.ToMinor(decimal.MaxValue / 100, 3));

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public void ScaleOutsideTheSchemasRangeIsRejected(int scale) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => MinorUnits.ToMinor(1m, scale));

    [Theory]
    [InlineData(125_500L, 3, "125.500")]
    [InlineData(418_923_114_000L, 3, "418,923,114.000")]
    [InlineData(-4_120L, 3, "-4.120")]
    [InlineData(1_234L, 2, "12.34")]
    [InlineData(5L, 3, "0.005")]
    [InlineData(0L, 3, "0.000")]
    public void FormatsWithoutDividing(long minor, int scale, string expected) =>
        Assert.Equal(expected, MinorUnits.Format(minor, scale));

    [Fact]
    public void RoundTripsThroughDecimalExactly()
    {
        // Every representable amount must survive the trip, or a displayed
        // figure will not equal the compared one.
        for (var minor = 0L; minor < 5_000L; minor += 7)
        {
            var asDecimal = MinorUnits.ToDecimal(minor, 3);
            Assert.Equal(minor, MinorUnits.ToMinor(asDecimal, 3));
        }
    }
}
