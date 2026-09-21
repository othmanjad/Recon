using Recon.Engine.Parsing;
using Xunit;

namespace Recon.UnitTests;

public class TransformTests
{
    private static string? Apply(string? value, string json) =>
        Transforms.Apply(value, Transforms.Parse(json));

    [Theory]
    [InlineData("  E2E-1  ", """[{"op":"Trim"}]""", "E2E-1")]
    [InlineData("e2e-1", """[{"op":"Upper"}]""", "E2E-1")]
    [InlineData("E2E-1", """[{"op":"Lower"}]""", "e2e-1")]
    [InlineData("E2E 1", """[{"op":"StripWhitespace"}]""", "E2E1")]
    [InlineData("E2E-1/A", """[{"op":"StripNonAlphanumeric"}]""", "E2E1A")]
    [InlineData("000123", """[{"op":"StripLeadingZeros"}]""", "123")]
    [InlineData("000", """[{"op":"StripLeadingZeros"}]""", "0")]
    public void AppliesSingleTransforms(string input, string json, string expected) =>
        Assert.Equal(expected, Apply(input, json));

    [Fact]
    public void AppliesAChainInOrder() =>
        // Order matters: trimming after stripping non-alphanumerics would be a
        // no-op, and the chain is user-configured.
        Assert.Equal("E2E1", Apply("  e2e-1 ", """[{"op":"Trim"},{"op":"Upper"},{"op":"StripNonAlphanumeric"}]"""));

    [Fact]
    public void SubstringTakesStartAndLength() =>
        Assert.Equal("2026", Apply("20260913120000", """[{"op":"Substring","start":0,"length":4}]"""));

    [Fact]
    public void SubstringPastTheEndYieldsEmptyRatherThanThrowing() =>
        Assert.Equal(string.Empty, Apply("abc", """[{"op":"Substring","start":10,"length":4}]"""));

    [Fact]
    public void SubstringClampsALengthBeyondTheValue() =>
        Assert.Equal("bc", Apply("abc", """[{"op":"Substring","start":1,"length":99}]"""));

    [Fact]
    public void RegexExtractTakesTheRequestedGroup() =>
        Assert.Equal("0099412",
            Apply("E2E-20260913-0099412", """[{"op":"RegexExtract","pattern":"-(\\d{7})$","group":1}]"""));

    [Fact]
    public void RegexExtractWithNoMatchYieldsNull() =>
        Assert.Null(Apply("no digits here", """[{"op":"RegexExtract","pattern":"(\\d+)$","group":1}]"""));

    [Fact]
    public void ReplaceSubstitutes() =>
        Assert.Equal("E2E/1", Apply("E2E-1", """[{"op":"Replace","from":"-","to":"/"}]"""));

    [Fact]
    public void ReplaceWithNoToRemoves() =>
        Assert.Equal("E2E1", Apply("E2E-1", """[{"op":"Replace","from":"-"}]"""));

    [Fact]
    public void NullPassesThrough() => Assert.Null(Apply(null, """[{"op":"Trim"}]"""));

    [Fact]
    public void AnEmptyChainIsTheIdentity() => Assert.Equal("E2E-1", Apply("E2E-1", "[]"));

    [Fact]
    public void AnUnknownTransformIsRejectedAtParseTime() =>
        // Rejected on save rather than at 2 a.m. on the first production file.
        Assert.Throws<TransformException>(() => Transforms.Parse("""[{"op":"Bogus"}]"""));

    [Fact]
    public void SubstringWithNoStartIsRejected() =>
        Assert.Throws<TransformException>(() => Transforms.Parse("""[{"op":"Substring","length":4}]"""));

    [Fact]
    public void AnInvalidRegexIsRejectedAtParseTime() =>
        Assert.Throws<TransformException>(
            () => Transforms.Parse("""[{"op":"RegexExtract","pattern":"([unclosed"}]"""));

