using Microsoft.Data.SqlClient;
using Recon.Domain.Conditions;
using Recon.Domain.Configuration;
using Recon.Domain.Money;

namespace Recon.Data;

/// <summary>
/// Loads a reconciliation definition, with both field registries, from
/// <c>cfg</c>.
///
/// <para>
/// A run loads this once at start and then serialises it into
/// <c>ops.ReconRun.DefinitionSnapshotJson</c>. Everything after that point
/// reads the snapshot, not this repository — see
/// <see cref="RunRepository"/>. That is review blocker B1: rules are edited in
/// place, so "version 3" today is not "version 3" last month, and an auditor
/// asking which rule matched a row in June would otherwise get a wrong answer
/// with a confident label.
/// </para>
/// </summary>
public sealed class ConfigurationRepository(SqlConnection connection)
{
    private readonly SqlConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    public async Task<Dictionary<string, Currency>> LoadCurrenciesAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await Db.QueryAsync(
            _connection,
            "SELECT CurrencyCode, Name, MinorUnits FROM cfg.Currency WHERE IsActive = 1;",
            r => new Currency(r.GetString(0), r.GetString(1), r.GetByte(2)),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return rows.ToDictionary(c => c.Code, StringComparer.Ordinal);
    }

    public async Task<Dictionary<string, string>> LoadSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await Db.QueryAsync(
            _connection,
            "SELECT SettingKey, SettingValue FROM cfg.PlatformSetting;",
            r => (Key: r.GetString(0), Value: r.GetString(1)),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return rows.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
    }

    public async Task<Dataset> LoadDatasetAsync(
        int datasetId, CancellationToken cancellationToken = default)
    {
        var datasets = await Db.QueryAsync(
            _connection,
            """
            SELECT DatasetId, CounterpartyId, Code, Name, ProviderType, TimeZone,
                   DefaultCurrency, DuplicateKeyFields, IsActive
            FROM cfg.Dataset WHERE DatasetId = @id;
            """,
            r => new
            {
                DatasetId = r.GetInt32(0),
                CounterpartyId = r.GetInt32(1),
                Code = r.GetString(2),
                Name = r.GetString(3),
                Provider = Db.ParseEnum<ProviderType>(r.GetString(4)),
                TimeZone = r.GetString(5),
                DefaultCurrency = r.GetNullableString("DefaultCurrency"),
                DuplicateKeyFields = r.GetNullableString("DuplicateKeyFields"),
                IsActive = r.GetBoolean(8),
            },
            c => c.With("@id", datasetId),
            cancellationToken).ConfigureAwait(false);

        var row = datasets.Count == 1
            ? datasets[0]
            : throw new InvalidOperationException($"dataset {datasetId} was not found");

        var fields = await LoadFieldsAsync(datasetId, cancellationToken).ConfigureAwait(false);

        return new Dataset
        {
            DatasetId = row.DatasetId,
            CounterpartyId = row.CounterpartyId,
            Code = row.Code,
            Name = row.Name,
            Provider = row.Provider,
            TimeZone = row.TimeZone,
            DefaultCurrency = row.DefaultCurrency,
            DuplicateKeyFields = row.DuplicateKeyFields,
            IsActive = row.IsActive,
            Fields = fields,
        };
    }

