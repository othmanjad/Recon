using Recon.Web.Services;

namespace Recon.Web.Models;

/// <summary>
/// View models. Deliberately flat records rather than the domain types: a view
/// wants a denormalized row with the counterparty code already joined, and
/// reshaping the domain model to suit Razor would put presentation concerns
/// into the engine.
/// </summary>
public sealed record RunRow
{
    public required long RunId { get; init; }
    public required int DefinitionId { get; init; }
    public required string DefinitionCode { get; init; }
    public required string DefinitionName { get; init; }
    public required DateOnly BusinessDate { get; init; }
    public string? SessionRef { get; init; }
    public required string RunType { get; init; }
    public required string Status { get; init; }
    public required bool IsCurrent { get; init; }
    public long? SourceRunId { get; init; }
    public required long StagingRunId { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public long? LeftRowCount { get; init; }
    public long? RightRowCount { get; init; }
    public long? MatchedCount { get; init; }
    public long? UnmatchedCount { get; init; }
    public long? AmbiguousCount { get; init; }
    public string? ErrorMessage { get; init; }
    public required string TriggeredBy { get; init; }

    public TimeSpan? Duration =>
        StartedAt is { } s && CompletedAt is { } c ? c - s : null;
}

public sealed record RunStepRow
{
    public required long RunStepId { get; init; }
    public required string StepName { get; init; }
    public string? Side { get; init; }
    public required string Status { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public long? RowsProcessed { get; init; }
    public long? RowsMatched { get; init; }
    public string? ErrorMessage { get; init; }
    public string? RuleCode { get; init; }
    public int? RuleSequence { get; init; }

    public double? Seconds =>
        StartedAt is { } s && CompletedAt is { } c ? (c - s).TotalSeconds : null;
}

public sealed record ControlTotalRow
{
    public required string CheckCode { get; init; }
    public required string DisplayName { get; init; }
    public required long ValueA { get; init; }
    public required long ValueB { get; init; }
    public required long Difference { get; init; }
    public required bool IsBalanced { get; init; }
    public required bool FailsRun { get; init; }
    public required bool IsAmount { get; init; }
}

public sealed record AggregateRow
{
    public required string DatasetCode { get; init; }
    public required string Side { get; init; }
    public required string GroupKey { get; init; }
    public required string MatchStatus { get; init; }
    public required long RowCount { get; init; }
    public required long AmountMinorSum { get; init; }
    public required string CurrencyCode { get; init; }
}

public sealed record ParseErrorRow2
{
    public int? RawRowNumber { get; init; }
    public required string ErrorType { get; init; }
    public string? FieldCode { get; init; }
    public required string Message { get; init; }
    public string? RawLine { get; init; }
}

public sealed record CounterpartyRow
{
    public required int CounterpartyId { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required bool IsActive { get; init; }
    public required int DatasetCount { get; init; }
    public required int DefinitionCount { get; init; }
    public required int FeeScheduleCount { get; init; }
    public AccessLevel? AccessLevel { get; init; }
}

public sealed record DefinitionRow
{
    public required int DefinitionId { get; init; }
    public required int CounterpartyId { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required bool IsActive { get; init; }
    public required string LeftDatasetCode { get; init; }
    public required string RightDatasetCode { get; init; }
    public required int WindowBefore { get; init; }
    public required int WindowAfter { get; init; }
    public required int ActivePasses { get; init; }
}

public sealed record DatasetRow
{
    public required int DatasetId { get; init; }
    public required int CounterpartyId { get; init; }
    public required string CounterpartyCode { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string ProviderType { get; init; }
    public string? DefaultCurrency { get; init; }
    public string? DuplicateKeyFields { get; init; }
    public required bool IsActive { get; init; }
    public required string TimeZone { get; init; }
    public required int FieldCount { get; init; }
}

public sealed record ExceptionRow
{
    public required long ExceptionId { get; init; }
    public required long RunId { get; init; }
    public required int DefinitionId { get; init; }
    public required string DefinitionCode { get; init; }
    public required DateOnly BusinessDate { get; init; }
    public required string Side { get; init; }
    public required string ExceptionCode { get; init; }
    public long? AmountMinor { get; init; }
    public string? CurrencyCode { get; init; }
    public required string Status { get; init; }
    public string? AssignedTo { get; init; }
    public string? ResolutionCode { get; init; }
    public string? ResolutionNote { get; init; }
    public string? KeyValuesJson { get; init; }
    public long? ClosedByRunId { get; init; }
    public DateTime? ClosedAt { get; init; }
    public string? ClosedBy { get; init; }
    public required int AgeDays { get; init; }

