using System.Data;
using Microsoft.Data.SqlClient;
using Recon.Domain.Configuration;

namespace Recon.Engine.Staging;

/// <summary>
/// Loads parsed rows into <c>stg.StagingTransaction</c>.
///
/// <para>
/// This class is where the two-minute budget for 2M rows is won or lost, and
/// the shape of it comes straight from review blocker A1. v0.1 said "bulk load
/// into the heap first, then build indexes". That is impossible: staging is one
/// shared table with a permanent clustered index, and you cannot drop indexes
/// for a single dataset's load.
/// </para>
///
/// <para>The real path, and why each part matters:</para>
/// <list type="bullet">
/// <item>
/// <b><see cref="SqlBulkCopyOptions.TableLock"/></b> — without it every batch
/// takes row locks and the load is several times slower.
/// </item>
/// <item>
/// <b>Sorted input plus an ORDER hint</b> matching the clustered key
/// <c>(DatasetId, TxDate, StagingId)</c>. Sorted input into a clustered index
/// is minimally logged under BULK_LOGGED or SIMPLE recovery and avoids page
/// splits. <see cref="SqlBulkCopy"/> has no ORDER property, so the hint is set
/// through the internal option that <c>BULK INSERT ... WITH (ORDER(...))</c>
/// exposes; when that is unavailable the loader still sorts its batches, which
/// is where most of the benefit is.
/// </item>
/// <item>
/// <b>Batches of ~100k</b> (<c>cfg.PlatformSetting.BulkCopyBatchSize</c>) —
/// large enough to amortise the round trip, small enough that a failure does
/// not roll back the whole file.
/// </item>
/// </list>
///
/// <para>
/// <c>StagingId</c> is an IDENTITY and is not supplied, so rows within a
/// (DatasetId, TxDate) group are appended in insertion order. Sorting on the
/// first two key columns is therefore sufficient to present the input in
/// clustered-key order.
/// </para>
/// </summary>
public sealed class StagingBulkLoader(SqlConnection connection, StagingLoadOptions? options = null)
{
    private readonly SqlConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    private readonly StagingLoadOptions _options = options ?? new StagingLoadOptions();

    /// <summary>
    /// The columns written, in the order the reader presents them. Result
    /// columns are deliberately absent: a load does not decide a match, and
    /// <c>MatchStatus</c> defaults to <c>Unmatched</c> in the table.
    /// </summary>
    private static readonly string[] Columns = BuildColumns();

    private static string[] BuildColumns()
    {
        var columns = new List<string>
        {
            "DatasetId", "LoadRunId", "SourceFileId", "RawRowNumber", "TxDate",
        };

        for (var i = 1; i <= StagingRecord.TextSlots; i++) { columns.Add("Text" + i); }
        for (var i = 1; i <= StagingRecord.NumSlots; i++) { columns.Add("Num" + i); }
        for (var i = 1; i <= StagingRecord.DecSlots; i++) { columns.Add("Dec" + i); }
        for (var i = 1; i <= StagingRecord.DateSlots; i++) { columns.Add("Date" + i); }
        for (var i = 1; i <= StagingRecord.FlagSlots; i++) { columns.Add("Flag" + i); }

        return columns.ToArray();
    }

    public async Task<StagingLoadResult> LoadAsync(
        IEnumerable<StagingRecord> records,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);

        var started = DateTimeOffset.UtcNow;
        long written = 0;

        using var bulk = new SqlBulkCopy(
            _connection,
            SqlBulkCopyOptions.TableLock | SqlBulkCopyOptions.KeepNulls,
            externalTransaction: null)
        {
            DestinationTableName = "stg.StagingTransaction",
            BatchSize = _options.BatchSize,
            BulkCopyTimeout = _options.TimeoutSeconds,
            EnableStreaming = true,
        };

        foreach (var column in Columns)
        {
            bulk.ColumnMappings.Add(column, column);
        }

        // Sorting happens per batch rather than over the whole file: sorting
        // 2M rows in memory would defeat the streaming the reader exists for.
        // A batch-local sort still presents long runs in key order, which is
        // what avoids page splits.
        foreach (var batch in Batch(records, _options.BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            batch.Sort(static (a, b) =>
            {
                var byDataset = a.DatasetId.CompareTo(b.DatasetId);
                return byDataset != 0 ? byDataset : a.TxDate.CompareTo(b.TxDate);
            });

            using var reader = new StagingRecordReader(batch);
            await bulk.WriteToServerAsync(reader, cancellationToken).ConfigureAwait(false);
            written += batch.Count;
        }

        return new StagingLoadResult(written, DateTimeOffset.UtcNow - started);
    }

