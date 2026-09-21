using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Recon.Engine.Parsing;

/// <summary>
/// The transform chain applied to a raw field value at parse time.
///
/// <para>
/// Stored as a JSON array in <c>cfg.FieldMapping.TransformChainJson</c>. Review
/// item B7: this was a pipe-delimited string (<c>"Trim|Upper|Substring"</c>),
/// which is fragile to parse and cannot carry arguments — a
/// <c>Substring</c> needs a start and a length, and there was nowhere to put
/// them.
/// </para>
/// </summary>
public sealed class TransformStep
{
    [JsonPropertyName("op")]
    public string Op { get; set; } = string.Empty;

    [JsonPropertyName("start")]
    public int? Start { get; set; }

    [JsonPropertyName("length")]
    public int? Length { get; set; }

    [JsonPropertyName("pattern")]
    public string? Pattern { get; set; }

    [JsonPropertyName("group")]
    public int? Group { get; set; }

    [JsonPropertyName("from")]
    public string? From { get; set; }

    [JsonPropertyName("to")]
    public string? To { get; set; }

    /// <summary>The cases of a <c>Map</c>, in order. First match wins.</summary>
    [JsonPropertyName("cases")]
    public List<MapCase>? Cases { get; set; }

    /// <summary>
    /// What a <c>Map</c> does with a value none of its cases names. Absent
    /// means "leave it alone", which is the safe default: a value nobody
    /// anticipated should arrive at staging as it was written, where a rule
    /// can see it, rather than be quietly turned into something else.
    /// </summary>
    [JsonPropertyName("otherwise")]
    public string? Otherwise { get; set; }

    /// <summary>
    /// Whether a <c>Map</c> matches regardless of case. Defaults to true:
    /// partners send TRUE, True and true for the same thing, and a map that
    /// caught one of the three would be a map that silently half-worked.
    /// </summary>
    [JsonPropertyName("ignoreCase")]
    public bool? IgnoreCase { get; set; }
}

/// <summary>One "this value means that value" line of a <c>Map</c>.</summary>
public sealed class MapCase
{
    [JsonPropertyName("from")]
    public string? From { get; set; }

    [JsonPropertyName("to")]
    public string? To { get; set; }
}

public static class Transforms
{
    /// <summary>
    /// A regex from configuration runs against 2M values per file, so a
    /// pathological pattern is a denial of service on the load. The timeout
    /// bounds it.
    /// </summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static IReadOnlyList<TransformStep> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        var steps = JsonSerializer.Deserialize<List<TransformStep>>(json, JsonOptions) ?? [];

        foreach (var step in steps)
        {
            Validate(step);
        }