    private async Task<List<DatasetField>> LoadFieldsAsync(
        int datasetId, CancellationToken cancellationToken)
    {
        return await Db.QueryAsync(
            _connection,
            """
            SELECT DatasetFieldId, DatasetId, FieldCode, DisplayLabel, DataType, FieldRole,
                   StorageSlot, IsMatchable, IsIndexed, IsRequired,
                   NormalizeForMatch, NormalizedSlot, DisplayOrder
            FROM cfg.DatasetField
            WHERE DatasetId = @id
            ORDER BY DisplayOrder, DatasetFieldId;
            """,
            r => new DatasetField
            {
                DatasetFieldId = r.GetInt32(0),
                DatasetId = r.GetInt32(1),
                FieldCode = r.GetString(2),
                DisplayLabel = r.GetString(3),
                DataType = Db.ParseEnum<FieldDataType>(r.GetString(4)),
                Role = r.GetNullableString("FieldRole") is { } role
                    ? Db.ParseEnum<FieldRole>(role)
                    : null,
                StorageSlot = r.GetString(6),
                IsMatchable = r.GetBoolean(7),
                IsIndexed = r.GetBoolean(8),
                IsRequired = r.GetBoolean(9),
                NormalizeForMatch = r.GetBoolean(10),
                NormalizedSlot = r.GetNullableString("NormalizedSlot"),
                DisplayOrder = r.GetInt32(12),
            },
            c => c.With("@id", datasetId),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReconciliationDefinition> LoadDefinitionAsync(
        int definitionId, CancellationToken cancellationToken = default)
    {
        var header = await Db.QueryAsync(
            _connection,
            """
            SELECT DefinitionId, CounterpartyId, Code, Name, LeftDatasetId, RightDatasetId,
                   MatchingWindowDaysBefore, MatchingWindowDaysAfter, Version, IsActive
            FROM cfg.ReconciliationDefinition WHERE DefinitionId = @id;
            """,
            r => new
            {
                DefinitionId = r.GetInt32(0),
                CounterpartyId = r.GetInt32(1),
                Code = r.GetString(2),
                Name = r.GetString(3),
                LeftDatasetId = r.GetInt32(4),
                RightDatasetId = r.GetInt32(5),
                Before = r.GetInt32(6),
                After = r.GetInt32(7),
                Version = r.GetInt32(8),
                IsActive = r.GetBoolean(9),
            },
            c => c.With("@id", definitionId),
            cancellationToken).ConfigureAwait(false);

        var row = header.Count == 1
            ? header[0]
            : throw new InvalidOperationException($"definition {definitionId} was not found");

        var left = await LoadDatasetAsync(row.LeftDatasetId, cancellationToken).ConfigureAwait(false);
        var right = await LoadDatasetAsync(row.RightDatasetId, cancellationToken).ConfigureAwait(false);

        var rules = await LoadRulesAsync(definitionId, left, right, cancellationToken).ConfigureAwait(false);
        var classifications = await LoadClassificationsAsync(definitionId, cancellationToken).ConfigureAwait(false);
        var controlTotals = await LoadControlTotalsAsync(definitionId, cancellationToken).ConfigureAwait(false);
        var exclusions = await LoadExclusionsAsync(
            [left.DatasetId, right.DatasetId], cancellationToken).ConfigureAwait(false);

        return new ReconciliationDefinition
        {
            DefinitionId = row.DefinitionId,
            CounterpartyId = row.CounterpartyId,
            Code = row.Code,
            Name = row.Name,
            Left = left,
            Right = right,
            MatchingWindowDaysBefore = row.Before,
            MatchingWindowDaysAfter = row.After,
            Version = row.Version,
            IsActive = row.IsActive,
            Rules = rules,
            Classifications = classifications,
            ControlTotals = controlTotals,
            Exclusions = exclusions,
        };
    }

    private async Task<List<MatchRule>> LoadRulesAsync(
        int definitionId, Dataset left, Dataset right, CancellationToken cancellationToken)
    {
        var rules = await Db.QueryAsync(
            _connection,
            """
            SELECT MatchRuleId, RuleCode, Name, Sequence, LeftFilterJson, RightFilterJson,
                   MatchMode, LeftGroupByFields, RightGroupByFields, AggregateFunction,
                   Cardinality, OnMultipleMatch, IsActive
            FROM cfg.MatchRule
            WHERE DefinitionId = @id
            ORDER BY Sequence;
            """,
            r => new
            {
                MatchRuleId = r.GetInt32(0),
                RuleCode = r.GetString(1),
                Name = r.GetString(2),
                Sequence = r.GetInt32(3),
                LeftFilterJson = r.GetNullableString("LeftFilterJson"),
                RightFilterJson = r.GetNullableString("RightFilterJson"),
                Mode = Db.ParseEnum<MatchMode>(r.GetString(6)),
                LeftGroupBy = r.GetNullableString("LeftGroupByFields"),
                RightGroupBy = r.GetNullableString("RightGroupByFields"),
                AggregateFunction = r.GetNullableString("AggregateFunction"),
                Cardinality = Db.ParseEnum<Cardinality>(r.GetString(10)),
                OnMultipleMatch = Db.ParseEnum<OnMultipleMatch>(r.GetString(11)),
                IsActive = r.GetBoolean(12),
            },
            c => c.With("@id", definitionId),
            cancellationToken).ConfigureAwait(false);

        var conditions = await Db.QueryAsync(
            _connection,
            """
            SELECT c.MatchRuleId, c.MatchConditionId, lf.FieldCode, rf.FieldCode,
                   c.ComparisonType, c.ToleranceValue, c.ToleranceUnit, c.Sequence,
                   c.UseNormalized, lf.NormalizeForMatch, rf.NormalizeForMatch
            FROM cfg.MatchCondition AS c
            JOIN cfg.MatchRule AS r ON r.MatchRuleId = c.MatchRuleId
            JOIN cfg.DatasetField AS lf ON lf.DatasetFieldId = c.LeftFieldId
            JOIN cfg.DatasetField AS rf ON rf.DatasetFieldId = c.RightFieldId
            WHERE r.DefinitionId = @id
            ORDER BY c.MatchRuleId, c.Sequence;
            """,
            r => new
            {
                MatchRuleId = r.GetInt32(0),
                Condition = new MatchCondition
                {
                    MatchConditionId = r.GetInt32(1),
                    LeftFieldCode = r.GetString(2),
                    RightFieldCode = r.GetString(3),
                    Comparison = Db.ParseEnum<ComparisonType>(r.GetString(4)),
                    ToleranceValue = r.GetNullableInt64("ToleranceValue"),
                    ToleranceUnit = r.GetNullableString("ToleranceUnit") is { } unit
                        ? Db.ParseEnum<ToleranceUnit>(unit)
                        : null,
                    Sequence = r.GetInt32(7),
                    // Read from the rule, not inferred from the fields. The
                    // earlier version derived this as "both sides have a
                    // companion", which made pass 1 a normalized pass too.
                    // Both fields must still HAVE a companion, or the
                    // comparison would put a normalized value against a raw
                    // one and match nothing — so a rule asking for it without
                    // the fields to support it is a configuration error and
                    // surfaces as one.
                    UseNormalized = r.GetBoolean(8)
                        ? r.GetBoolean(9) && r.GetBoolean(10)
                            ? true
                            : throw new InvalidOperationException(
                                $"match condition {r.GetInt32(1)} asks to compare normalized " +
                                $"companions, but {r.GetString(2)} or {r.GetString(3)} has none.")
                        : false,
                },
            },
            c => c.With("@id", definitionId),
            cancellationToken).ConfigureAwait(false);

        var byRule = conditions
            .GroupBy(x => x.MatchRuleId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<MatchCondition>)g.Select(x => x.Condition).ToList());

        return rules.Select(r => new MatchRule
        {
            MatchRuleId = r.MatchRuleId,
            RuleCode = r.RuleCode,
            Name = r.Name,
            Sequence = r.Sequence,
            LeftFilter = ConditionNode.Parse(r.LeftFilterJson),
            RightFilter = ConditionNode.Parse(r.RightFilterJson),
            Mode = r.Mode,
            LeftGroupByFields = SplitCodes(r.LeftGroupBy),
            RightGroupByFields = SplitCodes(r.RightGroupBy),
            AggregateFunction = r.AggregateFunction,
            Cardinality = r.Cardinality,
            OnMultipleMatch = r.OnMultipleMatch,
            IsActive = r.IsActive,
            Conditions = byRule.TryGetValue(r.MatchRuleId, out var list) ? list : [],
        }).ToList();
    }

    private async Task<List<ClassificationRule>> LoadClassificationsAsync(
        int definitionId, CancellationToken cancellationToken)
    {
        return await Db.QueryAsync(
            _connection,
            """
            SELECT ClassificationRuleId, ExceptionCode, DisplayName, AppliesToSide,
                   ConditionJson, ActionType, Severity, Sequence, IsActive
            FROM cfg.ClassificationRule
            WHERE DefinitionId = @id
            ORDER BY Sequence;
            """,
            r => new ClassificationRule
            {
                ClassificationRuleId = r.GetInt32(0),
                ExceptionCode = r.GetString(1),
                DisplayName = r.GetString(2),
                AppliesToSide = Db.ParseEnum<ClassificationSide>(r.GetString(3)),
                Condition = ConditionNode.Parse(r.GetString(4))
                    ?? throw new InvalidOperationException(
                        $"classification {r.GetString(1)} has an empty condition"),
                ActionType = r.GetString(5),
                Severity = r.GetString(6),
                Sequence = r.GetInt32(7),
                IsActive = r.GetBoolean(8),
            },
            c => c.With("@id", definitionId),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<ControlTotalDefinition>> LoadControlTotalsAsync(
        int definitionId, CancellationToken cancellationToken)
    {
        return await Db.QueryAsync(
            _connection,
            """
            SELECT ControlTotalId, CheckCode, DisplayName,
                   SourceAExpressionJson, SourceBExpressionJson,
                   Scope, SourceTypeA, SourceTypeB, PeriodDays,
                   ToleranceMinor, FailRunOnMismatch, IsActive
            FROM cfg.ControlTotalDefinition
            WHERE DefinitionId = @id
            ORDER BY ControlTotalId;
            """,
            r => new ControlTotalDefinition
            {
                ControlTotalId = r.GetInt32(0),
                CheckCode = r.GetString(1),
                DisplayName = r.GetString(2),
                SourceA = AggregateSpecJson.Parse(r.GetString(3)),
                SourceB = AggregateSpecJson.Parse(r.GetString(4)),
                Scope = Db.ParseEnum<ControlTotalScope>(r.GetString(5)),
                SourceTypeA = Db.ParseEnum<ControlTotalSource>(r.GetString(6)),
                SourceTypeB = Db.ParseEnum<ControlTotalSource>(r.GetString(7)),
                PeriodDays = r.GetNullableInt32("PeriodDays"),
                ToleranceMinor = r.GetInt64(9),
                FailRunOnMismatch = r.GetBoolean(10),
                IsActive = r.GetBoolean(11),
            },
            c => c.With("@id", definitionId),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<ExclusionRule>> LoadExclusionsAsync(
        int[] datasetIds, CancellationToken cancellationToken)
    {
        var all = new List<ExclusionRule>();

        foreach (var datasetId in datasetIds.Distinct())
        {
            var rules = await Db.QueryAsync(
                _connection,
                """
                SELECT ExclusionRuleId, DatasetId, Name, ConditionJson, ReasonCode, IsActive
                FROM cfg.ExclusionRule
                WHERE DatasetId = @id AND IsActive = 1
                ORDER BY ExclusionRuleId;
                """,
                r => new ExclusionRule
                {
                    ExclusionRuleId = r.GetInt32(0),
                    DatasetId = r.GetInt32(1),
                    Name = r.GetString(2),
                    Condition = ConditionNode.Parse(r.GetString(3))
                        ?? throw new InvalidOperationException(
                            $"exclusion rule {r.GetString(2)} has an empty condition"),
                    ReasonCode = r.GetString(4),
                    IsActive = r.GetBoolean(5),
                },
                c => c.With("@id", datasetId),
                cancellationToken).ConfigureAwait(false);

            all.AddRange(rules);
        }

        return all;
    }

    private static string[] SplitCodes(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? []
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

internal static class AggregateSpecJson
{
    private static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        // An aggregate spec names a MatchStatus and a Side by name, so the
        // enum converter is not optional — without it every control total
        // that restricts to matched rows fails to deserialize.
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static AggregateSpec Parse(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<AggregateSpec>(json, Options)
        ?? throw new InvalidOperationException($"aggregate spec is empty: {json}");
}
