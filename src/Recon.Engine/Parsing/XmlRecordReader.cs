using System.Xml;

namespace Recon.Engine.Parsing;

/// <summary>
/// Streams records from XML.
///
/// <para>
/// Uses <see cref="XmlReader"/> — forward-only, one element at a time — rather
/// than <c>XDocument</c> or <c>XmlDocument</c>, both of which load the whole
/// file. A pacs.008 batch for a full session would not fit in memory as a DOM,
/// and the whole point of the reader seam is that a new format inherits the
/// same streaming guarantee as CSV.
/// </para>
///
/// <para>
/// <c>RecordPath</c> from the format definition names the repeating element —
/// <c>Document/FIToFICstmrCdtTrf/CdtTrfTxInf</c> for a pacs.008, or just
/// <c>Transaction</c> for a partner's own shape. Each occurrence becomes one
/// record, and a field's <c>SourcePath</c> addresses a descendant by its path
/// relative to that element, or an attribute with a leading <c>@</c>.
/// </para>
/// </summary>
public sealed class XmlRecordReader(string? recordPath) : IRecordReader
{
    private readonly string[] _recordPath = Split(recordPath);

    public IEnumerable<CsvRow> Read(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        if (_recordPath.Length == 0)
        {
            throw new InvalidOperationException(
                "an XML format needs a RecordPath naming the repeating element; " +
                "without it the reader cannot tell where one record ends and the next begins");
        }

        var settings = new XmlReaderSettings
        {
            IgnoreComments = true,
            IgnoreWhitespace = true,
            IgnoreProcessingInstructions = true,
            // Both off deliberately: a DTD or an external entity in a partner
            // file is an XXE vector, and a reconciliation file has no
            // legitimate need for either.
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            CloseInput = false,
        };

        using var xml = XmlReader.Create(reader, settings);

        var stack = new List<string>();
        var recordNumber = 0;
        var lineInfo = xml as IXmlLineInfo;

        while (xml.Read())
        {
            if (xml.NodeType == XmlNodeType.Element)
            {
                // Namespaces are ignored: partners version them and a
                // configuration that had to track the exact namespace URI
                // would break on every scheme upgrade.
                stack.Add(xml.LocalName);

                if (Matches(stack))
                {
                    recordNumber++;
                    var line = lineInfo?.HasLineInfo() == true ? lineInfo.LineNumber : recordNumber;

                    using var element = xml.ReadSubtree();
                    element.Read();

                    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    ReadElement(element, prefix: string.Empty, values);

                    yield return ToRow(values, line);

                    // ReadSubtree leaves the outer reader on the end element,
                    // so the path pops here rather than on the next EndElement.
                    stack.RemoveAt(stack.Count - 1);
                    continue;
                }

                if (xml.IsEmptyElement)
                {
                    stack.RemoveAt(stack.Count - 1);
                }
            }
            else if (xml.NodeType == XmlNodeType.EndElement && stack.Count > 0)
            {
                stack.RemoveAt(stack.Count - 1);
            }
        }
    }

    /// <summary>
    /// Flattens one record element into path → value. A repeated child gets a
    /// 1-based index, so <c>Amt</c> and <c>Amt[2]</c> are addressable
    /// separately rather than one silently overwriting the other.
    /// </summary>
    private static void ReadElement(
        XmlReader reader, string prefix, Dictionary<string, string> values)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Attributes of the record element itself.
        if (reader.HasAttributes)
        {
            while (reader.MoveToNextAttribute())
            {
                values[Join(prefix, "@" + reader.LocalName)] = reader.Value;
            }

            reader.MoveToElement();
        }

        if (reader.IsEmptyElement)
        {
            return;
        }

        var depth = reader.Depth;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth <= depth)
            {
                return;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            var name = reader.LocalName;
            counts.TryGetValue(name, out var seen);
            counts[name] = seen + 1;

            var path = Join(prefix, seen == 0 ? name : $"{name}[{seen + 1}]");

            if (reader.HasAttributes)
            {
                var element = reader;
                while (element.MoveToNextAttribute())
                {
                    values[Join(path, "@" + element.LocalName)] = element.Value;
                }

                element.MoveToElement();
            }

            if (reader.IsEmptyElement)
            {
                values[path] = string.Empty;
                continue;
            }

            using var child = reader.ReadSubtree();
            child.Read();

            // A leaf is its text; a branch recurses. ReadElementContentAsString
            // would throw on a branch, so the shape decides.
            if (child.IsEmptyElement)
            {
                values[path] = string.Empty;
                continue;
            }

            var inner = child.ReadInnerXml();

            if (inner.Contains('<', StringComparison.Ordinal))
            {
                using var branch = XmlReader.Create(
                    new StringReader($"<r>{inner}</r>"),
                    new XmlReaderSettings
                    {
                        DtdProcessing = DtdProcessing.Prohibit,
                        XmlResolver = null,
                        IgnoreWhitespace = true,
                    });

                branch.Read();
                ReadElement(branch, path, values);
            }
            else
            {
                values[path] = inner;
            }
        }
    }

    private static CsvRow ToRow(Dictionary<string, string> values, int line)
    {
        var headers = values.Keys.ToArray();
        var fields = headers.Select(h => values[h]).ToArray();

        // Reusing CsvRow keeps the parser and every mapping unchanged: a
        // SourcePath is looked up by name whether that name came from a CSV
        // header or an XML path.
        return new CsvRow(fields, headers, line, string.Join(", ", headers.Zip(fields,
            (h, v) => $"{h}={v}")));
    }

    private bool Matches(List<string> stack)
    {
        if (stack.Count < _recordPath.Length)
        {
            return false;
        }

        // Matched from the end, so a RecordPath may be a suffix of the real
        // path — "CdtTrfTxInf" finds the element without naming every
        // ancestor.
        for (var i = 0; i < _recordPath.Length; i++)
        {
            var expected = _recordPath[^(i + 1)];
            var actual = stack[^(i + 1)];

            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static string[] Split(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? []
            : path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static string Join(string prefix, string name) =>
        prefix.Length == 0 ? name : prefix + "/" + name;
}
