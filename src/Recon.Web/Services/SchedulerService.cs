using System.Globalization;
using Microsoft.Data.SqlClient;
using Recon.Data;
using Recon.Domain.Configuration;
using Recon.Engine;
using Recon.Engine.Scheduling;

namespace Recon.Web.Services;

// CA1848 asks for LoggerMessage delegates. The scheduler logs once per tick
// and once per fired schedule — a handful of times a minute at most — so the
// allocation the rule guards against does not arise, and inlined templates
// keep each message beside the condition that produced it.
#pragma warning disable CA1848

/// <summary>
/// The scheduler (Phase 6). Wakes once a minute, fires the schedules due,
/// detects the alert conditions, and runs the housekeeping jobs.
///
/// <para>
/// In-process rather than a separate service, because the design's exit
/// criterion for this phase is "runs unattended for a full week" and a second
/// deployable to keep alive is a second thing that can be down. Running more
/// than one node is safe anyway: <c>sp_getapplock</c> keyed on
/// <c>(DefinitionId, BusinessDate)</c> means the second node's attempt is
/// refused and recorded as <c>Rejected</c>, not duplicated (review item B2).
/// </para>
///
/// <para>
/// Each tick takes its own connection and its own scope. A scheduler that
/// held one connection for a week would hold one transaction's worth of locks
/// for a week.
/// </para>
/// </summary>
public sealed class SchedulerService(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    ILogger<SchedulerService> logger) : BackgroundService
{
    private readonly IServiceScopeFactory _scopes =
        scopes ?? throw new ArgumentNullException(nameof(scopes));

    private readonly IConfiguration _configuration =
        configuration ?? throw new ArgumentNullException(nameof(configuration));

    private readonly ILogger<SchedulerService> _log =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// The minute already handled, so a tick that takes longer than its
    /// interval cannot fire the same schedule twice.
    /// </summary>
    private DateTime _lastMinute = DateTime.MinValue;

