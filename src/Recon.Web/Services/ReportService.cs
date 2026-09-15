using System.Globalization;
using System.Security.Claims;
using Microsoft.Data.SqlClient;
using Recon.Data;
using Recon.Domain.Conditions;
using Recon.Domain.Configuration;
using Recon.Engine.Reporting;

namespace Recon.Web.Services;

/// <summary>
/// Loads report definitions and streams their output.
///
/// <para>
/// Every export is written to <c>aud.AuditLog</c> with
/// <see cref="AuditAction.Export"/> before a byte reaches the client. That is
/// review item D3: the audit log captured configuration changes but not READS
/// of sensitive exports, and "regulators ask" who downloaded which report.
/// Logging first rather than after means a download that fails halfway is
/// still recorded as attempted.
/// </para>
/// </summary>
public sealed class ReportService(
    SqlConnection connection,
    ConfigurationRepository config,
    AuditService audit)
{
    private readonly SqlConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    private readonly ConfigurationRepository _config =
        config ?? throw new ArgumentNullException(nameof(config));

    private readonly AuditService _audit = audit ?? throw new ArgumentNullException(nameof(audit));

    public async Task<List<ReportDefinition>> ForDefinitionAsync(
        int definitionId, CancellationToken cancellationToken = default)
    {
        var definition = await _config.LoadDefinitionAsync(definitionId, cancellationToken)
            .ConfigureAwait(false);

        var headers = await Db.QueryAsync(
            _connection,
            """
            SELECT ReportDefinitionId, Code, Name, SheetName, DataScope,
                   FilterJson, OutputFormat, IncludeSubtotals, IsActive
            FROM cfg.ReportDefinition
            WHERE DefinitionId = @def
            ORDER BY Code;
            """,
            r => new
            {
                Id = r.GetInt32(0),
                Code = r.GetString(1),
                Name = r.GetString(2),
                SheetName = r.GetString(3),
                Scope = Db.ParseEnum<ReportScope>(r.GetString(4)),
                FilterJson = r.GetNullableString("FilterJson"),
                Format = Db.ParseEnum<ReportOutputFormat>(r.GetString(6)),
                Subtotals = r.GetBoolean(7),
                IsActive = r.GetBoolean(8),
            },
            c => c.With("@def", definitionId),
            cancellationToken).ConfigureAwait(false);

        var reports = new List<ReportDefinition>();

        foreach (var header in headers)
        {
            var columns = await Db.QueryAsync(
                _connection,
                """
                SELECT c.ReportColumnId, c.Header, c.Sequence, c.ComputedField,
                       c.DisplayFormat, c.ColumnWidth, f.FieldCode, f.DatasetId
                FROM cfg.ReportColumn AS c
                LEFT JOIN cfg.DatasetField AS f ON f.DatasetFieldId = c.DatasetFieldId
                WHERE c.ReportDefinitionId = @report
                ORDER BY c.Sequence;
                """,
                r => new
                {
                    Id = r.GetInt32(0),
                    Header = r.GetString(1),
                    Sequence = r.GetInt32(2),
                    Computed = r.GetNullableString("ComputedField"),
                    Format = r.GetNullableString("DisplayFormat"),
                    Width = r.GetNullableInt32("ColumnWidth"),
                    FieldCode = r.GetNullableString("FieldCode"),
                    DatasetId = r.GetNullableInt32("DatasetId"),
                },
                c => c.With("@report", header.Id),
                cancellationToken).ConfigureAwait(false);

            reports.Add(new ReportDefinition
            {
                ReportDefinitionId = header.Id,
                DefinitionId = definitionId,
                Code = header.Code,
                Name = header.Name,
                SheetName = header.SheetName,
                Scope = header.Scope,
                Filter = ConditionNode.Parse(header.FilterJson),
                OutputFormat = header.Format,
                IncludeSubtotals = header.Subtotals,
                IsActive = header.IsActive,
                Columns = columns.Select(c => new Recon.Engine.Reporting.ReportColumn
                {
                    ReportColumnId = c.Id,
                    Header = c.Header,
                    Sequence = c.Sequence,
                    ComputedField = c.Computed,
                    DisplayFormat = c.Format,
                    ColumnWidth = c.Width,
                    // A column naming a registry field is resolved against
                    // the side it belongs to, so the same report can mix the
                    // two datasets' vocabularies.
                    Field = c.FieldCode is { } code
                        ? (c.DatasetId == definition.Left.DatasetId
                            ? definition.Left.GetField(code)
                            : definition.Right.GetField(code))
                        : null,
                }).ToList(),
            });
        }

        return reports;
    }

    /// <summary>
    /// Streams one report for one run, straight to the response body.
    ///
    /// <para>
    /// Nothing is buffered: the reader, the writer and the HTTP response are
    /// one pipeline, which is what makes a 2M-row export possible at all.
    /// </para>
    /// </summary>
    public async Task<ReportResult> StreamAsync(
        ClaimsPrincipal? user,
        int reportDefinitionId,
        long runId,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        var run = await Db.QueryAsync(
            _connection,
            """
            SELECT r.DefinitionId, r.BusinessDate, r.StagingRunId
            FROM ops.ReconRun AS r WHERE r.RunId = @run;
            """,
            r => (
                DefinitionId: r.GetInt32(0),
                BusinessDate: DateOnly.FromDateTime(r.GetDateTime(1)),
                StagingRunId: r.GetInt64(2)),
            c => c.With("@run", runId),
            cancellationToken).ConfigureAwait(false);

        if (run.Count == 0)
        {
            throw new InvalidOperationException($"run {runId} was not found");
        }

        var definition = await _config.LoadDefinitionAsync(run[0].DefinitionId, cancellationToken)
            .ConfigureAwait(false);

        var reports = await ForDefinitionAsync(run[0].DefinitionId, cancellationToken)
            .ConfigureAwait(false);

        var report = reports.FirstOrDefault(r => r.ReportDefinitionId == reportDefinitionId)
            ?? throw new InvalidOperationException(
                $"report {reportDefinitionId} does not belong to run {runId}'s definition");

        // Logged BEFORE the bytes: a download interrupted halfway was still a
        // download attempt, and the log is the record of who asked.
        await _audit.RecordAsync(
            user, "Report", report.Code, AuditAction.Export,
            after: new { runId, report.Code, report.Scope, report.OutputFormat },
            notes: $"Exported {report.Code} for run {runId}",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var (from, to) = definition.WindowFor(run[0].BusinessDate);

        var statement = ReportSqlBuilder.Compile(
            report, definition, runId, run[0].StagingRunId, run[0].BusinessDate, from, to);

        // Read BEFORE the reader opens. One connection serves the request, so
        // a settings query issued while the report's reader was streaming
        // failed with "there is already an open DataReader associated with
        // this Connection" — and it failed after the response headers had
        // gone out, so the browser saw a truncated download rather than an
        // error.
        var maxRows = await MaxRowsPerSheetAsync(cancellationToken).ConfigureAwait(false);

        using var command = Db.Command(_connection, statement.Sql, timeoutSeconds: 1800);
        foreach (var p in statement.Parameters.Parameters)
        {
            command.Parameters.Add(new SqlParameter(p.ParameterName, p.SqlDbType)
            {
                Value = p.Value,
                Size = p.Size,
                Precision = p.Precision,
                Scale = p.Scale,
            });
        }

        // SequentialAccess: the reader never buffers a row it has passed,
        // which is what keeps memory flat across 2M of them.
        using var reader = await command
            .ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, cancellationToken)
            .ConfigureAwait(false);

        return report.OutputFormat == ReportOutputFormat.Csv
            ? await new CsvReportWriter()
                .WriteAsync(reader, destination, report.Columns, cancellationToken)
                .ConfigureAwait(false)
            : await new ExcelReportWriter(new ExcelReportOptions { MaxRowsPerSheet = maxRows })
                .WriteAsync(reader, destination, report.SheetName, report.Columns, cancellationToken)
                .ConfigureAwait(false);
    }

    public string FileName(ReportDefinition report, long runId) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{report.Code}_run{runId}.{(report.OutputFormat == ReportOutputFormat.Csv ? "csv" : "xlsx")}");

    private async Task<int> MaxRowsPerSheetAsync(CancellationToken cancellationToken)
    {
        var settings = await _config.LoadSettingsAsync(cancellationToken).ConfigureAwait(false);

        return settings.TryGetValue("ExcelMaxRowsPerSheet", out var raw)
            && int.TryParse(raw, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 1_000_000;
    }
}
