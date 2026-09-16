using Recon.Domain.Configuration;
using Recon.Engine.Parsing;
using Recon.Engine.Staging;
using Xunit;

namespace Recon.UnitTests;

public class RowParserTests
{
    private static readonly DateOnly BusinessDate = new(2026, 9, 13);

    private static FileFormat CliqFormat(Dataset dataset) => new()
    {
        FileFormatId = 1,
        Type = FileFormatType.Csv,
        EffectiveFrom = new DateOnly(2026, 1, 1),
        Mappings =
        [
            new FieldMapping { SourcePath = "EndToEndId", Field = dataset.GetField("REF_PRIMARY"), IsRequired = true },
            new FieldMapping { SourcePath = "TxnId", Field = dataset.GetField("REF_TXN") },
            new FieldMapping { SourcePath = "Account", Field = dataset.GetField("CRDTR_ACCT") },
            new FieldMapping { SourcePath = "Amount", Field = dataset.GetField("AMOUNT"), IsRequired = true },
            new FieldMapping { SourcePath = "Currency", Field = dataset.GetField("CURRENCY") },
            new FieldMapping
            {
                SourcePath = "TxnDateTime",
                Field = dataset.GetField("TX_DATETIME"),
                ParseFormat = "yyyyMMddHHmmss",
                IsRequired = true,
            },
            new FieldMapping { SourcePath = "Direction", Field = dataset.GetField("DIRECTION") },
            new FieldMapping { SourcePath = "Status", Field = dataset.GetField("STATUS") },
        ],
    };

    private static CsvRow Row(params string[] values) =>
        new(values,
            ["EndToEndId", "TxnId", "Account", "Amount", "Currency", "TxnDateTime", "Direction", "Status"],
            2,
            string.Join(",", values));

    private static (bool Ok, StagingRecord Record, ParseFailure? Error) Parse(
        params string[] values)
    {
        var dataset = Fixtures.CliqSession();
        var parser = new RowParser(dataset, CliqFormat(dataset), Fixtures.Jod);
        var record = new StagingRecord { LoadRunId = 4471 };
        var ok = parser.TryParse(Row(values), record, BusinessDate, out var error);
        return (ok, record, error);
    }

    [Fact]
    public void ParsesAGoodRowIntoTheRegistrysSlots()
    {
        var (ok, record, error) = Parse(
            "E2E-20260913-0099412", "TXN-1", "0079012345", "125.500",
            "JOD", "20260913121500", "Inward", "ACSC");

        Assert.True(ok);
        Assert.Null(error);

        // Each value in the slot its registry row named, not a slot the parser
        // chose.
        Assert.Equal("E2E-20260913-0099412", record.Text[0]);   // Text1
        Assert.Equal("TXN-1", record.Text[1]);                  // Text2
        Assert.Equal("0079012345", record.Text[3]);             // Text4
        Assert.Equal("JOD", record.Text[4]);                    // Text5
        Assert.Equal("Inward", record.Text[5]);                 // Text6
        Assert.Equal("ACSC", record.Text[6]);                   // Text7
    }

    [Fact]
    public void ScalesTheAmountToMinorUnitsUsingTheDatasetsCurrency()
    {
        var (ok, record, _) = Parse(
            "E2E-1", "TXN-1", "0079012345", "125.500", "JOD", "20260913121500", "Inward", "ACSC");

        Assert.True(ok);
        // 125.500 JOD at 3 minor units, as an integer.
        Assert.Equal(125_500L, record.Num[0]);
    }

    [Fact]
    public void WritesTheNormalizedCompanionAtParseTime()
    {
        // Blocker A2: this is the work that moves out of the join predicate.
        var (ok, record, _) = Parse(
            "e2e-2026 0913/0099412", "TXN-1", "0079-012-345", "125.500",
            "JOD", "20260913121500", "Inward", "ACSC");

        Assert.True(ok);
        Assert.Equal("E2E202609130099412", record.Text[20]);  // Text21, REF_PRIMARY
        Assert.Equal("0079012345", record.Text[21]);           // Text22, CRDTR_ACCT
    }

    [Fact]
    public void FieldsWithNoCompanionLeaveTheirCompanionSlotsEmpty()
    {
        var (_, record, _) = Parse(
            "E2E-1", "TXN-1", "0079012345", "125.500", "JOD", "20260913121500", "Inward", "ACSC");

        for (var i = 22; i < StagingRecord.TextSlots; i++)
        {
            Assert.Null(record.Text[i]);
        }
    }

    [Fact]
    public void DerivesThePartitionDateFromTheDateRoleField()
    {
        // TxDate drives partitioning, and it comes from the field whose ROLE is
        // Date — never from a field named "date" (§6).
        var (ok, record, _) = Parse(
            "E2E-1", "TXN-1", "0079012345", "125.500", "JOD", "20260913121500", "Inward", "ACSC");

        Assert.True(ok);
        Assert.Equal(new DateOnly(2026, 9, 13), record.TxDate);
    }

