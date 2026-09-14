namespace Recon.Domain.Money;

/// <summary>A row of <c>cfg.Currency</c>.</summary>
public sealed record Currency(string Code, string Name, int MinorUnits)
{
    public long ToMinor(decimal amount) => Money.MinorUnits.ToMinor(amount, MinorUnits);

    public long ToMinor(decimal amount, Rounding rounding) =>
        Money.MinorUnits.ToMinor(amount, MinorUnits, rounding);

    public string Format(long minor) => Money.MinorUnits.Format(minor, MinorUnits);
}
