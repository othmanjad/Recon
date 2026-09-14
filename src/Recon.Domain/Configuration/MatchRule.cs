using Recon.Domain.Conditions;

namespace Recon.Domain.Configuration;

/// <summary>One field pair the user picked in the rule builder.</summary>
public sealed record MatchCondition
{
    public required int MatchConditionId { get; init; }
    public required string LeftFieldCode { get; init; }
    public required string RightFieldCode { get; init; }
    public required ComparisonType Comparison { get; init; }

    /// <summary>Minor units for a numeric tolerance, or a span for a date one.</summary>
    public long? ToleranceValue { get; init; }
    public ToleranceUnit? ToleranceUnit { get; init; }

    /// <summary>
    /// Compare the parse-time normalized companions rather than the raw fields.
    /// This is what replaced the <c>Normalized</c> comparison type (A2): the
    /// work happens at load, so the rule stays an indexed <c>Exact</c> match.
    /// </summary>
    public bool UseNormalized { get; init; }

    public int Sequence { get; init; } = 1;
}

/// <summary>
/// One ordered pass of a rule set (§9.3).
///
/// Passes run in sequence and write <b>only</b> to <c>ops.MatchResult</c>;
/// staging is never updated mid-run (blocker A3). "Still unmatched" for pass N
/// is an anti-join against the results of passes 1..N-1.
/// </summary>
public sealed record MatchRule
{
    public required int MatchRuleId { get; init; }
    public required string RuleCode { get; init; }
    public required string Name { get; init; }
    public required int Sequence { get; init; }

    public ConditionNode? LeftFilter { get; init; }
    public ConditionNode? RightFilter { get; init; }

    public MatchMode Mode { get; init; } = MatchMode.Row;

    /// <summary>
    /// Aggregate mode only: the user-chosen grouping, which is what lets a
    /// JoPACC summary line or a settlement batch total reconcile against detail
    /// rows without special code (§9.2).
    /// </summary>
    public IReadOnlyList<string> LeftGroupByFields { get; init; } = [];
    public IReadOnlyList<string> RightGroupByFields { get; init; } = [];
    public string? AggregateFunction { get; init; }

    public Cardinality Cardinality { get; init; } = Cardinality.OneToOne;
    public OnMultipleMatch OnMultipleMatch { get; init; } = OnMultipleMatch.MarkAmbiguous;
    public bool IsActive { get; init; } = true;

    public required IReadOnlyList<MatchCondition> Conditions { get; init; }

    /// <summary>
    /// True when every condition can seek an index. The design requires the UI
    /// to warn about the others and to keep them out of early passes; the
    /// engine records the same judgement against the run so a slow pass is
    /// explainable after the fact.
    /// </summary>
    public bool IsFullySeekable =>
        Conditions.All(c => c.Comparison.Indexability() == Indexability.Seekable);
}

/// <summary>Rows that must never match, removed before pass 1 (finding C6).</summary>
public sealed record ExclusionRule
{
    public required int ExclusionRuleId { get; init; }
    public required int DatasetId { get; init; }
    public required string Name { get; init; }
    public required ConditionNode Condition { get; init; }

    /// <summary>Reported, never raised as an exception.</summary>
    public required string ReasonCode { get; init; }

    public bool IsActive { get; init; } = true;
}

/// <summary>Turns an unmatched row into business meaning, by rule rather than by code (§10).</summary>
public sealed record ClassificationRule
{
    public required int ClassificationRuleId { get; init; }
    public required string ExceptionCode { get; init; }
    public required string DisplayName { get; init; }
    public required ClassificationSide AppliesToSide { get; init; }
    public required ConditionNode Condition { get; init; }

    /// <summary>
    /// <c>ReportOnly</c> in this phase. Automatic Failed Inward posting exists
    /// as a seam in the model and is deliberately unimplemented — when the
    /// business asks for it, it is work behind an existing seam, not a
    /// redesign (§10).
    /// </summary>
    public string ActionType { get; init; } = "ReportOnly";

    public string Severity { get; init; } = "Normal";
    public int Sequence { get; init; }
    public bool IsActive { get; init; } = true;
}

public sealed record ControlTotalDefinition
{
    public required int ControlTotalId { get; init; }
    public required string CheckCode { get; init; }
    public required string DisplayName { get; init; }
    public required AggregateSpec SourceA { get; init; }
    public required AggregateSpec SourceB { get; init; }
    public ControlTotalScope Scope { get; init; } = ControlTotalScope.Run;
    public ControlTotalSource SourceTypeA { get; init; } = ControlTotalSource.Staging;
    public ControlTotalSource SourceTypeB { get; init; } = ControlTotalSource.Dataset;
    public int? PeriodDays { get; init; }

    /// <summary>Default 0 — exact. Any difference is a break (§3).</summary>
    public long ToleranceMinor { get; init; }

    /// <summary>
    /// A run with fully matched rows but a non-zero net difference is a failed
    /// run (§11), so this defaults to true.
    /// </summary>
    public bool FailRunOnMismatch { get; init; } = true;

    public bool IsActive { get; init; } = true;
}

/// <summary>
/// What to aggregate, for one side of a control total. Structured for the same
/// reason the filters are: the alternative is free SQL text.
/// </summary>
public sealed record AggregateSpec
{
    /// <summary>Count | SumAmount</summary>
    public required string Function { get; init; }

    public int? DatasetId { get; init; }
    public Side? Side { get; init; }

    /// <summary>Restricts the rows aggregated. Validated against the registry like any filter.</summary>
    public ConditionNode? Filter { get; init; }

    /// <summary>For a RunAggregate source: which persisted grouping to read.</summary>
    public string? GroupKey { get; init; }
    public MatchStatus? MatchStatus { get; init; }
}
