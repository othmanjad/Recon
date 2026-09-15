namespace Recon.Domain.Configuration;

/// <summary>
/// These mirror the schema's CHECK constraints exactly. The database collates
/// those whitelists case-sensitively (review finding X1), so the names here and
/// the strings stored must match character for character — a lower-case
/// 'sandbox' reaching the database is rejected rather than quietly stored.
/// </summary>
public enum ProviderType { File, Sql, Api }

// CA1720 objects to 'String', 'Integer' and 'Decimal' as identifiers. The
// objection does not apply here: these names are not a choice. The database
// stores these exact strings in cfg.DatasetField.DataType and collates the
// CHECK whitelist case-sensitively, so renaming a member to 'Text' would make
// the enum disagree with the column it represents. Round-tripping through a
// translation table to satisfy an analyser would add a failure mode to the
// one place that must not have one.
#pragma warning disable CA1720
public enum FieldDataType { String, Integer, Decimal, DateTime, Boolean }
#pragma warning restore CA1720

/// <summary>
/// What a field MEANS, independent of what the counterparty calls it.
///
/// This is how the engine stays counterparty-agnostic (§6): it never looks for
/// a field named "Amount", it looks for the field whose role is
/// <see cref="Amount"/>. That is what lets generic control totals, fee
/// calculation and date partitioning work across partners that name nothing
/// alike.
/// </summary>
public enum FieldRole
{
    Reference,
    OriginalReference,
    Amount,
    Currency,
    Date,
    Direction,
    Status,
    Party,
    TransactionType,
    Other,
}

/// <summary>
/// The comparisons the rule builder offers (§9.1).
///
/// <c>Normalized</c> is deliberately absent. Normalizing inside a join
/// predicate (<c>UPPER(TRIM(...))</c>) is non-sargable and defeats every index
/// — review blocker A2. A field flagged <c>NormalizeForMatch</c> gets a
/// companion slot filled at parse time, and the rule compares that companion
/// with <see cref="Exact"/>.
/// </summary>
public enum ComparisonType
{
    Exact,
    NumericExact,
    NumericTolerance,
    DateExact,
    DateWithin,
    StartsWith,
    EndsWith,
    Contains,
}

public enum ToleranceUnit { MinorUnit, Minute, Hour, Day }

public enum MatchMode { Row, Aggregate }

public enum Cardinality { OneToOne, OneToMany }

/// <summary>
/// At 2M rows a day, duplicate references are certain. A rule matching several
/// candidates must never silently pick one (§9.5) — hence
/// <see cref="MarkAmbiguous"/> as the default everywhere.
/// </summary>
public enum OnMultipleMatch { MarkAmbiguous, TakeEarliest, Fail }

public enum MatchStatus { Unmatched, Matched, Ambiguous, AutoClosed, Excluded, Duplicate }

public enum RunType { Scheduled, Manual, Rerun, Rematch, Sandbox }

public enum RunStatus { Pending, Running, Completed, Failed, Cancelled, Rejected, Resuming }

public enum RunStepName
{
    Acquire,
    Parse,
    Stage,

    /// <summary>
    /// Re-opens staged rows a previous run already judged, so that a Rematch
    /// or a Sandbox replay starts from the same state a fresh load would.
    /// Without it every statement before pass 1 — exclusions, duplicate
    /// detection, the passes themselves — filtered on
    /// <c>MatchStatus = 'Unmatched'</c> and therefore saw nothing.
    /// </summary>
    Reset,

    Exclude,
    Duplicates,
    Match,
    Classify,
    AutoClose,
    ControlTotals,
    Aggregate,
    Fees,
    Report,
}

public enum StepStatus { Pending, Running, Completed, Failed, Skipped }

public enum Side { Left, Right }

/// <summary>
/// Which side of a reconciliation a classification rule applies to.
///
/// Separate from <see cref="Side"/> on purpose: the schema's
/// <c>AppliesToSide</c> allows 'Both', and <see cref="Side"/> is used for real
/// left/right decisions throughout the engine where a third value would be
/// meaningless.
/// </summary>
public enum ClassificationSide { Left, Right, Both }

public static class ClassificationSideExtensions
{
    public static bool Covers(this ClassificationSide applies, Side side) =>
        applies == ClassificationSide.Both
        || (applies == ClassificationSide.Left && side == Side.Left)
        || (applies == ClassificationSide.Right && side == Side.Right);
}

public enum ControlTotalScope { Run, BusinessDate, Period }

public enum ControlTotalSource { Staging, MatchResult, RunAggregate, Dataset }

public enum FileFormatType { Csv, Xml, Json, FixedWidth }

/// <summary>
/// Extension point for the engine's own comparison metadata. The UI warns on
/// non-indexable comparisons; the compiler needs the same knowledge to decide
/// whether a pass can seek.
/// </summary>
public static class ComparisonTypeExtensions
{
    /// <summary>
    /// Whether a comparison can use an index. A non-sargable comparison in
    /// pass 1 scans 2M rows on both sides; the design requires the UI to warn
    /// and the engine to record it.
    /// </summary>
    public static Indexability Indexability(this ComparisonType type) => type switch
    {
        ComparisonType.Exact or ComparisonType.NumericExact or ComparisonType.DateExact =>
            Configuration.Indexability.Seekable,
        // A range seek: still an index seek, just over a span.
        ComparisonType.NumericTolerance or ComparisonType.DateWithin =>
            Configuration.Indexability.Seekable,
        ComparisonType.StartsWith => Configuration.Indexability.Partial,
        ComparisonType.EndsWith or ComparisonType.Contains =>
            Configuration.Indexability.Scan,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    public static bool RequiresTolerance(this ComparisonType type) =>
        type is ComparisonType.NumericTolerance or ComparisonType.DateWithin;
}

public enum Indexability { Seekable, Partial, Scan }
