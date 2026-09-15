using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Recon.Data;
using Recon.Domain.Configuration;
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
    public async Task<IActionResult> Index(int? id)
    {
        ViewData["Title"] = "Datasets & fields";

        var datasets = await queries.DatasetsAsync(User).ConfigureAwait(false);
        var selectedId = id ?? datasets.FirstOrDefault()?.DatasetId;

        Dataset? selected = null;
        if (selectedId is { } datasetId && datasets.Any(d => d.DatasetId == datasetId))
        {
            selected = await config.LoadDatasetAsync(datasetId).ConfigureAwait(false);
        }

        ViewData["Slots"] = await SlotCatalogueAsync().ConfigureAwait(false);
        ViewData["Datasets"] = datasets;
        ViewData["Selected"] = selected;
        ViewData["Problems"] = selected is null ? [] : ActivationProblems(selected);

        return View();
    }

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
