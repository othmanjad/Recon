using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Recon.Data;
using Recon.Domain.Configuration;

namespace Recon.Engine.Providers;

/// <summary>
/// Picks up the file a dataset expects for a business date, from a folder.
///
/// <para>
/// This is the first real provider, and its absence was a hole in a promise:
/// <c>cfg.AcquisitionDefinition</c> described where files come from and
/// nothing read it, so a scheduled run reconciled whatever happened to be
/// staged already. "Runs unattended for a full week" cannot be true without
/// something that fetches the day's file.
/// </para>
///
/// <para>
/// Folder only. SFTP and API are refused by name rather than half-built:
/// configuration for a method nothing implements is worse than no
/// configuration, because it looks like coverage. The seam the design
/// promised (§5.1) is that a new method is a new class here and nothing else
/// changes.
/// </para>
///
/// <para>
/// Every acquired file is recorded in <c>ops.SourceFile</c> with its
/// SHA-256, and the unique index on (dataset, hash, business date) is what
/// makes the same content arriving twice a <b>duplicate</b> rather than a
/// second run. The original stays on disk: at 2M rows a day, storing files
/// as database blobs is how a database becomes unmanageable (§13).
/// </para>
/// </summary>
public sealed class FolderAcquisition(SqlConnection connection)
{
    private readonly SqlConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>
    /// A regex from configuration runs against every name in a folder, so a
    /// pathological pattern is bounded.
    /// </summary>
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Finds the day's file and records its arrival.
    /// </summary>
    /// <param name="record">
    /// False makes this a probe: the folder is searched and the file is
    /// hashed, but no <c>ops.SourceFile</c> row is written. That is what the
    /// acquisition screen's "check now" button needs — an operator asking
    /// whether tonight's run will find the file should not thereby record
    /// that it arrived.
    /// </param>
    public async Task<AcquisitionOutcome> AcquireAsync(
        Dataset dataset,
        DateOnly businessDate,
        string? sessionRef,
        bool record = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        var definitions = await Db.QueryAsync(
            _connection,
            """
            SELECT AcquisitionId, Method, StorageRootPath, RetryCount, RetryDelaySeconds
            FROM cfg.AcquisitionDefinition
            WHERE DatasetId = @ds
            ORDER BY AcquisitionId;
            """,
            r => new
            {
                AcquisitionId = r.GetInt32(0),
                Method = r.GetString(1),
                Root = r.GetString(2),
                Retries = r.GetInt32(3),
                Delay = r.GetInt32(4),
            },
            c => c.With("@ds", dataset.DatasetId),
            cancellationToken).ConfigureAwait(false);

        if (definitions.Count == 0)
        {
            // Not an error: a dataset whose files are uploaded through the
            // portal has nothing to acquire, and saying "none configured" is
            // the right answer rather than a failure.
            return AcquisitionOutcome.NotConfigured(dataset.Code);
        }

        var definition = definitions[0];

        if (definition.Method is "Sftp" or "Api")
        {
            throw new NotSupportedException(
                $"{dataset.Code} is configured to acquire by {definition.Method}, which has no " +
                "provider yet. Folder acquisition and portal uploads are what exist; a method " +
                "that stored configuration and fetched nothing would look like coverage.");
        }

        if (definition.Method == "Manual")
        {
            return AcquisitionOutcome.Manual(dataset.Code);
        }

        var format = await Parsing.FileFormatLoader
            .LoadAsync(_connection, dataset, businessDate, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(format.FileNamePattern))
        {
            throw new InvalidOperationException(
                $"{dataset.Code}'s file format has no file-name pattern, so folder acquisition " +
                "cannot tell which file is today's. Add one, or upload the file through the " +
                "portal instead.");
        }

        if (!Directory.Exists(definition.Root))
        {
            throw new DirectoryNotFoundException(
                $"{dataset.Code}'s acquisition folder does not exist: {definition.Root}");
        }

        var pattern = Substitute(format.FileNamePattern, businessDate, sessionRef);
        Regex regex;

        try
        {
            regex = new Regex(pattern, RegexOptions.IgnoreCase, PatternTimeout);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                $"{dataset.Code}'s file-name pattern is not a valid regular expression after the " +
                $"date was substituted ('{pattern}'): {ex.Message}");
        }

        var matches = Directory.EnumerateFiles(definition.Root)
            .Where(p => regex.IsMatch(Path.GetFileName(p)))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        if (matches.Count == 0)
        {
            // The file-not-received condition, which is the alert the design
            // asks for: a session that never arrived must be distinguishable
            // from one that is not due yet.
            return AcquisitionOutcome.NotFound(dataset.Code, definition.Root, pattern);
        }

        if (matches.Count > 1)
        {
            // Picking one would be a guess about which file is the day's
            // truth, and the whole platform exists to not guess about that.
            throw new InvalidOperationException(
                $"{matches.Count} files in {definition.Root} match {dataset.Code}'s pattern for " +
                $"{businessDate:yyyy-MM-dd} ({string.Join(", ", matches.Select(Path.GetFileName))}). " +
                "Which one is the day's file is not something to guess at.");
        }

        var path = matches[0];
        var hash = await HashAsync(path, cancellationToken).ConfigureAwait(false);
        var size = new FileInfo(path).Length;

        var files = new SourceFileRepository(_connection);

        if (!record)
        {
            var seen = await files.FindAsync(dataset.DatasetId, hash, businessDate, cancellationToken)
                .ConfigureAwait(false);

            return seen is null
                ? AcquisitionOutcome.Found(dataset.Code, path, hash, size)
                : AcquisitionOutcome.Duplicate(dataset.Code, path, hash, seen.SourceFileId, seen.Status);
        }

