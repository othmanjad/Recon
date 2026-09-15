using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Recon.Data;
using Recon.Domain.Configuration;
using Recon.Engine.Sql;
using Recon.Web.Services;

namespace Recon.Web.Controllers;

/// <summary>
/// Transaction search — single and bulk, per dataset, in the dataset's own
/// field labels (design §13).
///
/// <para>
/// The design is explicit that this is a SQL Server problem and not a search
/// engine one: exact-reference and date-range lookups over correctly indexed
/// columns, and nothing in the requirements is a free-text problem.
/// Elasticsearch would be real operational overhead for no capability, so
/// this screen is deliberately built on the indexes that already exist — a
/// single term is a prefix seek on the Reference-role slot, and a bulk paste
/// is an <c>IN</c> list of exact values, which is how Operations actually
/// arrives: with a list of references from a partner's email.
/// </para>
///
/// <para>
/// Columns come from the registry, so the results table is labelled in the
/// counterparty's own vocabulary without the engine knowing any of it. Every
/// slot name is resolved through <see cref="SqlQueryBuilder.ResolveSlot"/> and
/// every value is a parameter: there is no second path to SQL here.
/// </para>
/// </summary>
[Authorize]
public sealed class SearchController(
    PortalQueries queries,
    ConfigurationRepository config,
    Microsoft.Data.SqlClient.SqlConnection connection,
    AccessService access) : Controller
{
    /// <summary>
    /// A bulk paste is a list somebody copied out of a spreadsheet or an
    /// email. More than this and the right answer is a report, not a search
    /// box — an IN list of thousands stops being a seek.
    /// </summary>
    private const int MaxTerms = 200;

    private const int MaxRows = 500;

    public async Task<IActionResult> Index(
        int? id, string? terms, string? status, DateOnly? from, DateOnly? to)
    {
        ViewData["Title"] = "Search";

        var datasets = await queries.DatasetsAsync(User).ConfigureAwait(false);
        var selectedId = id ?? datasets.FirstOrDefault()?.DatasetId;

        ViewData["Datasets"] = datasets;
        ViewData["Selected"] = selectedId;
        ViewData["Terms"] = terms;
        ViewData["Status"] = status;
        ViewData["From"] = from;
        ViewData["To"] = to;
        ViewData["MaxTerms"] = MaxTerms;
        ViewData["MaxRows"] = MaxRows;

        if (selectedId is not { } datasetId || !datasets.Any(d => d.DatasetId == datasetId))
        {
            ViewData["Dataset"] = null;
            return View();
        }

        // Access is checked against the dataset's counterparty rather than
        // inferred from the list: the id arrives from the query string.
        var row = datasets.First(d => d.DatasetId == datasetId);
        await access.RequireAsync(User, row.CounterpartyId, AccessLevel.Read).ConfigureAwait(false);

        var dataset = await config.LoadDatasetAsync(datasetId).ConfigureAwait(false);
        ViewData["Dataset"] = dataset;

        var reference = dataset.FieldWithRole(FieldRole.Reference);
        ViewData["ReferenceField"] = reference;

        if (reference is null)
        {
            ViewData["Problem"] =
                $"{dataset.Code} has no field with the Reference role, so there is nothing to " +
                "search by. Give one that role on the Datasets screen.";

            return View();
        }

        var parsed = ParseTerms(terms);
        ViewData["ParsedTerms"] = parsed;

        if (parsed.Count == 0 && from is null && to is null)
        {
            // An unfiltered search over a partitioned 700M-row table is not a
            // search. Rows is left unset rather than empty, because "nothing
            // matched" and "you have not asked anything yet" are different
            // answers and the screen says which.
            return View();
        }

        ViewData["Rows"] = await SearchAsync(dataset, reference, parsed, status, from, to)
            .ConfigureAwait(false);

        return View();
    }

    /// <summary>
    /// Splits a paste into terms on any of the separators a spreadsheet or an
    /// email produces, and caps the list.
    /// </summary>
    internal static List<string> ParseTerms(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split([',', ';', '\n', '\r', '\t', ' '], StringSplitOptions.RemoveEmptyEntries
                        | StringSplitOptions.TrimEntries)
                 .Distinct(StringComparer.Ordinal)
                 .Take(MaxTerms)
                 .ToList();

    private async Task<List<SearchHit>> SearchAsync(
        Dataset dataset,
        DatasetField reference,
        List<string> terms,
        string? status,
        DateOnly? from,
        DateOnly? to)
    {
        using var command = Db.Command(connection, string.Empty, timeoutSeconds: 120);

        var fields = dataset.Fields.OrderBy(f => f.DisplayOrder).ToList();

        // Slot names come from the registry through the one resolver, so a
        // tampered registry row naming something that is not a column throws
        // rather than producing SQL.
        var columns = fields
            .Select(f => "S." + SqlQueryBuilder.ResolveSlot(dataset, f.FieldCode, requireMatchable: false))
            .ToList();

        var clauses = new List<string> { "S.DatasetId = @ds" };
        command.Parameters.AddWithValue("@ds", dataset.DatasetId);

        var referenceSlot = SqlQueryBuilder.ResolveSlot(dataset, reference.FieldCode, requireMatchable: false);

        if (terms.Count == 1)
        {
            // One term is a prefix seek: Operations types the first characters
            // of a reference far more often than all of it.
            command.Parameters.AddWithValue("@t0", terms[0] + "%");
            clauses.Add($"S.{referenceSlot} LIKE @t0");
        }
        else if (terms.Count > 1)
        {
            var names = new List<string>(terms.Count);
            for (var i = 0; i < terms.Count; i++)
            {
                command.Parameters.AddWithValue("@t" + i, terms[i]);
                names.Add("@t" + i);
            }

            clauses.Add($"S.{referenceSlot} IN ({string.Join(", ", names)})");
        }

        // TxDate is the partition column, so a date range is partition
        // elimination rather than a filter.
        if (from is { } f2)
        {
            command.Parameters.AddWithValue("@from", f2.ToDateTime(TimeOnly.MinValue));
            clauses.Add("S.TxDate >= @from");
        }

        if (to is { } t2)
        {
            command.Parameters.AddWithValue("@to", t2.ToDateTime(TimeOnly.MinValue));
            clauses.Add("S.TxDate <= @to");
        }

        if (!string.IsNullOrEmpty(status))
        {
            command.Parameters.AddWithValue("@status", status);
            clauses.Add("S.MatchStatus = @status");
        }

        command.Parameters.AddWithValue("@take", MaxRows);

        command.CommandText = $"""
            SELECT TOP (@take)
                   S.StagingId, S.TxDate, S.LoadRunId, S.ResultRunId,
                   S.MatchStatus, S.ExceptionCode, S.MatchedWithId, S.RawRowNumber,
                   {string.Join(",\n                   ", columns)}
            FROM stg.StagingTransaction AS S
            WHERE {string.Join(" AND ", clauses)}
            ORDER BY S.TxDate DESC, S.StagingId DESC;
            """;

        var rows = new List<SearchHit>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var values = new List<string?>(fields.Count);

            for (var i = 0; i < fields.Count; i++)
            {
                var ordinal = 8 + i;
                values.Add(reader.IsDBNull(ordinal) ? null : Render(fields[i], reader, ordinal));
            }

            rows.Add(new SearchHit
            {
                StagingId = reader.GetInt64(0),
                TxDate = DateOnly.FromDateTime(reader.GetDateTime(1)),
                LoadRunId = reader.GetInt64(2),
                ResultRunId = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                MatchStatus = reader.GetString(4),
                ExceptionCode = reader.IsDBNull(5) ? null : reader.GetString(5),
                MatchedWithId = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                RawRowNumber = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                Values = values,
            });
        }

        return rows;
    }

    /// <summary>
    /// A slot's value as text. An Amount-role integer stays an integer here —
    /// the view formats it with the currency's scale, because only the view
    /// knows which currency the row is in.
    /// </summary>
    private static string Render(
        DatasetField field, Microsoft.Data.SqlClient.SqlDataReader reader, int ordinal) =>
        field.DataType switch
        {
            FieldDataType.Integer => reader.GetInt64(ordinal)
                .ToString(System.Globalization.CultureInfo.InvariantCulture),
            FieldDataType.Decimal => reader.GetDecimal(ordinal)
                .ToString(System.Globalization.CultureInfo.InvariantCulture),
            FieldDataType.DateTime => reader.GetDateTime(ordinal)
                .ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
            FieldDataType.Boolean => reader.GetBoolean(ordinal) ? "true" : "false",
            _ => reader.GetString(ordinal),
        };
}

public sealed record SearchHit
{
    public required long StagingId { get; init; }
    public required DateOnly TxDate { get; init; }
    public required long LoadRunId { get; init; }
    public long? ResultRunId { get; init; }
    public required string MatchStatus { get; init; }
    public string? ExceptionCode { get; init; }
    public long? MatchedWithId { get; init; }
    public int? RawRowNumber { get; init; }
    public required List<string?> Values { get; init; }
}