        return steps;
    }

    /// <summary>
    /// Rejects a chain that cannot work, at save time rather than at 2 a.m. on
    /// the first production file.
    /// </summary>
    /// <summary>
    /// The operations a transform chain may name.
    ///
    /// <para>
    /// Public so that the portal's mapping editor offers exactly these rather
    /// than a hand-written list beside them — a help text naming
    /// <c>RegexReplace</c> when the parser knows <c>RegexExtract</c> sends an
    /// operator to a run that fails.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> Operations =
    [
        "Trim",
        "Upper",
        "Lower",
        "Normalize",
        "StripWhitespace",
        "StripNonAlphanumeric",
        "StripLeadingZeros",
        "Substring",
        "RegexExtract",
        "Replace",
        "Map",
    ];

    public static void Validate(TransformStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        switch (step.Op)
        {
            case "Trim" or "Upper" or "Lower" or "StripNonAlphanumeric" or "StripWhitespace"
                or "Normalize" or "StripLeadingZeros":
                break;

            case "Substring":
                if (step.Start is null || step.Start < 0)
                {
                    throw new TransformException("Substring needs a non-negative 'start'");
                }

                if (step.Length is not null && step.Length < 0)
                {
                    throw new TransformException("Substring 'length' cannot be negative");
                }

                break;

            case "RegexExtract":
                if (string.IsNullOrEmpty(step.Pattern))
                {
                    throw new TransformException("RegexExtract needs a 'pattern'");
                }

                try
                {
                    _ = new Regex(step.Pattern, RegexOptions.None, RegexTimeout);
                }
                catch (ArgumentException ex)
                {
                    throw new TransformException($"RegexExtract pattern is not valid: {ex.Message}");
                }

                break;

            case "Replace":
                if (step.From is null)
                {
                    throw new TransformException("Replace needs a 'from'");
                }

                break;

            /* Replace substitutes a SUBSTRING, which is the wrong tool for a
               vocabulary: mapping "true" to "Inward" with it also rewrites
               "not true" and anything else the token appears inside. Map
               compares the whole value, which is what "this code means that
               one" actually means. */
            case "Map":
                if (step.Cases is null || step.Cases.Count == 0)
                {
                    throw new TransformException("Map needs at least one case");
                }

                var comparer = step.IgnoreCase == false
                    ? StringComparer.Ordinal
                    : StringComparer.OrdinalIgnoreCase;

                var seen = new HashSet<string>(comparer);

                foreach (var one in step.Cases)
                {
                    if (one.From is null)
                    {
                        throw new TransformException("every Map case needs a 'from'");
                    }

                    // A repeated 'from' is a line that can never fire, and
                    // the one it shadows is rarely the one meant.
                    if (!seen.Add(one.From))
                    {
                        throw new TransformException(
                            $"Map names '{one.From}' twice; the second can never apply");
                    }
                }

                break;

            default:
                throw new TransformException($"unknown transform '{step.Op}'");
        }
    }

    public static string? Apply(string? value, IReadOnlyList<TransformStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        if (value is null)
        {
            return null;
        }

        foreach (var step in steps)
        {
            value = ApplyOne(value, step);
            if (value is null)
            {
                return null;
            }
        }

        return value;
    }

    private static string? ApplyOne(string value, TransformStep step) => step.Op switch
    {
        "Trim" => value.Trim(),
        "Upper" => value.ToUpperInvariant(),
        "Lower" => value.ToLowerInvariant(),
        "StripWhitespace" => new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray()),
        "StripNonAlphanumeric" => new string(value.Where(char.IsLetterOrDigit).ToArray()),
        "StripLeadingZeros" => value.TrimStart('0') is { Length: 0 } ? "0" : value.TrimStart('0'),
        "Normalize" => Normalize(value),
        "Substring" => Substring(value, step),
        "RegexExtract" => RegexExtract(value, step),
        "Replace" => value.Replace(step.From!, step.To ?? string.Empty, StringComparison.Ordinal),
        "Map" => Map(value, step),
        _ => throw new TransformException($"unknown transform '{step.Op}'"),
    };

    private static string Map(string value, TransformStep step)
    {
        var comparison = step.IgnoreCase == false
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        foreach (var one in step.Cases!)
        {
            if (string.Equals(value, one.From, comparison))
            {
                return one.To ?? string.Empty;
            }
        }

        // No case names this value: "otherwise" when the configuration says
        // what to do with a stranger, and otherwise the value itself, so that
        // an unmapped spelling reaches staging where a rule can find it.
        return step.Otherwise ?? value;
    }

    private static string Substring(string value, TransformStep step)
    {
        var start = step.Start!.Value;
        if (start >= value.Length)
        {
            return string.Empty;
        }

        var available = value.Length - start;
        var take = step.Length is null ? available : Math.Min(step.Length.Value, available);
        return value.Substring(start, take);
    }

    private static string? RegexExtract(string value, TransformStep step)
    {
        var match = Regex.Match(value, step.Pattern!, RegexOptions.None, RegexTimeout);
        if (!match.Success)
        {
            return null;
        }

        var group = step.Group ?? 0;
        return group < match.Groups.Count ? match.Groups[group].Value : null;
    }

    /// <summary>
    /// The canonical normalization written into a field's companion slot when
    /// <c>NormalizeForMatch</c> is set (review blocker A2).
    ///
    /// <para>
    /// This runs once per value at load. The alternative the design rejected
    /// was running <c>UPPER(TRIM(REPLACE(...)))</c> inside a join predicate,
    /// which is non-sargable and makes the optimizer scan both sides — so the
    /// same work would be done 2M times per pass instead of once per row, and
    /// the index would go unused.
    /// </para>
    ///
    /// <para>
    /// Uppercase, no whitespace, no punctuation. Aggressive on purpose: the
    /// companion exists to absorb formatting noise between two systems' spelling
    /// of the same reference, and anything it preserves is noise the primary
    /// pass has to cope with.
    /// </para>
    /// </summary>
    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var sb = new StringBuilder(value.Length);

        foreach (var ch in value.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToUpperInvariant(ch));
            }
        }

        return sb.ToString();
    }
}

public sealed class TransformException(string message) : Exception(message);
