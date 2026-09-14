using System.Text.Json;
using System.Text.Json.Serialization;
using Recon.Domain.Configuration;

namespace Recon.Domain.Conditions;

/// <summary>
/// The ONE condition-tree schema, used by match-rule filters, classification
/// rules, exclusion rules, SQL source filters and report filters.
///
/// <para>
/// Review blocker D1: <c>cfg.SqlSourceDefinition.FilterExpression</c> was free
/// SQL text — an injection surface in the exact place the design promised none.
/// Review item B5: the replacements were described as "structured JSON" with no
/// schema defined, so each consumer would have invented its own. This type is
/// that schema, and <see cref="Recon.Domain.Conditions.ConditionValidator"/> is
/// the single gate every filter passes through.
/// </para>
///
/// <code>
/// { "op": "and" | "or",
///   "items": [ { "field": "&lt;FieldCode&gt;",
///                "cmp": "eq|ne|gt|gte|lt|lte|in|isnull|isnotnull",
///                "value": scalar | array },
///              { "op": "or", "items": [ ... ] } ] }
/// </code>
/// </summary>
public sealed class ConditionNode
{
    /// <summary>Set on a GROUP node. Mutually exclusive with <see cref="Field"/>.</summary>
    [JsonPropertyName("op")]
    public string? Op { get; set; }

    [JsonPropertyName("items")]
    public List<ConditionNode>? Items { get; set; }

    /// <summary>Set on a LEAF node: the field code, resolved against the registry.</summary>
    [JsonPropertyName("field")]
    public string? Field { get; set; }

    [JsonPropertyName("cmp")]
    public string? Cmp { get; set; }

    /// <summary>
    /// Left as <see cref="JsonElement"/> deliberately. The compiler
    /// parameterizes it; nothing ever concatenates it into SQL, so it does not
    /// need to be a CLR type until the parameter is bound.
    /// </summary>
    [JsonPropertyName("value")]
    public JsonElement? Value { get; set; }

    [JsonIgnore]
    public bool IsGroup => Op is not null;

    [JsonIgnore]
    public bool IsLeaf => Field is not null;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static ConditionNode? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonSerializer.Deserialize<ConditionNode>(json, JsonOptions);
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}

/// <summary>
/// The comparison vocabulary of a condition tree. Distinct from
/// <see cref="ComparisonType"/>, which compares a field on the LEFT dataset to
/// a field on the RIGHT: these compare one field to a literal, within one
/// dataset.
/// </summary>
public enum ConditionOperator
{
    Eq,
    Ne,
    Gt,
    Gte,
    Lt,
    Lte,
    In,
    IsNull,
    IsNotNull,
}

public static class ConditionOperators
{
    private static readonly Dictionary<string, ConditionOperator> Map = new(StringComparer.Ordinal)
    {
        ["eq"] = ConditionOperator.Eq,
        ["ne"] = ConditionOperator.Ne,
        ["gt"] = ConditionOperator.Gt,
        ["gte"] = ConditionOperator.Gte,
        ["lt"] = ConditionOperator.Lt,
        ["lte"] = ConditionOperator.Lte,
        ["in"] = ConditionOperator.In,
        ["isnull"] = ConditionOperator.IsNull,
        ["isnotnull"] = ConditionOperator.IsNotNull,
    };

    public static bool TryParse(string? token, out ConditionOperator op)
    {
        op = default;
        return token is not null && Map.TryGetValue(token, out op);
    }

    public static IReadOnlyCollection<string> All => Map.Keys;

    /// <summary>How many values the operator takes: 0, 1, or a list.</summary>
    public static ValueArity Arity(this ConditionOperator op) => op switch
    {
        ConditionOperator.IsNull or ConditionOperator.IsNotNull => ValueArity.None,
        ConditionOperator.In => ValueArity.List,
        _ => ValueArity.One,
    };

    public static string SqlOperator(this ConditionOperator op) => op switch
    {
        ConditionOperator.Eq => "=",
        ConditionOperator.Ne => "<>",
        ConditionOperator.Gt => ">",
        ConditionOperator.Gte => ">=",
        ConditionOperator.Lt => "<",
        ConditionOperator.Lte => "<=",
        _ => throw new InvalidOperationException(
            $"{op} does not map to a binary SQL operator; it is handled structurally."),
    };
}

public enum ValueArity { None, One, List }
