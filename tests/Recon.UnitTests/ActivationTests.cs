using Recon.Domain.Configuration;
using Xunit;

namespace Recon.UnitTests;

/// <summary>
/// Review item B6: the design stated that a definition cannot be activated
/// without the universal roles, but there was no enforcement point. This is it,
/// and these tests are what make it real.
/// </summary>
public class ActivationTests
{
    [Fact]
    public void AFullyMappedDefinitionActivates() =>
        Assert.Empty(Fixtures.CliqOm().ActivationProblems());

    [Fact]
    public void AMissingUniversalRoleBlocksActivation()
    {
        // Control totals, partitioning and fee logic all rely on these, so a
        // definition without them would run and produce numbers nobody can
        // check.
        var definition = Fixtures.CliqOm();
        var stripped = definition with
        {
            Left = definition.Left with
            {
                Fields = definition.Left.Fields.Where(f => f.Role != FieldRole.Amount).ToList(),
            },
        };

        var problems = stripped.ActivationProblems();

        Assert.NotEmpty(problems);
        Assert.Contains(problems, p => p.Contains("Amount", StringComparison.Ordinal));
    }

    [Fact]
    public void ADefinitionWithNoActivePassIsBlocked()
    {
        var definition = Fixtures.CliqOm();
        var inactive = definition with
        {
            Rules = definition.Rules.Select(r => r with { IsActive = false }).ToList(),
        };

        Assert.Contains(inactive.ActivationProblems(),
            p => p.Contains("no active pass", StringComparison.Ordinal));
    }

    [Fact]
    public void ANonSeekableFirstPassIsBlocked()
    {
        // A Contains in pass 1 scans both sides in full. At 2M rows a day that
        // is the difference between a minute and an hour, so it is a blocker
        // rather than a warning — the UI warns, this refuses.
        var definition = Fixtures.CliqOm();
        var bad = definition with
        {
            Rules =
            [
                definition.Rules[0] with
                {
                    Conditions =
                    [
                        definition.Rules[0].Conditions[0] with { Comparison = ComparisonType.Contains },
                    ],
                },
            ],
        };

        Assert.Contains(bad.ActivationProblems(),
            p => p.Contains("cannot seek an index", StringComparison.Ordinal));
    }

    [Fact]
    public void ADefinitionReconcilingADatasetAgainstItselfIsBlocked()
    {
        var definition = Fixtures.CliqOm();
        var self = definition with { Right = definition.Left };

        Assert.Contains(self.ActivationProblems(),
            p => p.Contains("against itself", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ComparisonType.Exact, Indexability.Seekable)]
    [InlineData(ComparisonType.NumericExact, Indexability.Seekable)]
    [InlineData(ComparisonType.NumericTolerance, Indexability.Seekable)]
    [InlineData(ComparisonType.DateWithin, Indexability.Seekable)]
    [InlineData(ComparisonType.StartsWith, Indexability.Partial)]
    [InlineData(ComparisonType.EndsWith, Indexability.Scan)]
    [InlineData(ComparisonType.Contains, Indexability.Scan)]
    public void IndexabilityMatchesTheDesignsTable(ComparisonType type, Indexability expected) =>
        // §9.1 publishes this table to Operations; the engine's judgement and
        // the UI's warning must be the same judgement.
        Assert.Equal(expected, type.Indexability());

    [Fact]
    public void TheMatchingWindowIsNotJustTheBusinessDate()
    {
        // Finding C5: a transaction near the session cut-off legitimately lands
        // in the next session, so the provider window must straddle the date.
        var definition = Fixtures.CliqOm();
        var (from, to) = definition.WindowFor(new DateOnly(2026, 9, 13));

        Assert.Equal(new DateOnly(2026, 9, 12), from);
        Assert.Equal(new DateOnly(2026, 9, 14), to);
    }

    [Fact]
    public void TheDuplicateKeyResolvesThroughTheRegistry()
    {
        var key = Fixtures.CliqSession().DuplicateKey();

        Assert.Single(key);
        Assert.Equal("REF_PRIMARY", key[0].FieldCode);
    }

    [Fact]
    public void ADatasetWithNoDeclaredKeyHasNoDuplicateDetection() =>
        // Legitimate configuration, not an error: a summary dataset has no
        // per-transaction reference to duplicate.
        Assert.Empty(Fixtures.OrangeMoney().DuplicateKey());

    [Fact]
    public void RequestingANormalizedSlotOnAFieldWithoutOneFails()
    {
        var field = Fixtures.CliqSession().GetField("REF_TXN");

        Assert.False(field.NormalizeForMatch);
        Assert.Throws<InvalidOperationException>(() => field.SlotFor(useNormalized: true));
    }

    [Fact]
    public void FieldLookupIsCaseSensitive()
    {
        // The database collates its vocabularies case-sensitively (finding X1).
        // A rule citing ref_primary for REF_PRIMARY is a configuration error
        // worth surfacing rather than guessing at.
        var dataset = Fixtures.CliqSession();

        Assert.True(dataset.TryGetField("REF_PRIMARY", out _));
        Assert.False(dataset.TryGetField("ref_primary", out _));
    }
}
