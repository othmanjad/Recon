using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Recon.Data;
using Recon.Domain.Conditions;
using Recon.Domain.Configuration;
using Recon.Engine.Reporting;
using Recon.Web.Services;

namespace Recon.Web.Controllers;

/// <summary>
/// The report builder and the exports themselves (Phase 4).
///
/// <para>
/// Columns are chosen from the field registry, never typed: a column naming a
/// field resolves to <c>cfg.DatasetField</c> and the compiler maps it to a
/// slot. The same property that protects the rule builder protects this —
/// nothing the user types becomes part of a SQL statement.
/// </para>
///
/// <para>
/// A transaction-level scope on a 2M-row day does not fit a worksheet
/// (1,048,576 rows — review item E1). The builder says so when Xlsx is
/// chosen for such a scope, and the writer splits sheets automatically rather
/// than truncating.
/// </para>
/// </summary>
[Authorize]
public sealed class ReportsController(
    PortalQueries queries,
    ConfigurationRepository config,
    ReportService reports,
    Microsoft.Data.SqlClient.SqlConnection connection,
    AccessService access,
    AuditService audit) : Controller
{
    public async Task<IActionResult> Index(int? id, long? runId)
    {
        ViewData["Title"] = "Reports";

        var grants = await access.GrantsAsync(User).ConfigureAwait(false);
        var ids = grants.Keys.ToList();
        var definitions = await queries.DefinitionsAsync(ids).ConfigureAwait(false);
        var selectedId = id ?? definitions.FirstOrDefault()?.DefinitionId;

        ViewData["Definitions"] = definitions;
        ViewData["Selected"] = selectedId;

        if (selectedId is { } definitionId && definitions.Any(d => d.DefinitionId == definitionId))
        {
            var definition = await config.LoadDefinitionAsync(definitionId).ConfigureAwait(false);

            ViewData["Definition"] = definition;
            ViewData["Reports"] = await reports.ForDefinitionAsync(definitionId).ConfigureAwait(false);

            // Only runs that can actually be exported: a report reads the
            // run's staged rows and results, so offering a Pending run would
            // produce an empty file and look like a broken report.
            ViewData["Runs"] = (await queries
                .RunsAsync(ids, definitionId, null, 40).ConfigureAwait(false))
                .Where(r => r.Status is "Completed" or "Failed")
                .ToList();

            ViewData["RunId"] = runId;
            ViewData["CanConfigure"] = grants.TryGetValue(definition.CounterpartyId, out var level)
                && level >= AccessLevel.Configure;
        }
        else
        {
            ViewData["Reports"] = new List<ReportDefinition>();
            ViewData["Runs"] = new List<Models.RunRow>();
            ViewData["CanConfigure"] = false;
        }

        ViewData["HasAnyAccess"] = ids.Count > 0;
        return View();
    }

    /// <summary>
    /// Streams one report for one run straight to the response body.
    ///
    /// <para>
    /// Nothing is buffered — the reader, the writer and the response are one
    /// pipeline, which is what makes a 2M-row export possible at all. The
    /// export is written to the audit log before a byte is sent, so a download
    /// that fails halfway is still recorded as attempted.
    /// </para>
    /// </summary>
    public async Task<IActionResult> Download(int id, long runId, CancellationToken cancellationToken)
    {
        var counterpartyId = await Db.ScalarAsync<int?>(
            connection,
            """
            SELECT d.CounterpartyId
            FROM ops.ReconRun AS r
            JOIN cfg.ReconciliationDefinition AS d ON d.DefinitionId = r.DefinitionId
            WHERE r.RunId = @run;
            """,
            c => c.With("@run", runId), cancellationToken).ConfigureAwait(false);

        if (counterpartyId is null)
        {
            return NotFound();
        }

        await access.RequireAsync(User, counterpartyId.Value, AccessLevel.Read, cancellationToken)
            .ConfigureAwait(false);

        var definitionId = await Db.ScalarAsync<int>(
            connection,
            "SELECT DefinitionId FROM ops.ReconRun WHERE RunId = @run;",
            c => c.With("@run", runId), cancellationToken).ConfigureAwait(false);

        var available = await reports.ForDefinitionAsync(definitionId, cancellationToken)
            .ConfigureAwait(false);

        var report = available.FirstOrDefault(r => r.ReportDefinitionId == id);
        if (report is null)
        {
            return NotFound();
        }

        Response.ContentType = report.OutputFormat == ReportOutputFormat.Csv
            ? "text/csv; charset=utf-8"
            : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

        Response.Headers.ContentDisposition =
            $"attachment; filename=\"{reports.FileName(report, runId)}\"";

        // Content-Length is unknown: the row count is not known until the
        // reader is drained, and counting first would mean running the query
        // twice. Chunked is the honest answer.
        await reports.StreamAsync(User, id, runId, Response.Body, cancellationToken)
            .ConfigureAwait(false);

        return new EmptyResult();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveReport(
        int definitionId,
        int? reportDefinitionId,
        string code,
        string name,
        string sheetName,
        string dataScope,
        string outputFormat,
        string? filterJson,
        bool includeSubtotals,
        bool isActive)
    {
        var definition = await config.LoadDefinitionAsync(definitionId).ConfigureAwait(false);
        await access.RequireAsync(User, definition.CounterpartyId, AccessLevel.Configure)
            .ConfigureAwait(false);

        // The filter is validated against the left registry before storage, so
        // an unknown field never reaches the report compiler.
        if (!string.IsNullOrWhiteSpace(filterJson))
        {
            var result = ConditionValidator.ValidateJson(filterJson, definition.Left);
            if (!result.IsValid)
            {
                TempData["Error"] = "The report filter was rejected: " + string.Join("; ", result.Errors);
                return RedirectToAction(nameof(Index), new { id = definitionId });
            }
        }

        var transactionLevel = dataScope is "Matched" or "Unmatched" or "All";

        try
        {
            if (reportDefinitionId is { } existing)
            {
                await Db.ExecuteAsync(
                    connection,
                    """
                    UPDATE cfg.ReportDefinition
                    SET Code = @code, Name = @name, SheetName = @sheet, DataScope = @scope,
                        OutputFormat = @format, FilterJson = @filter,
                        IncludeSubtotals = @subtotals, IsActive = @active
                    WHERE ReportDefinitionId = @id;
                    """,
                    Bind(existing)).ConfigureAwait(false);

                await audit.RecordAsync(User, "ReportDefinition", existing, AuditAction.Update,
                    after: new { code, dataScope, outputFormat }).ConfigureAwait(false);
            }
            else
            {
                var id = await Db.ScalarAsync<int>(
                    connection,
                    """
                    INSERT cfg.ReportDefinition
                        (DefinitionId, Code, Name, SheetName, DataScope, OutputFormat,
                         FilterJson, IncludeSubtotals, IsActive)
                    VALUES (@def, @code, @name, @sheet, @scope, @format,
                            @filter, @subtotals, @active);
                    SELECT CAST(SCOPE_IDENTITY() AS INT);
                    """,
                    Bind(null)).ConfigureAwait(false);

                await audit.RecordAsync(User, "ReportDefinition", id, AuditAction.Create,
                    after: new { code, dataScope, outputFormat }).ConfigureAwait(false);
            }

            TempData["Ok"] = transactionLevel && outputFormat == "Xlsx"
                ? $"{code} saved. It is a transaction-level report in Xlsx: a 2M-row day exceeds " +
                  "a worksheet, so the writer will split it across sheets. Csv streams as one file."
                : $"{code} saved.";
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            TempData["Error"] = "The database refused this report: " + ex.Message;
        }

        return RedirectToAction(nameof(Index), new { id = definitionId });

        Action<Microsoft.Data.SqlClient.SqlCommand> Bind(int? id) => command =>
        {
            if (id is { } value)
            {
                command.With("@id", value);
            }

            command.With("@def", definitionId)
                   .With("@code", code?.Trim())
                   .With("@name", name?.Trim())
                   .With("@sheet", string.IsNullOrWhiteSpace(sheetName) ? code?.Trim() : sheetName.Trim())
                   .With("@scope", dataScope)
                   .With("@format", outputFormat)
                   .With("@filter", string.IsNullOrWhiteSpace(filterJson) ? null : filterJson)
                   .With("@subtotals", includeSubtotals)
                   .With("@active", isActive);
        };
    }

    /// <summary>
    /// Adds a column. Either a registry field or one of the engine's computed
    /// columns — the CHECK constraint requires exactly one of the two, and a
    /// column that is neither would compile to nothing.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveColumn(
        int definitionId,
        int reportDefinitionId,
        int? reportColumnId,
        string? fieldCode,
        string? side,
        string? computedField,
        string header,
        int sequence,
        string? displayFormat,
        int? columnWidth)
    {
        var definition = await config.LoadDefinitionAsync(definitionId).ConfigureAwait(false);
        await access.RequireAsync(User, definition.CounterpartyId, AccessLevel.Configure)
            .ConfigureAwait(false);

        int? datasetFieldId = null;

        if (!string.IsNullOrWhiteSpace(fieldCode))
        {
            var dataset = string.Equals(side, "Right", StringComparison.Ordinal)
                ? definition.Right
                : definition.Left;

            // Resolved against the registry. A code that is not in it is
            // refused here and never becomes part of a statement.
            if (!dataset.TryGetField(fieldCode.Trim(), out var field))
            {
                TempData["Error"] =
                    $"'{fieldCode}' is not in the field registry of {dataset.Code}.";

                return RedirectToAction(nameof(Index), new { id = definitionId });
            }

            datasetFieldId = field.DatasetFieldId;
        }

        if (datasetFieldId is null && string.IsNullOrWhiteSpace(computedField))
        {
            TempData["Error"] =
                "A column must name either a registry field or a computed column; one that " +
                "names neither would compile to nothing.";

            return RedirectToAction(nameof(Index), new { id = definitionId });
        }

        if (!string.IsNullOrWhiteSpace(computedField)
            && !ReportSqlBuilder.ComputedColumns.Contains(computedField))
        {
            TempData["Error"] =
                $"'{computedField}' is not a computed column the compiler knows. Known: " +
                string.Join(", ", ReportSqlBuilder.ComputedColumns);

            return RedirectToAction(nameof(Index), new { id = definitionId });
        }

        try
        {
            if (reportColumnId is { } existing)
            {
                await Db.ExecuteAsync(
                    connection,
                    """
                    UPDATE cfg.ReportColumn
                    SET DatasetFieldId = @field, ComputedField = @computed, Header = @header,
                        DisplayFormat = @format, ColumnWidth = @width, Sequence = @seq
                    WHERE ReportColumnId = @id;
                    """,
                    Bind(existing)).ConfigureAwait(false);
            }
            else
            {
                await Db.ExecuteAsync(
                    connection,
                    """
                    INSERT cfg.ReportColumn
                        (ReportDefinitionId, DatasetFieldId, ComputedField, Header,
                         DisplayFormat, ColumnWidth, Sequence)
                    VALUES (@report, @field, @computed, @header, @format, @width, @seq);
                    """,
                    Bind(null)).ConfigureAwait(false);
            }

            await audit.RecordAsync(User, "ReportColumn", reportColumnId ?? 0,
                reportColumnId is null ? AuditAction.Create : AuditAction.Update,
                after: new { reportDefinitionId, header, fieldCode, computedField, sequence })
                .ConfigureAwait(false);

            TempData["Ok"] = $"Column '{header}' saved.";
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            // Most likely the unique constraint on (report, sequence): two
            // columns cannot occupy the same position, or the column order
            // would depend on the read.
            TempData["Error"] = "The database refused this column: " + ex.Message;
        }

        return RedirectToAction(nameof(Index), new { id = definitionId });

        Action<Microsoft.Data.SqlClient.SqlCommand> Bind(int? id) => command =>
        {
            if (id is { } value)
            {
                command.With("@id", value);
            }

            command.With("@report", reportDefinitionId)
                   .With("@field", datasetFieldId)
                   .With("@computed", string.IsNullOrWhiteSpace(computedField) ? null : computedField)
                   .With("@header", header?.Trim())
                   .With("@format", string.IsNullOrWhiteSpace(displayFormat) ? null : displayFormat.Trim())
                   .With("@width", columnWidth)
                   .With("@seq", sequence);
        };
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteColumn(int definitionId, int reportColumnId)
    {
        var definition = await config.LoadDefinitionAsync(definitionId).ConfigureAwait(false);
        await access.RequireAsync(User, definition.CounterpartyId, AccessLevel.Configure)
            .ConfigureAwait(false);

        await Db.ExecuteAsync(
            connection, "DELETE cfg.ReportColumn WHERE ReportColumnId = @id;",
            c => c.With("@id", reportColumnId)).ConfigureAwait(false);

        await audit.RecordAsync(User, "ReportColumn", reportColumnId, AuditAction.Delete)
            .ConfigureAwait(false);

        TempData["Ok"] = "Column removed.";
        return RedirectToAction(nameof(Index), new { id = definitionId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteReport(int definitionId, int reportDefinitionId)
    {
        var definition = await config.LoadDefinitionAsync(definitionId).ConfigureAwait(false);
        await access.RequireAsync(User, definition.CounterpartyId, AccessLevel.Configure)
            .ConfigureAwait(false);

        await Db.ExecuteAsync(
            connection,
            """
            DELETE cfg.ReportColumn WHERE ReportDefinitionId = @id;
            DELETE cfg.ReportDefinition WHERE ReportDefinitionId = @id;
            """,
            c => c.With("@id", reportDefinitionId)).ConfigureAwait(false);

        await audit.RecordAsync(User, "ReportDefinition", reportDefinitionId, AuditAction.Delete)
            .ConfigureAwait(false);

        TempData["Ok"] = "Report removed.";
        return RedirectToAction(nameof(Index), new { id = definitionId });
    }
}
