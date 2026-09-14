using Microsoft.Data.SqlClient;
using Recon.Data;

namespace Recon.IntegrationTests;

/// <summary>
/// Inserts a CliQ ↔ OM configuration into <c>cfg</c> so the tests exercise the
/// real load path: the engine reads its definition from the database exactly as
/// production would, rather than from an object built in C#.
/// </summary>
internal static class TestConfiguration
{
    /// <summary>
    /// Every test in the collection shares one database, and cfg.Counterparty,
    /// cfg.Dataset and cfg.ReconciliationDefinition all have UNIQUE codes. A
    /// per-invocation suffix keeps the tests independent without tearing the
    /// schema down between them — and the isolation is real, because each test
    /// then reconciles its own datasets.
    /// </summary>
    private static int _sequence;

    public static async Task<Ids> CreateAsync(SqlConnection connection)
    {
        var suffix = Interlocked.Increment(ref _sequence).ToString(
            System.Globalization.CultureInfo.InvariantCulture);

        var counterpartyId = await Db.ScalarAsync<int>(connection,
            """
            INSERT cfg.Counterparty (Code, Name, CreatedBy)
            VALUES (@code, N'Integration JoPACC', 'tests');
            SELECT CAST(SCOPE_IDENTITY() AS INT);
            """,
            c => c.With("@code", "IT_JOPACC_" + suffix)).ConfigureAwait(false);

        var leftId = await CreateDatasetAsync(connection, counterpartyId, "IT_CLIQ_" + suffix,
            "CliQ session", "File", duplicateKey: "REF_PRIMARY").ConfigureAwait(false);

        var rightId = await CreateDatasetAsync(connection, counterpartyId, "IT_OM_" + suffix,
            "OM transactions", "Sql", duplicateKey: null).ConfigureAwait(false);

        // Left: the CliQ side. REF_PRIMARY carries a normalized companion so
        // pass 2 can absorb formatting noise as an indexed Exact match.
        await AddFieldAsync(connection, leftId, "REF_PRIMARY", "End To End Id", "String",
            "Reference", "Text1", indexed: true, required: true,
            normalize: true, normalizedSlot: "Text21").ConfigureAwait(false);
        await AddFieldAsync(connection, leftId, "AMOUNT", "Amount", "Integer",
            "Amount", "Num1", required: true).ConfigureAwait(false);
        await AddFieldAsync(connection, leftId, "CURRENCY", "Currency", "String",
            "Currency", "Text5", matchable: false, required: true).ConfigureAwait(false);
        await AddFieldAsync(connection, leftId, "TX_DATETIME", "Transaction Date Time", "DateTime",
            "Date", "Date1", required: true).ConfigureAwait(false);
        await AddFieldAsync(connection, leftId, "DIRECTION", "Direction", "String",
            "Direction", "Text6", required: true).ConfigureAwait(false);
        await AddFieldAsync(connection, leftId, "STATUS", "Status", "String",
            "Status", "Text7").ConfigureAwait(false);

        // Right: the OM side, with matching roles under different names — the
        // point of the field registry.
        await AddFieldAsync(connection, rightId, "OM_REF", "OM Reference", "String",
            "Reference", "Text1", indexed: true, required: true,
            normalize: true, normalizedSlot: "Text21").ConfigureAwait(false);
        await AddFieldAsync(connection, rightId, "AMOUNT_MINOR", "Amount", "Integer",
            "Amount", "Num1", required: true).ConfigureAwait(false);
        await AddFieldAsync(connection, rightId, "CURRENCY", "Currency", "String",
            "Currency", "Text5", matchable: false, required: true).ConfigureAwait(false);
        await AddFieldAsync(connection, rightId, "POSTED_AT", "Posted At", "DateTime",
            "Date", "Date1", required: true).ConfigureAwait(false);
        await AddFieldAsync(connection, rightId, "DIRECTION", "Direction", "String",
            "Direction", "Text6", required: true).ConfigureAwait(false);
        await AddFieldAsync(connection, rightId, "STATUS", "Status", "String",
            "Status", "Text7").ConfigureAwait(false);

        var definitionId = await Db.ScalarAsync<int>(connection,
            """
            INSERT cfg.ReconciliationDefinition
                (CounterpartyId, Code, Name, LeftDatasetId, RightDatasetId, CreatedBy)
            VALUES (@cp, @code, N'Integration CliQ to OM', @left, @right, 'tests');
            SELECT CAST(SCOPE_IDENTITY() AS INT);
            """,
            c => c.With("@cp", counterpartyId)
                  .With("@code", "IT_CLIQ_OM_" + suffix)
                  .With("@left", leftId)
                  .With("@right", rightId)).ConfigureAwait(false);

        // Pass 1: the clean reference match.
        var pass1 = await AddRuleAsync(connection, definitionId, "P1_REF", "Primary reference", 1)
            .ConfigureAwait(false);
        await AddConditionAsync(connection, pass1, leftId, "REF_PRIMARY", rightId, "OM_REF", "Exact")
            .ConfigureAwait(false);

        // Pass 2: the SAME field pair, but comparing the parse-time normalized
        // companions. useNormalized is what distinguishes it from pass 1 — and
        // it has to be stated, not inferred from the fields.
        var pass2 = await AddRuleAsync(connection, definitionId, "P2_REF_NORM", "Normalized", 2)
            .ConfigureAwait(false);
        await AddConditionAsync(connection, pass2, leftId, "REF_PRIMARY", rightId, "OM_REF", "Exact",
            useNormalized: true).ConfigureAwait(false);

        // Pass 3: the composite last resort.
        var pass3 = await AddRuleAsync(connection, definitionId, "P3_COMPOSITE", "Amount + date", 3)
            .ConfigureAwait(false);
        await AddConditionAsync(connection, pass3, leftId, "AMOUNT", rightId, "AMOUNT_MINOR",
            "NumericExact").ConfigureAwait(false);
        await AddConditionAsync(connection, pass3, leftId, "TX_DATETIME", rightId, "POSTED_AT",
            "DateWithin", tolerance: 1, unit: "Day", sequence: 2).ConfigureAwait(false);

        // Rejected transactions leave the working set before pass 1 and never
        // become exceptions (finding C6).
        await Db.ExecuteAsync(connection,
            """
            INSERT cfg.ExclusionRule (DatasetId, Name, ConditionJson, ReasonCode)
            VALUES (@ds, N'Rejected', @json, 'REJECTED');
            """,
            c => c.With("@ds", leftId)
                  .With("@json", """{"op":"and","items":[{"field":"STATUS","cmp":"eq","value":"RJCT"}]}"""))
            .ConfigureAwait(false);

        // The two classifications the SRS names.
        await Db.ExecuteAsync(connection,
            """
            INSERT cfg.ClassificationRule
                (DefinitionId, ExceptionCode, DisplayName, AppliesToSide, ConditionJson, Sequence)
            VALUES (@def, 'FAILED_INWARD', N'In CliQ, not in OM', 'Left', @all, 1),
                   (@def, 'MISSING_IN_CLIQ', N'In OM, not in CliQ', 'Right', @all, 2);
            """,
            c => c.With("@def", definitionId)
                  .With("@all", """{"op":"and","items":[{"field":"DIRECTION","cmp":"isnotnull"}]}"""))
            .ConfigureAwait(false);

        return new Ids(counterpartyId, leftId, rightId, definitionId);
    }

