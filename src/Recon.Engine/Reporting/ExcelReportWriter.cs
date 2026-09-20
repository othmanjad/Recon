using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Data.SqlClient;
using Recon.Domain.Money;

namespace Recon.Engine.Reporting;

/// <summary>
/// Streams a result set to .xlsx, splitting sheets at the row limit.
///
/// <para>
/// Review item E1, and the reason it was a HIGH finding: a worksheet holds
/// 1,048,576 rows, a full day is 2,000,000, and "developers discover this in
/// Phase 4 at 3 a.m." This writer splits automatically at
/// <c>cfg.PlatformSetting.ExcelMaxRowsPerSheet</c> and names the parts
/// <c>Sheet</c>, <c>Sheet (2)</c>, and so on.
/// </para>
///
/// <para>
/// It uses <see cref="OpenXmlWriter"/> — the SAX-style writer — rather than
/// the DOM. ClosedXML and EPPlus build the whole workbook in memory and would
/// exhaust it long before 2M rows; so would OpenXML's own DOM API. Cells are
/// written as they are read, and memory stays flat.
/// </para>
/// </summary>
public sealed class ExcelReportWriter(ExcelReportOptions? options = null)
{
    private readonly ExcelReportOptions _options = options ?? new ExcelReportOptions();

    public async Task<ReportResult> WriteAsync(
        SqlDataReader reader,
        Stream destination,
        string sheetName,
        IReadOnlyList<ReportColumn>? columns = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(destination);

        // An .xlsx is a ZIP package, and the package writer reads back and
        // seeks in the stream it is building — it does not simply append. An
        // HTTP response body does neither, which is why writing a workbook
        // straight to it failed with "The stream was not opened for reading"
        // every time anyone downloaded one.
        if (destination.CanSeek && destination.CanRead)
        {
            return await WriteWorkbookAsync(
                reader, destination, sheetName, columns, cancellationToken).ConfigureAwait(false);
        }

        // A temporary FILE rather than a MemoryStream: this writer exists to
        // keep memory flat at two million rows, and buffering the workbook in
        // RAM to satisfy the package would give that away at exactly the size
        // where it matters. DeleteOnClose means it goes even if this throws.
        var scratch = Path.Combine(
            Path.GetTempPath(), $"recon-report-{Guid.NewGuid():N}.xlsx");

        var file = new FileStream(
            scratch, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 64 * 1024, FileOptions.DeleteOnClose | FileOptions.Asynchronous);

        await using (file.ConfigureAwait(false))
        {
            var result = await WriteWorkbookAsync(
                reader, file, sheetName, columns, cancellationToken).ConfigureAwait(false);

            file.Position = 0;
            await file.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);

            return result;
        }
    }

    private async Task<ReportResult> WriteWorkbookAsync(
        SqlDataReader reader,
        Stream destination,
        string sheetName,
        IReadOnlyList<ReportColumn>? columns,
        CancellationToken cancellationToken)
    {
        var amountScales = CsvReportWriter.AmountScales(reader, columns);
        var headers = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();

        using var document = SpreadsheetDocument.Create(destination, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());

        // Two number formats: one for money at 3 decimals with a thousands
        // separator, one for dates. Without them every amount opens as a
        // raw number and every date as a serial.
        AddStyles(workbookPart);

        var totalRows = 0L;
        var sheetIndex = 0;
        var more = true;

        while (more)
        {
            sheetIndex++;
            var partName = sheetIndex == 1 ? sheetName : $"{sheetName} ({sheetIndex})";
            var part = workbookPart.AddNewPart<WorksheetPart>();

            var (written, hasMore) = await WriteSheetAsync(
                part, reader, headers, amountScales, cancellationToken).ConfigureAwait(false);

            sheets.AppendChild(new Sheet
            {
                Id = workbookPart.GetIdOfPart(part),
                SheetId = (uint)sheetIndex,
                Name = Truncate(partName, 31),
            });

            totalRows += written;
            more = hasMore;

            // A sheet that received nothing is still appended when it is the
            // first: an empty report is a valid answer and an empty workbook
            // is not a readable one.
            if (written == 0 && sheetIndex > 1)
            {
                break;
            }
        }

        workbookPart.Workbook.Save();

        return new ReportResult(totalRows, sheetIndex,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
    }

    private async Task<(long Written, bool HasMore)> WriteSheetAsync(
        WorksheetPart part,
        SqlDataReader reader,
        IReadOnlyList<string> headers,
        Dictionary<int, int> amountScales,
        CancellationToken cancellationToken)
    {
        using var writer = OpenXmlWriter.Create(part);
        writer.WriteStartElement(new Worksheet());
        writer.WriteStartElement(new SheetData());

        // Header row, styled bold.
        writer.WriteStartElement(new Row());
        foreach (var header in headers)
        {
            WriteInlineString(writer, header, styleIndex: 1);
        }

        writer.WriteEndElement();

        long written = 0;
        var hasMore = false;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            writer.WriteStartElement(new Row());

            for (var i = 0; i < reader.FieldCount; i++)
            {
                WriteCell(writer, reader, i, amountScales);
            }

            writer.WriteEndElement();
            written++;

            if (written >= _options.MaxRowsPerSheet)
            {
                // The reader is left positioned for the next sheet, so the
                // split costs nothing: no buffering and no second query.
                hasMore = true;
                break;
            }
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.Close();

        return (written, hasMore);
    }

    private static void WriteCell(
        OpenXmlWriter writer, SqlDataReader reader, int index, Dictionary<int, int> amountScales)
    {
        if (reader.IsDBNull(index))
        {
            writer.WriteElement(new Cell());
            return;
        }

        if (amountScales.TryGetValue(index, out var scale) && reader.GetValue(index) is long minor)
        {
            // Written as a NUMBER, not text, so the column sums in Excel —
            // which is the first thing anyone does with a reconciliation
            // report.
            var value = MinorUnits.ToDecimal(minor, scale);
            writer.WriteElement(new Cell
            {
                DataType = CellValues.Number,
                StyleIndex = 2,
                CellValue = new CellValue(value.ToString(CultureInfo.InvariantCulture)),
            });
            return;
        }

        switch (reader.GetValue(index))
        {
            case DateTime dt:
                writer.WriteElement(new Cell
                {
                    DataType = CellValues.Number,
                    StyleIndex = 3,
                    CellValue = new CellValue(dt.ToOADate().ToString(CultureInfo.InvariantCulture)),
                });
                break;

            case decimal dec:
                writer.WriteElement(new Cell
                {
                    DataType = CellValues.Number,
                    StyleIndex = 2,
                    CellValue = new CellValue(dec.ToString(CultureInfo.InvariantCulture)),
                });
                break;

            case long l:
                writer.WriteElement(new Cell
                {
                    DataType = CellValues.Number,
                    CellValue = new CellValue(l.ToString(CultureInfo.InvariantCulture)),
                });
                break;

            case int n:
                writer.WriteElement(new Cell
                {
                    DataType = CellValues.Number,
                    CellValue = new CellValue(n.ToString(CultureInfo.InvariantCulture)),
                });
                break;

            case bool b:
                WriteInlineString(writer, b ? "Yes" : "No");
                break;

            case var other:
                WriteInlineString(writer, other.ToString() ?? string.Empty);
                break;
        }
    }

    /// <summary>
    /// Inline strings rather than the shared-string table. The shared table
    /// would be smaller for repetitive data, but it must be held in memory
    /// until the workbook closes — which is the thing this writer exists to
    /// avoid.
    /// </summary>
    private static void WriteInlineString(OpenXmlWriter writer, string value, uint styleIndex = 0)
    {
        var cell = new Cell { DataType = CellValues.InlineString };

        if (styleIndex != 0)
        {
            cell.StyleIndex = styleIndex;
        }

        writer.WriteStartElement(cell);
        writer.WriteElement(new InlineString(new Text(Sanitize(value))));
        writer.WriteEndElement();
    }

    /// <summary>
    /// Control characters are not legal in XML, and partner data contains
    /// them. Stripping them here is the difference between a report and a
    /// corrupt file that Excel refuses to open.
    /// </summary>
    private static string Sanitize(string value)
    {
        if (!value.Any(c => char.IsControl(c) && c is not ('\t' or '\n' or '\r')))
        {
            return value;
        }

        return new string(value
            .Where(c => !char.IsControl(c) || c is '\t' or '\n' or '\r')
            .ToArray());
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static void AddStyles(WorkbookPart workbookPart)
    {
        var part = workbookPart.AddNewPart<WorkbookStylesPart>();

        part.Stylesheet = new Stylesheet(
            new Fonts(
                new Font(),
                new Font(new Bold())),
            new Fills(new Fill(new PatternFill { PatternType = PatternValues.None })),
            new Borders(new Border()),
            new CellFormats(
                // 0: default
                new CellFormat(),
                // 1: header
                new CellFormat { FontId = 1, ApplyFont = true },
                // 2: money, 3 decimals with a thousands separator
                new CellFormat { NumberFormatId = 4U, ApplyNumberFormat = true },
                // 3: date and time
                new CellFormat { NumberFormatId = 22U, ApplyNumberFormat = true }));

        part.Stylesheet.Save();
    }
}

public sealed record ExcelReportOptions
{
    /// <summary>
    /// Mirrors <c>cfg.PlatformSetting.ExcelMaxRowsPerSheet</c>. The hard limit
    /// is 1,048,576 including the header; the default leaves room rather than
    /// discovering the boundary in production.
    /// </summary>
    public int MaxRowsPerSheet { get; init; } = 1_000_000;
}
