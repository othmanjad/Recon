using Microsoft.Data.SqlClient;
using Xunit;

namespace Recon.IntegrationTests;

/// <summary>
/// A live SQL Server, with the schema built from <c>db/</c>.
///
/// <para>
/// The schema scripts are executed rather than duplicated here. If a script and
/// the engine drift apart, these tests fail — which is the point: the unit
/// tests prove the compiler emits what it intends, and only a real server
/// proves the server agrees.
/// </para>
///
/// <para>
/// Set <c>RECON_TEST_CONNECTION</c> to point at an instance. Without it the
/// whole collection is skipped rather than failing, so <c>dotnet test</c> on a
/// machine with no database still passes the unit suite.
/// </para>
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private const string DatabaseName = "ReconEngineTests";

    public string? ConnectionString { get; private set; }

    public bool Available => ConnectionString is not null;

    public string SkipReason { get; private set; } =
        "RECON_TEST_CONNECTION is not set; start SQL Server (./db/tests/run.sh) and set it.";

    public async Task InitializeAsync()
    {
        var master = Environment.GetEnvironmentVariable("RECON_TEST_CONNECTION");

        if (string.IsNullOrWhiteSpace(master))
        {
            return;
        }

        try
        {
            await BuildDatabaseAsync(master).ConfigureAwait(false);

            var builder = new SqlConnectionStringBuilder(master)
            {
                InitialCatalog = DatabaseName,
            };

            ConnectionString = builder.ConnectionString;
        }
        catch (SqlException ex)
        {
            SkipReason = "could not prepare the test database: " + ex.Message;
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    private static async Task BuildDatabaseAsync(string masterConnectionString)
    {
        using var connection = new SqlConnection(masterConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await ExecuteAsync(connection,
            $"""
            IF DB_ID('{DatabaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{DatabaseName}];
            END;
            CREATE DATABASE [{DatabaseName}];
            """).ConfigureAwait(false);

        var builder = new SqlConnectionStringBuilder(masterConnectionString)
        {
            InitialCatalog = DatabaseName,
        };

        using var db = new SqlConnection(builder.ConnectionString);
        await db.OpenAsync().ConfigureAwait(false);

        foreach (var script in new[] { "01-schema.sql", "02-seed.sql", "03-roles.sql" })
        {
            var path = Path.Combine(RepositoryRoot(), "db", script);
            await RunBatchesAsync(db, await File.ReadAllTextAsync(path).ConfigureAwait(false))
                .ConfigureAwait(false);
        }

        // A5. The test database is created fresh, so there is no maintenance
        // window to worry about.
        await ExecuteAsync(db,
            $"ALTER DATABASE [{DatabaseName}] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;")
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Splits on <c>GO</c> and executes each batch. <c>GO</c> is a client
    /// directive, not T-SQL, so a driver cannot send a script containing it.
    /// </summary>
    private static async Task RunBatchesAsync(SqlConnection connection, string script)
    {
        var batches = System.Text.RegularExpressions.Regex.Split(
            script, @"(?im)^\s*GO\s*$", System.Text.RegularExpressions.RegexOptions.Multiline);

        foreach (var batch in batches)
        {
            if (string.IsNullOrWhiteSpace(batch))
            {
                continue;
            }

            await ExecuteAsync(connection, batch).ConfigureAwait(false);
        }
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 300;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Recon.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("could not locate the repository root from the test output directory");
    }
}

// CA1711 objects to the 'Collection' suffix. xUnit's collection-definition
// convention is exactly that suffix, and renaming the type to satisfy the
// analyser would make the fixture harder to find for anyone who knows xUnit.
#pragma warning disable CA1711
[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sqlserver";
}
#pragma warning restore CA1711