    public bool IsOpen => Status is "Open" or "InProgress";
}

public sealed record ExceptionCodeRow
{
    public required string Code { get; init; }
    public required string DisplayName { get; init; }
    public required string Severity { get; init; }
}

public sealed record SettingRow
{
    public required string Key { get; init; }
    public required string Value { get; init; }
    public required string DataType { get; init; }
    public required string Description { get; init; }
    public required DateTime ModifiedAt { get; init; }
    public string? ModifiedBy { get; init; }

    /// <summary>
    /// Settings whose value is still a placeholder rather than a decision are
    /// flagged in the UI. `ResultsMonthsOnline` is the one that matters: the
    /// regulatory retention period is an open question, and 84 months is a
    /// guess sitting in a column.
    ///
    /// The marker is looked for anywhere in the description, not at the start:
    /// the seeded text puts the sentence explaining the value first and the
    /// "OPEN:" caveat after it, so a StartsWith test found nothing and the
    /// flag never appeared.
    /// </summary>
    public bool IsOpenQuestion => Description.Contains("OPEN:", StringComparison.Ordinal)
        || Description.Contains("STILL OPEN", StringComparison.Ordinal);
}

public sealed record AuditRow
{
    public required long AuditId { get; init; }
    public required DateOnly AuditDate { get; init; }
    public required string EntityType { get; init; }
    public required string EntityId { get; init; }
    public required string Action { get; init; }
    public string? OldValueJson { get; init; }
    public string? NewValueJson { get; init; }
    public required string PerformedBy { get; init; }
    public required DateTime PerformedAt { get; init; }
    public string? IpAddress { get; init; }
    public string? Notes { get; init; }
}

public sealed record ExceptionFilter
{
    public string? ExceptionCode { get; init; }
    public string? Status { get; init; }
    public int? DefinitionId { get; init; }
    public int? MinAgeDays { get; init; }
    public int? MaxAgeDays { get; init; }
    public string? Search { get; init; }
    public int Take { get; init; } = 200;
}

public sealed record DashboardModel
{
    public required List<RunRow> Runs { get; init; }
    public required List<DefinitionRow> Definitions { get; init; }
    public int? SelectedDefinitionId { get; init; }
    public string? SelectedStatus { get; init; }
    public required int CurrentRunCount { get; init; }
    public required int RunningCount { get; init; }
    public required int FailedCount { get; init; }
    public required long MatchedRows { get; init; }
    public required long UnmatchedRows { get; init; }
    public required long AmbiguousRows { get; init; }
    public required bool HasAnyAccess { get; init; }
}

public sealed record RunDetailModel
{
    public required RunRow Run { get; init; }
    public required List<RunStepRow> Steps { get; init; }
    public required List<ControlTotalRow> ControlTotals { get; init; }
    public required List<AggregateRow> Aggregates { get; init; }
    public required List<ParseErrorRow2> ParseErrors { get; init; }

    /// <summary>
    /// The share of matches each pass contributed — the data-quality early
    /// warning of design §9.3. A rising share in the last pass means the clean
    /// reference match is degrading upstream.
    /// </summary>
    public List<(int Sequence, string Code, long Matched, double Share)> Distribution()
    {
        var passes = Steps
            .Where(s => s.StepName == "Match" && s.RuleSequence is not null)
            .OrderBy(s => s.RuleSequence)
            .ToList();

        var total = (double)passes.Sum(p => p.RowsMatched ?? 0);

        return passes
            .Select(p => (
                p.RuleSequence!.Value,
                p.RuleCode ?? "?",
                p.RowsMatched ?? 0,
                total <= 0 ? 0 : 100 * (p.RowsMatched ?? 0) / total))
            .ToList();
    }
}

public sealed record ExceptionWorkspaceModel
{
    public required List<ExceptionRow> Rows { get; init; }
    public required List<ExceptionCodeRow> Codes { get; init; }
    public required List<DefinitionRow> Definitions { get; init; }
    public required ExceptionFilter Filter { get; init; }
    public required bool HasAnyAccess { get; init; }
}