    private DateOnly _lastHousekeeping = DateOnly.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("Recon:Scheduler:Enabled", defaultValue: true))
        {
            _log.LogInformation("Scheduler disabled by configuration.");
            return;
        }

        var pollSeconds = _configuration.GetValue("Recon:Scheduler:PollSeconds", 60);
        _log.LogInformation("Scheduler started, polling every {Seconds}s.", pollSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A scheduler that dies on one bad tick is worse than one that
                // logs and carries on: the next session still needs to run.
                _log.LogError(ex, "Scheduler tick failed; continuing.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _log.LogInformation("Scheduler stopped.");
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var minute = new DateTime(
            DateTime.UtcNow.Year, DateTime.UtcNow.Month, DateTime.UtcNow.Day,
            DateTime.UtcNow.Hour, DateTime.UtcNow.Minute, 0, DateTimeKind.Utc);

        if (minute <= _lastMinute)
        {
            return;
        }

        _lastMinute = minute;

        using var scope = _scopes.CreateScope();
        var connection = scope.ServiceProvider.GetRequiredService<SqlConnection>();

        await FireDueSchedulesAsync(scope.ServiceProvider, connection, minute, cancellationToken)
            .ConfigureAwait(false);

        await RaiseAlertsAsync(connection, cancellationToken).ConfigureAwait(false);

        // Housekeeping is daily, not minutely. Running the purge and the
        // partition check every minute would be pure load for no signal.
        var today = DateOnly.FromDateTime(minute);
        if (today > _lastHousekeeping)
        {
            _lastHousekeeping = today;
            await HousekeepingAsync(connection, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task FireDueSchedulesAsync(
        IServiceProvider services,
        SqlConnection connection,
        DateTime utcMinute,
        CancellationToken cancellationToken)
    {
        var schedules = await Db.QueryAsync(
            connection,
            """
            SELECT s.ScheduleId, s.DefinitionId, d.Code, s.CronExpression, s.TimeZone
            FROM cfg.ScheduleDefinition AS s
            JOIN cfg.ReconciliationDefinition AS d ON d.DefinitionId = s.DefinitionId
            WHERE s.IsEnabled = 1 AND d.IsActive = 1;
            """,
            r => new
            {
                ScheduleId = r.GetInt32(0),
                DefinitionId = r.GetInt32(1),
                Code = r.GetString(2),
                Cron = r.GetString(3),
                TimeZone = r.GetString(4),
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        foreach (var schedule in schedules)
        {
            if (!CronExpression.TryParse(schedule.Cron, out var cron))
            {
                _log.LogError(
                    "Schedule {ScheduleId} for {Code} has an invalid cron expression '{Cron}'; skipping.",
                    schedule.ScheduleId, schedule.Code, schedule.Cron);
                continue;
            }

            // Evaluated in the SCHEDULE's time zone, not the server's. Jordan
            // is UTC+3: a server in UTC firing "22:00" would fire at 01:00
            // local, which is the wrong business date.
            var zone = ResolveTimeZone(schedule.TimeZone);
            var local = TimeZoneInfo.ConvertTimeFromUtc(utcMinute, zone);

            if (!cron!.Matches(local))
            {
                continue;
            }

            // The business date is the local date, for the same reason.
            var businessDate = DateOnly.FromDateTime(local);

            _log.LogInformation(
                "Schedule {ScheduleId} ({Code}) is due at {Local:yyyy-MM-dd HH:mm} {Zone}.",
                schedule.ScheduleId, schedule.Code, local, zone.Id);

            await RunAsync(services, schedule.DefinitionId, schedule.Code, businessDate,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(
        IServiceProvider services,
        int definitionId,
        string code,
        DateOnly businessDate,
        CancellationToken cancellationToken)
    {
        // A fresh scope per run: its own connection, because the application
        // lock is session-scoped and the run must hold it for its duration.
        using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var connection = scope.ServiceProvider.GetRequiredService<SqlConnection>();
        var config = scope.ServiceProvider.GetRequiredService<ConfigurationRepository>();
        var runs = scope.ServiceProvider.GetRequiredService<RunRepository>();

        var definition = await config.LoadDefinitionAsync(definitionId, cancellationToken)
            .ConfigureAwait(false);

        var problems = definition.ActivationProblems();
        if (problems.Count > 0)
        {
            _log.LogError("{Code} cannot be activated, so the schedule did not run it: {Problems}",
                code, string.Join("; ", problems));
            return;
        }

        if (!await runs.TryAcquireRunLockAsync(definitionId, businessDate, sessionRef: null,
                cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            // Another node, or a manual trigger, holds it. Recorded rather
            // than silently skipped: Operations needs to see that the
            // scheduled attempt did nothing and why.
            await runs.RecordRejectedAsync(
                definitionId, definition.Version, businessDate, null, RunType.Scheduled,
                "scheduler", "another run holds the lock", cancellationToken).ConfigureAwait(false);

            _log.LogWarning("{Code} for {Date:yyyy-MM-dd} is already running; attempt recorded as Rejected.",
                code, businessDate);
            return;
        }

        try
        {
            var run = await runs.CreateRunAsync(
                definition, businessDate, null, RunType.Scheduled, "scheduler",
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var outcome = await new ReconciliationRunner(connection, runs)
                .ExecuteAsync(definition, run, cancellationToken).ConfigureAwait(false);

            _log.LogInformation("{Code} run {RunId}: {Status}, {Matched} matched, {Unmatched} unmatched.",
                code, run.RunId, outcome.Status, outcome.Counts.Matched, outcome.Counts.Unmatched);

            if (outcome.Status == RunStatus.Failed)
            {
                var alerts = scope.ServiceProvider.GetService<AlertService>()
                    ?? new AlertService(connection, [new DashboardAlertChannel()]);

                await alerts.RaiseAsync(new AlertEvent
                {
                    EventType = outcome.ControlTotals.Any(t => !t.IsBalanced && t.FailsRun)
                        ? AlertEventType.ControlTotalMismatch
                        : AlertEventType.RunFailed,
                    DefinitionId = definitionId,
                    RunId = run.RunId,
                    Message = $"{code} run {run.RunId} failed: {outcome.Error}",
                }, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await runs.ReleaseRunLockAsync(definitionId, businessDate, null, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task RaiseAlertsAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        var detector = new AlertDetector(connection);
        var alerts = new AlertService(connection, [new DashboardAlertChannel()]);

        var settings = await new ConfigurationRepository(connection)
            .LoadSettingsAsync(cancellationToken).ConfigureAwait(false);

        var threshold = settings.TryGetValue("MatchDriftThresholdPct", out var raw)
            && decimal.TryParse(raw, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 15m;

        foreach (var alert in await detector
            .MatchDistributionDriftAsync(threshold, cancellationToken).ConfigureAwait(false))
        {
            await alerts.RaiseAsync(alert, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The daily jobs: sandbox purge, partition lookahead, and the retention
    /// check. Each corresponds to a review item that would otherwise become a
    /// production incident: E5, E2 and E3.
    /// </summary>
    private async Task HousekeepingAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var settings = await new ConfigurationRepository(connection)
            .LoadSettingsAsync(cancellationToken).ConfigureAwait(false);

        int Setting(string key, int fallback) =>
            settings.TryGetValue(key, out var raw)
                && int.TryParse(raw, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;

        // E5: sandbox runs write to production staging. Left alone they would
        // accumulate 2M rows per experiment.
        var purgeDays = Setting("SandboxPurgeDays", 7);

        var purged = await Db.ExecuteAsync(
            connection,
            """
            DELETE s FROM stg.StagingTransaction AS s
            JOIN ops.ReconRun AS r ON r.RunId = s.LoadRunId
            WHERE r.RunType = 'Sandbox'
              AND r.StartedAt < DATEADD(DAY, -@days, SYSDATETIME());
            """,
            c => c.With("@days", purgeDays),
            cancellationToken).ConfigureAwait(false);

        if (purged > 0)
        {
            _log.LogInformation("Purged {Rows} staged rows from sandbox runs older than {Days} days.",
                purged, purgeDays);
        }

        // E2: if no future partition exists at rollover, everything lands in
        // the last range and the sliding window breaks.
        var detector = new AlertDetector(connection);
        var shortage = await detector
            .PartitionShortageAsync(Setting("FuturePartitionsAlertAt", 2), cancellationToken)
            .ConfigureAwait(false);

        if (shortage is not null)
        {
            await new AlertService(connection, [new DashboardAlertChannel()])
                .RaiseAsync(shortage, cancellationToken).ConfigureAwait(false);
        }

        // File-not-received, for yesterday's business date: a session that
        // never arrived looks identical to one not due yet without this.
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        var alerts = new AlertService(connection, [new DashboardAlertChannel()]);

        foreach (var alert in await detector
            .FileNotReceivedAsync(yesterday, cancellationToken).ConfigureAwait(false))
        {
            await alerts.RaiseAsync(alert, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// IANA ids on Linux, Windows ids on Windows. .NET 8 converts between them
    /// on both platforms, but a misconfigured zone must not take the scheduler
    /// down — so an unknown one falls back to Jordan and says so.
    /// </summary>
    private TimeZoneInfo ResolveTimeZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            _log.LogError(ex, "Time zone '{Id}' is not known; falling back to Asia/Amman.", id);

            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById("Asia/Amman");
            }
            catch (Exception fallback) when (fallback is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // Jordan is UTC+3 with no DST, so this is exact rather than an
                // approximation.
                return TimeZoneInfo.CreateCustomTimeZone("Asia/Amman", TimeSpan.FromHours(3),
                    "Jordan", "Jordan");
            }
        }
    }
}
#pragma warning restore CA1848
