using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Recon.Data;

namespace Recon.Engine.Scheduling;

// CA1848 asks for LoggerMessage delegates. These log once per raised alert —
// a handful of times a day, not per row — so the allocation the rule guards
// against does not arise, and an inlined template keeps the message beside
// the condition that produced it.
#pragma warning disable CA1848

/// <summary>
/// Raises the alerts <c>cfg.AlertPolicy</c> configures.
///
/// <para>
/// Dispatch is deliberately pluggable and deliberately not implemented for
/// email or SMS. "Alert channels — email, SMS, dashboard?" is one of the
/// design's open questions (§15, before Phase 3), and building an SMTP client
/// against an unanswered question means building the wrong one. What is here
/// is the part that does not depend on the answer: detecting the condition,
/// recording it, and handing it to a channel.
/// </para>
///
/// <para>
/// Every raised alert is recorded whatever the channel, so "was anyone told"
/// is answerable even where delivery is not yet wired up.
/// </para>
/// </summary>
public sealed class AlertService(
    SqlConnection connection,
    IEnumerable<IAlertChannel>? channels = null,
    ILogger<AlertService>? logger = null)
{
    private readonly SqlConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    private readonly List<IAlertChannel> _channels = channels?.ToList() ?? [];

    private readonly ILogger _log =
        logger ?? (ILogger)Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    /// <summary>
    /// Raises every policy matching the event type for a definition, plus the
    /// global policies (<c>DefinitionId IS NULL</c>).
    /// </summary>
    public async Task<int> RaiseAsync(
        AlertEvent alert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var policies = await Db.QueryAsync(
            _connection,
            """
            SELECT AlertPolicyId, Channel, Recipients, ThresholdValue
            FROM cfg.AlertPolicy
            WHERE EventType = @type AND IsEnabled = 1
              AND (DefinitionId IS NULL OR DefinitionId = @def);
            """,
            r => new
            {
                Id = r.GetInt32(0),
                Channel = r.GetString(1),
                Recipients = r.GetNullableString("Recipients"),
                Threshold = r.IsDBNull(3) ? (decimal?)null : r.GetDecimal(3),
            },
            c => c.With("@type", alert.EventType.ToString()).With("@def", alert.DefinitionId),
            cancellationToken).ConfigureAwait(false);

        var raised = 0;

        foreach (var policy in policies)
        {
            // A threshold policy only fires above its threshold — which is
            // what makes MatchDistributionDrift a signal rather than a line in
            // every run's log.
            if (policy.Threshold is { } threshold
                && alert.Value is { } value
                && value <= threshold)
            {
                continue;
            }

            foreach (var channel in _channels.Where(c =>
                string.Equals(c.Name, policy.Channel, StringComparison.Ordinal)))
            {
                try
                {
                    await channel.SendAsync(alert, policy.Recipients, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A channel that fails must not stop the run that raised
                    // the alert, nor the other channels.
                    _log.LogWarning(ex, "Alert channel {Channel} failed for {Event}.",
                        channel.Name, alert.EventType);
                }
            }

            // Recorded whatever the channel did. This is how "was anyone
            // told" stays answerable while the channel question is open.
            await Db.ExecuteAsync(
                _connection,
                """
                INSERT aud.AuditLog (EntityType, EntityId, Action, NewValueJson, PerformedBy, Notes)
                VALUES ('Alert', @id, 'Execute', @payload, 'scheduler', @notes);
                """,
                c => c.With("@id", alert.EventType.ToString())
                      .With("@payload", System.Text.Json.JsonSerializer.Serialize(alert))
                      .With("@notes", Truncate(alert.Message, 1000)),
                cancellationToken).ConfigureAwait(false);

            raised++;
        }

        if (raised == 0)
        {
            // Worth saying: a condition that fired with no policy configured
            // looks identical to a condition that never fired.
            _log.LogInformation(
                "{Event} for definition {Definition} matched no enabled alert policy: {Message}",
                alert.EventType, alert.DefinitionId, alert.Message);
        }

        return raised;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}

/// <summary>Mirrors <c>CK_AlertPolicy_Event</c>, case for case.</summary>
public enum AlertEventType
{
    FileNotReceived,
    RunFailed,
    ControlTotalMismatch,
    ThresholdBreach,
    MatchDistributionDrift,
    PartitionShortage,
    ParseErrorLimit,
}

public sealed record AlertEvent
{
    public required AlertEventType EventType { get; init; }
    public int? DefinitionId { get; init; }
    public long? RunId { get; init; }
    public required string Message { get; init; }

    /// <summary>Compared against a policy's threshold, where it has one.</summary>
    public decimal? Value { get; init; }

    public DateTimeOffset RaisedAt { get; init; } = DateTimeOffset.UtcNow;
}

public interface IAlertChannel
{
    /// <summary>Matches <c>cfg.AlertPolicy.Channel</c>: Email | Sms | Dashboard.</summary>
    string Name { get; }

    Task SendAsync(AlertEvent alert, string? recipients, CancellationToken cancellationToken);
}

/// <summary>
/// The dashboard channel: the alert is already in the audit log, and the
/// portal reads it from there. Nothing to send.
///
/// <para>
/// This is the only channel implemented, on purpose. Email and SMS wait on
/// the design's open question about channels; a stub that silently swallowed
/// them would be worse than their absence, because Operations would believe
/// they were being sent.
/// </para>
/// </summary>
public sealed class DashboardAlertChannel(ILogger<DashboardAlertChannel>? logger = null) : IAlertChannel
{
    private readonly ILogger _log =
        logger ?? (ILogger)Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public string Name => "Dashboard";

    public Task SendAsync(AlertEvent alert, string? recipients, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alert);
        _ = recipients;

        _log.Log(
            alert.EventType is AlertEventType.RunFailed or AlertEventType.ControlTotalMismatch
                ? LogLevel.Error
                : LogLevel.Warning,
            "{Event}: {Message}",
            alert.EventType,
            alert.Message);

        return Task.CompletedTask;
    }
}

/// <summary>
/// Detects the conditions the scheduler alerts on.
/// </summary>
public sealed class AlertDetector(SqlConnection connection)
{
    private readonly SqlConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>
    /// Schedules whose file should have arrived by now and has not.
    ///
    /// <para>
    /// <c>ExpectedFileByTime</c> on the schedule is what makes this
    /// answerable; without it, a session that simply never arrived looks the
    /// same as one not due yet, and Operations finds out the next morning.
    /// </para>
    /// </summary>
    public Task<List<AlertEvent>> FileNotReceivedAsync(
        DateOnly businessDate, CancellationToken cancellationToken = default) =>
        Db.QueryAsync(
            _connection,
            """
            SELECT s.DefinitionId, d.Code, s.ExpectedFileByTime, s.TimeZone
            FROM cfg.ScheduleDefinition AS s
            JOIN cfg.ReconciliationDefinition AS d ON d.DefinitionId = s.DefinitionId
            WHERE s.IsEnabled = 1
              AND d.IsActive = 1
              AND s.ExpectedFileByTime IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM ops.ReconRun AS r
                  WHERE r.DefinitionId = s.DefinitionId
                    AND r.BusinessDate = @date
                    AND r.RunType <> 'Sandbox');
            """,
            r => new AlertEvent
            {
                EventType = AlertEventType.FileNotReceived,
                DefinitionId = r.GetInt32(0),
                Message = string.Create(CultureInfo.InvariantCulture,
                    $"{r.GetString(1)}: no run for {businessDate:yyyy-MM-dd}, and the file was expected by {r.GetTimeSpan(2):hh\\:mm} {r.GetString(3)}."),
            },
            c => c.With("@date", businessDate.ToDateTime(TimeOnly.MinValue)),
            cancellationToken);

    /// <summary>
    /// Runs whose last pass carried an unusual share of the matches.
    ///
    /// <para>
    /// The design calls the pass distribution a data-quality early-warning
    /// system (§9.3): a rising share in the last pass means the clean
    /// reference match is degrading upstream. This is the query that turns
    /// that observation into an alert instead of a chart nobody reads.
    /// </para>
    /// </summary>
    public Task<List<AlertEvent>> MatchDistributionDriftAsync(
        decimal thresholdPercent, CancellationToken cancellationToken = default) =>
        Db.QueryAsync(
            _connection,
            """
            WITH passes AS (
                SELECT s.RunId, r.Sequence, r.RuleCode, ISNULL(s.RowsMatched, 0) AS Matched,
                       SUM(ISNULL(s.RowsMatched, 0)) OVER (PARTITION BY s.RunId) AS TotalMatched,
                       MAX(r.Sequence) OVER (PARTITION BY s.RunId) AS LastSequence
                FROM ops.ReconRunStep AS s
                JOIN cfg.MatchRule AS r ON r.MatchRuleId = s.MatchRuleId
                WHERE s.StepName = 'Match' AND s.Status = 'Completed'
            )
            SELECT p.RunId, run.DefinitionId, d.Code, p.RuleCode,
                   CAST(100.0 * p.Matched / NULLIF(p.TotalMatched, 0) AS DECIMAL(9,2)) AS Share
            FROM passes AS p
            JOIN ops.ReconRun AS run ON run.RunId = p.RunId
            JOIN cfg.ReconciliationDefinition AS d ON d.DefinitionId = run.DefinitionId
            WHERE p.Sequence = p.LastSequence
              AND run.IsCurrent = 1
              AND run.RunType <> 'Sandbox'
              AND run.CompletedAt >= DATEADD(DAY, -1, SYSDATETIME())
              -- NULLIF here, not a sibling `TotalMatched > 0` predicate.
              -- SQL Server does not promise to evaluate the guard first, so
              -- a run whose passes matched nothing raised "divide by zero"
              -- and took the whole scheduler tick down with it — alerts and
              -- housekeeping included. A run with no matches simply has no
              -- distribution, so the comparison is UNKNOWN and the row is
              -- filtered out, which is the answer we want.
              AND 100.0 * p.Matched / NULLIF(p.TotalMatched, 0) > @threshold;
            """,
            r => new AlertEvent
            {
                EventType = AlertEventType.MatchDistributionDrift,
                RunId = r.GetInt64(0),
                DefinitionId = r.GetInt32(1),
                Value = r.GetDecimal(4),
                Message = string.Create(CultureInfo.InvariantCulture,
                    $"{r.GetString(2)} run {r.GetInt64(0)}: the last pass ({r.GetString(3)}) carried {r.GetDecimal(4):N2}% of matches. The clean reference match is degrading upstream."),
            },
            c => c.With("@threshold", thresholdPercent),
            cancellationToken);

    /// <summary>
    /// Whether the sliding partition window is running out (review item E2).
    ///
    /// <para>
    /// If no future partition exists at month rollover, every row lands in the
    /// last range and the window breaks — quietly, and expensively to undo.
    /// </para>
    /// </summary>
    public async Task<AlertEvent?> PartitionShortageAsync(
        int minimumFuture, CancellationToken cancellationToken = default)
    {
        var future = await Db.ScalarAsync<int>(
            _connection,
            """
            SELECT COUNT(*)
            FROM sys.partition_range_values AS rv
            JOIN sys.partition_functions AS pf ON pf.function_id = rv.function_id
            WHERE pf.name = 'pf_ByMonth' AND CAST(rv.value AS DATE) > CAST(SYSDATETIME() AS DATE);
            """,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (future >= minimumFuture)
        {
            return null;
        }

        return new AlertEvent
        {
            EventType = AlertEventType.PartitionShortage,
            Value = future,
            Message = string.Create(CultureInfo.InvariantCulture,
                $"Only {future} future monthly partition(s) remain on pf_ByMonth, below the minimum of {minimumFuture}. At rollover every row would land in the last range and the sliding window would break."),
        };
    }
}
#pragma warning restore CA1848
