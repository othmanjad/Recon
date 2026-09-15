using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;

namespace Recon.Web.Services;

// CA1848 asks for LoggerMessage delegates. This class logs when settings are
// saved and when the settings file cannot be read — once each, not per
// request — so the allocation the rule guards against does not arise.
#pragma warning disable CA1848

/// <summary>
/// The connection string and the runtime switches, owned by the portal rather
/// than by whoever edits <c>appsettings.json</c>.
///
/// <para>
/// This exists because "configure the connection string in a file, then
/// restart" is not something an operator can be asked to do on a machine they
/// did not deploy. The portal starts with no database at all, asks for one,
/// tests it, writes it down, and carries on — and the same screen can point it
/// at a different server later without a redeploy.
/// </para>
///
/// <para>
/// The file lives outside <c>appsettings.json</c> on purpose. Merging a
/// user-edited value into the file the repository ships means every future
/// deployment either overwrites the operator's setting or refuses to update.
/// <c>RECON_CONFIG_DIR</c> moves it — which is how a test can run against a
/// clean slate, and how a container keeps it on a mounted volume.
/// </para>
///
/// <para>
/// <b>The stored connection string contains a password in plain text</b> when
/// SQL authentication is used, and the file is written with owner-only
/// permissions where the platform supports it. That is the honest limit of a
/// single-node deployment with no secret store: a deployment with one should
/// set <c>ConnectionStrings:Recon</c> from it, which this class treats as
/// authoritative and never overwrites. Integrated Security avoids the problem
/// entirely and the form offers it first.
/// </para>
/// </summary>
public sealed class PlatformConfiguration
{
    private const string FileName = "recon.settings.json";

    private readonly string _path;
    private readonly string? _fromConfiguration;
    private readonly ILogger<PlatformConfiguration> _log;
    private readonly object _gate = new();

    private StoredSettings _settings;

    public PlatformConfiguration(
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<PlatformConfiguration> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        _log = logger ?? throw new ArgumentNullException(nameof(logger));

        var directory = Environment.GetEnvironmentVariable("RECON_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = environment.ContentRootPath;
        }

        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, FileName);

        // A connection string supplied by the host wins and is never written
        // over: a deployment that injects one from a secret store or an
        // environment variable has said where the truth lives.
        var supplied = configuration.GetConnectionString("Recon");
        _fromConfiguration = string.IsNullOrWhiteSpace(supplied) ? null : supplied;

