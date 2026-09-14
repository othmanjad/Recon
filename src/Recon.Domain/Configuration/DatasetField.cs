using Recon.Domain.Money;

namespace Recon.Domain.Configuration;

/// <summary>
/// One row of <c>cfg.DatasetField</c> — the field registry that makes the whole
/// design possible (§6).
///
/// <para>
/// <see cref="IsMatchable"/> is the security boundary. Rule conditions,
/// classification rules, exclusion rules, source filters and report filters
/// resolve field names against the registry and <b>only</b> the registry —
/// never from free user text. This is what prevents SQL injection through a
/// rule builder the user drives, and the design calls it the single most
/// important safety property in the system.
/// </para>
/// </summary>
public sealed record DatasetField
{
    public required int DatasetFieldId { get; init; }
    public required int DatasetId { get; init; }

    /// <summary>Stable internal code, e.g. <c>REF_PRIMARY</c>. Rules cite this.</summary>
    public required string FieldCode { get; init; }

    /// <summary>What Operations sees, e.g. "End To End Id".</summary>
    public required string DisplayLabel { get; init; }

    public required FieldDataType DataType { get; init; }
    public FieldRole? Role { get; init; }

    /// <summary>
    /// The physical column in <c>stg.StagingTransaction</c>, e.g. <c>Text1</c>.
    /// Nothing outside the provider and compiler layers may reference a slot
    /// directly — the registry is the only path to the data, which is what
    /// preserves the option to change storage models later (§7).
    /// </summary>
    public required string StorageSlot { get; init; }

    public bool IsMatchable { get; init; } = true;
    public bool IsIndexed { get; init; }
    public bool IsRequired { get; init; }

    /// <summary>
    /// When set, the parser writes a normalized copy of the value into
    /// <see cref="NormalizedSlot"/> at load time, and a rule can then compare
    /// that companion with <see cref="ComparisonType.Exact"/> — an indexed
    /// seek instead of the scan a runtime <c>UPPER(TRIM(...))</c> would force
    /// (blocker A2).
    /// </summary>
    public bool NormalizeForMatch { get; init; }

    /// <summary>
    /// A slot from the companion pool (<c>Text21..Text30</c>). The pools are
    /// separate because in v0.2 a companion could silently overwrite a mapped
    /// field (review finding 2); the database enforces the split.
    /// </summary>
    public string? NormalizedSlot { get; init; }

    public int DisplayOrder { get; init; }

    /// <summary>
    /// The slot a rule must read for this field: the normalized companion when
    /// the rule asked for it and the field has one, otherwise the field's own
    /// storage.
    /// </summary>
    public string SlotFor(bool useNormalized)
    {
        if (!useNormalized)
        {
            return StorageSlot;
        }

        if (!NormalizeForMatch || NormalizedSlot is null)
        {
            throw new InvalidOperationException(
                $"Field {FieldCode} has no normalized companion slot, so a rule cannot ask to compare one.");
        }

        return NormalizedSlot;
    }
}
