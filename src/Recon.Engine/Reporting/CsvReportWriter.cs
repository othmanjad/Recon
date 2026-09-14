using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using Recon.Domain.Money;

namespace Recon.Engine.Reporting;

/// <summary>
/// Streams a result set to CSV.
///
/// <para>
/// The default for transaction-level scopes (review item E1). A worksheet
/// holds 1,048,576 rows and a full day is 2,000,000, so CSV is not a fallback
/// here — it is the format that fits, and it streams in constant memory.
/// </para>
/// </summary>
public sealed class CsvReportWriter(CsvReportOptions? options = null)
{
    private readonly CsvReportOptions _options = options ?? new CsvReportOptions();

    public async Task<ReportResult> WriteAsync(
        SqlDataReader reader,
        Stream destination,
        IReadOnlyList<ReportColumn>? columns = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(destination);

        // A BOM so Excel opens a UTF-8 CSV with Arabic text correctly rather
        // than as mojibake — which matters here, since counterparty and
        // account names are Arabic.
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: _options.WriteByteOrderMark);

        await using var writer = new StreamWriter(destination, encoding, bufferSize: 64 * 1024,
            leaveOpen: true);

        var amountColumns = AmountScales(reader, columns);
        var rows = 0L;

        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (i > 0) { await writer.WriteAsync(_options.Delimiter).ConfigureAwait(false); }
            await writer.WriteAsync(Escape(reader.GetName(i))).ConfigureAwait(false);
        }

        await writer.WriteLineAsync().ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (i > 0) { await writer.WriteAsync(_options.Delimiter).ConfigureAwait(false); }
                await writer.WriteAsync(Escape(Value(reader, i, amountColumns))).ConfigureAwait(false);
            }

            await writer.WriteLineAsync().ConfigureAwait(false);
            rows++;

            if (rows % 50_000 == 0)
            {
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

        return new ReportResult(rows, 1, "text/csv");
    }

    /// <summary>
    /// Which columns hold integer minor units, and at what scale. An amount
    /// must be written as its decimal so a spreadsheet can sum it; writing
    /// 125500 where 125.500 was meant is a figure somebody will act on.
    /// </summary>
    internal static Dictionary<int, int> AmountScales(
        SqlDataReader reader, IReadOnlyList<ReportColumn>? columns)
    {
        var scales = new Dictionary<int, int>();

        if (columns is null)
        {
            return scales;
        }

        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            var column = columns.FirstOrDefault(c =>
                string.Equals(c.Header, name, StringComparison.Ordinal));

            if (column?.IsAmount == true)
            {
                // The currency's scale would be better, but a result set can
                // mix currencies per row. JOD's 3 is the platform default and
                // the header carries the currency column beside it.
                scales[i] = 3;
            }
        }

        return scales;
    }

    internal static string Value(SqlDataReader reader, int index, Dictionary<int, int> amountScales)
    {
        if (reader.IsDBNull(index))
        {
            return string.Empty;
        }

        if (amountScales.TryGetValue(index, out var scale))
        {
            var raw = reader.GetValue(index);
            if (raw is long minor)
            {
                return MinorUnits.ToDecimal(minor, scale)
                    .ToString("F" + scale, CultureInfo.InvariantCulture);
            }
        }

        return reader.GetValue(index) switch
        {
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            decimal dec => dec.ToString(CultureInfo.InvariantCulture),
            bool b => b ? "1" : "0",
            var other => other.ToString() ?? string.Empty,
        };
    }

    private string Escape(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        var needsQuotes = value.Contains(_options.Delimiter, StringComparison.Ordinal)
            || value.Contains('"', StringComparison.Ordinal)
            || value.Contains('\n', StringComparison.Ordinal)
            || value.Contains('\r', StringComparison.Ordinal);

        return needsQuotes
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }
}

public sealed record CsvReportOptions
{
    public char Delimiter { get; init; } = ',';

    /// <summary>On by default so Excel reads Arabic text rather than mojibake.</summary>
    public bool WriteByteOrderMark { get; init; } = true;
}

public sealed record ReportResult(long Rows, int Sheets, string ContentType);
