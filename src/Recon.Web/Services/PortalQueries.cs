using System.Security.Claims;
using Microsoft.Data.SqlClient;
using Recon.Data;
using Recon.Web.Models;

namespace Recon.Web.Services;

/// <summary>
/// Every read the portal does.
///
/// <para>
/// One class rather than a repository per screen, because the queries share
/// one property that must not be forgotten anywhere: <b>each is filtered by
/// the user's counterparty grants, in SQL</b>. A screen that fetched
/// everything and hid rows afterwards would still leak them through counts,
/// totals and paging.
/// </para>
///
/// <para>
/// Reads go to <c>ops.RunAggregate</c> wherever a total is wanted, never to a
/// scan of staging. That is what makes returning to "run 4471, matched inward
/// total" a primary-key read and what keeps the dashboard responsive while a
/// load is inserting 2M rows.
/// </para>
/// </summary>
public sealed class PortalQueries(SqlConnection connection, AccessService access)
{
    private readonly SqlConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    private readonly AccessService _access = access ?? throw new ArgumentNullException(nameof(access));

    // =================================================================
    // Dashboard
    // =================================================================

    public async Task<DashboardModel> DashboardAsync(
        ClaimsPrincipal? user,
        int? definitionId,
        string? status,
        CancellationToken cancellationToken = default)
    {
        var grants = await _access.GrantsAsync(user, cancellationToken).ConfigureAwait(false);
        var ids = grants.Keys.ToList();

        var runs = await RunsAsync(ids, definitionId, status, 40, cancellationToken)
            .ConfigureAwait(false);

        var definitions = await DefinitionsAsync(ids, cancellationToken).ConfigureAwait(false);

        // Tiles come from the current runs only. Including superseded reruns
        // would double-count a corrected day, which is the whole reason
        // IsCurrent exists.
        var current = runs.Where(r => r.IsCurrent).ToList();

        return new DashboardModel
        {
            Runs = runs,
            Definitions = definitions,
            SelectedDefinitionId = definitionId,
            SelectedStatus = status,
            CurrentRunCount = current.Count,
            RunningCount = current.Count(r => r.Status == "Running"),
            FailedCount = current.Count(r => r.Status == "Failed"),
            MatchedRows = current.Sum(r => r.MatchedCount ?? 0),
            UnmatchedRows = current.Sum(r => r.UnmatchedCount ?? 0),
            AmbiguousRows = current.Sum(r => r.AmbiguousCount ?? 0),
            HasAnyAccess = ids.Count > 0,
        };
    }

