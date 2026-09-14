namespace Recon.Domain.Configuration;

/// <summary>
/// The unit the user creates in the portal: left dataset ↔ right dataset, plus
/// the rules, classifications, control totals and schedule that govern it.
/// </summary>
public sealed record ReconciliationDefinition
{
    public required int DefinitionId { get; init; }
    public required int CounterpartyId { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }

    public required Dataset Left { get; init; }
    public required Dataset Right { get; init; }

    /// <summary>
    /// Which date range the providers pull for a business date. Late arrivals
    /// mean the window is never simply "= BusinessDate" (finding C5).
    /// </summary>
    public int MatchingWindowDaysBefore { get; init; } = 1;
    public int MatchingWindowDaysAfter { get; init; } = 1;

    public int Version { get; init; } = 1;
    public bool IsActive { get; init; }

    public required IReadOnlyList<MatchRule> Rules { get; init; }
    public IReadOnlyList<ClassificationRule> Classifications { get; init; } = [];
    public IReadOnlyList<ControlTotalDefinition> ControlTotals { get; init; } = [];
    public IReadOnlyList<ExclusionRule> Exclusions { get; init; } = [];

    public Dataset DatasetFor(Side side) => side == Side.Left ? Left : Right;

    public IEnumerable<MatchRule> ActivePasses =>
        Rules.Where(r => r.IsActive).OrderBy(r => r.Sequence);

    public (DateOnly From, DateOnly To) WindowFor(DateOnly businessDate) =>
        (businessDate.AddDays(-MatchingWindowDaysBefore),
         businessDate.AddDays(MatchingWindowDaysAfter));

    /// <summary>
    /// The universal roles a definition cannot run without (§6.1). Control
    /// totals, partitioning and fee logic all rely on them, and review item B6
    /// noted the rule was stated but had no enforcement point. This is it.
    /// </summary>
    public static readonly FieldRole[] RequiredRoles =
    [
        FieldRole.Reference,
        FieldRole.Amount,
        FieldRole.Currency,
        FieldRole.Date,
        FieldRole.Direction,
    ];

    public IReadOnlyList<string> ActivationProblems()
    {
        var problems = new List<string>();

        foreach (var side in new[] { Side.Left, Side.Right })
        {
            var ds = DatasetFor(side);
            var missing = RequiredRoles.Where(r => ds.FieldWithRole(r) is null).ToList();
            if (missing.Count > 0)
            {
                problems.Add(
                    $"{side} dataset {ds.Code} is missing required role(s): {string.Join(", ", missing)}");
            }
        }

        if (!Rules.Any(r => r.IsActive))
        {
            problems.Add("the definition has no active pass, so it would match nothing");
        }

        // A first pass that cannot seek an index scans both sides in full. At
        // 2M rows a day that is the difference between a minute and an hour.
        var first = ActivePasses.FirstOrDefault();
        if (first is not null && !first.IsFullySeekable)
        {
            problems.Add(
                $"pass {first.Sequence} ({first.RuleCode}) uses a comparison that cannot seek an index, " +
                "in the pass that runs against the full dataset");
        }

        if (Left.DatasetId == Right.DatasetId)
        {
            problems.Add("a definition cannot reconcile a dataset against itself");
        }

        return problems;
    }
}
