using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Recon.Data;

namespace Recon.Web.Services;

// CA1848 asks for LoggerMessage delegates. An install runs once and logs a
// line per script; the allocation the rule guards against does not arise.
#pragma warning disable CA1848

/// <summary>
/// Creates the database and applies the schema scripts, from the portal.
///
/// <para>
/// The scripts in <c>db/</c> are the schema — one consolidated definition, not
/// a migration chain — and they are what the test harness and the demo run
/// too. This class ships them with the application and applies them in order,
/// recording each one with the SHA-256 of its text in
/// <c>dbo.ReconSchemaScript</c>. That ledger is what makes the button
/// idempotent: a script whose hash is already recorded is not run again, and a
/// script whose text has changed since it was applied is reported rather than
/// silently re-run over live tables.
/// </para>
///
/// <para>
/// Batches are split on <c>GO</c>, which is a client instruction and not
/// T-SQL: <c>SqlCommand</c> would reject a script containing it. The splitter
/// only treats a line that is exactly <c>GO</c> as a separator, so the word
/// inside a string or a comment survives.
/// </para>
///
/// <para>
/// This is a deployment action and it is destructive in one direction only: it
/// creates and it adds. Nothing here drops a table, a database or a row —
/// re-pointing the portal at a different database is a connection-string
/// change, not a migration.
/// </para>
/// </summary>
public sealed class DatabaseInstaller(
    PlatformConfiguration configuration,
    IHostEnvironment environment,
    ILogger<DatabaseInstaller> logger)
{
    private readonly PlatformConfiguration _configuration =
        configuration ?? throw new ArgumentNullException(nameof(configuration));

    private readonly IHostEnvironment _environment =
        environment ?? throw new ArgumentNullException(nameof(environment));

    private readonly ILogger<DatabaseInstaller> _log =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// The schema, in the order it must be applied. The order is not
    /// alphabetical by accident — roles reference the schemas the first script
    /// creates, and the database options apply to a database that exists.
    /// </summary>
    private static readonly string[] SchemaScripts =
    [
        "db/01-schema.sql",
        "db/02-seed.sql",
        "db/03-roles.sql",
        "db/04-database-options.sql",
    ];

    /// <summary>
    /// The demo configuration: one counterparty, two datasets, their
    /// registries, a definition with four passes, classifications, control
    /// totals, fee schedules, reports and access grants. Re-runnable by
    /// design — it deletes its own rows first — so it is offered separately
    /// and can be applied again.
    /// </summary>
    public const string DemoScript = "demo/01-demo-config.sql";

    /// <summary>
    /// The objects the engine cannot run without.
    ///
    /// <para>
    /// Readiness is whether the schema is <b>there</b>, not whether this
    /// portal is what put it there. The ledger records what the portal
    /// applied, and gating on it meant a database built by the test harness,
    /// the demo script, a DBA running the same four files, or a restored
    /// backup was declared "not installed" and every screen redirected to
    /// setup. The portal is not the only legitimate way to build this schema
    /// and must not behave as though it were.
    /// </para>
    /// </summary>
    private static readonly string[] RequiredObjects =
    [
        "cfg.PlatformSetting",
        "cfg.Currency",
        "cfg.StorageSlotCatalogue",
        "cfg.Counterparty",
        "cfg.Dataset",
        "cfg.DatasetField",
        "cfg.FileFormatDefinition",
        "cfg.FieldMapping",
        "cfg.ReconciliationDefinition",
        "cfg.MatchRule",
        "cfg.MatchCondition",
        "cfg.UserCounterpartyAccess",
        "stg.StagingTransaction",
        "stg.ParseError",
        "ops.ReconRun",
        "ops.ReconRunStep",
        "ops.MatchResult",
        "ops.ReconException",
        "ops.RunAggregate",
        "ops.ControlTotalResult",
        "aud.AuditLog",
    ];

    public string? ResolveScript(string relativePath)
    {
        // Content root when running from the project, and the same layout
        // after publish because the csproj copies these files.
        var candidates = new[]
        {
            Path.Combine(_environment.ContentRootPath, relativePath),
            Path.Combine(AppContext.BaseDirectory, relativePath),

            // A developer running `dotnet run` from the repository root, or a
            // checkout where the web project is two levels down.
            Path.Combine(_environment.ContentRootPath, "..", "..", relativePath),
        };

        return candidates
            .Select(Path.GetFullPath)
            .FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// What the setup screen shows: whether the server answers, whether the
    /// database exists, what has been applied, and whether there is anything
    /// in it yet.
    /// </summary>
    public async Task<InstallStatus> StatusAsync(CancellationToken cancellationToken = default)
    {
        if (!_configuration.IsConfigured)
        {
            return new InstallStatus { Configured = false, Scripts = Describe(applied: null) };
        }

        var server = await PlatformConfiguration
            .TestAsync(_configuration.MasterConnectionString()!, cancellationToken)
            .ConfigureAwait(false);

        if (!server.Ok)
        {
            return new InstallStatus
            {
                Configured = true,
                ServerReachable = false,
                Message = server.Message,
                Scripts = Describe(applied: null),
            };
        }

        var database = await PlatformConfiguration
            .TestAsync(_configuration.ConnectionString!, cancellationToken)
            .ConfigureAwait(false);

        if (!database.Ok)
        {
            return new InstallStatus
            {
                Configured = true,
                ServerReachable = true,
                ServerVersion = server.Version,
                Login = server.Login,
                DatabaseExists = false,
                Message = database.Message,
                Scripts = Describe(applied: null),
            };
        }

        using var connection = new SqlConnection(_configuration.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var applied = await AppliedAsync(connection, cancellationToken).ConfigureAwait(false);

        var tables = await Db.ScalarAsync<int>(
            connection,
            """
            SELECT COUNT(*) FROM sys.tables AS t
            JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE s.name IN ('cfg', 'ops', 'stg', 'aud');
            """,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var missing = await MissingObjectsAsync(connection, cancellationToken).ConfigureAwait(false);

        var counts = missing.Count > 0
            ? new ContentCounts()
            : await CountsAsync(connection, cancellationToken).ConfigureAwait(false);

        var snapshot = await Db.ScalarAsync<bool?>(
            connection,
            "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID();",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new InstallStatus
        {
            Configured = true,
            ServerReachable = true,
            ServerVersion = server.Version,
            Edition = server.Edition,
            Login = server.Login,
            DatabaseExists = true,
            TableCount = tables,
            ReadCommittedSnapshot = snapshot ?? false,
            MissingObjects = missing,
            Scripts = Describe(applied, schemaPresent: missing.Count == 0),
            Counts = counts,
        };
    }

    /// <summary>
    /// Which required objects are absent. Named individually rather than
    /// counted, because "cfg.FieldMapping is missing" tells an operator which
    /// script did not finish and a number does not.
    /// </summary>
    private static async Task<List<string>> MissingObjectsAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        var present = await Db.QueryAsync(
            connection,
            """
            SELECT s.name + '.' + t.name
            FROM sys.tables AS t
            JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            WHERE s.name IN ('cfg', 'ops', 'stg', 'aud');
            """,
            r => r.GetString(0),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var set = present.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return RequiredObjects.Where(o => !set.Contains(o)).ToList();
    }

    /// <summary>
    /// Records the schema scripts as applied without running them, for a
    /// database that was built outside the portal.
    ///
    /// <para>
    /// The ledger is then true about what is in the database and honest about
    /// how it got there: the entry says "adopted", because the portal did not
    /// run these files and cannot know that the text it has is the text that
    /// was applied. It refuses when the objects are not actually present —
    /// that would be a ledger claiming a schema that is not there.
    /// </para>
    /// </summary>
    public async Task<InstallResult> AdoptAsync(
        string performedBy, CancellationToken cancellationToken = default)
    {
        using var connection = new SqlConnection(_configuration.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var missing = await MissingObjectsAsync(connection, cancellationToken).ConfigureAwait(false);

        if (missing.Count > 0)
        {
            return InstallResult.Failed(
                "The schema is not complete, so there is nothing to adopt: missing "
                + string.Join(", ", missing.Take(5))
                + (missing.Count > 5 ? $" and {missing.Count - 5} more" : string.Empty)
                + ". Install instead.");
        }

        await EnsureLedgerAsync(connection, cancellationToken).ConfigureAwait(false);
        var applied = await AppliedAsync(connection, cancellationToken).ConfigureAwait(false);

        var log = new List<string>();

        foreach (var relative in SchemaScripts)
        {
            if (applied.ContainsKey(relative))
            {
                continue;
            }

            var path = ResolveScript(relative);
            if (path is null)
            {
                continue;
            }

            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

            await RecordAsync(connection, relative, Hash(text),
                SplitBatches(text).Count, performedBy + " (adopted)", cancellationToken)
                .ConfigureAwait(false);

            log.Add($"recorded {relative} as already applied");
        }

        return log.Count > 0
            ? InstallResult.Succeeded(log)
            : InstallResult.Succeeded(["every script was already recorded"]);
    }

    /// <summary>
    /// Creates the database if it is missing, then applies whatever the ledger
    /// says has not been applied.
    /// </summary>
    public async Task<InstallResult> InstallAsync(
        string performedBy, CancellationToken cancellationToken = default)
    {
        if (!_configuration.IsConfigured)
        {
            return InstallResult.Failed("No connection string is configured yet.");
        }

        var databaseName = _configuration.DatabaseName;
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            return InstallResult.Failed(
                "The connection string names no database. It needs an Initial Catalog.");
        }

        var log = new List<string>();

        try
        {
            // CREATE DATABASE cannot run inside the database it creates, so
            // this one statement goes to master.
            using (var master = new SqlConnection(_configuration.MasterConnectionString()))
            {
                await master.OpenAsync(cancellationToken).ConfigureAwait(false);

                var exists = await Db.ScalarAsync<int>(
                    master,
                    "SELECT COUNT(*) FROM sys.databases WHERE name = @name;",
                    c => c.With("@name", databaseName),
                    cancellationToken).ConfigureAwait(false);

                if (exists == 0)
                {
                    // QUOTENAME, not interpolation: the name comes from a form.
                    // It cannot reach the statement as text.
                    await Db.ExecuteAsync(
                        master,
                        """
                        DECLARE @sql NVARCHAR(300) = N'CREATE DATABASE ' + QUOTENAME(@name) + N';';
                        EXEC sp_executesql @sql;
                        """,
                        c => c.With("@name", databaseName),
                        cancellationToken).ConfigureAwait(false);

                    log.Add($"created database [{databaseName}]");
                    _log.LogInformation("Created database {Database}.", databaseName);

                    // Every failed attempt to open a database that did not
                    // exist yet leaves a cached login failure in the pool.
                    // Without clearing it, the connection below is answered
                    // from that cache — the install is told by its own client
                    // library that the database it has just created does not
                    // exist. The connection strings this portal builds also
                    // set PoolBlockingPeriod=NeverBlock; this covers the ones
                    // supplied by a host, which do not.
                    SqlConnection.ClearAllPools();
                }
                else
                {
                    log.Add($"database [{databaseName}] already exists");
                }
            }

            using var connection = new SqlConnection(_configuration.ConnectionString);
            await OpenWithRetryAsync(connection, cancellationToken).ConfigureAwait(false);

            await EnsureLedgerAsync(connection, cancellationToken).ConfigureAwait(false);
            var applied = await AppliedAsync(connection, cancellationToken).ConfigureAwait(false);

            foreach (var relative in SchemaScripts)
            {
                var path = ResolveScript(relative);

                if (path is null)
                {
                    return InstallResult.Failed(
                        $"{relative} is missing from the deployment. The schema scripts ship with " +
                        "the application; without them the portal cannot build its own database.",
                        log);
                }

                var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                var hash = Hash(text);

                if (applied.TryGetValue(relative, out var record))
                {
                    log.Add(record.Hash == hash
                        ? $"{relative} already applied"
                        : $"{relative} already applied, but its text has CHANGED since — " +
                          "not re-run, because re-running a consolidated schema over live tables " +
                          "is not an upgrade path");

                    continue;
                }

                var batches = SplitBatches(text);
                await ApplyAsync(connection, relative, batches, cancellationToken).ConfigureAwait(false);

                await RecordAsync(connection, relative, hash, batches.Count, performedBy,
                    cancellationToken).ConfigureAwait(false);

                log.Add($"applied {relative} ({batches.Count} batches)");
                _log.LogInformation("Applied {Script} in {Batches} batches.", relative, batches.Count);
            }

            return InstallResult.Succeeded(log);
        }
        catch (SqlException ex)
        {
            // The server's message names the statement that failed, which is
            // what an operator needs. A half-applied script leaves its ledger
            // row unwritten, so the next attempt retries it.
            _log.LogError(ex, "Install failed.");
            return InstallResult.Failed(ex.Message, log);
        }
        catch (IOException ex)
        {
            return InstallResult.Failed(ex.Message, log);
        }
    }

    /// <summary>
    /// Applies the demo configuration, and grants the caller Configure access
    /// to what it creates — otherwise the person who just seeded the platform
    /// would open it and see nothing.
    /// </summary>
    public async Task<InstallResult> SeedDemoAsync(
        string performedBy, CancellationToken cancellationToken = default)
    {
        var path = ResolveScript(DemoScript);

        if (path is null)
        {
            return InstallResult.Failed($"{DemoScript} is missing from the deployment.");
        }

        var log = new List<string>();

        try
        {
            using var connection = new SqlConnection(_configuration.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var batches = SplitBatches(text);

            await ApplyAsync(connection, DemoScript, batches, cancellationToken).ConfigureAwait(false);
            await EnsureLedgerAsync(connection, cancellationToken).ConfigureAwait(false);
            await RecordAsync(connection, DemoScript, Hash(text), batches.Count, performedBy,
                cancellationToken).ConfigureAwait(false);

            log.Add("applied the demo configuration");

            var granted = await GrantEverythingAsync(connection, performedBy, cancellationToken)
                .ConfigureAwait(false);

            log.Add(granted == 0
                ? $"{performedBy} already had access"
                : $"granted {performedBy} Configure access to {granted} counterparty(ies)");

            return InstallResult.Succeeded(log);
        }
        catch (SqlException ex)
        {
            return InstallResult.Failed(ex.Message, log);
        }
        catch (IOException ex)
        {
            return InstallResult.Failed(ex.Message, log);
        }
    }

    /// <summary>
    /// The first-run escape hatch: grants the caller Configure on every
    /// counterparty that has no administrator.
    ///
    /// <para>
    /// Access is scoped per counterparty and a new account holds nothing,
    /// which is the correct default — but it also means a freshly installed
    /// platform has nobody who can grant anything. This is how the first
    /// person in gets in, and it is deliberately narrow: it only ever adds
    /// Configure where no Configure grant exists, so it cannot be used to
    /// break into a counterparty somebody already administers.
    /// </para>
    /// </summary>
    public async Task<int> ClaimUnadministeredAsync(
        string performedBy, CancellationToken cancellationToken = default)
    {
        using var connection = new SqlConnection(_configuration.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // SELECT @@ROWCOUNT rather than rows affected, for the same reason as
        // above: a script may have left SET NOCOUNT ON on this connection,
        // and this count decides what the operator is told.
        return await Db.ScalarAsync<int>(
            connection,
            """
            SET NOCOUNT ON;

            INSERT cfg.UserCounterpartyAccess (UserName, CounterpartyId, AccessLevel, GrantedBy)
            SELECT @user, c.CounterpartyId, 'Configure', @user
            FROM cfg.Counterparty AS c
            WHERE NOT EXISTS (
                      SELECT 1 FROM cfg.UserCounterpartyAccess AS a
                      WHERE a.CounterpartyId = c.CounterpartyId AND a.AccessLevel = 'Configure')
              AND NOT EXISTS (
                      SELECT 1 FROM cfg.UserCounterpartyAccess AS a
                      WHERE a.CounterpartyId = c.CounterpartyId AND a.UserName = @user);

            SELECT @@ROWCOUNT;
            """,
            c => c.With("@user", performedBy),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Grants the caller access to every counterparty they do not already
    /// hold.
    ///
    /// <para>
    /// The count comes from <c>SELECT @@ROWCOUNT</c> rather than from rows
    /// affected, because the script that ran just before this on the same
    /// connection left <c>SET NOCOUNT ON</c> in effect — and with NOCOUNT on,
    /// <c>ExecuteNonQuery</c> returns −1. The seed reported "granted access
    /// to −1 counterparty(ies)", and the claim button, which decides its
    /// message from the same number, would have said "nothing to claim" while
    /// granting something.
    /// </para>
    /// </summary>
    private static async Task<int> GrantEverythingAsync(
        SqlConnection connection, string performedBy, CancellationToken cancellationToken) =>
        await Db.ScalarAsync<int>(
            connection,
            """
            SET NOCOUNT ON;

            INSERT cfg.UserCounterpartyAccess (UserName, CounterpartyId, AccessLevel, GrantedBy)
            SELECT @user, c.CounterpartyId, 'Configure', @user
            FROM cfg.Counterparty AS c
            WHERE NOT EXISTS (
                SELECT 1 FROM cfg.UserCounterpartyAccess AS a
                WHERE a.CounterpartyId = c.CounterpartyId AND a.UserName = @user);

            SELECT @@ROWCOUNT;
            """,
            c => c.With("@user", performedBy),
            cancellationToken).ConfigureAwait(false);

    // =================================================================
    // The ledger
    // =================================================================

    /// <summary>
    /// Opens the connection, retrying briefly.
    ///
    /// <para>
    /// A database that has just been created is normally available at once,
    /// but "normally" is not a guarantee on a busy server, and a two-second
    /// wait is a better answer than telling an operator their install failed.
    /// Three attempts, then the server's own error.
    /// </para>
    /// </summary>
    private static async Task OpenWithRetryAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (SqlException ex) when (attempt < 3 && ex.Number is 4060 or 911)
            {
                SqlConnection.ClearAllPools();
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// In <c>dbo</c> rather than <c>cfg</c> on purpose: it must exist before
    /// the script that creates the <c>cfg</c> schema runs, and it is a record
    /// of deployment rather than platform configuration.
    /// </summary>
    private static Task<int> EnsureLedgerAsync(
        SqlConnection connection, CancellationToken cancellationToken) =>
        Db.ExecuteAsync(
            connection,
            """
            IF OBJECT_ID('dbo.ReconSchemaScript') IS NULL
            BEGIN
                CREATE TABLE dbo.ReconSchemaScript (
                    ScriptName   VARCHAR(200)  NOT NULL PRIMARY KEY,
                    Sha256       CHAR(64)      NOT NULL,
                    BatchCount   INT           NOT NULL,
                    AppliedAt    DATETIME2(3)  NOT NULL DEFAULT SYSDATETIME(),
                    AppliedBy    NVARCHAR(300) NOT NULL
                );
            END
            """,
            cancellationToken: cancellationToken);

    private static async Task<Dictionary<string, AppliedScript>> AppliedAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        var exists = await Db.ScalarAsync<int>(
            connection,
            "SELECT CASE WHEN OBJECT_ID('dbo.ReconSchemaScript') IS NULL THEN 0 ELSE 1 END;",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (exists == 0)
        {
            return [];
        }

        var rows = await Db.QueryAsync(
            connection,
            """
            SELECT ScriptName, Sha256, BatchCount, AppliedAt, AppliedBy
            FROM dbo.ReconSchemaScript ORDER BY ScriptName;
            """,
            r => new AppliedScript(
                r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetDateTime(3), r.GetString(4)),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return rows.ToDictionary(r => r.Name, StringComparer.Ordinal);
    }

    private static Task<int> RecordAsync(
        SqlConnection connection,
        string name,
        string hash,
        int batches,
        string performedBy,
        CancellationToken cancellationToken) =>
        Db.ExecuteAsync(
            connection,
            """
            UPDATE dbo.ReconSchemaScript
            SET Sha256 = @hash, BatchCount = @batches, AppliedAt = SYSDATETIME(), AppliedBy = @by
            WHERE ScriptName = @name;

            IF @@ROWCOUNT = 0
                INSERT dbo.ReconSchemaScript (ScriptName, Sha256, BatchCount, AppliedBy)
                VALUES (@name, @hash, @batches, @by);
            """,
            c => c.With("@name", name)
                  .With("@hash", hash)
                  .With("@batches", batches)
                  .With("@by", performedBy),
            cancellationToken);

    private List<ScriptState> Describe(
        Dictionary<string, AppliedScript>? applied, bool schemaPresent = false)
    {
        var states = new List<ScriptState>();

        foreach (var relative in SchemaScripts.Append(DemoScript))
        {
            var path = ResolveScript(relative);
            var hash = path is null ? null : Hash(File.ReadAllText(path));

            AppliedScript? record = null;
            var found = applied is not null && applied.TryGetValue(relative, out record);

            states.Add(new ScriptState
            {
                Name = relative,
                Present = path is not null,
                Optional = relative == DemoScript,
                Applied = found,
                AppliedAt = found ? record!.AppliedAt : null,
                AppliedBy = found ? record!.AppliedBy : null,
                Changed = found && hash is not null && record!.Hash != hash,

                // The objects exist but this portal has no record of applying
                // them: the schema was built by the test harness, the demo
                // script, a DBA or a restored backup. A true statement about
                // the ledger, not a problem with the database.
                AppliedElsewhere = !found && schemaPresent && relative != DemoScript,
            });
        }

        return states;
    }

    private static async Task<ContentCounts> CountsAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        var rows = await Db.QueryAsync(
            connection,
            """
            SELECT (SELECT COUNT(*) FROM cfg.Counterparty),
                   (SELECT COUNT(*) FROM cfg.Dataset),
                   (SELECT COUNT(*) FROM cfg.ReconciliationDefinition),
                   (SELECT COUNT(*) FROM cfg.UserCounterpartyAccess),
                   (SELECT COUNT(*) FROM ops.ReconRun),
                   (SELECT COUNT(*) FROM cfg.PlatformSetting);
            """,
            r => new ContentCounts
            {
                Counterparties = r.GetInt32(0),
                Datasets = r.GetInt32(1),
                Definitions = r.GetInt32(2),
                Grants = r.GetInt32(3),
                Runs = r.GetInt32(4),
                Settings = r.GetInt32(5),
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return rows.Count > 0 ? rows[0] : new ContentCounts();
    }

    // =================================================================
    // Script execution
    // =================================================================

    private static async Task ApplyAsync(
        SqlConnection connection,
        string name,
        List<string> batches,
        CancellationToken cancellationToken)
    {
        // A script sets session options — SET NOCOUNT ON, SET ANSI_NULLS ON —
        // and they outlive it on a pooled connection. Anything the portal runs
        // next on this connection inherits them, which is how a grant that
        // inserted one row reported −1. Reset after applying rather than
        // hoping every caller allows for it.
        try
        {
            await ApplyBatchesAsync(connection, name, batches, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            using var reset = Db.Command(connection, "SET NOCOUNT OFF;");
            await reset.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ApplyBatchesAsync(
        SqlConnection connection,
        string name,
        List<string> batches,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < batches.Count; i++)
        {
            using var command = Db.Command(connection, batches[i], timeoutSeconds: 600);

            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqlException ex)
            {
                // Which batch, and the first line of it: "Msg 2714 near line
                // 4711 of a 1300-line file" is not something anyone can act
                // on without this.
                var firstLine = batches[i].Split('\n')
                    .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? string.Empty;

                throw new SqlInstallException(
                    string.Create(CultureInfo.InvariantCulture,
                        $"{name}, batch {i + 1} of {batches.Count} (starting \"{firstLine}\"): {ex.Message}"),
                    ex);
            }
        }
    }

    /// <summary>
    /// Splits on <c>GO</c>, which is a client instruction rather than T-SQL:
    /// <c>SqlCommand</c> rejects a script containing it. Only a line that is
    /// exactly <c>GO</c> (with optional whitespace and an optional trailing
    /// semicolon) separates a batch, so the word inside a string literal or a
    /// comment is left alone.
    /// </summary>
    internal static List<string> SplitBatches(string script)
    {
        ArgumentNullException.ThrowIfNull(script);

        var batches = new List<string>();
        var current = new StringBuilder();

        foreach (var line in script.Split('\n'))
        {
            var trimmed = line.Trim().TrimEnd(';').Trim();

            if (string.Equals(trimmed, "GO", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                continue;
            }

            current.AppendLine(line);
        }

        Flush();
        return batches;

        void Flush()
        {
            var text = current.ToString();
            current.Clear();

            // A batch of only comments and whitespace is not a statement, and
            // sending one is an error rather than a no-op.
            if (HasStatement(text))
            {
                batches.Add(text);
            }
        }
    }

    /// <summary>
    /// Whether a batch contains anything but whitespace and comments. Written
    /// as a small scanner rather than a regular expression because a
    /// <c>/*</c> inside a string literal must not open a comment.
    /// </summary>
    internal static bool HasStatement(string batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var i = 0;
        var depth = 0;

        while (i < batch.Length)
        {
            var c = batch[i];

            if (depth > 0)
            {
                if (c == '*' && i + 1 < batch.Length && batch[i + 1] == '/')
                {
                    depth--;
                    i += 2;
                    continue;
                }

                if (c == '/' && i + 1 < batch.Length && batch[i + 1] == '*')
                {
                    depth++;
                    i += 2;
                    continue;
                }

                i++;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '-' && i + 1 < batch.Length && batch[i + 1] == '-')
            {
                while (i < batch.Length && batch[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < batch.Length && batch[i + 1] == '*')
            {
                depth++;
                i += 2;
                continue;
            }

            return true;
        }

        return false;
    }

    internal static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private sealed record AppliedScript(
        string Name, string Hash, int Batches, DateTime AppliedAt, string AppliedBy);
}

public sealed class SqlInstallException(string message, Exception inner) : Exception(message, inner);

public sealed record ScriptState
{
    public required string Name { get; init; }
    public required bool Present { get; init; }
    public required bool Optional { get; init; }
    public required bool Applied { get; init; }
    public DateTime? AppliedAt { get; init; }
    public string? AppliedBy { get; init; }

    /// <summary>
    /// The file's text differs from what was applied. Reported rather than
    /// acted on: re-running a consolidated schema over live tables is not an
    /// upgrade path, and pretending otherwise is how a deployment tool
    /// destroys data.
    /// </summary>
    public required bool Changed { get; init; }

    /// <summary>
    /// The objects are there but this portal did not record applying them —
    /// the database was built by the test harness, the demo script, a DBA
    /// running the same files, or a restored backup.
    /// </summary>
    public bool AppliedElsewhere { get; init; }
}

public sealed record ContentCounts
{
    public int Counterparties { get; init; }
    public int Datasets { get; init; }
    public int Definitions { get; init; }
    public int Grants { get; init; }
    public int Runs { get; init; }
    public int Settings { get; init; }

    public bool IsEmpty => Counterparties == 0 && Datasets == 0 && Definitions == 0;
}

public sealed record InstallStatus
{
    public required bool Configured { get; init; }
    public bool ServerReachable { get; init; }
    public string? ServerVersion { get; init; }
    public string? Edition { get; init; }
    public string? Login { get; init; }
    public bool DatabaseExists { get; init; }
    public int TableCount { get; init; }
    public bool ReadCommittedSnapshot { get; init; }
    public string? Message { get; init; }
    public required List<ScriptState> Scripts { get; init; }
    public ContentCounts Counts { get; init; } = new();

    /// <summary>
    /// Which of the objects the engine needs are absent.
    /// </summary>
    public List<string> MissingObjects { get; init; } = [];

    /// <summary>
    /// Ready means the schema is <b>there</b> — not that this portal is what
    /// put it there. Gating on the portal's own ledger declared a database
    /// built by the test harness or the demo script "not installed" and sent
    /// every screen to setup.
    ///
    /// <para>
    /// It deliberately does not require the demo configuration: an empty
    /// platform is a valid state, it is just not a useful screen yet.
    /// </para>
    /// </summary>
    public bool Ready =>
        Configured && ServerReachable && DatabaseExists && MissingObjects.Count == 0;

    /// <summary>
    /// The schema is present but the portal has no record of applying it.
    /// Worth saying, and worth offering to record, but not a problem.
    /// </summary>
    public bool SchemaUnrecorded =>
        Ready && Scripts.Any(s => s.AppliedElsewhere);

    public bool NeedsInstall =>
        Configured && ServerReachable && (!DatabaseExists || !Ready);
}

public sealed record InstallResult
{
    public required bool Ok { get; init; }
    public required string Message { get; init; }
    public required List<string> Log { get; init; }

    public static InstallResult Succeeded(List<string> log) =>
        new() { Ok = true, Message = "done", Log = log };

    public static InstallResult Failed(string message, List<string>? log = null) =>
        new() { Ok = false, Message = message, Log = log ?? [] };
}
#pragma warning restore CA1848
