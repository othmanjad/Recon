using System.Globalization;
using Microsoft.Data.SqlClient;
using Recon.Data;
using Recon.Engine.Scheduling;

namespace Recon.Web.Services;

/// <summary>
/// The jobs that keep the platform healthy, and the alert conditions it
/// detects.
///
/// <para>
/// Extracted from the scheduler so that the portal can run them on demand. An
/// operator investigating a partition warning at 11am should not have to wait
/// for tomorrow's tick, and a job that can only be triggered by the clock
/// cannot be tested from the outside. The scheduler calls exactly these
/// methods, so "run it now" and "the scheduler ran it" cannot come to mean
/// two different things.
/// </para>
///
/// <para>
/// Each job corresponds to a review item that would otherwise become a
/// production incident: the sandbox purge is E5 (sandbox runs write to
/// production staging and would accumulate 2M rows per experiment), the
/// partition lookahead is E2 (with no future partition at month rollover
/// every row lands in the last range and the sliding window breaks), and the
/// file-not-received check is what makes a session that never arrived
/// distinguishable from one that is not due yet.
/// </para>
/// </summary>
public sealed class MaintenanceService(SqlConnection connection)
{
    private readonly SqlConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>
    /// Detects the alert conditions and raises what it finds. Returns the
    /// messages raised, so a caller can show them rather than only log them.
    /// </summary>
    public async Task<List<string>> RaiseAlertsAsync(CancellationToken cancellationToken = default)
    {
        var detector = new AlertDetector(_connection);
        var alerts = new AlertService(_connection, [new DashboardAlertChannel()]);

        var settings = await new ConfigurationRepository(_connection)
            .LoadSettingsAsync(cancellationToken).ConfigureAwait(false);

        var threshold = settings.TryGetValue("MatchDriftThresholdPct", out var raw)
            && decimal.TryParse(raw, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 15m;

        var raised = new List<string>();

        foreach (var alert in await detector
            .MatchDistributionDriftAsync(threshold, cancellationToken).ConfigureAwait(false))
        {
            await alerts.RaiseAsync(alert, cancellationToken).ConfigureAwait(false);
            raised.Add(alert.Message);
        }

        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));

        foreach (var alert in await detector
            .FileNotReceivedAsync(yesterday, cancellationToken).ConfigureAwait(false))
        {
            await alerts.RaiseAsync(alert, cancellationToken).ConfigureAwait(false);
            raised.Add(alert.Message);
        }

        var shortage = await detector
            .PartitionShortageAsync(Setting(settings, "FuturePartitionsAlertAt", 2), cancellationToken)
            .ConfigureAwait(false);

        if (shortage is not null)
        {
            await alerts.RaiseAsync(shortage, cancellationToken).ConfigureAwait(false);
            raised.Add(shortage.Message);
        }

        return raised;
    }

    /// <summary>
    /// The daily jobs. Returns a line per job, whether or not it did
    /// anything: "purged nothing" is a useful answer and silence is not.
    /// </summary>
    public async Task<List<string>> HousekeepingAsync(CancellationToken cancellationToken = default)
    {
        var settings = await new ConfigurationRepository(_connection)
            .LoadSettingsAsync(cancellationToken).ConfigureAwait(false);

        var log = new List<string>();

        // E5: sandbox runs write to production staging. Left alone they would
        // accumulate 2M rows per experiment.
        var purgeDays = Setting(settings, "SandboxPurgeDays", 7);

        var purged = await Db.ExecuteAsync(
            _connection,
            """
            DELETE s FROM stg.StagingTransaction AS s
            JOIN ops.ReconRun AS r ON r.RunId = s.LoadRunId
            WHERE r.RunType = 'Sandbox'
              AND r.StartedAt < DATEADD(DAY, -@days, SYSDATETIME());
            """,
            c => c.With("@days", purgeDays),
            cancellationToken).ConfigureAwait(false);

        log.Add(string.Create(CultureInfo.InvariantCulture,
            $"sandbox purge: {purged:N0} staged row(s) from runs older than {purgeDays} days"));

        // E2 / E3: how much runway the sliding partition window has left, and
        // what retention is set to. Reported rather than acted on — splitting
        // a partition function is a maintenance-window operation and not
        // something a web request should do behind an operator's back.
        var partitions = await Db.QueryAsync(
            _connection,
            """
            SELECT COUNT(*) FROM sys.partition_range_values AS v
            JOIN sys.partition_functions AS f ON f.function_id = v.function_id
            WHERE f.name = 'pf_ByMonth' AND CAST(v.value AS DATE) > CAST(SYSDATETIME() AS DATE);
            """,
            r => r.GetInt32(0),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var future = partitions.Count > 0 ? partitions[0] : 0;
        var minimum = Setting(settings, "FuturePartitionsMin", 3);

        log.Add(future >= minimum
            ? string.Create(CultureInfo.InvariantCulture,
                $"partitions: {future} future month(s) ahead, at or above the minimum of {minimum}")
            : string.Create(CultureInfo.InvariantCulture,
                $"partitions: only {future} future month(s) ahead, BELOW the minimum of {minimum} — split the partition function in the next maintenance window, or every row will land in the last range at rollover"));

        var stagingMonths = Setting(settings, "StagingMonthsOnline", 3);
        var resultMonths = Setting(settings, "ResultsMonthsOnline", 84);

        log.Add(string.Create(CultureInfo.InvariantCulture,
            $"retention: staging {stagingMonths} month(s), results {resultMonths} month(s) — archiving is SWITCH PARTITION into an archive table, never DELETE"));

        return log;
    }

    private static int Setting(Dictionary<string, string> settings, string key, int fallback) =>
        settings.TryGetValue(key, out var raw)
            && int.TryParse(raw, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
}
