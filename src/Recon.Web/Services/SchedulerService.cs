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
    PlatformConfiguration platform,
    ILogger<SchedulerService> logger) : BackgroundService
{
    private readonly IServiceScopeFactory _scopes =
        scopes ?? throw new ArgumentNullException(nameof(scopes));

    private readonly IConfiguration _configuration =
        configuration ?? throw new ArgumentNullException(nameof(configuration));

    private readonly PlatformConfiguration _platform =
        platform ?? throw new ArgumentNullException(nameof(platform));

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
        // appsettings can switch it off for a whole deployment; the portal's
        // own switch (PlatformConfiguration) is checked per tick, so an
        // operator can stop it while a problem is being investigated and
        // start it again without a restart.
        if (!_configuration.GetValue("Recon:Scheduler:Enabled", defaultValue: true))
        {
            _log.LogInformation("Scheduler disabled by the host's configuration.");
            return;
        }

        var pollSeconds = _configuration.GetValue("Recon:Scheduler:PollSeconds", 60);
        _log.LogInformation("Scheduler started, polling every {Seconds}s.", pollSeconds);

        var waiting = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // A portal nobody has set up yet has no database to poll.
                // Waiting quietly is right; crash-looping on a connection
                // error until someone opens /setup is not.
                if (!_platform.IsConfigured)
                {
                    if (!waiting)
                    {
                        _log.LogInformation(
                            "Scheduler is waiting for a database to be configured.");
                        waiting = true;
                    }
                }
                else if (!_platform.SchedulerEnabled)
                {
                    if (!waiting)
                    {
                        _log.LogInformation("Scheduler switched off from the portal.");
                        waiting = true;
                    }
                }
                else
                {
                    if (waiting)
                    {
                        _log.LogInformation("Scheduler resuming.");
                        waiting = false;
                    }

                    await TickAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (SqlException ex) when (ex.Number is 4060 or 911 or 18456)
            {
                // The connection string names a database that does not exist
                // yet, or a login that cannot reach it — the state a portal is
                // in between saving a connection and pressing install. One
                // line, not a stack trace every minute.
                if (!waiting)
                {
                    _log.LogInformation(
                        "Scheduler is waiting for the database to be installed: {Message}",
                        ex.Message);

                    waiting = true;
                }
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

        // The same maintenance the portal's buttons run, so "run it now"
        // and "the scheduler ran it" cannot mean two different things.
        var maintenance = new MaintenanceService(connection);

        var raised = await maintenance.RaiseAlertsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var alert in raised)
        {
            _log.LogWarning("Alert: {Message}", alert);
        }

        // Housekeeping is daily, not minutely. Running the purge and the
        // partition check every minute would be pure load for no signal.
        var today = DateOnly.FromDateTime(minute);
        if (today > _lastHousekeeping)
        {
            _lastHousekeeping = today;

            foreach (var line in await maintenance.HousekeepingAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                _log.LogInformation("Housekeeping: {Line}", line);
            }
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

        // The whole sequence — the lock, the acquisition, the staging, the
        // passes — is SessionRunner's, the same one the portal's buttons use.
        // The scheduler used to carry its own copy, which is why a scheduled
        // run reconciled whatever happened to be staged already: the copy had
        // no acquisition step and nobody noticed, because the demo always
        // staged its files first.
        var sessions = scope.ServiceProvider.GetRequiredService<SessionRunner>();

        var outcome = await sessions.RunAsync(
            user: null, definitionId, businessDate, sessionRef: null, RunType.Scheduled,
            "scheduler", cancellationToken: cancellationToken).ConfigureAwait(false);

        // Only the arrivals: "this dataset's files are uploaded" is true every
        // night and would be two log lines a night saying nothing.
        foreach (var acquired in outcome.Acquired.Where(
                     a => a.HasFile || a.State == Recon.Engine.Providers.AcquisitionState.Duplicate))
        {
            _log.LogInformation("{Code}: {Message}", code, acquired.Message);
        }

        if (outcome.Refusal is { } refusal)
        {
            // Refused attempts are already recorded as Rejected runs. A
            // missing file is the one that matters most: it is the difference
            // between "nothing to reconcile" and "the partner did not send".
            _log.LogWarning("{Code} for {Date:yyyy-MM-dd} did not run: {Reason}",
                code, businessDate, refusal);

            var missing = outcome.Acquired.Any(
                a => a.State == Recon.Engine.Providers.AcquisitionState.NotFound);

            if (missing)
            {
                await RaiseAsync(
                    scope.ServiceProvider, connection, AlertEventType.FileNotReceived,
                    definitionId, null, $"{code} for {businessDate:yyyy-MM-dd}: {refusal}",
                    cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        foreach (var staged in outcome.Staged)
        {
            _log.LogInformation("{Code}: {Dataset} staged {Rows} rows in {Seconds:N1}s.",
                code, staged.Dataset, staged.RowsWritten, staged.Elapsed.TotalSeconds);
        }

        var result = outcome.Outcome!;

        _log.LogInformation("{Code} run {RunId}: {Status}, {Matched} matched, {Unmatched} unmatched.",
            code, outcome.RunId, result.Status, result.Counts.Matched, result.Counts.Unmatched);

        if (result.Status == RunStatus.Failed)
        {
            await RaiseAsync(
                scope.ServiceProvider, connection,
                result.ControlTotals.Any(t => !t.IsBalanced && t.FailsRun)
                    ? AlertEventType.ControlTotalMismatch
                    : AlertEventType.RunFailed,
                definitionId, outcome.RunId,
                $"{code} run {outcome.RunId} failed: {result.Error}",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RaiseAsync(
        IServiceProvider services,
        SqlConnection connection,
        AlertEventType type,
        int definitionId,
        long? runId,
        string message,
        CancellationToken cancellationToken)
    {
        var alerts = services.GetService<AlertService>()
            ?? new AlertService(connection, [new DashboardAlertChannel()]);

        await alerts.RaiseAsync(new AlertEvent
        {
            EventType = type,
            DefinitionId = definitionId,
            RunId = runId,
            Message = message,
        }, cancellationToken).ConfigureAwait(false);
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
