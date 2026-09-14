namespace Recon.Engine.Parsing;

/// <summary>
/// The seam the design promised: "a new physical format = one new reader
/// class; nothing else in the system changes" (§5.1).
///
/// <para>
/// A reader turns a stream into records of raw strings, addressable by the
/// <c>SourcePath</c> in a field mapping. What that path MEANS is the reader's
/// business — a column name for CSV, an XPath for XML, a JsonPath for JSON —
/// and nothing downstream needs to know which it got.
/// </para>
/// </summary>
public interface IRecordReader
{
    /// <summary>
    /// Streams records. Every implementation must stream: a 2M-row file must
    /// never be materialised, which is the constraint that rules out the
    /// convenient DOM API in both XML and JSON.
    /// </summary>
    IEnumerable<CsvRow> Read(TextReader reader);
}

/// <summary>
/// Selects the reader for a format. The only place in the system that knows
/// the set of physical formats exists.
/// </summary>
public static class RecordReaderFactory
{
    public static IRecordReader For(FileFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);

        return format.Type switch
        {
            Domain.Configuration.FileFormatType.Csv => new CsvRecordReader(format.Csv),
            Domain.Configuration.FileFormatType.Xml => new XmlRecordReader(format.RecordPath),
            Domain.Configuration.FileFormatType.Json => new JsonRecordReader(format.RecordPath),
            Domain.Configuration.FileFormatType.FixedWidth =>
                throw new NotSupportedException(
                    "FixedWidth is in the schema's format list but has no reader yet. " +
                    "It needs a column layout the field mapping does not currently carry, " +
                    "so it waits on a real fixed-width file rather than a guess at one."),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format.Type, null),
        };
    }
}

/// <summary>Adapts the existing CSV reader to the seam.</summary>
public sealed class CsvRecordReader(CsvOptions options) : IRecordReader
{
    private readonly CsvReader _reader = new(options);

    public IEnumerable<CsvRow> Read(TextReader reader) => _reader.Read(reader);
}
