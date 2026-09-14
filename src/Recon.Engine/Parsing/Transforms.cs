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
        _ => throw new TransformException($"unknown transform '{step.Op}'"),
    };

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