        _settings = Load(_path, _log);
    }

    /// <summary>Where the settings file is, for the setup screen to show.</summary>
    public string SettingsPath => _path;

    /// <summary>
    /// True when the host supplied the connection string, in which case the
    /// setup screen shows it as read-only rather than offering to change
    /// something it cannot.
    /// </summary>
    public bool IsFromHost => _fromConfiguration is not null;

    public string? ConnectionString => _fromConfiguration ?? _settings.ConnectionString;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString);

    /// <summary>
    /// Whether the in-process scheduler should run. Stored here rather than in
    /// <c>appsettings.json</c> so that it can be switched off from the portal
    /// while a problem is being investigated, without a restart.
    /// </summary>
    public bool SchedulerEnabled => _settings.SchedulerEnabled;

    /// <summary>Where uploaded and acquired files are kept. Never the database.</summary>
    public string StorageRoot => string.IsNullOrWhiteSpace(_settings.StorageRoot)
        ? Path.Combine(Path.GetDirectoryName(_path)!, "files")
        : _settings.StorageRoot;

    public string? DatabaseName =>
        ConnectionString is { } connectionString && TryParse(connectionString, out var builder)
            ? builder!.InitialCatalog
            : null;

    public string? ServerName =>
        ConnectionString is { } connectionString && TryParse(connectionString, out var builder)
            ? builder!.DataSource
            : null;

    /// <summary>
    /// A connection string for the <c>master</c> database of the same server,
    /// which is where <c>CREATE DATABASE</c> has to run.
    /// </summary>
    public string? MasterConnectionString()
    {
        if (ConnectionString is not { } connectionString || !TryParse(connectionString, out var builder))
        {
            return null;
        }

        builder!.InitialCatalog = "master";
        return builder.ConnectionString;
    }

    public void Save(string connectionString, string? storageRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        lock (_gate)
        {
            _settings = _settings with
            {
                ConnectionString = connectionString,
                StorageRoot = string.IsNullOrWhiteSpace(storageRoot) ? _settings.StorageRoot : storageRoot,
            };

            Write();
        }
    }

    public void SetSchedulerEnabled(bool enabled)
    {
        lock (_gate)
        {
            _settings = _settings with { SchedulerEnabled = enabled };
            Write();
        }
    }

    /// <summary>
    /// Opens a connection and reads the server's version, so the answer is
    /// "this server, this database" and not merely "no exception".
    /// </summary>
    public static async Task<ConnectionTest> TestAsync(
        string connectionString, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT @@VERSION, DB_NAME(), SUSER_SNAME(), " +
                "CAST(SERVERPROPERTY('Edition') AS NVARCHAR(200));";

            using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new ConnectionTest { Ok = false, Message = "the server answered nothing" };
            }

            var version = reader.GetString(0).Split('\n')[0].Trim();

            return new ConnectionTest
            {
                Ok = true,
                Version = version,
                Database = reader.IsDBNull(1) ? null : reader.GetString(1),
                Login = reader.IsDBNull(2) ? null : reader.GetString(2),
                Edition = reader.IsDBNull(3) ? null : reader.GetString(3),
                Message = "connected",
            };
        }
        catch (SqlException ex)
        {
            // The server's own message names the actual problem — a wrong
            // password, a database that does not exist, a firewall — and a
            // paraphrase would be less useful.
            return new ConnectionTest
            {
                Ok = false,
                Message = ex.Message,
                // 4060: cannot open the named database. It is the one failure
                // the setup screen can fix by itself, so it is called out.
                DatabaseMissing = ex.Errors.Cast<SqlError>().Any(e => e.Number is 4060 or 911),
            };
        }
        catch (InvalidOperationException ex)
        {
            return new ConnectionTest { Ok = false, Message = ex.Message };
        }
    }

    /// <summary>
    /// Builds a connection string from the parts the form collects, so that a
    /// user never has to type one — and so that the parts the platform
    /// requires (<c>MultipleActiveResultSets</c> off, a real application name)
    /// are not left to chance.
    /// </summary>
    public static string Build(
        string server,
        string database,
        bool integratedSecurity,
        string? userId,
        string? password,
        bool encrypt,
        bool trustServerCertificate)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            IntegratedSecurity = integratedSecurity,
            Encrypt = encrypt,
            TrustServerCertificate = trustServerCertificate,
            ApplicationName = "Recon Portal",

            // A request uses one connection and one reader at a time; the
            // engine's checkpointing depends on a run and its steps sharing
            // one session, which is also why the application lock works.
            MultipleActiveResultSets = false,

            // Long enough for a schema install over a slow link, short enough
            // that a wrong server name is a message rather than a wait.
            ConnectTimeout = 15,

            // The portal deliberately connects to a database that does not
            // exist yet — that is the state it is in between saving a
            // connection and installing. With the default blocking period,
            // SqlClient caches that login failure for five seconds and
            // answers the NEXT attempt from the cache without contacting the
            // server: the install created the database and was then told, by
            // its own client library, that the database it had just created
            // did not exist.
            PoolBlockingPeriod = PoolBlockingPeriod.NeverBlock,
        };

        if (!integratedSecurity)
        {
            builder.UserID = userId ?? string.Empty;
            builder.Password = password ?? string.Empty;
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// The connection string with the password replaced, for display and for
    /// the log. Nothing that renders a connection string may render the
    /// original.
    /// </summary>
    public static string Redact(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString) || !TryParse(connectionString, out var builder))
        {
            return string.Empty;
        }

        if (!string.IsNullOrEmpty(builder!.Password))
        {
            builder.Password = "********";
        }

        return builder.ConnectionString;
    }

    private static bool TryParse(string connectionString, out SqlConnectionStringBuilder? builder)
    {
        try
        {
            builder = new SqlConnectionStringBuilder(connectionString);
            return true;
        }
        catch (ArgumentException)
        {
            builder = null;
            return false;
        }
    }

    private static StoredSettings Load(string path, ILogger logger)
    {
        if (!File.Exists(path))
        {
            return new StoredSettings();
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<StoredSettings>(json) ?? new StoredSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt settings file must not stop the portal from starting:
            // starting is how the operator gets to the screen that fixes it.
            logger.LogError(ex, "Could not read {Path}; starting unconfigured.", path);
            return new StoredSettings();
        }
    }

    private void Write()
    {
        var json = JsonSerializer.Serialize(_settings, SerializerOptions);

        // Written to a temporary file and moved, so a crash halfway through
        // cannot leave the portal with half a connection string.
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, json);

        if (!OperatingSystem.IsWindows())
        {
            // Owner read/write only: the file holds a password.
            File.SetUnixFileMode(temporary,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temporary, _path, overwrite: true);

        _log.LogInformation("Saved platform settings to {Path}.", _path);
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record StoredSettings
    {
        public string? ConnectionString { get; init; }
        public bool SchedulerEnabled { get; init; } = true;
        public string? StorageRoot { get; init; }
    }
}

public sealed record ConnectionTest
{
    public required bool Ok { get; init; }
    public required string Message { get; init; }
    public string? Version { get; init; }
    public string? Database { get; init; }
    public string? Login { get; init; }
    public string? Edition { get; init; }

    /// <summary>
    /// The server answered but the named database does not exist — the one
    /// failure the setup screen can fix itself.
    /// </summary>
    public bool DatabaseMissing { get; init; }
}
#pragma warning restore CA1848
