using Recon.Domain.Configuration;

namespace Recon.Engine.Parsing;

/// <summary>One row of <c>cfg.FieldMapping</c> or <c>cfg.SqlFieldMapping</c>.</summary>
public sealed record FieldMapping
{
    /// <summary>CSV column name or ordinal, an XPath, a JsonPath, or a SQL column.</summary>
    public required string SourcePath { get; init; }

    public required DatasetField Field { get; init; }

    /// <summary>A .NET format string for parsing, e.g. <c>yyyyMMddHHmmss</c>.</summary>
    public string? ParseFormat { get; init; }

    public IReadOnlyList<TransformStep> Transforms { get; init; } = [];

    public string? DefaultValue { get; init; }

    /// <summary>When set, a missing value rejects the row rather than staging a NULL.</summary>
    public bool IsRequired { get; init; }
}

public sealed record FileFormat
{
    public required int FileFormatId { get; init; }
    public required FileFormatType Type { get; init; }
    public int Version { get; init; } = 1;
    public DateOnly EffectiveFrom { get; init; }
    public DateOnly? EffectiveTo { get; init; }
    public CsvOptions Csv { get; init; } = new();

    /// <summary>
    /// For XML and JSON: the path to the repeating record. An XML format
    /// cannot be read without it — the reader would not know where one record
    /// ends and the next begins — and a JSON format defaults to the root
    /// array.
    /// </summary>
    public string? RecordPath { get; init; }

    public string? FileNamePattern { get; init; }

    /// <summary>
    /// Review item E4: a wrong-format file would otherwise produce 2M
    /// <c>ParseError</c> rows, each carrying its raw line. The parse aborts
    /// past this count and the file is marked Failed.
    /// </summary>
    public int MaxParseErrors { get; init; } = 1000;

    public required IReadOnlyList<FieldMapping> Mappings { get; init; }

    /// <summary>
    /// Effective-dated versioning is what lets a 2024 file still be read
    /// correctly after the layout changes in 2026 — without it, historical
    /// re-runs break (§8).
    /// </summary>
    public bool CoversDate(DateOnly date) =>
        date >= EffectiveFrom && (EffectiveTo is null || date <= EffectiveTo);
}
