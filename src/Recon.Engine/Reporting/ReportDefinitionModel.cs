using Recon.Domain.Conditions;
using Recon.Domain.Configuration;

namespace Recon.Engine.Reporting;

/// <summary>One row of <c>cfg.ReportDefinition</c>, with its columns.</summary>
public sealed record ReportDefinition
{
    public required int ReportDefinitionId { get; init; }
    public required int DefinitionId { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string SheetName { get; init; }

    /// <summary>Matched | Unmatched | Exceptions | ControlTotals | All</summary>
    public required ReportScope Scope { get; init; }

    public ConditionNode? Filter { get; init; }
    public ReportOutputFormat OutputFormat { get; init; } = ReportOutputFormat.Xlsx;
    public bool IncludeSubtotals { get; init; }
    public bool IsActive { get; init; } = true;

    public required IReadOnlyList<ReportColumn> Columns { get; init; }

    /// <summary>
    /// A worksheet holds 1,048,576 rows. A full day is 2M, so a
    /// transaction-level scope in Excel needs sheet splitting — and the design
    /// makes CSV the default for those scopes for that reason (E1). This is
    /// the judgement the portal shows Operations before they pick a format.
    /// </summary>
    public bool IsTransactionLevel =>
        Scope is ReportScope.All or ReportScope.Matched or ReportScope.Unmatched;
}

public sealed record ReportColumn
{
    public required int ReportColumnId { get; init; }
    public required string Header { get; init; }
    public required int Sequence { get; init; }

    /// <summary>A registry field, resolved through the dataset like anything else.</summary>
    public DatasetField? Field { get; init; }

    /// <summary>
    /// A value the engine computes rather than stores in a slot:
    /// <c>ExceptionCode</c>, <c>MatchStatus</c>, <c>MatchedByRuleCode</c>,
    /// <c>AmountDiffMinor</c>, <c>StagingId</c>, <c>TxDate</c>.
    /// </summary>
    public string? ComputedField { get; init; }

    public string? DisplayFormat { get; init; }
    public int? ColumnWidth { get; init; }

    /// <summary>
    /// Whether the column holds integer minor units. The writers need this:
    /// an amount must be written as its decimal so a spreadsheet can sum it,
    /// and a count must not be.
    /// </summary>
    public bool IsAmount =>
        Field?.Role == FieldRole.Amount
        || ComputedField is "AmountDiffMinor" or "AmountMinor";
}

public enum ReportScope { Matched, Unmatched, Exceptions, ControlTotals, All }

public enum ReportOutputFormat { Xlsx, Csv }
