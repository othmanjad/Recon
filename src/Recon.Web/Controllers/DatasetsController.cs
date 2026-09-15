using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Recon.Data;
using Recon.Domain.Configuration;
using Recon.Engine.Parsing;
using Recon.Web.Services;

namespace Recon.Web.Controllers;

/// <summary>
/// Datasets and the field registry — the mapping editor of Phase 3.
/// </summary>
[Authorize]
public sealed class DatasetsController(
    PortalQueries queries,
    ConfigurationRepository config,
    Microsoft.Data.SqlClient.SqlConnection connection,
    AccessService access,
    AuditService audit) : Controller
{
    /// <summary>
    /// The dataset screen. <paramref name="blank"/> means "no dataset
    /// selected": without it, the editor always carried the first dataset's
    /// id and a second dataset could not be created from the portal at all —
    /// every save was an edit of whichever dataset happened to be first.
    /// </summary>
    public async Task<IActionResult> Index(int? id, int? formatId, bool blank = false)
    {
        ViewData["Title"] = "Datasets & fields";

        var datasets = await queries.DatasetsAsync(User).ConfigureAwait(false);
        var selectedId = blank ? null : id ?? datasets.FirstOrDefault()?.DatasetId;

        Dataset? selected = null;
        if (selectedId is { } datasetId && datasets.Any(d => d.DatasetId == datasetId))
        {
            selected = await config.LoadDatasetAsync(datasetId).ConfigureAwait(false);
        }

        var grants = await access.GrantsAsync(User).ConfigureAwait(false);

        ViewData["Slots"] = await SlotCatalogueAsync().ConfigureAwait(false);
        ViewData["Datasets"] = datasets;
        ViewData["Selected"] = selected;
        ViewData["SelectedRow"] = selected is null
            ? null
            : datasets.First(d => d.DatasetId == selected.DatasetId);

        ViewData["Problems"] = selected is null ? [] : ActivationProblems(selected);

        ViewData["Counterparties"] = (await queries.CounterpartiesAsync(User).ConfigureAwait(false))
            .Where(c => c.AccessLevel >= AccessLevel.Configure)
            .ToList();

        ViewData["Currencies"] = (await config.LoadCurrenciesAsync().ConfigureAwait(false))
            .Values.OrderBy(c => c.Code, StringComparer.Ordinal).ToList();

        ViewData["CanConfigure"] = selected is not null
            && grants.TryGetValue(selected.CounterpartyId, out var level)
            && level >= AccessLevel.Configure;

        // The file formats and, for the one being looked at, its mappings.
        // A dataset without a format cannot be loaded at all — the parser
        // would have to guess a layout — so the screen shows the absence
        // rather than leaving it to be discovered at 3am.
        var formats = selected is null
            ? []
            : await FormatsAsync(selected.DatasetId).ConfigureAwait(false);

        ViewData["Formats"] = formats;

        var format = formatId is { } chosen
            ? formats.FirstOrDefault(f => f.FileFormatId == chosen)
            : formats.FirstOrDefault();

        ViewData["Format"] = format;
        ViewData["Mappings"] = format is null
            ? new List<MappingRow>()
            : await MappingsAsync(format.FileFormatId).ConfigureAwait(false);

        return View();
    }

    /// <summary>
    /// Creates or edits a dataset.
    ///
    /// <para>
    /// A new dataset is created inactive and with no fields: it cannot be
    /// otherwise, because the universal roles are what activation checks and
    /// a dataset's fields are added one at a time below. That order is the
    /// onboarding sequence of design §2.1 rather than an accident of this
    /// form.
    /// </para>
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveDataset(
        int counterpartyId,
        int? datasetId,
        string code,
        string name,
        string providerType,
        string timeZone,
        string? defaultCurrency,
        string? duplicateKeyFields)
    {
        await access.RequireAsync(User, counterpartyId, AccessLevel.Configure).ConfigureAwait(false);

        // The duplicate key is a list of field codes, and a code that is not
        // in the registry would fail at load time — inside duplicate
        // detection, on a 2M-row file, at night.
        if (datasetId is { } existingId && !string.IsNullOrWhiteSpace(duplicateKeyFields))
        {
            var dataset = await config.LoadDatasetAsync(existingId).ConfigureAwait(false);

            foreach (var fieldCode in duplicateKeyFields
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!dataset.TryGetField(fieldCode, out _))
                {
                    TempData["Error"] =
                        $"'{fieldCode}' is not in {dataset.Code}'s field registry, so it cannot be " +
                        "part of the duplicate key. Duplicate detection would fail at load time.";

                    return RedirectToAction(nameof(Index), new { id = existingId });
                }
            }
        }

        try
        {
            if (datasetId is { } existing)
            {
                await Db.ExecuteAsync(
                    connection,
                    """
                    UPDATE cfg.Dataset
                    SET Code = @code, Name = @name,
                        ProviderType = @provider, TimeZone = @zone,
                        DefaultCurrency = @currency, DuplicateKeyFields = @dupKey
                    WHERE DatasetId = @id;
                    """,
                    Bind(existing)).ConfigureAwait(false);

                await audit.RecordAsync(User, "Dataset", existing, AuditAction.Update,
                    after: new { code, name, providerType, duplicateKeyFields }).ConfigureAwait(false);

                TempData["Ok"] = $"{code} saved.";
                return RedirectToAction(nameof(Index), new { id = existing });
            }

            var id = await Db.ScalarAsync<int>(
                connection,
                """
                INSERT cfg.Dataset
                    (CounterpartyId, Code, Name, ProviderType, TimeZone,
                     DefaultCurrency, DuplicateKeyFields, IsActive, CreatedBy)
                VALUES (@cp, @code, @name, @provider, @zone,
                        @currency, @dupKey, 0, @by);
                SELECT CAST(SCOPE_IDENTITY() AS INT);
                """,
                Bind(null)).ConfigureAwait(false);

            await audit.RecordAsync(User, "Dataset", id, AuditAction.Create,
                after: new { code, name, providerType, counterpartyId }).ConfigureAwait(false);

            TempData["Ok"] =
                $"{code} created, inactive and with no fields. Add its registry below, then a file " +
                "format and its mappings, then activate it.";

            return RedirectToAction(nameof(Index), new { id });
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            TempData["Error"] = "The database refused this dataset: " + ex.Message;
            return RedirectToAction(nameof(Index), new { id = datasetId });
        }

        Action<Microsoft.Data.SqlClient.SqlCommand> Bind(int? id) => command =>
        {
            if (id is { } value)
            {
                command.With("@id", value);
            }

            command.With("@cp", counterpartyId)
                   .With("@code", code?.Trim())
                   .With("@name", name?.Trim())
                   .With("@provider", providerType)
                   .With("@zone", string.IsNullOrWhiteSpace(timeZone) ? "Asia/Amman" : timeZone.Trim())
                   .With("@currency", string.IsNullOrWhiteSpace(defaultCurrency) ? null : defaultCurrency)
                   .With("@dupKey", string.IsNullOrWhiteSpace(duplicateKeyFields)
                       ? null
                       : string.Join(",", duplicateKeyFields.Split(',',
                           StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
                   .With("@by", access.UserName(User));
        };
    }

    /// <summary>
    /// Creates or edits a file format.
    ///
    /// <para>
    /// Formats are effective-dated and versioned, which is what lets a file
    /// from 2024 still be read correctly after the layout changes in 2026
    /// (§8). A layout change is therefore a NEW version with a later
    /// effective date — editing the old one in place would silently change
    /// how every historical re-run parses.
    /// </para>
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveFormat(
        int datasetId,
        int? fileFormatId,
        string formatType,
        int version,
        DateOnly effectiveFrom,
        DateOnly? effectiveTo,
        string? delimiter,
        string? textQualifier,
        string encoding,
        bool hasHeader,
        int skipLeadingLines,
        int skipTrailingLines,
        string? recordPath,
        string? fileNamePattern,
        int maxParseErrors)
    {
        var counterpartyId = await CounterpartyOfDatasetAsync(datasetId).ConfigureAwait(false);
        await access.RequireAsync(User, counterpartyId, AccessLevel.Configure).ConfigureAwait(false);

        // Each reader needs the one thing it cannot work without, and finding
        // that out at load time means a failed run rather than a message.
        if (formatType == "Csv" && string.IsNullOrEmpty(delimiter))
        {
            TempData["Error"] = "A CSV format needs a delimiter.";
            return RedirectToAction(nameof(Index), new { id = datasetId });
        }

        if (formatType is "Xml" or "Json" && string.IsNullOrWhiteSpace(recordPath))
        {
            TempData["Error"] =
                $"A {formatType} format needs a record path: without it the reader does not know " +
                "where one record ends and the next begins.";

            return RedirectToAction(nameof(Index), new { id = datasetId });
        }

        if (formatType == "FixedWidth")
        {
            TempData["Error"] =
                "FixedWidth is in the schema's list but has no reader yet — it needs a column " +
                "layout the field mapping does not carry. It waits on a real fixed-width file " +
                "rather than a guess at one.";

            return RedirectToAction(nameof(Index), new { id = datasetId });
        }

        try
        {
            if (fileFormatId is { } existing)
            {
                await Db.ExecuteAsync(
                    connection,
                    """
                    UPDATE cfg.FileFormatDefinition
                    SET FormatType = @type, Version = @version,
                        EffectiveFrom = @from, EffectiveTo = @to,
                        Delimiter = @delimiter, TextQualifier = @qualifier, Encoding = @encoding,
                        HasHeader = @header, SkipLeadingLines = @skipLeading,
                        SkipTrailingLines = @skipTrailing, RecordPath = @recordPath,
                        FileNamePattern = @pattern, MaxParseErrors = @maxErrors
                    WHERE FileFormatId = @id;
                    """,
                    BindFormat(existing)).ConfigureAwait(false);

                await audit.RecordAsync(User, "FileFormatDefinition", existing, AuditAction.Update,
                    after: new { formatType, version, effectiveFrom, effectiveTo }).ConfigureAwait(false);

                TempData["Ok"] = $"Format version {version} saved.";
                return RedirectToAction(nameof(Index), new { id = datasetId, formatId = existing });
            }

            var id = await Db.ScalarAsync<int>(
                connection,
                """
                INSERT cfg.FileFormatDefinition
                    (DatasetId, FormatType, Version, EffectiveFrom, EffectiveTo, Delimiter,
                     TextQualifier, Encoding, HasHeader, SkipLeadingLines, SkipTrailingLines,
                     RecordPath, FileNamePattern, MaxParseErrors)
                VALUES (@ds, @type, @version, @from, @to, @delimiter,
                        @qualifier, @encoding, @header, @skipLeading, @skipTrailing,
                        @recordPath, @pattern, @maxErrors);
                SELECT CAST(SCOPE_IDENTITY() AS INT);
                """,
                BindFormat(null)).ConfigureAwait(false);

            await audit.RecordAsync(User, "FileFormatDefinition", id, AuditAction.Create,
                after: new { datasetId, formatType, version, effectiveFrom }).ConfigureAwait(false);

            TempData["Ok"] =
                $"Format version {version} created. Map its fields below — a format with no " +
                "mappings stages nothing.";

            return RedirectToAction(nameof(Index), new { id = datasetId, formatId = id });
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            TempData["Error"] = "The database refused this format: " + ex.Message;
            return RedirectToAction(nameof(Index), new { id = datasetId });
        }

        Action<Microsoft.Data.SqlClient.SqlCommand> BindFormat(int? id) => command =>
        {
            if (id is { } value)
            {
                command.With("@id", value);
            }

            command.With("@ds", datasetId)
                   .With("@type", formatType)
                   .With("@version", version)
                   .With("@from", effectiveFrom.ToDateTime(TimeOnly.MinValue))
                   .With("@to", effectiveTo?.ToDateTime(TimeOnly.MinValue))
                   .With("@delimiter", string.IsNullOrEmpty(delimiter) ? null : delimiter)
                   .With("@qualifier", string.IsNullOrEmpty(textQualifier) ? null : textQualifier)
                   .With("@encoding", string.IsNullOrWhiteSpace(encoding) ? "UTF-8" : encoding.Trim())
                   .With("@header", hasHeader)
                   .With("@skipLeading", skipLeadingLines)
                   .With("@skipTrailing", skipTrailingLines)
                   .With("@recordPath", string.IsNullOrWhiteSpace(recordPath) ? null : recordPath.Trim())
                   .With("@pattern", string.IsNullOrWhiteSpace(fileNamePattern) ? null : fileNamePattern.Trim())
                   .With("@maxErrors", maxParseErrors);
        };
    }

    /// <summary>
    /// Creates or edits one field mapping: where in the file this registry
    /// field comes from, how to parse it, and what to do to it first.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveMapping(
        int datasetId,
        int fileFormatId,
        int? fieldMappingId,
        string fieldCode,
        string sourcePath,
        string? parseFormat,
        string? transformChainJson,
        string? defaultValue,
        bool isRequired)
    {
        var counterpartyId = await CounterpartyOfDatasetAsync(datasetId).ConfigureAwait(false);
        await access.RequireAsync(User, counterpartyId, AccessLevel.Configure).ConfigureAwait(false);

        var dataset = await config.LoadDatasetAsync(datasetId).ConfigureAwait(false);

        // Resolved against the registry, like everything else: a mapping is
        // how a file's column becomes a field, and a field that is not in the
        // registry has no slot to be written to.
        if (!dataset.TryGetField(fieldCode?.Trim() ?? string.Empty, out var field))
        {
            TempData["Error"] = $"'{fieldCode}' is not in {dataset.Code}'s field registry.";
            return RedirectToAction(nameof(Index), new { id = datasetId, formatId = fileFormatId });
        }

        // The transform chain is validated by the engine's own parser, so the
        // portal accepts exactly what the loader will accept — an unknown
        // operation or a missing argument is refused here rather than at 3am.
        if (!string.IsNullOrWhiteSpace(transformChainJson))
        {
            try
            {
                var steps = Transforms.Parse(transformChainJson);
                TempData["Ok"] = $"{steps.Count} transform step(s) accepted.";
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException
                                          or TransformException
                                          or ArgumentException or InvalidOperationException)
            {
                TempData["Error"] = "The transform chain was rejected: " + ex.Message;
                return RedirectToAction(nameof(Index), new { id = datasetId, formatId = fileFormatId });
            }
        }

        try
        {
            if (fieldMappingId is { } existing)
            {
                await Db.ExecuteAsync(
                    connection,
                    """
                    UPDATE cfg.FieldMapping
                    SET DatasetFieldId = @field, SourcePath = @path, ParseFormat = @format,
                        TransformChainJson = @transforms, DefaultValue = @default,
                        IsRequired = @required
                    WHERE FieldMappingId = @id;
                    """,
                    BindMapping(existing)).ConfigureAwait(false);
            }
            else
            {
                await Db.ExecuteAsync(
                    connection,
                    """
                    INSERT cfg.FieldMapping
                        (FileFormatId, DatasetFieldId, SourcePath, ParseFormat,
                         TransformChainJson, DefaultValue, IsRequired)
                    VALUES (@fmt, @field, @path, @format, @transforms, @default, @required);
                    """,
                    BindMapping(null)).ConfigureAwait(false);
            }

            await audit.RecordAsync(
                User, "FieldMapping", fieldMappingId ?? 0,
                fieldMappingId is null ? AuditAction.Create : AuditAction.Update,
                after: new { fileFormatId, field.FieldCode, sourcePath, parseFormat, isRequired })
                .ConfigureAwait(false);

            TempData["Ok"] = $"{field.FieldCode} ← {sourcePath} saved.";
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            // Most likely the unique constraint on (format, field): one
            // mapping per field, or the parser's source for it would depend
            // on read order.
            TempData["Error"] = "The database refused this mapping: " + ex.Message;
        }

        return RedirectToAction(nameof(Index), new { id = datasetId, formatId = fileFormatId });

        Action<Microsoft.Data.SqlClient.SqlCommand> BindMapping(int? id) => command =>
        {
            if (id is { } value)
            {
                command.With("@id", value);
            }

            command.With("@fmt", fileFormatId)
                   .With("@field", field.DatasetFieldId)
                   .With("@path", sourcePath?.Trim())
                   .With("@format", string.IsNullOrWhiteSpace(parseFormat) ? null : parseFormat.Trim())
                   .With("@transforms", string.IsNullOrWhiteSpace(transformChainJson)
                       ? null
                       : transformChainJson.Trim())
                   .With("@default", string.IsNullOrWhiteSpace(defaultValue) ? null : defaultValue)
                   .With("@required", isRequired);
        };
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteMapping(int datasetId, int fileFormatId, int fieldMappingId)
    {
        var counterpartyId = await CounterpartyOfDatasetAsync(datasetId).ConfigureAwait(false);
        await access.RequireAsync(User, counterpartyId, AccessLevel.Configure).ConfigureAwait(false);

        await Db.ExecuteAsync(
            connection, "DELETE cfg.FieldMapping WHERE FieldMappingId = @id;",
            c => c.With("@id", fieldMappingId)).ConfigureAwait(false);

        await audit.RecordAsync(User, "FieldMapping", fieldMappingId, AuditAction.Delete)
            .ConfigureAwait(false);

        TempData["Ok"] = "Mapping removed.";
        return RedirectToAction(nameof(Index), new { id = datasetId, formatId = fileFormatId });
    }

    private Task<List<FormatRow>> FormatsAsync(int datasetId) =>
        Db.QueryAsync(
            connection,
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

    private Task<List<MappingRow>> MappingsAsync(int fileFormatId) =>
        Db.QueryAsync(
            connection,
            """
            SELECT m.FieldMappingId, f.FieldCode, f.DisplayLabel, f.DataType, f.StorageSlot,
                   m.SourcePath, m.ParseFormat, m.TransformChainJson, m.DefaultValue, m.IsRequired
            FROM cfg.FieldMapping AS m
            JOIN cfg.DatasetField AS f ON f.DatasetFieldId = m.DatasetFieldId
            WHERE m.FileFormatId = @fmt
            ORDER BY f.DisplayOrder;
            """,
            r => new MappingRow
            {
                FieldMappingId = r.GetInt32(0),
                FieldCode = r.GetString(1),
                DisplayLabel = r.GetString(2),
                DataType = r.GetString(3),
                StorageSlot = r.GetString(4),
                SourcePath = r.GetString(5),
                ParseFormat = r.GetNullableString("ParseFormat"),
                TransformChainJson = r.GetNullableString("TransformChainJson"),
                DefaultValue = r.GetNullableString("DefaultValue"),
                IsRequired = r.GetBoolean(9),
            },
            c => c.With("@fmt", fileFormatId));

    /// <summary>
    /// The activation gate for one dataset (review item B6).
    ///
    /// <para>
    /// A dataset cannot be activated without the universal roles: control
    /// totals, partitioning and fee logic all rely on them, so a definition
    /// missing one would run and produce numbers nobody can check.
    /// </para>
    /// </summary>
    internal static List<string> ActivationProblems(Dataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        var problems = new List<string>();

        var missing = ReconciliationDefinition.RequiredRoles
            .Where(role => dataset.FieldWithRole(role) is null)
            .ToList();

        if (missing.Count > 0)
        {
            problems.Add(
                $"missing universal role(s): {string.Join(", ", missing)} — control totals, " +
                "partitioning and fee logic all rely on them");
        }

        var indexed = dataset.Fields.Count(f => f.IsIndexed);
        if (indexed > 4)
        {
            problems.Add(
                $"{indexed} fields are marked indexed, above the cap of 4 — every extra index " +
                "is a 2M-row maintenance cost on every load");
        }

        // A companion slot outside the companion pool is the silent
        // data-corruption path the schema now blocks; saying so here means the
        // operator sees it before the database refuses the save.
        foreach (var field in dataset.Fields.Where(f => f.NormalizeForMatch))
        {
            if (field.NormalizedSlot is null)
            {
                problems.Add($"{field.FieldCode} is marked for normalization but has no companion slot");
            }
            else if (!field.NormalizedSlot.StartsWith("Text", StringComparison.Ordinal))
            {
                problems.Add($"{field.FieldCode}'s companion slot must be a text slot");
            }
        }

        return problems;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveField(
        int datasetId,
        int? datasetFieldId,
        string fieldCode,
        string displayLabel,
        string dataType,
        string? fieldRole,
        string storageSlot,
        bool isMatchable,
        bool isIndexed,
        bool isRequired,
        bool normalizeForMatch,
        string? normalizedSlot,
        int displayOrder)
    {
        var counterpartyId = await CounterpartyOfDatasetAsync(datasetId).ConfigureAwait(false);
        await access.RequireAsync(User, counterpartyId, AccessLevel.Configure).ConfigureAwait(false);

        try
        {
            if (datasetFieldId is { } existing)
            {
                var before = await FieldSnapshotAsync(existing).ConfigureAwait(false);

                await Db.ExecuteAsync(
                    connection,
                    """
                    UPDATE cfg.DatasetField
                    SET FieldCode = @code, DisplayLabel = @label, DataType = @type,
                        FieldRole = @role, StorageSlot = @slot,
                        IsMatchable = @matchable, IsIndexed = @indexed, IsRequired = @required,
                        NormalizeForMatch = @normalize, NormalizedSlot = @normSlot,
                        DisplayOrder = @order
                    WHERE DatasetFieldId = @id;
                    """,
                    Bind(existing)).ConfigureAwait(false);

                await audit.RecordAsync(User, "DatasetField", existing, AuditAction.Update,
                    before, after: new { fieldCode, storageSlot, fieldRole, isMatchable })
                    .ConfigureAwait(false);

                TempData["Ok"] = $"Field {fieldCode} updated.";
            }
            else
            {
                var id = await Db.ScalarAsync<int>(
                    connection,
                    """
                    INSERT cfg.DatasetField
                        (DatasetId, FieldCode, DisplayLabel, DataType, FieldRole, StorageSlot,
                         IsMatchable, IsIndexed, IsRequired, NormalizeForMatch, NormalizedSlot,
                         DisplayOrder)
                    VALUES (@ds, @code, @label, @type, @role, @slot,
                            @matchable, @indexed, @required, @normalize, @normSlot, @order);
                    SELECT CAST(SCOPE_IDENTITY() AS INT);
                    """,
                    Bind(null)).ConfigureAwait(false);

                await audit.RecordAsync(User, "DatasetField", id, AuditAction.Create,
                    after: new { fieldCode, storageSlot, fieldRole }).ConfigureAwait(false);

                TempData["Ok"] = $"Field {fieldCode} added.";
            }
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            // The database enforces the slot pools, the type match and the
            // uniqueness. Its message names the actual rule, which is more
            // useful than a paraphrase — so it is surfaced rather than
            // replaced.
            TempData["Error"] = "The database refused this field: " + ex.Message;
        }

        return RedirectToAction(nameof(Index), new { id = datasetId });

        Action<Microsoft.Data.SqlClient.SqlCommand> Bind(int? id) => command =>
        {
            if (id is { } value)
            {
                command.With("@id", value);
            }

            command.With("@ds", datasetId)
                   .With("@code", fieldCode?.Trim())
                   .With("@label", displayLabel?.Trim())
                   .With("@type", dataType)
                   .With("@role", string.IsNullOrWhiteSpace(fieldRole) ? null : fieldRole)
                   .With("@slot", storageSlot)
                   .With("@matchable", isMatchable)
                   .With("@indexed", isIndexed)
                   .With("@required", isRequired)
                   .With("@normalize", normalizeForMatch)
                   .With("@normSlot", string.IsNullOrWhiteSpace(normalizedSlot) ? null : normalizedSlot)
                   .With("@order", displayOrder);
        };
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Activate(int id, bool active)
    {
        var counterpartyId = await CounterpartyOfDatasetAsync(id).ConfigureAwait(false);
        await access.RequireAsync(User, counterpartyId, AccessLevel.Configure).ConfigureAwait(false);

        var dataset = await config.LoadDatasetAsync(id).ConfigureAwait(false);

        if (active)
        {
            var problems = ActivationProblems(dataset);
            if (problems.Count > 0)
            {
                TempData["Error"] = "Cannot activate: " + string.Join("; ", problems);
                return RedirectToAction(nameof(Index), new { id });
            }
        }

        await Db.ExecuteAsync(
            connection,
            """
            UPDATE cfg.Dataset
            SET IsActive = @active,
                ActivatedAt = CASE WHEN @active = 1 THEN SYSDATETIME() ELSE ActivatedAt END
            WHERE DatasetId = @id;
            """,
            c => c.With("@id", id).With("@active", active)).ConfigureAwait(false);

        await audit.RecordAsync(
            User, "Dataset", id,
            active ? AuditAction.Activate : AuditAction.Deactivate,
            after: new { dataset.Code, active }).ConfigureAwait(false);

        TempData["Ok"] = $"{dataset.Code} {(active ? "activated" : "deactivated")}.";
        return RedirectToAction(nameof(Index), new { id });
    }

    private Task<List<SlotOption>> SlotCatalogueAsync() =>
        Db.QueryAsync(
            connection,
            """
            SELECT SlotName, SlotType, MaxLength, IsNormalizedOnly
            FROM cfg.StorageSlotCatalogue
            ORDER BY SlotType, LEN(SlotName), SlotName;
            """,
            r => new SlotOption(
                r.GetString(0), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetInt32(2), r.GetBoolean(3)));

    private async Task<int> CounterpartyOfDatasetAsync(int datasetId)
    {
        var id = await Db.ScalarAsync<int?>(
            connection,
            "SELECT CounterpartyId FROM cfg.Dataset WHERE DatasetId = @id;",
            c => c.With("@id", datasetId)).ConfigureAwait(false);

        return id ?? throw new InvalidOperationException($"dataset {datasetId} was not found");
    }

    private Task<List<object>> FieldSnapshotAsync(int fieldId) =>
        Db.QueryAsync<object>(
            connection,
            """
            SELECT FieldCode, DisplayLabel, DataType, FieldRole, StorageSlot,
                   IsMatchable, IsIndexed, NormalizeForMatch, NormalizedSlot
            FROM cfg.DatasetField WHERE DatasetFieldId = @id;
            """,
            r => new
            {
                FieldCode = r.GetString(0),
                DisplayLabel = r.GetString(1),
                DataType = r.GetString(2),
                FieldRole = r.IsDBNull(3) ? null : r.GetString(3),
                StorageSlot = r.GetString(4),
                IsMatchable = r.GetBoolean(5),
                IsIndexed = r.GetBoolean(6),
                NormalizeForMatch = r.GetBoolean(7),
                NormalizedSlot = r.IsDBNull(8) ? null : r.GetString(8),
            },
            c => c.With("@id", fieldId));
}

public sealed record SlotOption(string Name, string Type, int? MaxLength, bool NormalizedOnly);

public sealed record FormatRow
{
    public required int FileFormatId { get; init; }
    public required string FormatType { get; init; }
    public required int Version { get; init; }
    public required DateOnly EffectiveFrom { get; init; }
    public DateOnly? EffectiveTo { get; init; }
    public string? Delimiter { get; init; }
    public string? TextQualifier { get; init; }
    public required string Encoding { get; init; }
    public required bool HasHeader { get; init; }
    public required int SkipLeadingLines { get; init; }
    public required int SkipTrailingLines { get; init; }
    public string? RecordPath { get; init; }
    public string? FileNamePattern { get; init; }
    public required int MaxParseErrors { get; init; }
    public required int MappingCount { get; init; }

    public bool CoversToday =>
        EffectiveFrom <= DateOnly.FromDateTime(DateTime.Today)
        && (EffectiveTo is null || EffectiveTo >= DateOnly.FromDateTime(DateTime.Today));
}

public sealed record MappingRow
{
    public required int FieldMappingId { get; init; }
    public required string FieldCode { get; init; }
    public required string DisplayLabel { get; init; }
    public required string DataType { get; init; }
    public required string StorageSlot { get; init; }
    public required string SourcePath { get; init; }
    public string? ParseFormat { get; init; }
    public string? TransformChainJson { get; init; }
    public string? DefaultValue { get; init; }
    public required bool IsRequired { get; init; }
}