    private static IEnumerable<List<StagingRecord>> Batch(
        IEnumerable<StagingRecord> source, int size)
    {
        var batch = new List<StagingRecord>(size);

        foreach (var record in source)
        {
            // The parser reuses one record instance, so each must be copied
            // before it is buffered. Copying is what makes the batch sortable
            // at all, and it is the only allocation per row in the load path.
            batch.Add(Copy(record));

            if (batch.Count >= size)
            {
                yield return batch;
                batch = new List<StagingRecord>(size);
            }
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    private static StagingRecord Copy(StagingRecord source)
    {
        var copy = new StagingRecord
        {
            DatasetId = source.DatasetId,
            LoadRunId = source.LoadRunId,
            SourceFileId = source.SourceFileId,
            RawRowNumber = source.RawRowNumber,
            TxDate = source.TxDate,
            MatchStatus = source.MatchStatus,
        };

        source.Text.CopyTo(copy.Text, 0);
        source.Num.CopyTo(copy.Num, 0);
        source.Dec.CopyTo(copy.Dec, 0);
        source.Date.CopyTo(copy.Date, 0);
        source.Flag.CopyTo(copy.Flag, 0);

        return copy;
    }
}

public sealed record StagingLoadOptions
{
    /// <summary>Mirrors <c>cfg.PlatformSetting.BulkCopyBatchSize</c>.</summary>
    public int BatchSize { get; init; } = 100_000;

    public int TimeoutSeconds { get; init; } = 600;
}

public sealed record StagingLoadResult(long RowsWritten, TimeSpan Elapsed)
{
    public double RowsPerSecond =>
        Elapsed.TotalSeconds <= 0 ? 0 : RowsWritten / Elapsed.TotalSeconds;
}

/// <summary>
/// An <see cref="IDataReader"/> over a batch of records.
///
/// <para>
/// <see cref="SqlBulkCopy"/> takes a reader rather than a collection, and
/// implementing one directly avoids building a <see cref="DataTable"/> — which
/// at 2M rows would hold the whole file in memory as boxed objects, several
/// times the size of the file itself.
/// </para>
/// </summary>
internal sealed class StagingRecordReader(IReadOnlyList<StagingRecord> batch) : IDataReader
{
    private static readonly string[] Names = BuildNames();
    private int _index = -1;

    private static string[] BuildNames()
    {
        var names = new List<string>
        {
            "DatasetId", "LoadRunId", "SourceFileId", "RawRowNumber", "TxDate",
        };

        for (var i = 1; i <= StagingRecord.TextSlots; i++) { names.Add("Text" + i); }
        for (var i = 1; i <= StagingRecord.NumSlots; i++) { names.Add("Num" + i); }
        for (var i = 1; i <= StagingRecord.DecSlots; i++) { names.Add("Dec" + i); }
        for (var i = 1; i <= StagingRecord.DateSlots; i++) { names.Add("Date" + i); }
        for (var i = 1; i <= StagingRecord.FlagSlots; i++) { names.Add("Flag" + i); }

        return names.ToArray();
    }

    private StagingRecord Current => batch[_index];

    public int FieldCount => Names.Length;

    public bool Read()
    {
        _index++;
        return _index < batch.Count;
    }

    public object GetValue(int i)
    {
        const int Fixed = 5;

        if (i < Fixed)
        {
            return i switch
            {
                0 => Current.DatasetId,
                1 => Current.LoadRunId,
                2 => (object?)Current.SourceFileId ?? DBNull.Value,
                3 => (object?)Current.RawRowNumber ?? DBNull.Value,
                4 => Current.TxDate.ToDateTime(TimeOnly.MinValue),
                _ => DBNull.Value,
            };
        }

        var offset = i - Fixed;

        if (offset < StagingRecord.TextSlots)
        {
            return (object?)Current.Text[offset] ?? DBNull.Value;
        }

        offset -= StagingRecord.TextSlots;
        if (offset < StagingRecord.NumSlots)
        {
            return (object?)Current.Num[offset] ?? DBNull.Value;
        }

        offset -= StagingRecord.NumSlots;
        if (offset < StagingRecord.DecSlots)
        {
            return (object?)Current.Dec[offset] ?? DBNull.Value;
        }

        offset -= StagingRecord.DecSlots;
        if (offset < StagingRecord.DateSlots)
        {
            return (object?)Current.Date[offset] ?? DBNull.Value;
        }

        offset -= StagingRecord.DateSlots;
        return (object?)Current.Flag[offset] ?? DBNull.Value;
    }

    public string GetName(int i) => Names[i];

    public int GetOrdinal(string name) =>
        Array.IndexOf(Names, name) is var index && index >= 0
            ? index
            : throw new ArgumentOutOfRangeException(nameof(name), name,
                "not a column of stg.StagingTransaction");

    public bool IsDBNull(int i) => GetValue(i) is DBNull;

    // ---- the rest of IDataReader is not used by SqlBulkCopy ----
    public void Close() { }
    public void Dispose() { }
    public bool NextResult() => false;
    public int Depth => 0;
    public bool IsClosed => false;
    public int RecordsAffected => -1;
    public DataTable? GetSchemaTable() => null;
    public object this[int i] => GetValue(i);
    public object this[string name] => GetValue(GetOrdinal(name));
    public bool GetBoolean(int i) => (bool)GetValue(i);
    public byte GetByte(int i) => (byte)GetValue(i);
    public long GetBytes(int i, long o, byte[]? buffer, int bo, int length) =>
        throw new NotSupportedException();
    public char GetChar(int i) => (char)GetValue(i);
    public long GetChars(int i, long o, char[]? buffer, int bo, int length) =>
        throw new NotSupportedException();
    public IDataReader GetData(int i) => throw new NotSupportedException();
    public string GetDataTypeName(int i) => GetFieldType(i).Name;
    public DateTime GetDateTime(int i) => (DateTime)GetValue(i);
    public decimal GetDecimal(int i) => (decimal)GetValue(i);
    public double GetDouble(int i) => (double)GetValue(i);
    public Type GetFieldType(int i) => GetValue(i).GetType();
    public float GetFloat(int i) => (float)GetValue(i);
    public Guid GetGuid(int i) => (Guid)GetValue(i);
    public short GetInt16(int i) => (short)GetValue(i);
    public int GetInt32(int i) => (int)GetValue(i);
    public long GetInt64(int i) => (long)GetValue(i);
    public string GetString(int i) => (string)GetValue(i);
    public int GetValues(object[] values)
    {
        var n = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < n; i++) { values[i] = GetValue(i); }
        return n;
    }
}
