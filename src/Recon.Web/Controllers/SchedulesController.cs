using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Recon.Data;
using Recon.Engine.Scheduling;
using Recon.Web.Services;

namespace Recon.Web.Controllers;

/// <summary>
/// Schedules and alert policies (Phase 6).
///
/// <para>
/// Two things on this screen are easy to get silently wrong, so both are
/// shown rather than assumed. The cron expression is evaluated in the
/// <b>schedule's</b> time zone, not the server's: a server in UTC firing
/// "22:00" for a Jordan session fires at 01:00 local, which is the wrong
/// business date — so the next fire times are rendered in both zones. And
/// <c>ExpectedFileByTime</c> is what makes a file that never arrived
/// distinguishable from one not due yet; a schedule without it can never
/// raise <c>FileNotReceived</c>.
/// </para>
/// </summary>
[Authorize]
public sealed class SchedulesController(
    PortalQueries queries,
    Microsoft.Data.SqlClient.SqlConnection connection,
    AccessService access,
    AuditService audit,
    IConfiguration configuration) : Controller
{
    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Schedules & alerts";

        var grants = await access.GrantsAsync(User).ConfigureAwait(false);
        var ids = grants.Keys.ToList();

        ViewData["Definitions"] = await queries.DefinitionsAsync(ids).ConfigureAwait(false);
        ViewData["Schedules"] = await SchedulesAsync(ids).ConfigureAwait(false);
        ViewData["Policies"] = await PoliciesAsync(ids).ConfigureAwait(false);
        ViewData["CanConfigure"] = grants.Values.Any(l => l >= AccessLevel.Configure);

        // Whether the in-process scheduler is actually running. A disabled
        // scheduler with enabled schedules is the failure that looks like
        // nothing at all.
        ViewData["SchedulerEnabled"] =
            configuration.GetValue("Recon:Scheduler:Enabled", defaultValue: true);

        ViewData["ServerNow"] = DateTime.UtcNow;
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSchedule(
        int? scheduleId,
        int definitionId,
        string cronExpression,
        string timeZone,
        string? expectedFileByTime,
        bool isEnabled)
    {
        var counterpartyId = await CounterpartyOfDefinitionAsync(definitionId).ConfigureAwait(false);
        await access.RequireAsync(User, counterpartyId, AccessLevel.Configure).ConfigureAwait(false);

        // Parsed here, not at 3am. An invalid expression is logged and skipped
        // by the scheduler, which means the run simply never happens.
        if (!CronExpression.TryParse(cronExpression, out _))
        {
            TempData["Error"] =
                $"'{cronExpression}' is not a valid 5-field cron expression " +
                "(minute hour day-of-month month day-of-week). It would be skipped silently.";

            return RedirectToAction(nameof(Index));
        }

        if (!TryResolveZone(timeZone, out _))
        {
            TempData["Error"] =
                $"Time zone '{timeZone}' is not known to this server. The scheduler would fall " +
                "back to Asia/Amman, which is unlikely to be what you meant.";

            return RedirectToAction(nameof(Index));
        }

        TimeOnly? expected = null;
        if (!string.IsNullOrWhiteSpace(expectedFileByTime))
        {
            if (!TimeOnly.TryParse(expectedFileByTime, CultureInfo.InvariantCulture, out var parsed))
            {
                TempData["Error"] = $"'{expectedFileByTime}' is not a time of day (HH:mm).";
                return RedirectToAction(nameof(Index));
            }

            expected = parsed;
        }

        if (scheduleId is { } existing)
        {
            await Db.ExecuteAsync(
                connection,
                """
                UPDATE cfg.ScheduleDefinition
                SET DefinitionId = @def, CronExpression = @cron, TimeZone = @zone,
                    ExpectedFileByTime = @expected, IsEnabled = @enabled
                WHERE ScheduleId = @id;
                """,
                Bind(existing)).ConfigureAwait(false);

            await audit.RecordAsync(User, "ScheduleDefinition", existing, AuditAction.Update,
                after: new { definitionId, cronExpression, timeZone, isEnabled }).ConfigureAwait(false);

            TempData["Ok"] = "Schedule saved.";
        }
        else
        {
            var id = await Db.ScalarAsync<int>(
                connection,
                """
                INSERT cfg.ScheduleDefinition
                    (DefinitionId, CronExpression, TimeZone, ExpectedFileByTime, IsEnabled)
                VALUES (@def, @cron, @zone, @expected, @enabled);
                SELECT CAST(SCOPE_IDENTITY() AS INT);
                """,
                Bind(null)).ConfigureAwait(false);

            await audit.RecordAsync(User, "ScheduleDefinition", id, AuditAction.Create,
                after: new { definitionId, cronExpression, timeZone, isEnabled }).ConfigureAwait(false);

            TempData["Ok"] = "Schedule created.";
        }

        return RedirectToAction(nameof(Index));

        Action<Microsoft.Data.SqlClient.SqlCommand> Bind(int? id) => command =>
        {
            if (id is { } value)
            {
                command.With("@id", value);
            }

            command.With("@def", definitionId)
                   .With("@cron", cronExpression.Trim())
                   .With("@zone", timeZone.Trim())
                   .With("@expected", expected is { } t ? t.ToTimeSpan() : null)
                   .With("@enabled", isEnabled);
        };
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteSchedule(int scheduleId)
    {
        var definitionId = await Db.ScalarAsync<int>(
            connection,
            "SELECT DefinitionId FROM cfg.ScheduleDefinition WHERE ScheduleId = @id;",
            c => c.With("@id", scheduleId)).ConfigureAwait(false);

        var counterpartyId = await CounterpartyOfDefinitionAsync(definitionId).ConfigureAwait(false);
        await access.RequireAsync(User, counterpartyId, AccessLevel.Configure).ConfigureAwait(false);

        await Db.ExecuteAsync(
            connection, "DELETE cfg.ScheduleDefinition WHERE ScheduleId = @id;",
            c => c.With("@id", scheduleId)).ConfigureAwait(false);

        await audit.RecordAsync(User, "ScheduleDefinition", scheduleId, AuditAction.Delete)
            .ConfigureAwait(false);

        TempData["Ok"] = "Schedule removed.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SavePolicy(
        int? alertPolicyId,
        int? definitionId,
        string eventType,
        decimal? thresholdValue,
        string channel,
        string? recipients,
        bool isEnabled)
    {
        // A platform-wide policy (no definition) needs Configure somewhere;
        // a definition-scoped one needs Configure on that counterparty.
        if (definitionId is { } scoped)
        {
            var counterpartyId = await CounterpartyOfDefinitionAsync(scoped).ConfigureAwait(false);
            await access.RequireAsync(User, counterpartyId, AccessLevel.Configure).ConfigureAwait(false);
        }
        else
        {
            var grants = await access.GrantsAsync(User).ConfigureAwait(false);
            if (!grants.Values.Any(l => l >= AccessLevel.Configure))
            {
                TempData["Error"] = "A platform-wide alert policy requires Configure access.";
                return RedirectToAction(nameof(Index));
            }
        }

        // Only the Dashboard channel is implemented. Storing an Email policy
        // that nothing delivers would be worse than refusing it: Operations
        // would believe they are being told.
        if (!string.Equals(channel, "Dashboard", StringComparison.Ordinal))
        {
            TempData["Error"] =
                $"The {channel} channel is not implemented yet, so a policy using it would never " +
                "deliver. Only Dashboard alerts are raised today.";

            return RedirectToAction(nameof(Index));
        }

        try
        {
            if (alertPolicyId is { } existing)
            {
                await Db.ExecuteAsync(
                    connection,
                    """
                    UPDATE cfg.AlertPolicy
                    SET DefinitionId = @def, EventType = @event, ThresholdValue = @threshold,
                        Channel = @channel, Recipients = @recipients, IsEnabled = @enabled
                    WHERE AlertPolicyId = @id;
                    """,
                    Bind(existing)).ConfigureAwait(false);

                await audit.RecordAsync(User, "AlertPolicy", existing, AuditAction.Update,
                    after: new { eventType, channel, isEnabled }).ConfigureAwait(false);
            }
            else
            {
                var id = await Db.ScalarAsync<int>(
                    connection,
                    """
                    INSERT cfg.AlertPolicy
                        (DefinitionId, EventType, ThresholdValue, Channel, Recipients, IsEnabled)
                    VALUES (@def, @event, @threshold, @channel, @recipients, @enabled);
                    SELECT CAST(SCOPE_IDENTITY() AS INT);
                    """,
                    Bind(null)).ConfigureAwait(false);

                await audit.RecordAsync(User, "AlertPolicy", id, AuditAction.Create,
                    after: new { eventType, channel, isEnabled }).ConfigureAwait(false);
            }

            TempData["Ok"] = "Alert policy saved.";
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            TempData["Error"] = "The database refused this policy: " + ex.Message;
        }

        return RedirectToAction(nameof(Index));

        Action<Microsoft.Data.SqlClient.SqlCommand> Bind(int? id) => command =>
        {
            if (id is { } value)
            {
                command.With("@id", value);
            }

            command.With("@def", definitionId)
                   .With("@event", eventType)
                   .With("@threshold", thresholdValue)
                   .With("@channel", channel)
                   .With("@recipients", string.IsNullOrWhiteSpace(recipients) ? null : recipients.Trim())
                   .With("@enabled", isEnabled);
        };
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeletePolicy(int alertPolicyId)
    {
        var grants = await access.GrantsAsync(User).ConfigureAwait(false);
        if (!grants.Values.Any(l => l >= AccessLevel.Configure))
        {
            TempData["Error"] = "Removing an alert policy requires Configure access.";
            return RedirectToAction(nameof(Index));
        }

        await Db.ExecuteAsync(
            connection, "DELETE cfg.AlertPolicy WHERE AlertPolicyId = @id;",
            c => c.With("@id", alertPolicyId)).ConfigureAwait(false);

        await audit.RecordAsync(User, "AlertPolicy", alertPolicyId, AuditAction.Delete)
            .ConfigureAwait(false);

        TempData["Ok"] = "Alert policy removed.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// The next three fire times for a cron expression, in the schedule's zone
    /// and in UTC. Answers "did I mean 22:00 local or 22:00 server" before the
    /// schedule is saved rather than after a run lands on the wrong date.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Preview(string cronExpression, string timeZone)
    {
        if (!CronExpression.TryParse(cronExpression, out var cron))
        {
            return Json(new { valid = false, error = "not a valid 5-field cron expression" });
        }

        if (!TryResolveZone(timeZone, out var zone))
        {
            return Json(new { valid = false, error = $"time zone '{timeZone}' is not known here" });
        }

        var times = new List<object>();
        var cursor = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone!);

        for (var i = 0; i < 3; i++)
        {
            var next = cron!.Next(cursor);
            if (next is null)
            {
                break;
            }

            times.Add(new
            {
                local = next.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                utc = TimeZoneInfo.ConvertTimeToUtc(
                        DateTime.SpecifyKind(next.Value, DateTimeKind.Unspecified), zone!)
                    .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            });

            cursor = next.Value;
        }

        return Json(new { valid = true, zone = zone!.Id, times });
    }

    private static bool TryResolveZone(string id, out TimeZoneInfo? zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = null;
            return false;
        }
    }

    private async Task<int> CounterpartyOfDefinitionAsync(int definitionId)
    {
        var id = await Db.ScalarAsync<int?>(
            connection,
            "SELECT CounterpartyId FROM cfg.ReconciliationDefinition WHERE DefinitionId = @id;",
            c => c.With("@id", definitionId)).ConfigureAwait(false);

        return id ?? throw new InvalidOperationException($"definition {definitionId} was not found");
    }

    private async Task<List<ScheduleRow>> SchedulesAsync(IReadOnlyCollection<int> counterpartyIds)
    {
        using var command = Db.Command(connection, string.Empty);
        var filter = AccessService.CounterpartyFilter("d", counterpartyIds, command);

        command.CommandText = $"""
            SELECT s.ScheduleId, s.DefinitionId, d.Code, s.CronExpression, s.TimeZone,
                   s.ExpectedFileByTime, s.IsEnabled, d.IsActive
            FROM cfg.ScheduleDefinition AS s
            JOIN cfg.ReconciliationDefinition AS d ON d.DefinitionId = s.DefinitionId
            WHERE {filter}
            ORDER BY d.Code, s.ScheduleId;
            """;

        var rows = new List<ScheduleRow>();
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var cronText = reader.GetString(3);
            var zoneId = reader.GetString(4);

            var valid = CronExpression.TryParse(cronText, out var cron);
            var zoneKnown = TryResolveZone(zoneId, out var zone);

            DateTime? nextLocal = null;
            DateTime? nextUtc = null;

            if (valid && zoneKnown)
            {
                var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone!);
                nextLocal = cron!.Next(localNow);

                if (nextLocal is { } local)
                {
                    nextUtc = TimeZoneInfo.ConvertTimeToUtc(
                        DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone!);
                }
            }

            rows.Add(new ScheduleRow
            {
                ScheduleId = reader.GetInt32(0),
                DefinitionId = reader.GetInt32(1),
                DefinitionCode = reader.GetString(2),
                CronExpression = cronText,
                TimeZone = zoneId,
                ExpectedFileByTime = reader.IsDBNull(5)
                    ? null
                    : TimeOnly.FromTimeSpan(reader.GetTimeSpan(5)),
                IsEnabled = reader.GetBoolean(6),
                DefinitionActive = reader.GetBoolean(7),
                CronValid = valid,
                TimeZoneKnown = zoneKnown,
                NextLocal = nextLocal,
                NextUtc = nextUtc,
            });
        }

        return rows;
    }

    private async Task<List<AlertPolicyRow>> PoliciesAsync(IReadOnlyCollection<int> counterpartyIds)
    {
        using var command = Db.Command(connection, string.Empty);
        var filter = AccessService.CounterpartyFilter("d", counterpartyIds, command);

        // A policy with no definition is platform-wide, so it is included for
        // anyone with access to anything — a LEFT JOIN plus an explicit NULL
        // test, because the access filter on a NULL join produces UNKNOWN and
        // would drop exactly those rows.
        command.CommandText = $"""
            SELECT p.AlertPolicyId, p.DefinitionId, d.Code, p.EventType, p.ThresholdValue,
                   p.Channel, p.Recipients, p.IsEnabled
            FROM cfg.AlertPolicy AS p
            LEFT JOIN cfg.ReconciliationDefinition AS d ON d.DefinitionId = p.DefinitionId
            WHERE p.DefinitionId IS NULL OR {filter}
            ORDER BY p.EventType, p.AlertPolicyId;
            """;

        var rows = new List<AlertPolicyRow>();
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            rows.Add(new AlertPolicyRow
            {
                AlertPolicyId = reader.GetInt32(0),
                DefinitionId = reader.IsDBNull(1) ? null : reader.GetInt32(1),
                DefinitionCode = reader.IsDBNull(2) ? null : reader.GetString(2),
                EventType = reader.GetString(3),
                ThresholdValue = reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                Channel = reader.GetString(5),
                Recipients = reader.IsDBNull(6) ? null : reader.GetString(6),
                IsEnabled = reader.GetBoolean(7),
            });
        }

        return rows;
    }
}

