using System.Globalization;
using Recon.Domain.Configuration;
using Recon.Domain.Money;
using Recon.Engine.Staging;

namespace Recon.Engine.Parsing;

/// <summary>
/// Turns a raw source row into a <see cref="StagingRecord"/>: applies the
/// transform chain, converts to the slot's type, writes the normalized
/// companions, and derives the partition date.
///
/// <para>
/// Everything the mapping needs is resolved once in the constructor — slot
/// references, currency scale, the Date-role field — so the per-row path does
/// no lookups by name and no allocation beyond the values themselves.
/// </para>
/// </summary>
public sealed class RowParser
{
    private readonly Dataset _dataset;
    private readonly Currency _currency;
    private readonly PreparedMapping[] _mappings;
    private readonly SlotRef? _dateSlot;
    private readonly FieldRole? _dateRole;

    public RowParser(Dataset dataset, FileFormat format, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(currency);

        _dataset = dataset;
        _currency = currency;

        _mappings = format.Mappings.Select(m => new PreparedMapping(
            m,
            SlotRef.Parse(m.Field.StorageSlot),
            m.Field.NormalizeForMatch && m.Field.NormalizedSlot is not null
                ? SlotRef.Parse(m.Field.NormalizedSlot)
                : null)).ToArray();

        // TxDate is the partition column, so it must come from somewhere
        // deterministic. The Date-role field is that somewhere — the engine
        // never looks for a field NAMED "date" (§6).
        var dateField = dataset.FieldWithRole(FieldRole.Date);
        if (dateField is not null)
        {
            _dateSlot = SlotRef.Parse(dateField.StorageSlot);
            _dateRole = FieldRole.Date;
        }
    }

    /// <summary>
    /// Parses one row into <paramref name="record"/>, which the caller reuses.
    /// Returns false and fills <paramref name="error"/> when the row must be
    /// rejected.
    /// </summary>
    public bool TryParse(
        CsvRow row,
        StagingRecord record,
        DateOnly businessDate,
        out ParseFailure? error)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(record);

        record.Reset();
        record.DatasetId = _dataset.DatasetId;
        record.RawRowNumber = row.LineNumber;
        error = null;

        foreach (var prepared in _mappings)
        {
            var mapping = prepared.Mapping;

            if (!row.TryGet(mapping.SourcePath, out var raw))
            {
                raw = null;
            }

            var value = Transforms.Apply(raw, mapping.Transforms);

            if (string.IsNullOrEmpty(value))
            {
                value = mapping.DefaultValue;
            }

            if (string.IsNullOrEmpty(value))
            {
                if (mapping.IsRequired || mapping.Field.IsRequired)
                {
                    error = new ParseFailure(
                        ParseErrorType.MissingRequired,
                        mapping.Field.FieldCode,
                        $"required field '{mapping.Field.FieldCode}' is absent or empty",
                        row.LineNumber,
                        row.RawLine);
                    return false;
                }

                continue;
            }

            try
            {
                record.Set(prepared.Slot, Convert(value, mapping));
            }
            catch (FormatException)
            {
                error = new ParseFailure(
                    ParseErrorType.TypeConversion,
                    mapping.Field.FieldCode,
                    $"'{Truncate(value)}' is not a valid {mapping.Field.DataType}",
                    row.LineNumber,
                    row.RawLine);
                return false;
            }
            catch (OverflowException)
            {
                error = new ParseFailure(
                    ParseErrorType.TypeConversion,
                    mapping.Field.FieldCode,
                    $"'{Truncate(value)}' is out of range for {mapping.Field.DataType}",
                    row.LineNumber,
                    row.RawLine);
                return false;
            }

            // The normalized companion, written at parse time so the rule that
            // uses it stays an indexed Exact match (blocker A2).
            if (prepared.NormalizedSlot is { } normalizedSlot)
            {
                record.Set(normalizedSlot, Transforms.Normalize(value));
            }
        }

        if (!TrySetPartitionDate(record, businessDate, row, out error))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// <c>TxDate</c> drives partitioning, so a row with an unreadable date
    /// cannot be staged: it would land in whichever partition the default
    /// chose, and every later query over that range would be wrong.
    /// </summary>
    private bool TrySetPartitionDate(
        StagingRecord record,
        DateOnly businessDate,
        CsvRow row,
        out ParseFailure? error)
    {
        error = null;

        if (_dateSlot is null)
        {
            // No Date-role field: a summary dataset. The business date stands
            // in, which is correct for a row that describes the whole session.
            record.TxDate = businessDate;
            return true;
        }

        var value = record.Get(_dateSlot.Value);

        if (value is DateTime dt)
        {
            record.TxDate = DateOnly.FromDateTime(dt);
            return true;
        }

        if (_dateRole == FieldRole.Date)
        {
            error = new ParseFailure(
                ParseErrorType.MissingRequired,
                _dataset.FieldWithRole(FieldRole.Date)?.FieldCode ?? "(date)",
                "the Date-role field is empty, so the row has no partition date",
                row.LineNumber,
                row.RawLine);
            return false;
        }

        record.TxDate = businessDate;
        return true;
    }

    private object? Convert(string value, FieldMapping mapping) => mapping.Field.DataType switch
    {
        FieldDataType.String => value,

        // An Amount-role integer arrives as a decimal in the file and is
        // scaled to minor units here — the one place the conversion happens.
        FieldDataType.Integer when mapping.Field.Role == FieldRole.Amount =>
            _currency.ToMinor(decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture)),

        FieldDataType.Integer => long.Parse(value, CultureInfo.InvariantCulture),

        FieldDataType.Decimal =>
            decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture),

        FieldDataType.DateTime => mapping.ParseFormat is null
            ? DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.None)
            : DateTime.ParseExact(value, mapping.ParseFormat, CultureInfo.InvariantCulture,
                                  DateTimeStyles.None),

        FieldDataType.Boolean => ParseBoolean(value),

        _ => throw new ArgumentOutOfRangeException(nameof(mapping)),
    };

    /// <summary>
    /// Partners spell booleans every way imaginable. Anything outside this set
    /// is a format error rather than a silent false.
    /// </summary>
    private static bool ParseBoolean(string value) => value.Trim().ToUpperInvariant() switch
    {
        "1" or "TRUE" or "Y" or "YES" or "T" => true,
        "0" or "FALSE" or "N" or "NO" or "F" => false,
        _ => throw new FormatException($"'{value}' is not a boolean"),
    };

    private static string Truncate(string value) =>
        value.Length <= 60 ? value : value[..60] + "…";

    private sealed record PreparedMapping(FieldMapping Mapping, SlotRef Slot, SlotRef? NormalizedSlot);
}

public enum ParseErrorType { MissingRequired, TypeConversion, FormatInvalid, RowShape, Unknown }

public sealed record ParseFailure(
    ParseErrorType Type,
    string? FieldCode,
    string Message,
    int RawRowNumber,
    string RawLine);