    [Fact]
    public void ReplaceWithNoFromIsRejected() =>
        Assert.Throws<TransformException>(() => Transforms.Parse("""[{"op":"Replace","to":"x"}]"""));

    // =================================================================
    // Normalize — the companion-slot value (blocker A2)
    // =================================================================

    [Theory]
    [InlineData("e2e-2026 0913/0099412", "E2E202609130099412")]
    [InlineData("  E2E-1  ", "E2E1")]
    [InlineData("E2E_1", "E2E1")]
    [InlineData("", "")]
    public void NormalizeStripsFormattingNoise(string input, string expected) =>
        Assert.Equal(expected, Transforms.Normalize(input));

    [Fact]
    public void NormalizeCollapsesTheWaysTwoSystemsSpellOneReference()
    {
        // The whole point of the companion slot: these are one reference, and
        // the primary pass would miss the pairing without it.
        string[] spellings = ["E2E-20260913-0099412", "e2e 20260913 0099412", "E2E/20260913/0099412"];

        var normalized = spellings.Select(Transforms.Normalize).Distinct().ToList();

        Assert.Single(normalized);
        Assert.Equal("E2E202609130099412", normalized[0]);
    }

    /* Map: the operation the direction columns need. Two sides spell one
       meaning differently — STTS_CD says debt/crdt, the other side sends a
       boolean — and a reconciliation cannot compare them until one vocabulary
       is chosen at parse time. */

    private const string DirectionMap = """
        [{"op":"Map","cases":[{"from":"true","to":"Inward"},{"from":"false","to":"Outward"}]}]
        """;

    [Theory]
    [InlineData("true", "Inward")]
    [InlineData("false", "Outward")]
    [InlineData("TRUE", "Inward")]
    [InlineData("False", "Outward")]
    public void MapTranslatesAWholeValue(string input, string expected) =>
        Assert.Equal(expected, Apply(input, DirectionMap));

    [Fact]
    public void MapLeavesAValueItDoesNotNameAlone() =>
        // Not "Outward", and not empty: a spelling nobody anticipated reaches
        // staging as it was written, where a rule can catch it. Turning it
        // into one of the two known values would be inventing data.
        Assert.Equal("maybe", Apply("maybe", DirectionMap));

    [Fact]
    public void MapUsesOtherwiseWhenTheConfigurationSaysWhatToDoWithAStranger() =>
        Assert.Equal("UNKNOWN", Apply("maybe", """
            [{"op":"Map","cases":[{"from":"true","to":"Inward"}],"otherwise":"UNKNOWN"}]
            """));

    [Fact]
    public void MapMatchesTheWholeValueRatherThanASubstring()
    {
        // This is why Map exists beside Replace: Replace would rewrite the
        // token wherever it appeared.
        Assert.Equal("not true", Apply("not true", DirectionMap));
        Assert.Equal("not Inward", Apply("not true", """
            [{"op":"Replace","from":"true","to":"Inward"}]
            """));
    }

    [Fact]
    public void MapCanBeCaseSensitiveWhenTheFileReallyMeansIt() =>
        Assert.Equal("D", Apply("D", """
            [{"op":"Map","ignoreCase":false,"cases":[{"from":"d","to":"Debit"}]}]
            """));

    [Fact]
    public void MapNeedsACase() =>
        Assert.Throws<TransformException>(() => Transforms.Parse("""[{"op":"Map","cases":[]}]"""));

    [Fact]
    public void MapRefusesAValueItNamesTwice() =>
        // The second line could never fire, and the one it shadows is rarely
        // the one that was meant.
        Assert.Throws<TransformException>(() => Transforms.Parse("""
            [{"op":"Map","cases":[{"from":"true","to":"Inward"},{"from":"TRUE","to":"Outward"}]}]
            """));

    [Fact]
    public void NormalizeIsIdempotent()
    {
        var once = Transforms.Normalize("e2e-1/a");
        Assert.Equal(once, Transforms.Normalize(once));
    }
}
