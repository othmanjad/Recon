using Microsoft.Data.SqlClient;

namespace Recon.Data;

/// <summary>
/// The record of a file's arrival: <c>ops.SourceFile</c>.
///
/// <para>
/// One class because there are two ways a file arrives — an operator uploads
/// it, or acquisition fetches it from a folder — and both must produce the
/// same record. Two inserts would drift, and the thing that drifted would be
/// the audit trail.
/// </para>
///
/// <para>
/// What is stored is the path, the SHA-256, the size and the counts; never the
/// bytes. At two million rows a day, files as database blobs is how a database
/// becomes unmanageable (§13), and the original stays on disk as the
/// byte-identical record the design asks for.
/// </para>
///
/// <para>
/// The unique index on (DatasetId, FileHash, BusinessDate) is what makes the
/// same content arriving twice a <b>duplicate</b> rather than a second run, so
/// <see cref="RecordAsync"/> looks for that row before inserting and returns
/// the existing one instead of failing on the index.
/// </para>
/// </summary>
public sealed class SourceFileRepository(SqlConnection connection)
{
    private readonly SqlConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>
    /// Records an arrival, or returns the arrival that already carried these
    /// bytes for this dataset and date.
    /// </summary>
    public async Task<SourceFileRecord> RecordAsync(
        int datasetId,
        string fileName,
        string storagePath,
        string sha256,
        long bytes,
        DateOnly businessDate,
        string? sessionRef,
        CancellationToken cancellationToken = default)
    {
        var existing = await FindAsync(datasetId, sha256, businessDate, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return existing with { IsDuplicate = true };
        }

        var id = await Db.ScalarAsync<long>(
            _connection,
            """
            INSERT ops.SourceFile
                (DatasetId, FileName, StoragePath, FileHash, FileSizeBytes,
                 BusinessDate, SessionRef, Status)
            VALUES (@ds, @name, @path, @hash, @size, @date, @session, 'Received');
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);
            """,
            c => c.With("@ds", datasetId)
                  .With("@name", fileName)
                  .With("@path", storagePath)
                  .With("@hash", sha256)
                  .With("@size", bytes)
                  .With("@date", businessDate.ToDateTime(TimeOnly.MinValue))
                  .With("@session", sessionRef),
            cancellationToken).ConfigureAwait(false);

        return new SourceFileRecord
        {
            SourceFileId = id,
            DatasetId = datasetId,
            FileName = fileName,
            StoragePath = storagePath,
            Sha256 = sha256,
            Bytes = bytes,
            BusinessDate = businessDate,
            Status = "Received",
            IsDuplicate = false,
        };
    }

    public async Task<SourceFileRecord?> FindAsync(
        int datasetId,
        string sha256,
        DateOnly businessDate,
        CancellationToken cancellationToken = default)
    {
        var rows = await Db.QueryAsync(
            _connection,
            """
            SELECT SourceFileId, DatasetId, FileName, StoragePath, FileHash,
                   FileSizeBytes, Status
            FROM ops.SourceFile
            WHERE DatasetId = @ds AND FileHash = @hash AND BusinessDate = @date;
            """,
            r => new SourceFileRecord
            {
                SourceFileId = r.GetInt64(0),
                DatasetId = r.GetInt32(1),
                FileName = r.GetString(2),
                StoragePath = r.GetString(3),
                Sha256 = r.GetString(4),
                Bytes = r.GetInt64(5),
                BusinessDate = businessDate,
                Status = r.GetString(6),
                IsDuplicate = false,
            },
            c => c.With("@ds", datasetId)
                  .With("@hash", sha256)
                  .With("@date", businessDate.ToDateTime(TimeOnly.MinValue)),
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>
    /// Records what parsing did with the file, so the row is the file's life
    /// rather than only its arrival: an operator asking "did yesterday's
    /// statement load, and how many rows did it reject" is asking this table.
    /// </summary>
    public Task<int> RecordParsedAsync(
        long sourceFileId,
        DateOnly businessDate,
        long rows,
        long errors,
        bool failed,
        CancellationToken cancellationToken = default) =>
        Db.ExecuteAsync(
            _connection,
            """
            UPDATE ops.SourceFile
            SET Status = @status, ProcessedAt = SYSDATETIME(), RowCnt = @rows, ErrorCnt = @errors
            WHERE SourceFileId = @id AND BusinessDate = @date;
            """,
            c => c.With("@id", sourceFileId)
                  .With("@date", businessDate.ToDateTime(TimeOnly.MinValue))
                  .With("@status", failed ? "Failed" : "Parsed")
                  .With("@rows", rows)
                  .With("@errors", errors),
            cancellationToken);

    /// <summary>
    /// The files received for a dataset and date, newest first — what the
    /// acquisition screen shows so an operator can see whether today's file
    /// arrived without opening a query window.
    /// </summary>
    public Task<List<SourceFileRecord>> RecentAsync(
        int datasetId,
        int take = 10,
        CancellationToken cancellationToken = default) =>
        Db.QueryAsync(
            _connection,
            """
            SELECT TOP (@take) SourceFileId, DatasetId, FileName, StoragePath, FileHash,
                   FileSizeBytes, BusinessDate, Status, ReceivedAt, RowCnt, ErrorCnt
            FROM ops.SourceFile
            WHERE DatasetId = @ds
            ORDER BY ReceivedAt DESC;
            """,
            r => new SourceFileRecord
            {
                SourceFileId = r.GetInt64(0),
                DatasetId = r.GetInt32(1),
                FileName = r.GetString(2),
                StoragePath = r.GetString(3),
                Sha256 = r.GetString(4),
                Bytes = r.GetInt64(5),
                BusinessDate = DateOnly.FromDateTime(r.GetDateTime(6)),
                Status = r.GetString(7),
                ReceivedAt = r.GetDateTime(8),
                Rows = r.GetNullableInt64("RowCnt"),
                Errors = r.GetNullableInt64("ErrorCnt"),
                IsDuplicate = false,
            },
            c => c.With("@take", take).With("@ds", datasetId),
            cancellationToken);
}

public sealed record SourceFileRecord
{
    public required long SourceFileId { get; init; }
    public required int DatasetId { get; init; }
    public required string FileName { get; init; }
    public required string StoragePath { get; init; }
    public required string Sha256 { get; init; }
    public required long Bytes { get; init; }
    public required DateOnly BusinessDate { get; init; }
    public required string Status { get; init; }
    public DateTime? ReceivedAt { get; init; }
    public long? Rows { get; init; }
    public long? Errors { get; init; }

    /// <summary>
    /// True when these bytes had already been received for this dataset and
    /// date. The run is not refused for it — a retry after a configuration fix
    /// is legitimate — but it is said out loud, because the alternative is an
    /// operator believing a new file arrived when none did.
    /// </summary>
    public required bool IsDuplicate { get; init; }
}