    /// <summary>
    /// Adds a control total comparing the two sides' matched totals. Separate
    /// so a test can choose whether the run has to balance.
    /// </summary>
    public static Task<int> AddControlTotalAsync(
        SqlConnection connection, Ids ids, string checkCode, long tolerance, bool failRun) =>
        Db.ExecuteAsync(connection,
            """
            INSERT cfg.ControlTotalDefinition
                (DefinitionId, CheckCode, DisplayName,
                 SourceAExpressionJson, SourceBExpressionJson,
                 Scope, SourceTypeA, SourceTypeB, ToleranceMinor, FailRunOnMismatch)
            VALUES (@def, @code, N'Matched totals agree',
                    @a, @b, 'Run', 'Staging', 'Staging', @tol, @fail);
            """,
            c => c.With("@def", ids.DefinitionId)
                  .With("@code", checkCode)
                  .With("@a", $"{{\"function\":\"SumAmount\",\"datasetId\":{ids.LeftDatasetId},\"matchStatus\":\"Matched\"}}")
                  .With("@b", $"{{\"function\":\"SumAmount\",\"datasetId\":{ids.RightDatasetId},\"matchStatus\":\"Matched\"}}")
                  .With("@tol", tolerance)
                  .With("@fail", failRun));

    private static async Task<int> CreateDatasetAsync(
        SqlConnection connection, int counterpartyId, string code, string name,
        string provider, string? duplicateKey) =>
        await Db.ScalarAsync<int>(connection,
            """
            INSERT cfg.Dataset
                (CounterpartyId, Code, Name, ProviderType, DefaultCurrency,
                 DuplicateKeyFields, IsActive, CreatedBy)
            VALUES (@cp, @code, @name, @provider, 'JOD', @dup, 1, 'tests');
            SELECT CAST(SCOPE_IDENTITY() AS INT);
            """,
            c => c.With("@cp", counterpartyId)
                  .With("@code", code)
                  .With("@name", name)
                  .With("@provider", provider)
                  .With("@dup", duplicateKey)).ConfigureAwait(false);

