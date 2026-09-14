using System.Text.Json;
using Recon.Domain.Conditions;
using Recon.Domain.Configuration;
using Recon.Engine.Sql;
using Xunit;

namespace Recon.UnitTests;

/// <summary>
/// The tests that matter most in this solution.
///
/// <para>
/// The design's central safety claim is that a user can build matching rules
/// from the portal without opening a SQL injection surface, because field names
/// resolve through the registry and values are parameterized. These tests
/// attack that claim directly.
/// </para>
/// </summary>
public class SqlQueryBuilderSafetyTests
{
    private static readonly string[] Statuses = ["ACSC", "ACSP", "RJCT"];

    private static ConditionNode Leaf(string field, string cmp, object? value = null)
    {
        var node = new ConditionNode { Field = field, Cmp = cmp };

        if (value is not null)
        {
            node.Value = JsonSerializer.SerializeToElement(value);
        }

        return node;
    }

    private static ConditionNode And(params ConditionNode[] items) =>
        new() { Op = "and", Items = [.. items] };

    // =================================================================
    // Field codes must come from the registry
    // =================================================================

    [Fact]
    public void FieldCodeNotInRegistryIsRejected()
    {
        var dataset = Fixtures.CliqSession();
        var tree = And(Leaf("NO_SUCH_FIELD", "eq", "x"));

        var ex = Assert.Throws<ConditionValidationException>(
            () => SqlQueryBuilder.CompileCondition(tree, dataset, "S", new SqlParameterBag()));

        Assert.Contains("not in the field registry", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    // A classic payload, and the variants that try to escape a quoted context,
    // a comment, or an identifier.
    [InlineData("STATUS'; DROP TABLE stg.StagingTransaction--")]
    [InlineData("Text1; EXEC sp_configure")]
    [InlineData("1=1 OR 1=1")]
    [InlineData("STATUS/*")]
    [InlineData("[StagingId]")]
    [InlineData("STATUS UNION ALL SELECT * FROM sys.objects")]
    public void InjectionPayloadAsFieldCodeIsRejected(string payload)
    {
        var dataset = Fixtures.CliqSession();
        var tree = And(Leaf(payload, "eq", "x"));

        Assert.Throws<ConditionValidationException>(
            () => SqlQueryBuilder.CompileCondition(tree, dataset, "S", new SqlParameterBag()));
    }

    [Fact]
    public void FieldThatIsNotMatchableIsRejected()
    {
        // CURRENCY exists and is required, but the registry withholds it from
        // the rule builder. A real field is a different failure from a missing
        // one, and the message must say which.
        var dataset = Fixtures.CliqSession();
        var tree = And(Leaf("CURRENCY", "eq", "JOD"));

        var ex = Assert.Throws<ConditionValidationException>(
            () => SqlQueryBuilder.CompileCondition(tree, dataset, "S", new SqlParameterBag()));

        Assert.Contains("not marked matchable", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MatchConditionOnNonMatchableFieldIsRejected()
    {
        // The same boundary, reached through a match rule rather than a filter.
        var definition = Fixtures.CliqOm();
        var rule = new MatchRule
        {
            MatchRuleId = 99,
            RuleCode = "BAD",
            Name = "Bad",
            Sequence = 1,
            Conditions =
            [
                new MatchCondition
                {
                    MatchConditionId = 99,
                    LeftFieldCode = "CURRENCY",
                    RightFieldCode = "CURRENCY",
                    Comparison = ComparisonType.Exact,
                },
            ],
        };

        Assert.Throws<FieldNotMatchableException>(() => SqlQueryBuilder.CompileRowPass(
            new PassContext
            {
                Definition = definition,
                Rule = rule,
                RunId = 1,
                StagingRunId = 1,
                BusinessDate = new DateOnly(2026, 9, 13),
                WindowFrom = new DateOnly(2026, 9, 12),
                WindowTo = new DateOnly(2026, 9, 14),
            }));
    }

    [Fact]
    public void RegistryRowNamingAnImpossibleSlotIsRejected()
    {
        // Unreachable through the portal, reachable by editing cfg.DatasetField
        // directly. The slot whitelist is the backstop.
        var dataset = new Dataset
        {
            DatasetId = 9,
            CounterpartyId = 1,
            Code = "TAMPERED",
            Name = "Tampered",
            Provider = ProviderType.File,
            Fields =
            [
                new DatasetField
                {
                    DatasetFieldId = 1,
                    DatasetId = 9,
                    FieldCode = "EVIL",
                    DisplayLabel = "Evil",
                    DataType = FieldDataType.String,
                    StorageSlot = "Text1 FROM sys.objects--",
                },
            ],
        };

        var ex = Assert.Throws<SqlCompilationException>(
            () => SqlQueryBuilder.ResolveSlot(dataset, "EVIL"));

        Assert.Contains("not a column of stg.StagingTransaction", ex.Message, StringComparison.Ordinal);
    }

    // =================================================================
    // Values must be parameters, never text
    // =================================================================

    [Fact]
    public void FilterValuesBecomeParametersAndNeverAppearInTheSql()
    {
        var dataset = Fixtures.CliqSession();
        var parameters = new SqlParameterBag();

        const string Payload = "RJCT'; DELETE stg.StagingTransaction--";
        var tree = And(Leaf("STATUS", "eq", Payload));

        var sql = SqlQueryBuilder.CompileCondition(tree, dataset, "S", parameters);

        // The value is bound, not written.
        Assert.DoesNotContain(Payload, sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE", sql, StringComparison.Ordinal);
        Assert.Contains("@p0", sql, StringComparison.Ordinal);
        Assert.Equal(Payload, parameters.Parameters[0].Value);
    }

    [Fact]
    public void GeneratedPassContainsNoUserValuesAtAll()
    {
        var definition = Fixtures.CliqOm();
        var rule = definition.Rules.Single(r => r.RuleCode == "P4_COMPOSITE");

        var statement = SqlQueryBuilder.CompileRowPass(new PassContext
        {
            Definition = definition,
            Rule = rule,
            RunId = 4471,
            StagingRunId = 4471,
            BusinessDate = new DateOnly(2026, 9, 13),
            WindowFrom = new DateOnly(2026, 9, 12),
            WindowTo = new DateOnly(2026, 9, 14),
        });

        // Dates, ids and tolerances are all parameters: no literal from the
        // run's inputs is in the text.
        Assert.DoesNotContain("2026-09-13", statement.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("4471", statement.Sql, StringComparison.Ordinal);
        Assert.NotEmpty(statement.Parameters.Parameters);
    }

    [Fact]
    public void InListBindsEveryValueSeparately()
    {
        var dataset = Fixtures.CliqSession();
        var parameters = new SqlParameterBag();
        var tree = And(Leaf("STATUS", "in", Statuses));

        var sql = SqlQueryBuilder.CompileCondition(tree, dataset, "S", parameters);

        Assert.Contains("IN (@p0, @p1, @p2)", sql, StringComparison.Ordinal);
        Assert.Equal(3, parameters.Parameters.Count);
        Assert.DoesNotContain("ACSC", sql, StringComparison.Ordinal);
    }

    // =================================================================
    // Semantics the design is explicit about
    // =================================================================

    [Fact]
    public void NotEqualAlsoMatchesRowsWhoseValueIsAbsent()
    {
        // "not equal to RJCT" must include a row with no status at all. A plain
        // <> would evaluate to UNKNOWN and drop it, which is not what
        // Operations means and not what a reconciliation should do.
        var dataset = Fixtures.CliqSession();
        var parameters = new SqlParameterBag();
        var tree = And(Leaf("STATUS", "ne", "RJCT"));

        var sql = SqlQueryBuilder.CompileCondition(tree, dataset, "S", parameters);

        Assert.Contains("IS NULL OR", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizedConditionComparesTheCompanionSlots()
    {
        // Blocker A2: the normalized comparison must read the parse-time
        // companion (Text21), not wrap the raw column in UPPER/TRIM.
        var definition = Fixtures.CliqOm();
        var rule = definition.Rules.Single(r => r.RuleCode == "P2_REF_NORM");

        var statement = SqlQueryBuilder.CompileRowPass(new PassContext
        {
            Definition = definition,
            Rule = rule,
            RunId = 1,
            StagingRunId = 1,
            BusinessDate = new DateOnly(2026, 9, 13),
            WindowFrom = new DateOnly(2026, 9, 12),
            WindowTo = new DateOnly(2026, 9, 14),
        });

        Assert.Contains("L.Text21 = R.Text21", statement.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("UPPER(", statement.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("TRIM(", statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void PassReadsStagingRunIdNotTheExecutingRunId()
    {
        // Review finding 1: on a Rematch the run executing and the run that
        // staged the rows differ. Reading LoadRunId = RunId would return
        // nothing at all.
        var definition = Fixtures.CliqOm();
        var rule = definition.Rules[0];

        var statement = SqlQueryBuilder.CompileRowPass(new PassContext
        {
            Definition = definition,
            Rule = rule,
            RunId = 4472,
            StagingRunId = 4468,
            BusinessDate = new DateOnly(2026, 9, 12),
            WindowFrom = new DateOnly(2026, 9, 11),
            WindowTo = new DateOnly(2026, 9, 13),
        });

        Assert.Contains("L.LoadRunId = @p", statement.Sql, StringComparison.Ordinal);

        var loadRunValues = statement.Parameters.Parameters
            .Where(p => p.Value is long)
            .Select(p => (long)p.Value)
            .ToList();

        // The staging run id is bound; the executing run id is bound too (for
        // the anti-join), and the two are distinguishable.
        Assert.Contains(4468L, loadRunValues);
        Assert.Contains(4472L, loadRunValues);
    }

    [Fact]
    public void PassAntiJoinsAgainstThisRunsExistingResults()
    {
        // Blocker A3: "still unmatched" is an anti-join on ops.MatchResult, not
        // a read of a status column that passes would have to update.
        var definition = Fixtures.CliqOm();
        var statement = SqlQueryBuilder.CompileRowPass(new PassContext
        {
            Definition = definition,
            Rule = definition.Rules[0],
            RunId = 1,
            StagingRunId = 1,
            BusinessDate = new DateOnly(2026, 9, 13),
            WindowFrom = new DateOnly(2026, 9, 12),
            WindowTo = new DateOnly(2026, 9, 14),
        });

        Assert.Contains("NOT EXISTS", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("ops.MatchResult", statement.Sql, StringComparison.Ordinal);

        // And it must not update staging.
        Assert.DoesNotContain("UPDATE stg.StagingTransaction", statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void PassCountsCandidatesPerSideBeforeWriting()
    {
        // Finding A4: a composite pass can produce many-to-many candidate sets.
        // Counting per side first is what stops the explosion and makes
        // OnMultipleMatch meaningful.
        var definition = Fixtures.CliqOm();
        var statement = SqlQueryBuilder.CompileRowPass(new PassContext
        {
            Definition = definition,
            Rule = definition.Rules.Single(r => r.RuleCode == "P4_COMPOSITE"),
            RunId = 1,
            StagingRunId = 1,
            BusinessDate = new DateOnly(2026, 9, 13),
            WindowFrom = new DateOnly(2026, 9, 12),
            WindowTo = new DateOnly(2026, 9, 14),
        });

        Assert.Contains("COUNT(*) OVER (PARTITION BY L.StagingId)", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("COUNT(*) OVER (PARTITION BY R.StagingId)", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("'Ambiguous'", statement.Sql, StringComparison.Ordinal);

        // Never UPDATE ... FROM a join.
        Assert.DoesNotContain("UPDATE L", statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ToleranceIsWrittenAsARangeSoItCanSeek()
    {
        var definition = Fixtures.CliqOm();
        var statement = SqlQueryBuilder.CompileRowPass(new PassContext
        {
            Definition = definition,
            Rule = definition.Rules.Single(r => r.RuleCode == "P4_COMPOSITE"),
            RunId = 1,
            StagingRunId = 1,
            BusinessDate = new DateOnly(2026, 9, 13),
            WindowFrom = new DateOnly(2026, 9, 12),
            WindowTo = new DateOnly(2026, 9, 14),
        });

        // BETWEEN on the left column, not ABS(DATEDIFF(...)) which cannot seek.
        Assert.Contains("BETWEEN DATEADD(DAY,", statement.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DATEDIFF", statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void PassWithNoConditionsIsRejected()
    {
        // Would otherwise be a cross join of 2M × 2M rows.
        var definition = Fixtures.CliqOm();
        var rule = new MatchRule
        {
            MatchRuleId = 50, RuleCode = "EMPTY", Name = "Empty", Sequence = 9, Conditions = [],
        };

        var ex = Assert.Throws<SqlCompilationException>(() => SqlQueryBuilder.CompileRowPass(
            new PassContext
            {
                Definition = definition,
                Rule = rule,
                RunId = 1,
                StagingRunId = 1,
                BusinessDate = new DateOnly(2026, 9, 13),
                WindowFrom = new DateOnly(2026, 9, 12),
                WindowTo = new DateOnly(2026, 9, 14),
            }));

        Assert.Contains("every row against every row", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AmountDifferenceUsesTheIntegerMinorUnitSlots()
    {
        var definition = Fixtures.CliqOm();
        var statement = SqlQueryBuilder.CompileRowPass(new PassContext
        {
            Definition = definition,
            Rule = definition.Rules[0],
            RunId = 1,
            StagingRunId = 1,
            BusinessDate = new DateOnly(2026, 9, 13),
            WindowFrom = new DateOnly(2026, 9, 12),
            WindowTo = new DateOnly(2026, 9, 14),
        });

        Assert.Contains("(L.Num1 - R.Num1) AS AmountDiffMinor", statement.Sql, StringComparison.Ordinal);
        // No decimal slot is ever used for arithmetic.
        Assert.DoesNotContain("L.Dec", statement.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CAST(L.Num1 AS DECIMAL", statement.Sql, StringComparison.Ordinal);
    }
}

/// <summary>
/// Regression tests for defects that only appeared when the engine ran against
/// a real server. Each is here so the same mistake cannot return silently.
/// </summary>
public class PassWorkingSetTests
{
    private static PassContext Context(ReconciliationDefinition definition, MatchRule rule) => new()
    {
        Definition = definition,
        Rule = rule,
        RunId = 1,
        StagingRunId = 1,
        BusinessDate = new DateOnly(2026, 9, 13),
        WindowFrom = new DateOnly(2026, 9, 12),
        WindowTo = new DateOnly(2026, 9, 14),
    };

    [Fact]
    public void APassSeesOnlyRowsStillInTheWorkingSet()
    {
        // Exclusions and duplicate detection run before pass 1 and mark rows
        // Excluded and Duplicate. The design says those rows never enter
        // matching — and until this clause existed, they did: a file with the
        // same reference twice joined both left rows to one right row, and a
        // clean match came out Ambiguous.
        var definition = Fixtures.CliqOm();
        var statement = SqlQueryBuilder.CompileRowPass(Context(definition, definition.Rules[0]));

        Assert.Contains("L.MatchStatus = 'Unmatched'", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("R.MatchStatus = 'Unmatched'", statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAggregatePassSeesOnlyRowsStillInTheWorkingSet()
    {
        var definition = Fixtures.CliqOm();
        var rule = definition.Rules[0] with
        {
            MatchRuleId = 80,
            RuleCode = "AGG",
            Mode = MatchMode.Aggregate,
            LeftGroupByFields = ["DIRECTION"],
            AggregateFunction = "SumAndCount",
            Conditions =
            [
                new MatchCondition
                {
                    MatchConditionId = 80,
                    LeftFieldCode = "AMOUNT",
                    RightFieldCode = "AMOUNT_MINOR",
                    Comparison = ComparisonType.NumericExact,
                },
            ],
        };

        var statement = SqlQueryBuilderAggregate.CompileAggregatePass(Context(definition, rule));

        Assert.Contains("L.MatchStatus = 'Unmatched'", statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AggregateModeGroupsAndSumsTheAmountRoleField()
    {
        var definition = Fixtures.CliqOm();
        var rule = definition.Rules[0] with
        {
            MatchRuleId = 81,
            RuleCode = "AGG2",
            Mode = MatchMode.Aggregate,
            LeftGroupByFields = ["DIRECTION"],
            AggregateFunction = "SumAndCount",
            Conditions =
            [
                new MatchCondition
                {
                    MatchConditionId = 81,
                    LeftFieldCode = "AMOUNT",
                    RightFieldCode = "AMOUNT_MINOR",
                    Comparison = ComparisonType.NumericExact,
                },
            ],
        };

        var statement = SqlQueryBuilderAggregate.CompileAggregatePass(Context(definition, rule));

        // The group key is the Direction slot, the sum is the Amount slot, and
        // the Amount pair compares the SUM to the reported total — which is
        // what makes "one summary line ↔ many transactions" an ordinary rule.
        Assert.Contains("GROUP BY L.Text6", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("SUM(CAST(L.Num1 AS BIGINT))", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("G.AmountMinorSum = R.Num1", statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAggregateRuleWithNoGroupingIsRejected()
    {
        var definition = Fixtures.CliqOm();
        var rule = definition.Rules[0] with { Mode = MatchMode.Aggregate, LeftGroupByFields = [] };

        var ex = Assert.Throws<SqlCompilationException>(
            () => SqlQueryBuilderAggregate.CompileAggregatePass(Context(definition, rule)));

        Assert.Contains("sum the whole dataset by accident", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AggregateModeRejectsAComparisonThatCannotApplyToATotal()
    {
        var definition = Fixtures.CliqOm();
        var rule = definition.Rules[0] with
        {
            Mode = MatchMode.Aggregate,
            LeftGroupByFields = ["DIRECTION"],
            AggregateFunction = "SumAndCount",
            Conditions =
            [
                definition.Rules[0].Conditions[0] with { Comparison = ComparisonType.Contains },
            ],
        };

        // A grouped total cannot be substring-matched, and pretending otherwise
        // would produce SQL that runs and means nothing.
        Assert.Throws<SqlCompilationException>(
            () => SqlQueryBuilderAggregate.CompileAggregatePass(Context(definition, rule)));
    }

    [Fact]
    public void FinalizeStagingStampsTheRunThatProducedTheStatus()
    {
        // Review finding 1: without ResultRunId a Rematch overwrites the source
        // run's row-level results while claiming they survive.
        var statement = SqlQueryBuilderLifecycle.CompileFinalizeStaging(
            Fixtures.CliqOm(), runId: 4472, stagingRunId: 4468,
            new DateOnly(2026, 9, 12), new DateOnly(2026, 9, 14));

        Assert.Contains("ResultRunId = @RunId", statement.Sql, StringComparison.Ordinal);
        // And it must not clobber the statuses set before matching.
        Assert.Contains("NOT IN ('Excluded','Duplicate')", statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateDetectionKeepsTheEarliestOccurrence()
    {
        var statement = SqlQueryBuilderLifecycle.CompileDuplicateDetection(
            Fixtures.CliqSession(), stagingRunId: 1,
            new DateOnly(2026, 9, 12), new DateOnly(2026, 9, 14));

        // Ordering by the source row number means the row flagged is the later
        // arrival in the file, not an arbitrary one.
        Assert.Contains("ORDER BY S.RawRowNumber", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("r.rn > 1", statement.Sql, StringComparison.Ordinal);
        Assert.Contains("'Duplicate'", statement.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ADatasetWithNoDeclaredKeyGeneratesNoDuplicateDetection()
    {
        var statement = SqlQueryBuilderLifecycle.CompileDuplicateDetection(
            Fixtures.OrangeMoney(), stagingRunId: 1,
            new DateOnly(2026, 9, 12), new DateOnly(2026, 9, 14));

        Assert.DoesNotContain("UPDATE", statement.Sql, StringComparison.Ordinal);
    }
}
