using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Recon.Data;
using Recon.Domain.Conditions;
using Recon.Domain.Configuration;
using Recon.Web.Services;

namespace Recon.Web.Controllers;

/// <summary>
/// The visual rule builder, and the sandbox dry-run beside it.
///
/// <para>
/// Every field dropdown is populated from the dataset's field registry, and
/// every condition is stored as ids resolved from it. That is the design's
/// single most important safety property (§6): rule conditions resolve field
/// names against the registry and never from free user text, which is what
/// prevents SQL injection through a builder the user drives.
/// </para>
/// </summary>
[Authorize]
public sealed class RulesController(
    PortalQueries queries,
    ConfigurationRepository config,
    Microsoft.Data.SqlClient.SqlConnection connection,
    AccessService access,
    AuditService audit,
    SandboxService sandbox) : Controller
{
    public async Task<IActionResult> Index(int? id)
    {
        ViewData["Title"] = "Rule builder";

        var grants = await access.GrantsAsync(User).ConfigureAwait(false);
        var definitions = await queries.DefinitionsAsync(grants.Keys.ToList()).ConfigureAwait(false);
        var selectedId = id ?? definitions.FirstOrDefault()?.DefinitionId;

        ReconciliationDefinition? definition = null;
        if (selectedId is { } definitionId && definitions.Any(d => d.DefinitionId == definitionId))
        {
            definition = await config.LoadDefinitionAsync(definitionId).ConfigureAwait(false);
            ViewData["Replayable"] = await sandbox.ReplayableRunsAsync(definitionId)
                .ConfigureAwait(false);
        }

        ViewData["Definitions"] = definitions;
        ViewData["Selected"] = definition;
        ViewData["Problems"] = definition?.ActivationProblems() ?? [];

        return View();
    }

    /// <summary>
    /// Saves one pass and its conditions.
    ///
    /// <para>
    /// Field codes arrive from the form and are resolved against the registry
    /// here. A code that is not in it, or is in it but not matchable, is
    /// refused before anything is written — the same gate the compiler applies,
    /// applied earlier so the operator sees a message rather than a failed run.
    /// </para>
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SavePass(
        int definitionId,
        int? matchRuleId,
        string ruleCode,
        string name,
        int sequence,
        string matchMode,
        string onMultipleMatch,
        string? leftFilterJson,
        string[]? leftFieldCodes,
        string[]? rightFieldCodes,
        string[]? comparisons,
        long?[]? tolerances,
        string[]? toleranceUnits,
        bool[]? useNormalized)
    {
        var definition = await config.LoadDefinitionAsync(definitionId).ConfigureAwait(false);
        await access.RequireAsync(User, definition.CounterpartyId, AccessLevel.Configure)
            .ConfigureAwait(false);

        // The filter is validated against the LEFT registry before it is
        // stored, so an unknown field never reaches the compiler.
        if (!string.IsNullOrWhiteSpace(leftFilterJson))
        {
            var result = ConditionValidator.ValidateJson(leftFilterJson, definition.Left);
            if (!result.IsValid)
            {
                TempData["Error"] = "The row filter was rejected: " + string.Join("; ", result.Errors);
                return RedirectToAction(nameof(Index), new { id = definitionId });
            }
        }

        var conditions = BuildConditions(
            definition, leftFieldCodes, rightFieldCodes, comparisons,
            tolerances, toleranceUnits, useNormalized, out var error);

        if (error is not null)
        {
            TempData["Error"] = error;
            return RedirectToAction(nameof(Index), new { id = definitionId });
        }

        if (conditions.Count == 0)
        {
            TempData["Error"] =
                "A pass needs at least one condition. A pass with none would match every row " +
                "against every row.";

            return RedirectToAction(nameof(Index), new { id = definitionId });
        }

        try
        {
            int ruleId;

            if (matchRuleId is { } existing)
            {
                await Db.ExecuteAsync(
                    connection,
                    """
                    UPDATE cfg.MatchRule
                    SET RuleCode = @code, Name = @name, Sequence = @seq,
                        MatchMode = @mode, OnMultipleMatch = @multi, LeftFilterJson = @filter
                    WHERE MatchRuleId = @id;

                    DELETE cfg.MatchCondition WHERE MatchRuleId = @id;
                    """,
                    c => c.With("@id", existing)
                          .With("@code", ruleCode.Trim())
                          .With("@name", name.Trim())
                          .With("@seq", sequence)
                          .With("@mode", matchMode)
                          .With("@multi", onMultipleMatch)
                          .With("@filter", string.IsNullOrWhiteSpace(leftFilterJson) ? null : leftFilterJson))
                    .ConfigureAwait(false);

                ruleId = existing;
            }
            else
            {
                ruleId = await Db.ScalarAsync<int>(
                    connection,
                    """
                    INSERT cfg.MatchRule
                        (DefinitionId, RuleCode, Name, Sequence, MatchMode, OnMultipleMatch,
                         LeftFilterJson)
                    VALUES (@def, @code, @name, @seq, @mode, @multi, @filter);
                    SELECT CAST(SCOPE_IDENTITY() AS INT);
                    """,
                    c => c.With("@def", definitionId)
                          .With("@code", ruleCode.Trim())
                          .With("@name", name.Trim())
                          .With("@seq", sequence)
                          .With("@mode", matchMode)
                          .With("@multi", onMultipleMatch)
                          .With("@filter", string.IsNullOrWhiteSpace(leftFilterJson) ? null : leftFilterJson))
                    .ConfigureAwait(false);
            }

            var order = 1;
            foreach (var condition in conditions)
            {
                await Db.ExecuteAsync(
                    connection,
                    """
                    INSERT cfg.MatchCondition
                        (MatchRuleId, LeftFieldId, RightFieldId, ComparisonType,
                         ToleranceValue, ToleranceUnit, UseNormalized, Sequence)
                    VALUES (@rule, @left, @right, @cmp, @tol, @unit, @norm, @seq);
                    """,
                    c => c.With("@rule", ruleId)
                          .With("@left", condition.LeftFieldId)
                          .With("@right", condition.RightFieldId)
                          .With("@cmp", condition.Comparison)
                          .With("@tol", condition.Tolerance)
                          .With("@unit", condition.Unit)
                          .With("@norm", condition.UseNormalized)
                          .With("@seq", order++))
                    .ConfigureAwait(false);
            }

            await audit.RecordAsync(
                User, "MatchRule", ruleId,
                matchRuleId is null ? AuditAction.Create : AuditAction.Update,
                after: new { ruleCode, sequence, matchMode, conditions = conditions.Count })
                .ConfigureAwait(false);

            TempData["Ok"] = $"Pass {sequence} ({ruleCode}) saved with {conditions.Count} condition(s).";
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            TempData["Error"] = "The database refused this pass: " + ex.Message;
        }

        return RedirectToAction(nameof(Index), new { id = definitionId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeletePass(int definitionId, int matchRuleId)
    {
        var definition = await config.LoadDefinitionAsync(definitionId).ConfigureAwait(false);
        await access.RequireAsync(User, definition.CounterpartyId, AccessLevel.Configure)
            .ConfigureAwait(false);

        await Db.ExecuteAsync(
            connection,
            """
            DELETE cfg.MatchCondition WHERE MatchRuleId = @id;
            DELETE cfg.MatchRule WHERE MatchRuleId = @id;
            """,
            c => c.With("@id", matchRuleId)).ConfigureAwait(false);

        await audit.RecordAsync(User, "MatchRule", matchRuleId, AuditAction.Delete)
            .ConfigureAwait(false);

        TempData["Ok"] = "Pass removed.";
        return RedirectToAction(nameof(Index), new { id = definitionId });
    }

    /// <summary>
    /// Validates a condition tree without saving it — what the builder's
    /// Validate button calls.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ValidateFilter(int definitionId, string side, string json)
    {
        var definition = await config.LoadDefinitionAsync(definitionId).ConfigureAwait(false);
        var dataset = string.Equals(side, "Right", StringComparison.OrdinalIgnoreCase)
            ? definition.Right
            : definition.Left;

        var result = ConditionValidator.ValidateJson(json, dataset);

        return Json(new
        {
            valid = result.IsValid,
            errors = result.Errors,
            dataset = dataset.Code,
        });
    }

    /// <summary>
    /// The sandbox dry-run (design §2.1 step 6).
    ///
    /// <para>
    /// The design calls this non-optional: without it Operations activates
    /// broken rules against production data, and the first time they do it the
    /// platform loses its credibility.
    /// </para>
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DryRun(int definitionId, long sourceRunId)
    {
        var definition = await config.LoadDefinitionAsync(definitionId).ConfigureAwait(false);
        await access.RequireAsync(User, definition.CounterpartyId, AccessLevel.Operate)
            .ConfigureAwait(false);

        try
        {
            var result = await sandbox.DryRunAsync(definitionId, sourceRunId, access.UserName(User))
                .ConfigureAwait(false);

            await audit.RecordAsync(
                User, "ReconRun", result.RunId, AuditAction.Execute,
                after: new { sandbox = true, sourceRunId },
                notes: $"Sandbox dry-run of {definition.Code} over run {sourceRunId}")
                .ConfigureAwait(false);

            ViewData["Title"] = "Dry-run result";
            ViewData["Definition"] = definition;
            return View("DryRun", result);
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Index), new { id = definitionId });
        }
    }

    /// <summary>
    /// Resolves the submitted field codes against the two registries. This is
    /// the gate: a code that is not in the registry, or is in it but not
    /// matchable, is refused here.
    /// </summary>
    private static List<ConditionInput> BuildConditions(
        ReconciliationDefinition definition,
        string[]? leftCodes,
        string[]? rightCodes,
        string[]? comparisons,
        long?[]? tolerances,
        string[]? units,
        bool[]? useNormalized,
        out string? error)
    {
        error = null;
        var conditions = new List<ConditionInput>();

        if (leftCodes is null || rightCodes is null || comparisons is null)
        {
            return conditions;
        }

        for (var i = 0; i < leftCodes.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(leftCodes[i]) || i >= rightCodes.Length
                || string.IsNullOrWhiteSpace(rightCodes[i]))
            {
                continue;
            }

            if (!definition.Left.TryGetField(leftCodes[i], out var left))
            {
                error = $"'{leftCodes[i]}' is not in the field registry of {definition.Left.Code}.";
                return conditions;
            }

            if (!left.IsMatchable)
            {
                error = $"'{left.FieldCode}' is not marked matchable and cannot appear in a rule.";
                return conditions;
            }

            if (!definition.Right.TryGetField(rightCodes[i], out var right))
            {
                error = $"'{rightCodes[i]}' is not in the field registry of {definition.Right.Code}.";
                return conditions;
            }

            if (!right.IsMatchable)
            {
                error = $"'{right.FieldCode}' is not marked matchable and cannot appear in a rule.";
                return conditions;
            }

            var comparison = i < comparisons.Length ? comparisons[i] : "Exact";
            var normalized = useNormalized is not null && i < useNormalized.Length && useNormalized[i];

            if (normalized && (!left.NormalizeForMatch || !right.NormalizeForMatch))
            {
                error =
                    $"'{left.FieldCode}' and '{right.FieldCode}' cannot compare normalized " +
                    "companions unless both have one — otherwise the comparison puts a " +
                    "normalized value against a raw one and matches nothing.";

                return conditions;
            }

            var tolerance = tolerances is not null && i < tolerances.Length ? tolerances[i] : null;
            var unit = units is not null && i < units.Length && !string.IsNullOrWhiteSpace(units[i])
                ? units[i]
                : null;

            if (comparison is "NumericTolerance" or "DateWithin" && (tolerance is null || unit is null))
            {
                error =
                    $"{comparison} on '{left.FieldCode}' needs a tolerance value and unit; " +
                    "without them it is an exact match nobody asked for.";

                return conditions;
            }

            conditions.Add(new ConditionInput
            {
                LeftFieldId = left.DatasetFieldId,
                RightFieldId = right.DatasetFieldId,
                Comparison = comparison,
                Tolerance = tolerance,
                Unit = unit,
                UseNormalized = normalized,
            });
        }

        return conditions;
    }

    private sealed record ConditionInput
    {
        public required int LeftFieldId { get; init; }
        public required int RightFieldId { get; init; }
        public required string Comparison { get; init; }
        public long? Tolerance { get; init; }
        public string? Unit { get; init; }
        public required bool UseNormalized { get; init; }
    }
}
