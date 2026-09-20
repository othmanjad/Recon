using System.Globalization;
using Microsoft.Data.SqlClient;
using Recon.Data;
using Recon.Domain.Configuration;
using Recon.Web.Controllers;

namespace Recon.Web.Services;

/// <summary>
/// Whether a dataset can actually read a file — the half of the activation
/// gate that used to be missing.
///
/// <para>
/// It lives here rather than on the datasets screen because three screens
/// need the same answer: the dataset's own activation panel, the definition
/// that pairs two datasets, and the rule builder that shows what still stands
/// between a definition and a run. A dataset was activated with no file format
/// at all, a definition was built on it, and the mistake surfaced at the first
/// upload as "has no file format effective on ..." — which is a configuration
/// error discovered at run time, the one thing those screens exist to prevent.
/// </para>
/// </summary>
public sealed class DatasetReadiness(SqlConnection connection)
{
    private readonly SqlConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    public Task<List<FormatRow>> FormatsAsync(int datasetId) =>
        Db.QueryAsync(
            _connection,
            """
            SELECT f.FileFormatId, f.FormatType, f.Version, f.EffectiveFrom, f.EffectiveTo,
                   f.Delimiter, f.TextQualifier, f.Encoding, f.HasHeader,
                   f.SkipLeadingLines, f.SkipTrailingLines, f.RecordPath, f.FileNamePattern,
                   f.MaxParseErrors,
                   (SELECT COUNT(*) FROM cfg.FieldMapping WHERE FileFormatId = f.FileFormatId)
            FROM cfg.FileFormatDefinition AS f
            WHERE f.DatasetId = @ds
            ORDER BY f.EffectiveFrom DESC, f.Version DESC;
            """,
            r => new FormatRow
            {
                FileFormatId = r.GetInt32(0),
                FormatType = r.GetString(1),
                Version = r.GetInt32(2),
                EffectiveFrom = DateOnly.FromDateTime(r.GetDateTime(3)),
                EffectiveTo = r.IsDBNull(4) ? null : DateOnly.FromDateTime(r.GetDateTime(4)),
                Delimiter = r.GetNullableString("Delimiter"),
                TextQualifier = r.GetNullableString("TextQualifier"),
                Encoding = r.GetString(7),
                HasHeader = r.GetBoolean(8),
                SkipLeadingLines = r.GetInt32(9),
                SkipTrailingLines = r.GetInt32(10),
                RecordPath = r.GetNullableString("RecordPath"),
                FileNamePattern = r.GetNullableString("FileNamePattern"),
                MaxParseErrors = r.GetInt32(13),
                MappingCount = r.GetInt32(14),
            },
            c => c.With("@ds", datasetId));

    /// <summary>
    /// The problems of one dataset's layout, fetched and judged together.
    /// </summary>
    public async Task<List<string>> ProblemsAsync(Dataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        var formats = await FormatsAsync(dataset.DatasetId).ConfigureAwait(false);
        return FormatProblems(dataset, formats);
    }

    /// <summary>
    /// Both sides of a definition, each problem named with the dataset it
    /// belongs to — "which side is broken" is the first thing anyone asks.
    /// </summary>
    public async Task<List<string>> ProblemsAsync(ReconciliationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var problems = new List<string>();

        foreach (var side in new[] { Side.Left, Side.Right })
        {
            var dataset = definition.DatasetFor(side);

            problems.AddRange((await ProblemsAsync(dataset).ConfigureAwait(false))
                .Select(p => $"{side} dataset {dataset.Code}: {p}"));
        }

        return problems;
    }

    /// <summary>
    /// Whether this dataset could actually parse a file today.
    ///
    /// <para>
    /// The roles gate asked whether the registry was complete and never
    /// whether there was a layout to read a file with, so a dataset with no
    /// file format — or with formats that all start tomorrow, or all ended
    /// yesterday — activated happily and failed at the first upload with
    /// "no file format effective on ...". That is a configuration mistake
    /// discovered at run time, which is the one thing this screen exists to
    /// prevent.
    /// </para>
    ///
    /// <para>
    /// Only for <see cref="ProviderType.File"/>: a dataset backed by a SQL
    /// view has no file to lay out, and demanding a format for one would be
    /// an invented rule.
    /// </para>
    /// </summary>
    public static List<string> FormatProblems(Dataset dataset, IReadOnlyList<FormatRow> formats)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(formats);

        var problems = new List<string>();

        if (dataset.Provider != ProviderType.File)
        {
            return problems;
        }

        if (formats.Count == 0)
        {
            problems.Add(
                "no file format: a file cannot be parsed without one, and guessing a layout " +
                "would stage wrong data");

            return problems;
        }

        var inForce = formats.FirstOrDefault(f => f.CoversToday);

        if (inForce is null)
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var ranges = string.Join(", ", formats
                .OrderBy(f => f.EffectiveFrom)
                .Select(f => $"v{f.Version} {f.EffectiveFrom:yyyy-MM-dd}→" +
                             $"{(f.EffectiveTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "open")}"));

            problems.Add(
                $"no file format is effective today ({today:yyyy-MM-dd}) — what exists covers " +
                $"{ranges}. A run for a date outside those ranges cannot parse its file.");

            return problems;
        }

        if (inForce.MappingCount == 0)
        {
            problems.Add(
                $"the format in force today (v{inForce.Version}) maps no fields, so it would " +
                "stage nothing");
        }

        return problems;
    }
}