    private static Task<int> AddFieldAsync(
        SqlConnection connection, int datasetId, string code, string label,
        string dataType, string? role, string slot,
        bool matchable = true, bool indexed = false, bool required = false,
        bool normalize = false, string? normalizedSlot = null) =>
        Db.ExecuteAsync(connection,
            """
            INSERT cfg.DatasetField
                (DatasetId, FieldCode, DisplayLabel, DataType, FieldRole, StorageSlot,
                 IsMatchable, IsIndexed, IsRequired, NormalizeForMatch, NormalizedSlot)
            VALUES (@ds, @code, @label, @type, @role, @slot,
                    @matchable, @indexed, @required, @normalize, @normSlot);
            """,
            c => c.With("@ds", datasetId)
                  .With("@code", code)
                  .With("@label", label)
                  .With("@type", dataType)
                  .With("@role", role)
                  .With("@slot", slot)
                  .With("@matchable", matchable)
                  .With("@indexed", indexed)
                  .With("@required", required)
                  .With("@normalize", normalize)
                  .With("@normSlot", normalizedSlot));

    private static async Task<int> AddRuleAsync(
        SqlConnection connection, int definitionId, string code, string name, int sequence) =>
        await Db.ScalarAsync<int>(connection,
            """
            INSERT cfg.MatchRule (DefinitionId, RuleCode, Name, Sequence)
            VALUES (@def, @code, @name, @seq);
            SELECT CAST(SCOPE_IDENTITY() AS INT);
            """,
            c => c.With("@def", definitionId)
                  .With("@code", code)
                  .With("@name", name)
                  .With("@seq", sequence)).ConfigureAwait(false);

    private static Task<int> AddConditionAsync(
        SqlConnection connection, int ruleId,
        int leftDatasetId, string leftField, int rightDatasetId, string rightField,
        string comparison, long? tolerance = null, string? unit = null, int sequence = 1,
        bool useNormalized = false) =>
        Db.ExecuteAsync(connection,
            """
            INSERT cfg.MatchCondition
                (MatchRuleId, LeftFieldId, RightFieldId, ComparisonType,
                 ToleranceValue, ToleranceUnit, Sequence, UseNormalized)
            SELECT @rule,
                   (SELECT DatasetFieldId FROM cfg.DatasetField
                    WHERE DatasetId = @leftDs AND FieldCode = @leftField),
                   (SELECT DatasetFieldId FROM cfg.DatasetField
                    WHERE DatasetId = @rightDs AND FieldCode = @rightField),
                   @cmp, @tol, @unit, @seq, @norm;
            """,
            c => c.With("@rule", ruleId)
                  .With("@leftDs", leftDatasetId)
                  .With("@leftField", leftField)
                  .With("@rightDs", rightDatasetId)
                  .With("@rightField", rightField)
                  .With("@cmp", comparison)
                  .With("@tol", tolerance)
                  .With("@unit", unit)
                  .With("@seq", sequence)
                  .With("@norm", useNormalized));

    internal sealed record Ids(int CounterpartyId, int LeftDatasetId, int RightDatasetId, int DefinitionId);
}
