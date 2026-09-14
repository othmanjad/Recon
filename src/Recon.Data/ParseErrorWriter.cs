using Microsoft.Data.SqlClient;

namespace Recon.Data;

/// <summary>
/// Records rejected rows in <c>stg.ParseError</c>.
///
/// <para>
/// A count of rejections is not enough. An operator handed "1 rejected" cannot
/// act on it; the row's line number, the field that failed, the reason and the
/// raw line are what make a partner file fixable — and the schema has columns
/// for all four. Without this the count was reported to the console and then
/// lost.
/// </para>
///
/// <para>
/// Capped by <c>cfg.FileFormatDefinition.MaxParseErrors</c> upstream (E4): a
/// wrong-format file would otherwise write one row per source row, each
/// carrying its own raw line.
/// </para>
/// </summary>
public sealed class ParseErrorWriter(SqlConnection connection)
{
    private readonly SqlConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    public async Task<int> WriteAsync(
        long runId,
        DateOnly businessDate,
        long? sourceFileId,
        IReadOnlyList<ParseErrorRow> errors,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(errors);

        if (errors.Count == 0)
        {
            return 0;
        }

        var written = 0;

        // One statement per row is acceptable here precisely because the count
        // is bounded: past MaxParseErrors the file is abandoned, so this never
        // becomes a 2M-row insert loop.
        foreach (var error in errors)
        {
            await Db.ExecuteAsync(
                _connection,
                """
                INSERT stg.ParseError
                    (RunId, SourceFileId, BusinessDate, RawRowNumber,
                     ErrorType, FieldCode, ErrorMessage, RawLine)
                VALUES (@run, @file, @date, @line, @type, @field, @message, @raw);
                """,
                c => c.With("@run", runId)
                      .With("@file", sourceFileId)
                      .With("@date", businessDate.ToDateTime(TimeOnly.MinValue))
                      .With("@line", error.RawRowNumber)
                      .With("@type", error.ErrorType)
                      .With("@field", error.FieldCode)
                      .With("@message", Truncate(error.Message, 1000))
                      .With("@raw", error.RawLine),
                cancellationToken).ConfigureAwait(false);

            written++;
        }

        return written;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}

public sealed record ParseErrorRow(
    string ErrorType,
    string? FieldCode,
    string Message,
    int RawRowNumber,
    string RawLine);
