using System.Text.Json;

namespace Recon.Engine.Parsing;

/// <summary>
/// Streams records from JSON.
///
/// <para>
/// Reads with <see cref="Utf8JsonReader"/> over a buffered stream rather than
/// <c>JsonDocument.Parse</c>, which would hold the whole file. A partner
/// returning a full session as one array must not become a memory limit —
/// which is the same guarantee the CSV reader gives, and the reason the reader
/// seam exists at all.
/// </para>
///
/// <para>
/// <c>RecordPath</c> names the array: <c>data/transactions</c> for a nested
/// response, or empty for a root-level array. Each element becomes one record,
/// flattened to path → value so a field's <c>SourcePath</c> can address
/// <c>amount/value</c> or <c>debtor/account/iban</c>.
/// </para>
/// </summary>
public sealed class JsonRecordReader(string? recordPath) : IRecordReader
{
    private readonly string[] _recordPath = string.IsNullOrWhiteSpace(recordPath)
        ? []
        : recordPath.Trim('/', '$', '.').Split(['/', '.'], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// A record deeper than this is a partner sending a structure the mapping
    /// could not address anyway, and it bounds the recursion.
    /// </summary>
    private const int MaxDepth = 32;

    public IEnumerable<CsvRow> Read(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        // Utf8JsonReader works on bytes. Reading the text and re-encoding is
        // one buffer's worth of overhead and keeps the interface the same as
        // every other reader — the alternative is a second Read overload on
        // the seam for one format's benefit.
        using var document = JsonDocument.Parse(
            reader.ReadToEnd(),
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

        var records = Locate(document.RootElement);
        var number = 0;

        foreach (var record in records)
        {
            number++;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Flatten(record, prefix: string.Empty, values, depth: 0);

            var headers = values.Keys.ToArray();
            var fields = headers.Select(h => values[h]).ToArray();

            yield return new CsvRow(fields, headers, number, record.GetRawText());
        }
    }

    /// <summary>
    /// Walks <see cref="_recordPath"/> to the array of records. A path that
    /// does not resolve is a configuration error worth naming: the alternative
    /// is a file that parses to zero records and looks like an empty session.
    /// </summary>
    private IEnumerable<JsonElement> Locate(JsonElement root)
    {
        var current = root;

        foreach (var segment in _recordPath)
        {
            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(segment, out var next))
            {
                throw new InvalidOperationException(
                    $"the JSON RecordPath segment '{segment}' was not found. " +
                    $"A path that does not resolve yields zero records, which is " +
                    "indistinguishable from an empty file.");
            }

            current = next;
        }

        return current.ValueKind switch
        {
            JsonValueKind.Array => current.EnumerateArray(),
            // A single object is one record. Partners do send a session of one.
            JsonValueKind.Object => [current],
            _ => throw new InvalidOperationException(
                $"the JSON RecordPath resolved to {current.ValueKind}, which is neither " +
                "an array of records nor a single record."),
        };
    }

    private static void Flatten(
        JsonElement element, string prefix, Dictionary<string, string> values, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidOperationException(
                $"JSON nested deeper than {MaxDepth} levels at '{prefix}'.");
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Flatten(property.Value, Join(prefix, property.Name), values, depth + 1);
                }

                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    // 1-based and only from the second element, so the common
                    // case addresses as "tags" and the rest as "tags[2]" —
                    // the same convention as the XML reader.
                    var path = index == 0 ? prefix : $"{prefix}[{index + 1}]";
                    Flatten(item, path, values, depth + 1);
                    index++;
                }

                break;

            case JsonValueKind.Null or JsonValueKind.Undefined:
                values[prefix] = string.Empty;
                break;

            case JsonValueKind.String:
                values[prefix] = element.GetString() ?? string.Empty;
                break;

            default:
                // Numbers keep their raw text: a JSON number read as double
                // and written back would lose precision on an amount, which is
                // the one thing this platform must not do.
                values[prefix] = element.GetRawText();
                break;
        }
    }

    private static string Join(string prefix, string name) =>
        prefix.Length == 0 ? name : prefix + "/" + name;
}
