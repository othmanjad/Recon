using Microsoft.Data.SqlClient;
using Recon.Data;
using Recon.Domain.Configuration;

namespace Recon.Engine.Parsing;

/// <summary>
/// Loads the file format effective for a business date, with its mappings.
///
/// <para>
/// Effective-dated versioning is what lets a 2024 file still be read correctly
/// after the layout changes in 2026 (§8). Picking "the latest format" instead
/// would break every historical re-run, so the date decides.
/// </para>
/// </summary>
public static class FileFormatLoader
{
    public static async Task<FileFormat> LoadAsync(
        SqlConnection connection,
        Dataset dataset,
        DateOnly businessDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        var formats = await Db.QueryAsync(
            connection,
            """
            SELECT FileFormatId, FormatType, Version, EffectiveFrom, EffectiveTo,
                   Delimiter, TextQualifier, HasHeader, SkipLeadingLines, SkipTrailingLines,
                   FileNamePattern, MaxParseErrors, RecordPath
            FROM cfg.FileFormatDefinition
            WHERE DatasetId = @dataset
              AND EffectiveFrom <= @date
              AND (EffectiveTo IS NULL OR EffectiveTo >= @date)
            ORDER BY Version DESC;
            """,
            r => new
            {
                FileFormatId = r.GetInt32(0),
                Type = Db.ParseEnum<FileFormatType>(r.GetString(1)),
                Version = r.GetInt32(2),
                EffectiveFrom = DateOnly.FromDateTime(r.GetDateTime(3)),
                EffectiveTo = r.IsDBNull(4) ? (DateOnly?)null : DateOnly.FromDateTime(r.GetDateTime(4)),
                Delimiter = r.GetNullableString("Delimiter"),
                TextQualifier = r.GetNullableString("TextQualifier"),
                HasHeader = r.GetBoolean(7),
                SkipLeading = r.GetInt32(8),
                SkipTrailing = r.GetInt32(9),
                FileNamePattern = r.GetNullableString("FileNamePattern"),
                MaxParseErrors = r.GetInt32(11),
                // XML and JSON cannot be read without it: the reader would
                // not know where one record ends and the next begins.
                RecordPath = r.GetNullableString("RecordPath"),
            },
            c => c.With("@dataset", dataset.DatasetId)
                  .With("@date", businessDate.ToDateTime(TimeOnly.MinValue)),
            cancellationToken).ConfigureAwait(false);

        if (formats.Count == 0)
        {
            throw new InvalidOperationException(
                $"dataset {dataset.Code} has no file format effective on {businessDate:yyyy-MM-dd}. " +
                "A file cannot be parsed without one, and guessing a layout would stage wrong data.");
        }

        var format = formats[0];

        var mappings = await Db.QueryAsync(
            connection,
            """
            SELECT m.SourcePath, f.FieldCode, m.ParseFormat, m.TransformChainJson,
                   m.DefaultValue, m.IsRequired
            FROM cfg.FieldMapping AS m
            JOIN cfg.DatasetField AS f ON f.DatasetFieldId = m.DatasetFieldId
            WHERE m.FileFormatId = @format
            ORDER BY m.FieldMappingId;
            """,
            r => new FieldMapping
            {
                SourcePath = r.GetString(0),
                Field = dataset.GetField(r.GetString(1)),
                ParseFormat = r.GetNullableString("ParseFormat"),
                Transforms = Transforms.Parse(r.GetNullableString("TransformChainJson")),
                DefaultValue = r.GetNullableString("DefaultValue"),
                IsRequired = r.GetBoolean(5),
            },
            c => c.With("@format", format.FileFormatId),
            cancellationToken).ConfigureAwait(false);

        return new FileFormat
        {
            FileFormatId = format.FileFormatId,
            Type = format.Type,
            Version = format.Version,
            EffectiveFrom = format.EffectiveFrom,
            EffectiveTo = format.EffectiveTo,
            FileNamePattern = format.FileNamePattern,
            MaxParseErrors = format.MaxParseErrors,
            RecordPath = format.RecordPath,
            Csv = new CsvOptions
            {
                Delimiter = string.IsNullOrEmpty(format.Delimiter) ? ',' : format.Delimiter[0],
                TextQualifier = string.IsNullOrEmpty(format.TextQualifier)
                    ? null
                    : format.TextQualifier[0],
                HasHeader = format.HasHeader,
                SkipLeadingLines = format.SkipLeading,
                SkipTrailingLines = format.SkipTrailing,
            },
            Mappings = mappings,
        };
    }
}
