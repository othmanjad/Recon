using System.Text.Json;
using Recon.Domain.Configuration;

namespace Recon.Domain.Conditions;

/// <summary>
/// Validates a condition tree against a dataset's field registry.
///
/// <para>
/// <b>This is the security boundary, and it is the server-side one.</b> The
/// portal runs an identical check in JavaScript so Operations gets an
/// immediate error, but that copy is a courtesy: a client-side validator can
/// be bypassed by anyone who can reach the API, so nothing may reach the SQL
/// compiler without passing through here.
/// </para>
///
/// <para>
/// Two rejections matter most: a field code that is not in the registry (a
/// typo, or an injection attempt), and a field that is in the registry but not
/// marked matchable (a field deliberately withheld from the rule builder).
/// </para>
/// </summary>
public static class ConditionValidator
{
    /// <summary>
    /// A tree deeper than this is a sign the user is building something the
    /// rule engine should not be asked to compile, and it bounds the
    /// recursion.
    /// </summary>
    public const int MaxDepth = 8;

    /// <summary>Guards against a payload designed to exhaust the compiler.</summary>
    public const int MaxLeaves = 200;

    public static ValidationResult Validate(ConditionNode? root, Dataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        var errors = new List<string>();

        if (root is null)
        {
            // An absent filter is valid: it means "no filter".
            return new ValidationResult(errors);
        }

        var leaves = 0;
        Walk(root, "$", 1, dataset, errors, ref leaves);

        if (leaves > MaxLeaves)
        {
            errors.Add($"$: {leaves} conditions exceeds the maximum of {MaxLeaves}");
        }

        return new ValidationResult(errors);
    }

    /// <summary>Parses and validates in one step, reporting malformed JSON as an error rather than throwing.</summary>
    public static ValidationResult ValidateJson(string? json, Dataset dataset)
    {
        ConditionNode? root;
        try
        {
            root = ConditionNode.Parse(json);
        }
        catch (JsonException ex)
        {
            return new ValidationResult([$"$: not valid JSON — {ex.Message}"]);
        }

        return Validate(root, dataset);
    }

    private static void Walk(
        ConditionNode node,
        string path,
        int depth,
        Dataset dataset,
        List<string> errors,
        ref int leaves)
    {
        if (depth > MaxDepth)
        {
            errors.Add($"{path}: nested deeper than {MaxDepth} levels");
            return;
        }

        if (node.IsGroup && node.IsLeaf)
        {
            errors.Add($"{path}: a node is either a group (op/items) or a condition (field/cmp), not both");
            return;
        }

        if (node.IsGroup)
        {
            if (node.Op is not ("and" or "or"))
            {
                errors.Add($"{path}.op: must be \"and\" or \"or\", got \"{node.Op}\"");
            }

            if (node.Items is null || node.Items.Count == 0)
            {
                errors.Add($"{path}.items: must be a non-empty array");
                return;
            }

            for (var i = 0; i < node.Items.Count; i++)
            {
                Walk(node.Items[i], $"{path}.items[{i}]", depth + 1, dataset, errors, ref leaves);
            }

            return;
        }

        if (!node.IsLeaf)
        {
            errors.Add($"{path}: neither a group (op/items) nor a condition (field/cmp)");
            return;
        }

        leaves++;

        // ---- the security-relevant check ----
        if (string.IsNullOrEmpty(node.Field))
        {
            errors.Add($"{path}.field: must be a non-empty field code");
        }
        else if (!dataset.TryGetField(node.Field, out var field))
        {
            errors.Add($"{path}.field: \"{node.Field}\" is not in the field registry of dataset {dataset.Code}");
        }
        else if (!field.IsMatchable)
        {
            errors.Add($"{path}.field: \"{node.Field}\" is not marked matchable and cannot appear in a condition");
        }

        if (!ConditionOperators.TryParse(node.Cmp, out var op))
        {
            errors.Add($"{path}.cmp: unknown comparator \"{node.Cmp}\"");
            return;
        }

        var hasValue = node.Value is { } v && v.ValueKind != JsonValueKind.Null;
        var isArray = node.Value is { ValueKind: JsonValueKind.Array };

        switch (op.Arity())
        {
            case ValueArity.None when hasValue:
                errors.Add($"{path}.value: {node.Cmp} takes no value");
                break;

            case ValueArity.One when !hasValue:
                errors.Add($"{path}.value: {node.Cmp} requires a value");
                break;

            case ValueArity.One when isArray:
                errors.Add($"{path}.value: {node.Cmp} takes a single value, not a list");
                break;

            case ValueArity.List when !isArray || node.Value!.Value.GetArrayLength() == 0:
                errors.Add($"{path}.value: in requires a non-empty list");
                break;

            default:
                break;
        }
    }
}

public sealed record ValidationResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public void ThrowIfInvalid(string what)
    {
        if (!IsValid)
        {
            throw new ConditionValidationException(what, Errors);
        }
    }
}

public sealed class ConditionValidationException(string what, IReadOnlyList<string> errors)
    : Exception($"{what} is not a valid condition tree: {string.Join("; ", errors)}")
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
