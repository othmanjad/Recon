using System.Text.Json;
using Recon.Domain.Conditions;
using Xunit;

namespace Recon.UnitTests;

/// <summary>
/// The server-side condition-tree gate. The portal runs an identical check in
/// JavaScript for immediate feedback; these tests cover the copy that actually
/// protects anything.
/// </summary>
public class ConditionValidatorTests
{
    private static readonly string[] TwoStatuses = ["ACSC", "RJCT"];
    private static readonly string[] NoStatuses = [];

    private static ConditionNode Leaf(string field, string cmp, object? value = null)
    {
        var node = new ConditionNode { Field = field, Cmp = cmp };
        if (value is not null)
        {
            node.Value = JsonSerializer.SerializeToElement(value);
        }

        return node;
    }

    [Fact]
    public void AcceptsAWellFormedNestedTree()
    {
        var tree = new ConditionNode
        {
            Op = "and",
            Items =
            [
                Leaf("STATUS", "ne", "RJCT"),
                new ConditionNode
                {
                    Op = "or",
                    Items = [Leaf("DIRECTION", "eq", "Inward"), Leaf("AMOUNT", "gte", 1000)],
                },
            ],
        };

        Assert.True(ConditionValidator.Validate(tree, Fixtures.CliqSession()).IsValid);
    }

    [Fact]
    public void AnAbsentFilterIsValid() =>
        // No filter means "no filter", not "an empty filter that matches
        // nothing" — a rule without a row filter is the common case.
        Assert.True(ConditionValidator.Validate(null, Fixtures.CliqSession()).IsValid);

    [Fact]
    public void RejectsAFieldOutsideTheRegistry()
    {
        var result = ConditionValidator.Validate(Leaf("NOPE", "eq", 1), Fixtures.CliqSession());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("not in the field registry", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsANonMatchableField()
    {
        var result = ConditionValidator.Validate(Leaf("CURRENCY", "eq", "JOD"), Fixtures.CliqSession());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("not marked matchable", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsAnUnknownComparator()
    {
        var result = ConditionValidator.Validate(Leaf("STATUS", "regex", ".*"), Fixtures.CliqSession());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("unknown comparator", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsAGroupWithNoItems()
    {
        var result = ConditionValidator.Validate(
            new ConditionNode { Op = "and", Items = [] }, Fixtures.CliqSession());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("non-empty array", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsAnUnknownGroupOperator()
    {
        var result = ConditionValidator.Validate(
            new ConditionNode { Op = "xor", Items = [Leaf("STATUS", "eq", "A")] },
            Fixtures.CliqSession());

        Assert.False(result.IsValid);
    }

    [Fact]
    public void RejectsANodeThatIsBothGroupAndCondition()
    {
        var node = new ConditionNode { Op = "and", Field = "STATUS", Items = [] };
        var result = ConditionValidator.Validate(node, Fixtures.CliqSession());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("not both", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsInWithAnEmptyList()
    {
        var result = ConditionValidator.Validate(
            Leaf("STATUS", "in", NoStatuses), Fixtures.CliqSession());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("non-empty list", StringComparison.Ordinal));
    }

    [Fact]
    public void AcceptsInWithAList() =>
        Assert.True(ConditionValidator.Validate(
            Leaf("STATUS", "in", TwoStatuses), Fixtures.CliqSession()).IsValid);

    [Fact]
    public void RejectsEqWithAList()
    {
        var result = ConditionValidator.Validate(
            Leaf("STATUS", "eq", TwoStatuses), Fixtures.CliqSession());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("single value", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsEqWithNoValue()
    {
        var result = ConditionValidator.Validate(Leaf("STATUS", "eq"), Fixtures.CliqSession());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("requires a value", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsIsNullCarryingAValue()
    {
        var result = ConditionValidator.Validate(
            Leaf("STATUS", "isnull", "x"), Fixtures.CliqSession());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("takes no value", StringComparison.Ordinal));
    }

    [Fact]
    public void AcceptsIsNullWithoutAValue() =>
        Assert.True(ConditionValidator.Validate(
            Leaf("STATUS", "isnull"), Fixtures.CliqSession()).IsValid);

    [Fact]
    public void RejectsOverDeepNesting()
    {
        ConditionNode node = Leaf("STATUS", "eq", "A");
        for (var i = 0; i < ConditionValidator.MaxDepth + 4; i++)
        {
            node = new ConditionNode { Op = "and", Items = [node] };
        }

        var result = ConditionValidator.Validate(node, Fixtures.CliqSession());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("nested deeper", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsMoreLeavesThanTheCompilerShouldBeAskedToHandle()
    {
        var items = Enumerable.Range(0, ConditionValidator.MaxLeaves + 1)
            .Select(_ => Leaf("STATUS", "eq", "A"))
            .ToList();

        var result = ConditionValidator.Validate(
            new ConditionNode { Op = "and", Items = items }, Fixtures.CliqSession());

        Assert.False(result.IsValid);
    }

    [Fact]
    public void ReportsMalformedJsonAsAnErrorRatherThanThrowing()
    {
        var result = ConditionValidator.ValidateJson("{not json", Fixtures.CliqSession());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("not valid JSON", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsEveryProblemRatherThanTheFirst()
    {
        // Operations fixing a rule should see all of what is wrong with it.
        var tree = new ConditionNode
        {
            Op = "and",
            Items = [Leaf("NOPE", "eq", 1), Leaf("CURRENCY", "eq", "JOD"), Leaf("STATUS", "in", NoStatuses)],
        };

        var result = ConditionValidator.Validate(tree, Fixtures.CliqSession());

        Assert.False(result.IsValid);
        Assert.Equal(3, result.Errors.Count);
    }
}
