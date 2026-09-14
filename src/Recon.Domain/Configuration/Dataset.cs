namespace Recon.Domain.Configuration;

/// <summary>
/// A named stream of transactions from one counterparty, with its own field
/// registry. A reconciliation is always dataset ↔ dataset — not "file vs
/// database" — which is the generalization that lets file↔file, api↔database
/// and calculated↔reported all be the same operation to the engine (§2).
/// </summary>
public sealed record Dataset
{
    public required int DatasetId { get; init; }
    public required int CounterpartyId { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required ProviderType Provider { get; init; }
    public string TimeZone { get; init; } = "Asia/Amman";
    public string? DefaultCurrency { get; init; }

    /// <summary>
    /// Comma-separated field codes forming the dataset's declared key. A file
    /// containing the same reference twice would otherwise match one row and
    /// orphan the other with no signal that the SOURCE was the problem
    /// (finding C3); second and later occurrences are flagged
    /// <see cref="MatchStatus.Duplicate"/> before matching and never enter it.
    /// </summary>
    public string? DuplicateKeyFields { get; init; }

    public bool IsActive { get; init; }

    public required IReadOnlyList<DatasetField> Fields { get; init; }

    private Dictionary<string, DatasetField>? _byCode;

    private Dictionary<string, DatasetField> ByCode =>
        _byCode ??= Fields.ToDictionary(f => f.FieldCode, StringComparer.Ordinal);

    /// <summary>
    /// Resolves a field code against the registry. <b>Ordinal, case-sensitive
    /// lookup on purpose:</b> the database collates its enum whitelists
    /// case-sensitively (finding X1), and a rule citing <c>ref_primary</c> for
    /// <c>REF_PRIMARY</c> is a configuration error worth surfacing, not
    /// guessing at.
    /// </summary>
    public bool TryGetField(string fieldCode, out DatasetField field) =>
        ByCode.TryGetValue(fieldCode, out field!);

    public DatasetField GetField(string fieldCode) =>
        TryGetField(fieldCode, out var f)
            ? f
            : throw new FieldNotInRegistryException(fieldCode, Code);

    public IEnumerable<DatasetField> Matchable => Fields.Where(f => f.IsMatchable);

    public DatasetField? FieldWithRole(FieldRole role) =>
        Fields.FirstOrDefault(f => f.Role == role);

    /// <summary>The duplicate-detection key, resolved to fields.</summary>
    public IReadOnlyList<DatasetField> DuplicateKey()
    {
        if (string.IsNullOrWhiteSpace(DuplicateKeyFields))
        {
            return [];
        }

        return DuplicateKeyFields
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(GetField)
            .ToList();
    }
}

/// <summary>
/// Thrown when anything cites a field the dataset's registry does not contain.
/// This is the exception that stops an injection attempt through a user-built
/// rule: the code never reaches the compiler, so it never reaches SQL.
/// </summary>
public sealed class FieldNotInRegistryException(string fieldCode, string datasetCode)
    : Exception($"Field '{fieldCode}' is not in the field registry of dataset '{datasetCode}'.")
{
    public string FieldCode { get; } = fieldCode;
    public string DatasetCode { get; } = datasetCode;
}

/// <summary>
/// Thrown when something cites a real field that the registry does not mark
/// matchable. Distinct from <see cref="FieldNotInRegistryException"/> because
/// the causes differ: one is a typo or an attack, the other is a field
/// deliberately withheld from the rule builder.
/// </summary>
public sealed class FieldNotMatchableException(string fieldCode, string datasetCode)
    : Exception($"Field '{fieldCode}' of dataset '{datasetCode}' is not marked matchable and cannot appear in a condition.")
{
    public string FieldCode { get; } = fieldCode;
    public string DatasetCode { get; } = datasetCode;
}
