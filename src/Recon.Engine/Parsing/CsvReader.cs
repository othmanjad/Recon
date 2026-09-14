namespace Recon.Engine.Parsing;

/// <summary>
/// A streaming RFC 4180 CSV reader.
///
/// <para>
/// Streaming is not a preference at this volume: a 2M-row session file must
/// never be materialised as a list of rows. The reader yields one row at a
/// time and the pipeline feeds it straight into <c>SqlBulkCopy</c>, so peak
/// memory is one row plus a buffer regardless of file size.
/// </para>
///
/// <para>
/// Hand-written rather than taken from a package because the behaviour that
/// matters here is narrow and specific: configurable delimiter and qualifier
/// from <c>cfg.FileFormatDefinition</c>, exact physical row numbers for
/// <c>stg.ParseError.RawRowNumber</c>, and a raw line to store with a rejected
/// row. A general-purpose library would be carried for the 10% of its surface
/// this needs.
/// </para>
/// </summary>
public sealed class CsvReader(CsvOptions options)
{
    private readonly CsvOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// Reads the file as rows of raw strings. Header handling, skipped lines
    /// and trailing lines all come from the format definition.
    /// </summary>
    public IEnumerable<CsvRow> Read(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var physicalLine = 0;
        string[]? header = null;

        // A trailing-line skip means the last N rows cannot be yielded until
        // the file ends, so they are held in a small ring.
        var pending = new Queue<CsvRow>();

        foreach (var (fields, lineNumber, rawLine) in ReadRecords(reader))
        {
            physicalLine = lineNumber;

            if (physicalLine <= _options.SkipLeadingLines)
            {
                continue;
            }

            if (_options.HasHeader && header is null)
            {
                header = fields;
                continue;
            }

            var row = new CsvRow(fields, header, lineNumber, rawLine);

            if (_options.SkipTrailingLines == 0)
            {
                yield return row;
                continue;
            }

            pending.Enqueue(row);
            if (pending.Count > _options.SkipTrailingLines)
            {
                yield return pending.Dequeue();
            }
        }

        // Whatever is still queued is inside the trailing window and dropped —
        // a JoPACC-style footer row, typically.
    }

    /// <summary>
    /// Splits the stream into records, honouring quoted fields that contain
    /// the delimiter, an escaped qualifier (<c>""</c>), or a newline.
    /// </summary>
    private IEnumerable<(string[] Fields, int LineNumber, string RawLine)> ReadRecords(TextReader reader)
    {
        var delimiter = _options.Delimiter;
        var qualifier = _options.TextQualifier;

        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
        var raw = new System.Text.StringBuilder();
        var inQuotes = false;
        var lineNumber = 1;
        var recordStartLine = 1;
        var sawAnything = false;

        int c;
        while ((c = reader.Read()) >= 0)
        {
            var ch = (char)c;
            raw.Append(ch);
            sawAnything = true;

            if (inQuotes)
            {
                if (qualifier is not null && ch == qualifier)
                {
                    // A doubled qualifier inside a quoted field is a literal one.
                    if (reader.Peek() == qualifier)
                    {
                        reader.Read();
                        raw.Append(qualifier.Value);
                        field.Append(qualifier.Value);
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    if (ch == '\n')
                    {
                        lineNumber++;
                    }

                    field.Append(ch);
                }

                continue;
            }

            if (qualifier is not null && ch == qualifier && field.Length == 0)
            {
                inQuotes = true;
                continue;
            }

            if (ch == delimiter)
            {
                fields.Add(field.ToString());
                field.Clear();
                continue;
            }

            if (ch is '\r')
            {
                continue;
            }

            if (ch is '\n')
            {
                fields.Add(field.ToString());
                field.Clear();

                yield return (fields.ToArray(),
                              recordStartLine,
                              raw.ToString().TrimEnd('\r', '\n'));

                fields.Clear();
                raw.Clear();
                lineNumber++;
                recordStartLine = lineNumber;
                sawAnything = false;
                continue;
            }

            field.Append(ch);
        }

        // A final record with no newline terminator.
        if (sawAnything && (field.Length > 0 || fields.Count > 0))
        {
            fields.Add(field.ToString());
            yield return (fields.ToArray(), recordStartLine, raw.ToString().TrimEnd('\r', '\n'));
        }
    }
}

public sealed record CsvOptions
{
    public char Delimiter { get; init; } = ',';

    /// <summary>Null disables quoting entirely, for formats that do not use it.</summary>
    public char? TextQualifier { get; init; } = '"';

    public bool HasHeader { get; init; } = true;
    public int SkipLeadingLines { get; init; }
    public int SkipTrailingLines { get; init; }
}

/// <summary>
/// One physical record. <see cref="LineNumber"/> is the line the record STARTED
/// on, which is what a parse error must report for a multi-line quoted field.
/// </summary>
public sealed class CsvRow(string[] fields, string[]? header, int lineNumber, string rawLine)
{
    private Dictionary<string, int>? _byName;

    public string[] Fields { get; } = fields;
    public string[]? Header { get; } = header;
    public int LineNumber { get; } = lineNumber;
    public string RawLine { get; } = rawLine;

    private Dictionary<string, int> ByName =>
        _byName ??= Header is null
            ? []
            : Header
                .Select((name, index) => (name: name.Trim(), index))
                .GroupBy(x => x.name, StringComparer.OrdinalIgnoreCase)
                // A duplicated header name takes the first column, which is
                // what a spreadsheet does; the alternative is rejecting files
                // that partners send successfully to everyone else.
                .ToDictionary(g => g.Key, g => g.First().index, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a field by <c>SourcePath</c>: a header name, or a 1-based ordinal
    /// when the format has no header.
    /// </summary>
    public bool TryGet(string sourcePath, out string? value)
    {
        value = null;

        if (Header is not null && ByName.TryGetValue(sourcePath.Trim(), out var index))
        {
            if (index >= Fields.Length)
            {
                return false;
            }

            value = Fields[index];
            return true;
        }

        if (int.TryParse(sourcePath, out var ordinal) && ordinal >= 1 && ordinal <= Fields.Length)
        {
            value = Fields[ordinal - 1];
            return true;
        }

        return false;
    }
}