    [Fact]
    public void ADatasetWithNoDateRoleIsStampedWithTheBusinessDate()
    {
        /* The Date role is optional (it was required until it was asked to be
           optional), and this is the behaviour that makes it safe to be: TxDate
           is the partition column, so it must come from somewhere
           deterministic, and for a dataset with no Date-role field that
           somewhere is the business date. A summary feed — one row describing a
           whole session — has no transaction date of its own.

           The row still carries its date VALUE if it has one; what changes is
           only where the partition date comes from. */
        var full = Fixtures.CliqSession();
        var dateless = full with
        {
            Fields = full.Fields.Where(f => f.Role != FieldRole.Date).ToList(),
        };

        // The mapping goes with the field: a dataset with no Date-role field
        // has nothing to map the file's date column to, and the column is
        // simply not read.
        var format = CliqFormat(full);
        var parser = new RowParser(
            dateless,
            format with
            {
                Mappings = format.Mappings.Where(m => m.Field.Role != FieldRole.Date).ToList(),
            },
            Fixtures.Jod);

        var record = new StagingRecord { LoadRunId = 4471 };

        var ok = parser.TryParse(
            Row("E2E-1", "TXN-1", "0079012345", "125.500", "JOD", "20260101121500", "Inward", "ACSC"),
            record, BusinessDate, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(BusinessDate, record.TxDate);
    }

    [Fact]
    public void ParsesADateUsingTheConfiguredFormat()
    {
        var (ok, record, _) = Parse(
            "E2E-1", "TXN-1", "0079012345", "125.500", "JOD", "20260913121500", "Inward", "ACSC");

        Assert.True(ok);
        Assert.Equal(new DateTime(2026, 9, 13, 12, 15, 0, DateTimeKind.Unspecified), record.Date[0]);
    }

    [Fact]
    public void RejectsARowMissingARequiredField()
    {
        var (ok, _, error) = Parse(
            string.Empty, "TXN-1", "0079012345", "125.500", "JOD", "20260913121500", "Inward", "ACSC");

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Equal(ParseErrorType.MissingRequired, error.Type);
        Assert.Equal("REF_PRIMARY", error.FieldCode);
    }

    [Fact]
    public void RejectsAnUnparseableAmount()
    {
        var (ok, _, error) = Parse(
            "E2E-1", "TXN-1", "0079012345", "not a number", "JOD", "20260913121500", "Inward", "ACSC");

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Equal(ParseErrorType.TypeConversion, error.Type);
        Assert.Equal("AMOUNT", error.FieldCode);
    }

    [Fact]
    public void RejectsAnUnparseableDate()
    {
        var (ok, _, error) = Parse(
            "E2E-1", "TXN-1", "0079012345", "125.500", "JOD", "13-09-2026", "Inward", "ACSC");

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Equal(ParseErrorType.TypeConversion, error.Type);
    }

    [Fact]
    public void AnErrorCarriesTheLineAndTheRawLine()
    {
        // stg.ParseError needs both to be useful to an operator.
        var (ok, _, error) = Parse(
            "E2E-1", "TXN-1", "0079012345", "oops", "JOD", "20260913121500", "Inward", "ACSC");

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Equal(2, error.RawRowNumber);
        Assert.Contains("oops", error.RawLine, StringComparison.Ordinal);
    }

    [Fact]
    public void ReusingOneRecordDoesNotLeakValuesBetweenRows()
    {
        // The parser reuses a single record to avoid 2M allocations. If Reset
        // missed a slot, a NULL in row N would silently inherit row N-1's
        // value — a class of bug that would corrupt matching invisibly.
        var dataset = Fixtures.CliqSession();
        var parser = new RowParser(dataset, CliqFormat(dataset), Fixtures.Jod);
        var record = new StagingRecord { LoadRunId = 1 };

        Assert.True(parser.TryParse(
            Row("E2E-1", "TXN-1", "ACC-1", "1.000", "JOD", "20260913121500", "Inward", "ACSC"),
            record, BusinessDate, out _));
        Assert.Equal("ACSC", record.Text[6]);

        // Second row omits Status entirely.
        Assert.True(parser.TryParse(
            Row("E2E-2", "TXN-2", "ACC-2", "2.000", "JOD", "20260913121500", "Inward", string.Empty),
            record, BusinessDate, out _));

        Assert.Null(record.Text[6]);
        Assert.Equal("E2E-2", record.Text[0]);
        Assert.Equal(2_000L, record.Num[0]);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("Y", true)]
    [InlineData("true", true)]
    [InlineData("0", false)]
    [InlineData("N", false)]
    [InlineData("FALSE", false)]
    public void ParsesTheWaysPartnersSpellBooleans(string value, bool expected)
    {
        var dataset = Fixtures.OrangeMoney();
        var format = new FileFormat
        {
            FileFormatId = 2,
            Type = FileFormatType.Csv,
            EffectiveFrom = new DateOnly(2026, 1, 1),
            Mappings =
            [
                new FieldMapping { SourcePath = "Ref", Field = dataset.GetField("OM_REF"), IsRequired = true },
                new FieldMapping { SourcePath = "Posted", Field = dataset.GetField("POSTED_AT"), IsRequired = true },
                new FieldMapping { SourcePath = "Reversed", Field = dataset.GetField("IS_REVERSED") },
            ],
        };

        var parser = new RowParser(dataset, format, Fixtures.Jod);
        var record = new StagingRecord { LoadRunId = 1 };
        var row = new CsvRow(["OM-1", "2026-09-13T12:15:00", value],
                             ["Ref", "Posted", "Reversed"], 2, "raw");

        Assert.True(parser.TryParse(row, record, BusinessDate, out _));
        Assert.Equal(expected, record.Flag[0]);
    }

    [Fact]
    public void RejectsAnUnrecognisedBoolean()
    {
        var dataset = Fixtures.OrangeMoney();
        var format = new FileFormat
        {
            FileFormatId = 2,
            Type = FileFormatType.Csv,
            EffectiveFrom = new DateOnly(2026, 1, 1),
            Mappings =
            [
                new FieldMapping { SourcePath = "Ref", Field = dataset.GetField("OM_REF"), IsRequired = true },
                new FieldMapping { SourcePath = "Posted", Field = dataset.GetField("POSTED_AT"), IsRequired = true },
                new FieldMapping { SourcePath = "Reversed", Field = dataset.GetField("IS_REVERSED") },
            ],
        };

        var parser = new RowParser(dataset, format, Fixtures.Jod);
        var record = new StagingRecord { LoadRunId = 1 };
        var row = new CsvRow(["OM-1", "2026-09-13T12:15:00", "maybe"],
                             ["Ref", "Posted", "Reversed"], 2, "raw");

        // Silently staging false would be worse than rejecting the row: a
        // reversal flag read wrong changes a classification.
        Assert.False(parser.TryParse(row, record, BusinessDate, out var error));
        Assert.Equal(ParseErrorType.TypeConversion, error!.Type);
    }

    [Fact]
    public void AppliesTheMappingsTransformChain()
    {
        var dataset = Fixtures.CliqSession();
        var format = new FileFormat
        {
            FileFormatId = 3,
            Type = FileFormatType.Csv,
            EffectiveFrom = new DateOnly(2026, 1, 1),
            Mappings =
            [
                new FieldMapping
                {
                    SourcePath = "EndToEndId",
                    Field = dataset.GetField("REF_PRIMARY"),
                    IsRequired = true,
                    Transforms = Transforms.Parse("""[{"op":"Trim"},{"op":"Upper"}]"""),
                },
                new FieldMapping { SourcePath = "Amount", Field = dataset.GetField("AMOUNT"), IsRequired = true },
                new FieldMapping
                {
                    SourcePath = "TxnDateTime",
                    Field = dataset.GetField("TX_DATETIME"),
                    ParseFormat = "yyyyMMddHHmmss",
                    IsRequired = true,
                },
            ],
        };

        var parser = new RowParser(dataset, format, Fixtures.Jod);
        var record = new StagingRecord { LoadRunId = 1 };
        var row = new CsvRow(["  e2e-1  ", "1.000", "20260913121500"],
                             ["EndToEndId", "Amount", "TxnDateTime"], 2, "raw");

        Assert.True(parser.TryParse(row, record, BusinessDate, out _));
        Assert.Equal("E2E-1", record.Text[0]);
    }

    [Fact]
    public void FallsBackToTheDefaultValueWhenAFieldIsAbsent()
    {
        var dataset = Fixtures.CliqSession();
        var format = new FileFormat
        {
            FileFormatId = 4,
            Type = FileFormatType.Csv,
            EffectiveFrom = new DateOnly(2026, 1, 1),
            Mappings =
            [
                new FieldMapping { SourcePath = "EndToEndId", Field = dataset.GetField("REF_PRIMARY"), IsRequired = true },
                new FieldMapping { SourcePath = "Amount", Field = dataset.GetField("AMOUNT"), IsRequired = true },
                new FieldMapping
                {
                    SourcePath = "TxnDateTime",
                    Field = dataset.GetField("TX_DATETIME"),
                    ParseFormat = "yyyyMMddHHmmss",
                    IsRequired = true,
                },
                // A partner who omits the column entirely for domestic traffic.
                new FieldMapping
                {
                    SourcePath = "Currency",
                    Field = dataset.GetField("CURRENCY"),
                    DefaultValue = "JOD",
                },
            ],
        };

        var parser = new RowParser(dataset, format, Fixtures.Jod);
        var record = new StagingRecord { LoadRunId = 1 };
        var row = new CsvRow(["E2E-1", "1.000", "20260913121500"],
                             ["EndToEndId", "Amount", "TxnDateTime"], 2, "raw");

        Assert.True(parser.TryParse(row, record, BusinessDate, out _));
        Assert.Equal("JOD", record.Text[4]);
    }
}