public sealed record ScheduleRow
{
    public required int ScheduleId { get; init; }
    public required int DefinitionId { get; init; }
    public required string DefinitionCode { get; init; }
    public required string CronExpression { get; init; }
    public required string TimeZone { get; init; }
    public TimeOnly? ExpectedFileByTime { get; init; }
    public required bool IsEnabled { get; init; }
    public required bool DefinitionActive { get; init; }
    public required bool CronValid { get; init; }
    public required bool TimeZoneKnown { get; init; }
    public DateTime? NextLocal { get; init; }
    public DateTime? NextUtc { get; init; }

    /// <summary>
    /// An enabled schedule on an inactive definition never fires: the
    /// scheduler joins on <c>d.IsActive = 1</c>. That combination looks
    /// configured and does nothing, so it is called out.
    /// </summary>
    public bool WillNeverFire => IsEnabled && (!DefinitionActive || !CronValid);
}

public sealed record AlertPolicyRow
{
    public required int AlertPolicyId { get; init; }
    public int? DefinitionId { get; init; }
    public string? DefinitionCode { get; init; }
    public required string EventType { get; init; }
    public decimal? ThresholdValue { get; init; }
    public required string Channel { get; init; }
    public string? Recipients { get; init; }
    public required bool IsEnabled { get; init; }
}
