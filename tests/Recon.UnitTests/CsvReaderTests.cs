using Recon.Engine.Parsing;
using Xunit;

namespace Recon.UnitTests;

public class CsvReaderTests
{
    private static List<CsvRow> Read(string content, CsvOptions? options = null) =>
        new CsvReader(options ?? new CsvOptions())
            .Read(new StringReader(content))
            .ToList();

    [Fact]
    public void ReadsHeaderAndRows()
    {
        var rows = Read("Ref,Amount\nE2E-1,125.500\nE2E-2,48.000\n");

        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].TryGet("Ref", out var reference));
        Assert.Equal("E2E-1", reference);
        Assert.True(rows[1].TryGet("Amount", out var amount));
        Assert.Equal("48.000", amount);
    }

    [Fact]
    public void ReadsAQuotedFieldContainingTheDelimiter()
    {
        var rows = Read("Ref,Name\nE2E-1,\"Doe, John\"\n");

        Assert.True(rows[0].TryGet("Name", out var name));
        Assert.Equal("Doe, John", name);
    }

    [Fact]
    public void ReadsADoubledQualifierAsALiteralOne()
    {
        var rows = Read("Ref,Note\nE2E-1,\"say \"\"hello\"\"\"\n");

        Assert.True(rows[0].TryGet("Note", out var note));
        Assert.Equal("say \"hello\"", note);
    }

    [Fact]
    public void ReadsAQuotedFieldSpanningLines()
    {
        var rows = Read("Ref,Note\nE2E-1,\"line one\nline two\"\nE2E-2,plain\n");

        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].TryGet("Note", out var note));
        Assert.Equal("line one\nline two", note);
    }

    [Fact]
    public void ReportsTheLineTheRecordStartedOn()
    {
        // A parse error on a multi-line record must point at its first line, or
        // the operator opens the file at the wrong place.
        var rows = Read("Ref,Note\nE2E-1,\"a\nb\"\nE2E-2,c\n");

        Assert.Equal(2, rows[0].LineNumber);
        Assert.Equal(4, rows[1].LineNumber);
    }

    [Fact]
    public void ReadsTheFinalRecordWithNoTrailingNewline()
    {
        var rows = Read("Ref,Amount\nE2E-1,125.500");

        Assert.Single(rows);
        Assert.True(rows[0].TryGet("Amount", out var amount));
        Assert.Equal("125.500", amount);
    }

    [Fact]
    public void HonoursACustomDelimiter()
    {
        var rows = Read("Ref|Amount\nE2E-1|125.500\n", new CsvOptions { Delimiter = '|' });

        Assert.True(rows[0].TryGet("Amount", out var amount));
        Assert.Equal("125.500", amount);
    }

    [Fact]
    public void SkipsLeadingAndTrailingLines()
    {
        // JoPACC-style files arrive with a preamble and a footer; both come
        // from cfg.FileFormatDefinition rather than being hard-coded.
        var rows = Read(
            "GENERATED 2026-09-13\nRef,Amount\nE2E-1,1.000\nE2E-2,2.000\nTOTAL,3.000\n",
            new CsvOptions { SkipLeadingLines = 1, SkipTrailingLines = 1 });

        Assert.Equal(2, rows.Count);
        Assert.True(rows[1].TryGet("Ref", out var last));
        Assert.Equal("E2E-2", last);
    }

    [Fact]
    public void ReadsByOrdinalWhenThereIsNoHeader()
    {
        var rows = Read("E2E-1,125.500\n", new CsvOptions { HasHeader = false });

        Assert.True(rows[0].TryGet("1", out var first));
        Assert.Equal("E2E-1", first);
        Assert.True(rows[0].TryGet("2", out var second));
        Assert.Equal("125.500", second);
    }

    [Fact]
    public void MissingColumnIsReportedRatherThanThrowing()
    {
        var rows = Read("Ref\nE2E-1\n");

        Assert.False(rows[0].TryGet("Amount", out var value));
        Assert.Null(value);
    }

    [Fact]
    public void ARowShorterThanTheHeaderDoesNotThrow()
    {
        // Real partner files do this. A ragged row must be a parse error the
        // pipeline can record, not an exception that stops the file.
        var rows = Read("Ref,Amount,Status\nE2E-1,125.500\n");

        Assert.True(rows[0].TryGet("Amount", out _));
        Assert.False(rows[0].TryGet("Status", out _));
    }

    [Fact]
    public void KeepsTheRawLineForAnErrorReport()
    {
        var rows = Read("Ref,Amount\nE2E-1,oops\n");

        Assert.Equal("E2E-1,oops", rows[0].RawLine);
    }

    [Fact]
    public void ReadsCrLfLineEndings()
    {
        var rows = Read("Ref,Amount\r\nE2E-1,125.500\r\n");

        Assert.Single(rows);
        Assert.True(rows[0].TryGet("Ref", out var reference));
        Assert.Equal("E2E-1", reference);
    }

    [Fact]
    public void EmptyFileYieldsNoRows() => Assert.Empty(Read(string.Empty));

    [Fact]
    public void HeaderOnlyFileYieldsNoRows() => Assert.Empty(Read("Ref,Amount\n"));
}
