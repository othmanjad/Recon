using Recon.Domain.Configuration;
using Recon.Domain.Money;

namespace Recon.UnitTests;

/// <summary>
/// A CliQ ↔ OM definition shaped exactly like the one in the design (§9.3),
/// so the tests exercise the real thing rather than a toy.
/// </summary>
internal static class Fixtures
{
    public static readonly Currency Jod = new("JOD", "Jordanian Dinar", 3);
    public static readonly Currency Usd = new("USD", "US Dollar", 2);

    public static Dataset CliqSession() => new()
    {
        DatasetId = 1,
        CounterpartyId = 1,
        Code = "CLIQ_SESSION",
        Name = "CliQ Session File",
        Provider = ProviderType.File,
        DefaultCurrency = "JOD",
        DuplicateKeyFields = "REF_PRIMARY",
        IsActive = true,
        Fields =
        [
            Field(11, 1, "REF_PRIMARY", "End To End Id", FieldDataType.String,
                FieldRole.Reference, "Text1", indexed: true, required: true,
                normalize: true, normalizedSlot: "Text21"),
            Field(12, 1, "REF_TXN", "Transaction Id", FieldDataType.String,
                FieldRole.Reference, "Text2", indexed: true, required: true),
            Field(13, 1, "ORIG_REF", "Original Reference", FieldDataType.String,
                FieldRole.OriginalReference, "Text3"),
            Field(14, 1, "CRDTR_ACCT", "Creditor Account", FieldDataType.String,
                FieldRole.Party, "Text4", normalize: true, normalizedSlot: "Text22"),
            Field(15, 1, "AMOUNT", "Amount", FieldDataType.Integer,
                FieldRole.Amount, "Num1", required: true),
            // Not matchable: the currency is context, not a matching key, and
            // the tests use it to prove the boundary is enforced.
            Field(16, 1, "CURRENCY", "Currency", FieldDataType.String,
                FieldRole.Currency, "Text5", matchable: false, required: true),
            Field(17, 1, "TX_DATETIME", "Transaction Date Time", FieldDataType.DateTime,
                FieldRole.Date, "Date1", required: true),
            Field(18, 1, "DIRECTION", "Direction", FieldDataType.String,
                FieldRole.Direction, "Text6", required: true),
            Field(19, 1, "STATUS", "Status", FieldDataType.String,
                FieldRole.Status, "Text7"),
        ],
    };

    public static Dataset OrangeMoney() => new()
    {
        DatasetId = 2,
        CounterpartyId = 1,
        Code = "OM_TXN",
        Name = "Orange Money Transactions",
        Provider = ProviderType.Sql,
        DefaultCurrency = "JOD",
        IsActive = true,
        Fields =
        [
            Field(21, 2, "OM_REF", "OM Reference", FieldDataType.String,
                FieldRole.Reference, "Text1", indexed: true, required: true,
                normalize: true, normalizedSlot: "Text21"),
            Field(22, 2, "EXT_REF", "External Reference", FieldDataType.String,
                FieldRole.Reference, "Text2", indexed: true),
            Field(23, 2, "ACCOUNT", "Account", FieldDataType.String,
                FieldRole.Party, "Text3", required: true),
            Field(24, 2, "AMOUNT_MINOR", "Amount", FieldDataType.Integer,
                FieldRole.Amount, "Num1", required: true),
            Field(25, 2, "CURRENCY", "Currency", FieldDataType.String,
                FieldRole.Currency, "Text4", matchable: false, required: true),
            Field(26, 2, "POSTED_AT", "Posted At", FieldDataType.DateTime,
                FieldRole.Date, "Date1", required: true),
            Field(27, 2, "DIRECTION", "Direction", FieldDataType.String,
                FieldRole.Direction, "Text5", required: true),
            Field(28, 2, "IS_REVERSED", "Is Reversed", FieldDataType.Boolean,
                FieldRole.Status, "Flag1"),
        ],
    };

    /// <summary>The four-pass rule set from §9.3.</summary>
    public static ReconciliationDefinition CliqOm() => new()
    {
        DefinitionId = 1,
        CounterpartyId = 1,
        Code = "CLIQ_OM_INWARD",
        Name = "CliQ to OM",
        Left = CliqSession(),
        Right = OrangeMoney(),
        IsActive = true,
        Rules =
        [
            new MatchRule
            {
                MatchRuleId = 1, RuleCode = "P1_REF", Name = "Primary reference", Sequence = 1,
                Conditions =
                [
                    new MatchCondition
                    {
                        MatchConditionId = 1,
                        LeftFieldCode = "REF_PRIMARY",
                        RightFieldCode = "OM_REF",
                        Comparison = ComparisonType.Exact,
                    },
                ],
            },
            new MatchRule
            {
                MatchRuleId = 2, RuleCode = "P2_REF_NORM", Name = "Normalized companion", Sequence = 2,
                Conditions =
                [
                    new MatchCondition
                    {
                        MatchConditionId = 2,
                        LeftFieldCode = "REF_PRIMARY",
                        RightFieldCode = "OM_REF",
                        Comparison = ComparisonType.Exact,
                        UseNormalized = true,
                    },
                ],
            },
            new MatchRule
            {
                MatchRuleId = 4, RuleCode = "P4_COMPOSITE", Name = "Amount + date + account", Sequence = 4,
                Conditions =
                [
                    new MatchCondition
                    {
                        MatchConditionId = 4,
                        LeftFieldCode = "AMOUNT",
                        RightFieldCode = "AMOUNT_MINOR",
                        Comparison = ComparisonType.NumericExact,
                    },
                    new MatchCondition
                    {
                        MatchConditionId = 5,
                        LeftFieldCode = "TX_DATETIME",
                        RightFieldCode = "POSTED_AT",
                        Comparison = ComparisonType.DateWithin,
                        ToleranceValue = 1,
                        ToleranceUnit = ToleranceUnit.Day,
                        Sequence = 2,
                    },
                    new MatchCondition
                    {
                        MatchConditionId = 6,
                        LeftFieldCode = "CRDTR_ACCT",
                        RightFieldCode = "ACCOUNT",
                        Comparison = ComparisonType.Exact,
                        Sequence = 3,
                    },
                ],
            },
        ],
    };

    private static DatasetField Field(
        int id, int datasetId, string code, string label,
        FieldDataType type, FieldRole? role, string slot,
        bool matchable = true, bool indexed = false, bool required = false,
        bool normalize = false, string? normalizedSlot = null) => new()
    {
        DatasetFieldId = id,
        DatasetId = datasetId,
        FieldCode = code,
        DisplayLabel = label,
        DataType = type,
        Role = role,
        StorageSlot = slot,
        IsMatchable = matchable,
        IsIndexed = indexed,
        IsRequired = required,
        NormalizeForMatch = normalize,
        NormalizedSlot = normalizedSlot,
    };
}