    public async Task<List<RunRow>> RunsAsync(
        IReadOnlyCollection<int> counterpartyIds,
        int? definitionId,
        string? status,
        int take,
        CancellationToken cancellationToken = default)
    {
        using var command = Db.Command(_connection, string.Empty);
        var filter = AccessService.CounterpartyFilter("d", counterpartyIds, command);

        command.Parameters.AddWithValue("@take", take);

        var definitionClause = string.Empty;
        if (definitionId is { } id)
        {
            command.Parameters.AddWithValue("@def", id);
            definitionClause = " AND r.DefinitionId = @def";
        }

        var statusClause = string.Empty;
        if (!string.IsNullOrEmpty(status))
        {
            command.Parameters.AddWithValue("@status", status);
            statusClause = " AND r.Status = @status";
        }

        command.CommandText = $"""
            SELECT TOP (@take)
                   r.RunId, r.DefinitionId, d.Code, d.Name, r.BusinessDate, r.SessionRef,
                   r.RunType, r.Status, r.IsCurrent, r.SourceRunId, r.StagingRunId,
                   r.StartedAt, r.CompletedAt,
                   r.LeftRowCnt, r.RightRowCnt, r.MatchedCnt, r.UnmatchedCnt, r.AmbiguousCnt,
                   r.ErrorMessage, r.TriggeredBy
            FROM ops.ReconRun AS r
            JOIN cfg.ReconciliationDefinition AS d ON d.DefinitionId = r.DefinitionId
            WHERE {filter}{definitionClause}{statusClause}
            ORDER BY r.RunId DESC;
            """;

        var rows = new List<RunRow>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new RunRow
            {
                RunId = reader.GetInt64(0),
                DefinitionId = reader.GetInt32(1),
                DefinitionCode = reader.GetString(2),
                DefinitionName = reader.GetString(3),
                BusinessDate = DateOnly.FromDateTime(reader.GetDateTime(4)),
                SessionRef = reader.IsDBNull(5) ? null : reader.GetString(5),
                RunType = reader.GetString(6),
                Status = reader.GetString(7),
                IsCurrent = reader.GetBoolean(8),
                SourceRunId = reader.IsDBNull(9) ? null : reader.GetInt64(9),
                StagingRunId = reader.GetInt64(10),
                StartedAt = reader.IsDBNull(11) ? null : reader.GetDateTime(11),
                CompletedAt = reader.IsDBNull(12) ? null : reader.GetDateTime(12),
                LeftRowCount = reader.IsDBNull(13) ? null : reader.GetInt64(13),
                RightRowCount = reader.IsDBNull(14) ? null : reader.GetInt64(14),
                MatchedCount = reader.IsDBNull(15) ? null : reader.GetInt64(15),
                UnmatchedCount = reader.IsDBNull(16) ? null : reader.GetInt64(16),
                AmbiguousCount = reader.IsDBNull(17) ? null : reader.GetInt64(17),
                ErrorMessage = reader.IsDBNull(18) ? null : reader.GetString(18),
                TriggeredBy = reader.GetString(19),
            });
        }

        return rows;
    }

    /// <summary>
    /// A run's detail: its steps with timings and the SQL they ran, its
    /// control totals, and its pass distribution.
    /// </summary>
    public async Task<RunDetailModel?> RunDetailAsync(
        ClaimsPrincipal? user, long runId, CancellationToken cancellationToken = default)
    {
        var grants = await _access.GrantsAsync(user, cancellationToken).ConfigureAwait(false);
        var runs = await RunsAsync(grants.Keys.ToList(), null, null, 1_000_000, cancellationToken)
            .ConfigureAwait(false);

        var run = runs.FirstOrDefault(r => r.RunId == runId);
        if (run is null)
        {
            // Either it does not exist or the user may not see it. The same
            // answer for both: which counterparties exist is itself a
            // disclosure.
            return null;
        }

        var steps = await Db.QueryAsync(
            _connection,
            """
            SELECT s.StepName, s.Side, s.Status, s.StartedAt, s.CompletedAt,
                   s.RowsProcessed, s.RowsMatched, s.ErrorMessage,
                   r.RuleCode, r.Sequence, s.RunStepId
            FROM ops.ReconRunStep AS s
            LEFT JOIN cfg.MatchRule AS r ON r.MatchRuleId = s.MatchRuleId
            WHERE s.RunId = @run
            ORDER BY s.RunStepId;
            """,
            r => new RunStepRow
            {
                RunStepId = r.GetInt64(10),
                StepName = r.GetString(0),
                Side = r.IsDBNull(1) ? null : r.GetString(1),
                Status = r.GetString(2),
                StartedAt = r.IsDBNull(3) ? null : r.GetDateTime(3),
                CompletedAt = r.IsDBNull(4) ? null : r.GetDateTime(4),
                RowsProcessed = r.IsDBNull(5) ? null : r.GetInt64(5),
                RowsMatched = r.IsDBNull(6) ? null : r.GetInt64(6),
                ErrorMessage = r.IsDBNull(7) ? null : r.GetString(7),
                RuleCode = r.IsDBNull(8) ? null : r.GetString(8),
                RuleSequence = r.IsDBNull(9) ? null : r.GetInt32(9),
            },
            c => c.With("@run", runId),
            cancellationToken).ConfigureAwait(false);

        var totals = await Db.QueryAsync(
            _connection,
            """
            SELECT c.CheckCode, c.DisplayName, t.ValueA, t.ValueB, t.DifferenceMinor,
                   t.IsBalanced, c.FailRunOnMismatch, c.SourceAExpressionJson
            FROM ops.ControlTotalResult AS t
            JOIN cfg.ControlTotalDefinition AS c ON c.ControlTotalId = t.ControlTotalId
            WHERE t.RunId = @run
            ORDER BY c.CheckCode;
            """,
            r => new ControlTotalRow
            {
                CheckCode = r.GetString(0),
                DisplayName = r.GetString(1),
                ValueA = r.GetInt64(2),
                ValueB = r.GetInt64(3),
                Difference = r.GetInt64(4),
                IsBalanced = r.GetBoolean(5),
                FailsRun = r.GetBoolean(6),
                // The aggregate function says whether these are money or a
                // count. Formatting a count as money turns 15 rows into 0.015.
                IsAmount = r.GetString(7).Contains("SumAmount", StringComparison.Ordinal),
            },
            c => c.With("@run", runId),
            cancellationToken).ConfigureAwait(false);

        var aggregates = await Db.QueryAsync(
            _connection,
            """
            SELECT d.Code, a.Side, a.GroupKey, a.MatchStatus, a.RowCnt, a.AmountMinorSum, a.CurrencyCode
            FROM ops.RunAggregate AS a
            JOIN cfg.Dataset AS d ON d.DatasetId = a.DatasetId
            WHERE a.RunId = @run
            ORDER BY a.Side, a.GroupKey, a.MatchStatus;
            """,
            r => new AggregateRow
            {
                DatasetCode = r.GetString(0),
                Side = r.GetString(1),
                GroupKey = r.GetString(2),
                MatchStatus = r.GetString(3),
                RowCount = r.GetInt64(4),
                AmountMinorSum = r.GetInt64(5),
                CurrencyCode = r.GetString(6),
            },
            c => c.With("@run", runId),
            cancellationToken).ConfigureAwait(false);

        var parseErrors = await Db.QueryAsync(
            _connection,
            """
            SELECT TOP (200) RawRowNumber, ErrorType, FieldCode, ErrorMessage, RawLine
            FROM stg.ParseError WHERE RunId = @run ORDER BY RawRowNumber;
            """,
            r => new ParseErrorRow2
            {
                RawRowNumber = r.IsDBNull(0) ? null : r.GetInt32(0),
                ErrorType = r.GetString(1),
                FieldCode = r.IsDBNull(2) ? null : r.GetString(2),
                Message = r.GetString(3),
                RawLine = r.IsDBNull(4) ? null : r.GetString(4),
            },
            c => c.With("@run", runId),
            cancellationToken).ConfigureAwait(false);

        return new RunDetailModel
        {
            Run = run,
            Steps = steps,
            ControlTotals = totals,
            Aggregates = aggregates,
            ParseErrors = parseErrors,
        };
    }

    // =================================================================
    // Configuration
    // =================================================================

    public async Task<List<CounterpartyRow>> CounterpartiesAsync(
        ClaimsPrincipal? user, CancellationToken cancellationToken = default)
    {
        var grants = await _access.GrantsAsync(user, cancellationToken).ConfigureAwait(false);

        using var command = Db.Command(_connection, string.Empty);
        var filter = AccessService.CounterpartyFilter("c", grants.Keys.ToList(), command);

        command.CommandText = $"""
            SELECT c.CounterpartyId, c.Code, c.Name, c.Description, c.IsActive,
                   (SELECT COUNT(*) FROM cfg.Dataset WHERE CounterpartyId = c.CounterpartyId),
                   (SELECT COUNT(*) FROM cfg.ReconciliationDefinition WHERE CounterpartyId = c.CounterpartyId),
                   (SELECT COUNT(*) FROM cfg.FeeSchedule WHERE CounterpartyId = c.CounterpartyId)
            FROM cfg.Counterparty AS c
            WHERE {filter}
            ORDER BY c.Code;
            """;

        var rows = new List<CounterpartyRow>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetInt32(0);
            rows.Add(new CounterpartyRow
            {
                CounterpartyId = id,
                Code = reader.GetString(1),
                Name = reader.GetString(2),
                Description = reader.IsDBNull(3) ? null : reader.GetString(3),
                IsActive = reader.GetBoolean(4),
                DatasetCount = reader.GetInt32(5),
                DefinitionCount = reader.GetInt32(6),
                FeeScheduleCount = reader.GetInt32(7),
                AccessLevel = grants.TryGetValue(id, out var level) ? level : null,
            });
        }

        return rows;
    }

    public async Task<List<DefinitionRow>> DefinitionsAsync(
        IReadOnlyCollection<int> counterpartyIds, CancellationToken cancellationToken = default)
    {
        using var command = Db.Command(_connection, string.Empty);
        var filter = AccessService.CounterpartyFilter("d", counterpartyIds, command);

        command.CommandText = $"""
            SELECT d.DefinitionId, d.CounterpartyId, d.Code, d.Name, d.IsActive,
                   l.Code, r.Code, d.MatchingWindowDaysBefore, d.MatchingWindowDaysAfter,
                   (SELECT COUNT(*) FROM cfg.MatchRule WHERE DefinitionId = d.DefinitionId AND IsActive = 1)
            FROM cfg.ReconciliationDefinition AS d
            JOIN cfg.Dataset AS l ON l.DatasetId = d.LeftDatasetId
            JOIN cfg.Dataset AS r ON r.DatasetId = d.RightDatasetId
            WHERE {filter}
            ORDER BY d.Code;
            """;

        var rows = new List<DefinitionRow>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new DefinitionRow
            {
                DefinitionId = reader.GetInt32(0),
                CounterpartyId = reader.GetInt32(1),
                Code = reader.GetString(2),
                Name = reader.GetString(3),
                IsActive = reader.GetBoolean(4),
                LeftDatasetCode = reader.GetString(5),
                RightDatasetCode = reader.GetString(6),
                WindowBefore = reader.GetInt32(7),
                WindowAfter = reader.GetInt32(8),
                ActivePasses = reader.GetInt32(9),
            });
        }

        return rows;
    }

    public async Task<List<DatasetRow>> DatasetsAsync(
        ClaimsPrincipal? user, CancellationToken cancellationToken = default)
    {
        var grants = await _access.GrantsAsync(user, cancellationToken).ConfigureAwait(false);

        using var command = Db.Command(_connection, string.Empty);
        var filter = AccessService.CounterpartyFilter("d", grants.Keys.ToList(), command);

        command.CommandText = $"""
            SELECT d.DatasetId, d.CounterpartyId, c.Code, d.Code, d.Name, d.ProviderType,
                   d.DefaultCurrency, d.DuplicateKeyFields, d.IsActive, d.TimeZone,
                   (SELECT COUNT(*) FROM cfg.DatasetField WHERE DatasetId = d.DatasetId)
            FROM cfg.Dataset AS d
            JOIN cfg.Counterparty AS c ON c.CounterpartyId = d.CounterpartyId
            WHERE {filter}
            ORDER BY c.Code, d.Code;
            """;

        var rows = new List<DatasetRow>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new DatasetRow
            {
                DatasetId = reader.GetInt32(0),
                CounterpartyId = reader.GetInt32(1),
                CounterpartyCode = reader.GetString(2),
                Code = reader.GetString(3),
                Name = reader.GetString(4),
                ProviderType = reader.GetString(5),
                DefaultCurrency = reader.IsDBNull(6) ? null : reader.GetString(6),
                DuplicateKeyFields = reader.IsDBNull(7) ? null : reader.GetString(7),
                IsActive = reader.GetBoolean(8),
                TimeZone = reader.GetString(9),
                FieldCount = reader.GetInt32(10),
            });
        }

        return rows;
    }

    // =================================================================
    // Exceptions
    // =================================================================

    public async Task<ExceptionWorkspaceModel> ExceptionsAsync(
        ClaimsPrincipal? user,
        ExceptionFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var grants = await _access.GrantsAsync(user, cancellationToken).ConfigureAwait(false);

        using var command = Db.Command(_connection, string.Empty);
        var access = AccessService.CounterpartyFilter("d", grants.Keys.ToList(), command);
        var clauses = new List<string> { access };

        if (!string.IsNullOrEmpty(filter.ExceptionCode))
        {
            command.Parameters.AddWithValue("@code", filter.ExceptionCode);
            clauses.Add("e.ExceptionCode = @code");
        }

        if (!string.IsNullOrEmpty(filter.Status))
        {
            command.Parameters.AddWithValue("@status", filter.Status);
            clauses.Add("e.Status = @status");
        }

        if (filter.DefinitionId is { } definitionId)
        {
            command.Parameters.AddWithValue("@def", definitionId);
            clauses.Add("e.DefinitionId = @def");
        }

        // Aging is computed here, in the query layer, filtering on the indexed
        // BusinessDate. The design removed a non-deterministic AgeDays computed
        // column for exactly this reason (review item A7): it could not be
        // indexed and was evaluated per row on every read.
        if (filter.MinAgeDays is { } minAge)
        {
            command.Parameters.AddWithValue("@maxDate", DateTime.Today.AddDays(-minAge));
            clauses.Add("e.BusinessDate <= @maxDate");
        }

        if (filter.MaxAgeDays is { } maxAge)
        {
            command.Parameters.AddWithValue("@minDate", DateTime.Today.AddDays(-maxAge));
            clauses.Add("e.BusinessDate >= @minDate");
        }

        if (!string.IsNullOrEmpty(filter.Search))
        {
            // A prefix match, so the index on the reference can still seek.
            // A leading wildcard would scan, and this box is used constantly.
            command.Parameters.AddWithValue("@search", filter.Search + "%");
            clauses.Add("""
                EXISTS (SELECT 1 FROM stg.StagingTransaction AS s
                        WHERE s.StagingId = e.StagingId AND s.Text1 LIKE @search)
                """);
        }

        command.Parameters.AddWithValue("@take", filter.Take);
        var where = string.Join(" AND ", clauses);

        command.CommandText = $"""
            SELECT TOP (@take)
                   e.ExceptionId, e.RunId, e.DefinitionId, d.Code, e.BusinessDate,
                   e.Side, e.ExceptionCode, e.AmountMinor, e.CurrencyCode, e.Status,
                   e.AssignedTo, e.ResolutionCode, e.ResolutionNote, e.KeyValuesJson,
                   e.ClosedByRunId, e.ClosedAt, e.ClosedBy,
                   DATEDIFF(DAY, e.BusinessDate, CAST(SYSDATETIME() AS DATE)) AS AgeDays
            FROM ops.ReconException AS e
            JOIN cfg.ReconciliationDefinition AS d ON d.DefinitionId = e.DefinitionId
            WHERE {where}
            ORDER BY e.BusinessDate DESC, e.ExceptionId DESC;
            """;

        var rows = new List<ExceptionRow>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ExceptionRow
            {
                ExceptionId = reader.GetInt64(0),
                RunId = reader.GetInt64(1),
                DefinitionId = reader.GetInt32(2),
                DefinitionCode = reader.GetString(3),
                BusinessDate = DateOnly.FromDateTime(reader.GetDateTime(4)),
                Side = reader.GetString(5),
                ExceptionCode = reader.GetString(6),
                AmountMinor = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                CurrencyCode = reader.IsDBNull(8) ? null : reader.GetString(8),
                Status = reader.GetString(9),
                AssignedTo = reader.IsDBNull(10) ? null : reader.GetString(10),
                ResolutionCode = reader.IsDBNull(11) ? null : reader.GetString(11),
                ResolutionNote = reader.IsDBNull(12) ? null : reader.GetString(12),
                KeyValuesJson = reader.IsDBNull(13) ? null : reader.GetString(13),
                ClosedByRunId = reader.IsDBNull(14) ? null : reader.GetInt64(14),
                ClosedAt = reader.IsDBNull(15) ? null : reader.GetDateTime(15),
                ClosedBy = reader.IsDBNull(16) ? null : reader.GetString(16),
                AgeDays = reader.GetInt32(17),
            });
        }

        var codes = await Db.QueryAsync(
            _connection,
            """
            SELECT DISTINCT ExceptionCode, DisplayName, Severity
            FROM cfg.ClassificationRule WHERE IsActive = 1 ORDER BY ExceptionCode;
            """,
            r => new ExceptionCodeRow
            {
                Code = r.GetString(0),
                DisplayName = r.GetString(1),
                Severity = r.GetString(2),
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new ExceptionWorkspaceModel
        {
            Rows = rows,
            Codes = codes,
            Definitions = await DefinitionsAsync(grants.Keys.ToList(), cancellationToken)
                .ConfigureAwait(false),
            Filter = filter,
            HasAnyAccess = grants.Count > 0,
        };
    }

    public Task<List<SettingRow>> SettingsAsync(CancellationToken cancellationToken = default) =>
        Db.QueryAsync(
            _connection,
            """
            SELECT SettingKey, SettingValue, DataType, Description, ModifiedAt, ModifiedBy
            FROM cfg.PlatformSetting ORDER BY SettingKey;
            """,
            r => new SettingRow
            {
                Key = r.GetString(0),
                Value = r.GetString(1),
                DataType = r.GetString(2),
                Description = r.GetString(3),
                ModifiedAt = r.GetDateTime(4),
                ModifiedBy = r.IsDBNull(5) ? null : r.GetString(5),
            },
            cancellationToken: cancellationToken);

    public Task<List<AuditRow>> AuditAsync(
        string? entityType, string? performedBy, int take,
        CancellationToken cancellationToken = default) =>
        Db.QueryAsync(
            _connection,
            """
            SELECT TOP (@take) AuditId, AuditDate, EntityType, EntityId, Action,
                   OldValueJson, NewValueJson, PerformedBy, PerformedAt, IpAddress, Notes
            FROM aud.AuditLog
            WHERE (@type IS NULL OR EntityType = @type)
              AND (@by IS NULL OR PerformedBy = @by)
            ORDER BY AuditId DESC;
            """,
            r => new AuditRow
            {
                AuditId = r.GetInt64(0),
                AuditDate = DateOnly.FromDateTime(r.GetDateTime(1)),
                EntityType = r.GetString(2),
                EntityId = r.GetString(3),
                Action = r.GetString(4),
                OldValueJson = r.IsDBNull(5) ? null : r.GetString(5),
                NewValueJson = r.IsDBNull(6) ? null : r.GetString(6),
                PerformedBy = r.GetString(7),
                PerformedAt = r.GetDateTime(8),
                IpAddress = r.IsDBNull(9) ? null : r.GetString(9),
                Notes = r.IsDBNull(10) ? null : r.GetString(10),
            },
            c => c.With("@take", take)
                  .With("@type", string.IsNullOrEmpty(entityType) ? null : entityType)
                  .With("@by", string.IsNullOrEmpty(performedBy) ? null : performedBy),
            cancellationToken);
}