        // Recorded before it is parsed, so a file that fails mid-parse is
        // still on the record as having arrived. The insert is
        // SourceFileRepository's, the same one an upload goes through: two
        // ways in, one record.
        var arrival = await files.RecordAsync(
            dataset.DatasetId,
            Path.GetFileName(path),
            path,
            hash,
            size,
            businessDate,
            sessionRef,
            cancellationToken).ConfigureAwait(false);

        return arrival.IsDuplicate
            ? AcquisitionOutcome.Duplicate(dataset.Code, path, hash, arrival.SourceFileId, arrival.Status)
            : AcquisitionOutcome.Acquired(dataset.Code, path, hash, arrival.SourceFileId, size);
    }

    /// <summary>
    /// Substitutes date and session tokens into the file-name pattern, so one
    /// stored pattern can name a different file every day.
    ///
    /// <para>
    /// The tokens are braced — <c>{yyyyMMdd}</c> — because a regex is full of
    /// characters that mean something else, and a substitution scheme that
    /// collided with regex syntax would be a trap.
    /// </para>
    /// </summary>
    internal static string Substitute(string pattern, DateOnly businessDate, string? sessionRef)
    {
        var date = businessDate.ToDateTime(TimeOnly.MinValue);

        return pattern
            .Replace("{yyyyMMdd}", date.ToString("yyyyMMdd", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{yyyy-MM-dd}", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{ddMMyyyy}", date.ToString("ddMMyyyy", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{yyyy}", date.ToString("yyyy", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{MM}", date.ToString("MM", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{dd}", date.ToString("dd", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{session}", sessionRef ?? string.Empty, StringComparison.Ordinal);
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        // Streamed: a session file is far too large to hash from memory, and
        // the hash is what identifies the bytes that arrived.
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);

        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed record AcquisitionOutcome
{
    public required AcquisitionState State { get; init; }
    public required string Dataset { get; init; }
    public string? Path { get; init; }
    public string? Sha256 { get; init; }
    public long? SourceFileId { get; init; }
    public long Bytes { get; init; }
    public required string Message { get; init; }

    public bool HasFile =>
        State is AcquisitionState.Acquired or AcquisitionState.Uploaded && Path is not null;

    public static AcquisitionOutcome Acquired(
        string dataset, string path, string hash, long sourceFileId, long bytes) =>
        new()
        {
            State = AcquisitionState.Acquired,
            Dataset = dataset,
            Path = path,
            Sha256 = hash,
            SourceFileId = sourceFileId,
            Bytes = bytes,
            Message = $"{dataset}: acquired {System.IO.Path.GetFileName(path)} ({bytes:N0} bytes)",
        };

    /// <summary>
    /// The file did not have to be fetched because an operator supplied it.
    /// Recorded the same way an acquired file is — the record of an arrival
    /// should not depend on how the bytes got here — but reported differently,
    /// because "acquired" would be a lie about a file somebody uploaded.
    /// </summary>
    public static AcquisitionOutcome Uploaded(
        string dataset, string path, string hash, long sourceFileId, long bytes) =>
        new()
        {
            State = AcquisitionState.Uploaded,
            Dataset = dataset,
            Path = path,
            Sha256 = hash,
            SourceFileId = sourceFileId,
            Bytes = bytes,
            Message = $"{dataset}: uploaded {System.IO.Path.GetFileName(path)} ({bytes:N0} bytes)",
        };

    /// <summary>
    /// A probe found the file and did not record it. Distinct from
    /// <see cref="Acquired"/> because nothing happened: the point of the
    /// distinction is that checking is not consuming.
    /// </summary>
    public static AcquisitionOutcome Found(
        string dataset, string path, string hash, long bytes) =>
        new()
        {
            State = AcquisitionState.Found,
            Dataset = dataset,
            Path = path,
            Sha256 = hash,
            Bytes = bytes,
            Message =
                $"{dataset}: {System.IO.Path.GetFileName(path)} ({bytes:N0} bytes) is waiting in " +
                "the folder; nothing has been recorded or staged.",
        };

    public static AcquisitionOutcome Duplicate(
        string dataset, string path, string hash, long sourceFileId, string status) =>
        new()
        {
            State = AcquisitionState.Duplicate,
            Dataset = dataset,
            Path = path,
            Sha256 = hash,
            SourceFileId = sourceFileId,
            Message =
                $"{dataset}: {System.IO.Path.GetFileName(path)} has the same content as a file " +
                $"already received for this date (source file {sourceFileId}, {status}). The same " +
                "content twice is a duplicate, not a second run.",
        };

    public static AcquisitionOutcome NotFound(string dataset, string folder, string pattern) =>
        new()
        {
            State = AcquisitionState.NotFound,
            Dataset = dataset,
            Message = $"{dataset}: no file in {folder} matches {pattern}.",
        };

    public static AcquisitionOutcome NotConfigured(string dataset) =>
        new()
        {
            State = AcquisitionState.NotConfigured,
            Dataset = dataset,
            Message = $"{dataset} has no acquisition configured; its files are uploaded.",
        };

    public static AcquisitionOutcome Manual(string dataset) =>
        new()
        {
            State = AcquisitionState.Manual,
            Dataset = dataset,
            Message = $"{dataset} is acquired manually; nothing to fetch.",
        };
}

public enum AcquisitionState
{
    Acquired,

    /// <summary>An operator supplied the file; nothing was fetched.</summary>
    Uploaded,

    /// <summary>A probe found the file without recording or staging it.</summary>
    Found,

    /// <summary>The same content was already received for this business date.</summary>
    Duplicate,

    /// <summary>Nothing in the folder matches — the file-not-received condition.</summary>
    NotFound,

    NotConfigured,
    Manual,
}
