using Recon.Domain.Configuration;

namespace Recon.Engine.Staging;

/// <summary>
/// One row bound for <c>stg.StagingTransaction</c>, held as slot arrays.
///
/// <para>
/// Slots rather than named properties because that is what the table is, and
/// because the alternative — a dictionary keyed by field code — would allocate
/// per row. At 2M rows a load, per-row allocation is the difference between
/// fitting the two-minute budget and not.
/// </para>
/// </summary>
public sealed class StagingRecord
{
    public const int TextSlots = 30;
    public const int NumSlots = 15;
    public const int DecSlots = 5;
    public const int DateSlots = 8;
    public const int FlagSlots = 5;

    public int DatasetId { get; set; }
    public long LoadRunId { get; set; }
    public long? SourceFileId { get; set; }
    public int? RawRowNumber { get; set; }

    /// <summary>The partition column. Derived from the Date-role field.</summary>
    public DateOnly TxDate { get; set; }

    public string?[] Text { get; } = new string?[TextSlots];
    public long?[] Num { get; } = new long?[NumSlots];
    public decimal?[] Dec { get; } = new decimal?[DecSlots];
    public DateTime?[] Date { get; } = new DateTime?[DateSlots];
    public bool?[] Flag { get; } = new bool?[FlagSlots];

    public MatchStatus MatchStatus { get; set; } = MatchStatus.Unmatched;

    public void Reset()
    {
        Array.Clear(Text);
        Array.Clear(Num);
        Array.Clear(Dec);
        Array.Clear(Date);
        Array.Clear(Flag);
        SourceFileId = null;
        RawRowNumber = null;
        MatchStatus = MatchStatus.Unmatched;
    }

    /// <summary>
    /// Writes a value into the slot the registry assigned to the field.
    /// Parsing the slot name is done once per mapping by
    /// <see cref="SlotRef.Parse"/>, never per row.
    /// </summary>
    public void Set(SlotRef slot, object? value)
    {
        switch (slot.Kind)
        {
            case SlotKind.Text:
                Text[slot.Index] = (string?)value;
                break;
            case SlotKind.Num:
                Num[slot.Index] = (long?)value;
                break;
            case SlotKind.Dec:
                Dec[slot.Index] = (decimal?)value;
                break;
            case SlotKind.Date:
                Date[slot.Index] = (DateTime?)value;
                break;
            case SlotKind.Flag:
                Flag[slot.Index] = (bool?)value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(slot), slot.Kind, null);
        }
    }

    public object? Get(SlotRef slot) => slot.Kind switch
    {
        SlotKind.Text => Text[slot.Index],
        SlotKind.Num => Num[slot.Index],
        SlotKind.Dec => Dec[slot.Index],
        SlotKind.Date => Date[slot.Index],
        SlotKind.Flag => Flag[slot.Index],
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot.Kind, null),
    };
}

public enum SlotKind { Text, Num, Dec, Date, Flag }

/// <summary>
/// A parsed slot name: <c>Text7</c> becomes (<see cref="SlotKind.Text"/>, 6).
/// Parsed once at configuration load, so the hot path is an array index.
/// </summary>
public readonly record struct SlotRef(SlotKind Kind, int Index)
{
    public static SlotRef Parse(string slotName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slotName);

        var (kind, prefixLength, max) = slotName switch
        {
            _ when slotName.StartsWith("Text", StringComparison.Ordinal) =>
                (SlotKind.Text, 4, StagingRecord.TextSlots),
            _ when slotName.StartsWith("Num", StringComparison.Ordinal) =>
                (SlotKind.Num, 3, StagingRecord.NumSlots),
            _ when slotName.StartsWith("Dec", StringComparison.Ordinal) =>
                (SlotKind.Dec, 3, StagingRecord.DecSlots),
            _ when slotName.StartsWith("Date", StringComparison.Ordinal) =>
                (SlotKind.Date, 4, StagingRecord.DateSlots),
            _ when slotName.StartsWith("Flag", StringComparison.Ordinal) =>
                (SlotKind.Flag, 4, StagingRecord.FlagSlots),
            _ => throw new ArgumentException($"'{slotName}' is not a storage slot name.", nameof(slotName)),
        };

        if (!int.TryParse(slotName.AsSpan(prefixLength), out var oneBased)
            || oneBased < 1 || oneBased > max)
        {
            throw new ArgumentException(
                $"'{slotName}' is out of range; {kind} slots run 1..{max}.", nameof(slotName));
        }

        return new SlotRef(kind, oneBased - 1);
    }

    public string Name => Kind switch
    {
        SlotKind.Text => "Text" + (Index + 1),
        SlotKind.Num => "Num" + (Index + 1),
        SlotKind.Dec => "Dec" + (Index + 1),
        SlotKind.Date => "Date" + (Index + 1),
        SlotKind.Flag => "Flag" + (Index + 1),
        _ => throw new InvalidOperationException(),
    };
}
