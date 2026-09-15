using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Recon.Data;
using Recon.Domain.Conditions;
using Recon.Domain.Configuration;
using Recon.Engine.Sql;
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

        ViewData["CanConfigure"] = definition is not null
            && grants.TryGetValue(definition.CounterpartyId, out var level)
            && level >= AccessLevel.Configure;

        // Loaded here rather than from the definition, which carries only the
        // ACTIVE rules — the engine has no use for the others, but an editor
        // that could not show a deactivated rule could not re-activate one
        // either.
        ViewData["Exclusions"] = definition is null
            ? new List<ExclusionRow>()
            : await ExclusionsAsync(definition).ConfigureAwait(false);

        ViewData["Classifications"] = definition is null
            ? new List<ClassificationRow>()
            : await ClassificationsAsync(definition.DefinitionId).ConfigureAwait(false);

        ViewData["ControlTotals"] = definition is null
            ? new List<ControlTotalRow2>()
            : await ControlTotalsAsync(definition.DefinitionId).ConfigureAwait(false);

        return View();
    }

    private Task<List<ExclusionRow>> ExclusionsAsync(ReconciliationDefinition definition) =>
        Db.QueryAsync(
            connection,
            """
            SELECT e.ExclusionRuleId, e.DatasetId, d.Code, e.Name, e.ConditionJson,
                   e.ReasonCode, e.IsActive
            FROM cfg.ExclusionRule AS e
            JOIN cfg.Dataset AS d ON d.DatasetId = e.DatasetId
            WHERE e.DatasetId IN (@left, @right)
            ORDER BY d.Code, e.Name;
            """,
            r => new ExclusionRow
            {
                ExclusionRuleId = r.GetInt32(0),
                DatasetId = r.GetInt32(1),
                DatasetCode = r.GetString(2),
                Name = r.GetString(3),
                ConditionJson = r.GetString(4),
                ReasonCode = r.GetString(5),
                IsActive = r.GetBoolean(6),
            },
            c => c.With("@left", definition.Left.DatasetId)
                  .With("@right", definition.Right.DatasetId));

    private Task<List<ClassificationRow>> ClassificationsAsync(int definitionId) =>
        Db.QueryAsync(
            connection,
            """
            SELECT ClassificationRuleId, ExceptionCode, DisplayName, AppliesToSide,
                   ConditionJson, ActionType, Severity, Sequence, IsActive
            FROM cfg.ClassificationRule
            WHERE DefinitionId = @def
            ORDER BY Sequence;
            """,
            r => new ClassificationRow
            {
                ClassificationRuleId = r.GetInt32(0),
                ExceptionCode = r.GetString(1),
                DisplayName = r.GetString(2),
                AppliesToSide = r.GetString(3),
                ConditionJson = r.GetString(4),
                ActionType = r.GetString(5),
                Severity = r.GetString(6),
                Sequence = r.GetInt32(7),
                IsActive = r.GetBoolean(8),
            },
            c => c.With("@def", definitionId));

    private Task<List<ControlTotalRow2>> ControlTotalsAsync(int definitionId) =>
        Db.QueryAsync(
            connection,
            """
            SELECT c.ControlTotalId, c.CheckCode, c.DisplayName,
                   c.SourceAExpressionJson, c.SourceBExpressionJson,
                   c.Scope, c.SourceTypeA, c.SourceTypeB, c.PeriodDays,
                   c.ToleranceMinor, c.FailRunOnMismatch, c.IsActive,
                   CASE WHEN EXISTS (SELECT 1 FROM ops.ControlTotalResult AS r
                                     WHERE r.ControlTotalId = c.ControlTotalId)
                        THEN 1 ELSE 0 END
            FROM cfg.ControlTotalDefinition AS c
            WHERE c.DefinitionId = @def
            ORDER BY c.CheckCode;
            """,
            r => new ControlTotalRow2
            {
                ControlTotalId = r.GetInt32(0),
                CheckCode = r.GetString(1),
                DisplayName = r.GetString(2),
                SourceAJson = r.GetString(3),
                SourceBJson = r.GetString(4),
                Scope = r.GetString(5),
                SourceTypeA = r.GetString(6),
                SourceTypeB = r.GetString(7),
                PeriodDays = r.GetNullableInt32("PeriodDays"),
                ToleranceMinor = r.GetInt64(9),
                FailRunOnMismatch = r.GetBoolean(10),
                IsActive = r.GetBoolean(11),
                HasResults = r.GetInt32(12) == 1,
            },
            c => c.With("@def", definitionId));

    /// <summary>
    /// Saves an exclusion rule: rows that must never enter matching.
    ///
    /// <para>
    /// They leave the working set before pass 1 and are reported separately.
    /// They never generate exceptions — a transaction the scheme rejected is
    /// not a break, and treating it as one buries the real ones.
    /// </para>
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveExclusion(
        int definitionId,
        int datasetId,
        int? exclusionRuleId,
        string name,
        string conditionJson,
        string reasonCode,
        bool isActive)
    {
        var definition = await config.LoadDefinitionAsync(definitionId).ConfigureAwait(false);
        await access.RequireAsync(User, definition.CounterpartyId, AccessLevel.Configure)
            .ConfigureAwait(false);

        var dataset = definition.Left.DatasetId == datasetId ? definition.Left
            : definition.Right.DatasetId == datasetId ? definition.Right
            : null;

        if (dataset is null)
        {
            TempData["Error"] = "That dataset is not one of this definition's two sides.";
            return RedirectToAction(nameof(Index), new { id = definitionId });
        }

        // The same gate the rule builder applies: the condition resolves
        // against that dataset's registry and never from free text.
        var result = ConditionValidator.ValidateJson(conditionJson, dataset);

        if (!result.IsValid)
        {
            TempData["Error"] = "The exclusion condition was rejected: "
                + string.Join("; ", result.Errors);

            return RedirectToAction(nameof(Index), new { id = definitionId });
        }

        try
        {
            if (exclusionRuleId is { } existing)
            {
                await Db.ExecuteAsync(
                    connection,
                    """
                    UPDATE cfg.ExclusionRule
                    SET DatasetId = @ds, Name = @name, ConditionJson = @json,
                        ReasonCode = @reason, IsActive = @active
                    WHERE ExclusionRuleId = @id;
                    """,
                    c => c.With("@id", existing)
                          .With("@ds", datasetId)
                          .With("@name", name?.Trim())
                          .With("@json", conditionJson)
                          .With("@reason", reasonCode?.Trim())
                          .With("@active", isActive)).ConfigureAwait(false);

                await audit.RecordAsync(User, "ExclusionRule", existing, AuditAction.Update,
                    after: new { datasetId, name, reasonCode, isActive }).ConfigureAwait(false);
            }
            else
            {
                var id = await Db.ScalarAsync<int>(
                    connection,
                    """
                    INSERT cfg.ExclusionRule (DatasetId, Name, ConditionJson, ReasonCode, IsActive)
                    VALUES (@ds, @name, @json, @reason, @active);
                    SELECT CAST(SCOPE_IDENTITY() AS INT);
                    """,
                    c => c.With("@ds", datasetId)
                          .With("@name", name?.Trim())
                          .With("@json", conditionJson)
                          .With("@reason", reasonCode?.Trim())
                          .With("@active", isActive)).ConfigureAwait(false);

                await audit.RecordAsync(User, "ExclusionRule", id, AuditAction.Create,
                    after: new { datasetId, name, reasonCode }).ConfigureAwait(false);
            }

            TempData["Ok"] = $"Exclusion '{name}' saved, and it resolves against {dataset.Code}.";
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            TempData["Error"] = "The database refused this exclusion: " + ex.Message;
        }

        return RedirectToAction(nameof(Index), new { id = definitionId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteExclusion(int definitionId, int exclusionRuleId)
    {
        var definition = await config.LoadDefinitionAsync(definitionId).ConfigureAwait(false);
        await access.RequireAsync(User, definition.CounterpartyId, AccessLevel.Configure)
            .ConfigureAwait(false);

        await Db.ExecuteAsync(
            connection, "DELETE cfg.ExclusionRule WHERE ExclusionRuleId = @id;",
            c => c.With("@id", exclusionRuleId)).ConfigureAwait(false);

        await audit.RecordAsync(User, "ExclusionRule", exclusionRuleId, AuditAction.Delete)
            .ConfigureAwait(false);

        TempData["Ok"] = "Exclusion removed.";
        return RedirectToAction(nameof(Index), new { id = definitionId });
    }

    /// <summary>
    /// Saves a classification rule: what an unmatched row is called, and how
    /// serious it is.
    ///
    /// <para>
    /// First matching rule wins, in sequence, per side. Without any rules an
    /// unmatched row stays unmatched and raises no exception — which is
    /// correct: an exception code the configuration never named would be
    /// invented.
    /// </para>
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveClassification(
        int definitionId,
        int? classificationRuleId,
        string exceptionCode,
        string displayName,
        string appliesToSide,
        string conditionJson,
        string actionType,
        string severity,
        int sequence,
        bool isActive)
    {
        var definition = await config.LoadDefinitionAsync(definitionId).ConfigureAwait(false);
        await access.RequireAsync(User, definition.CounterpartyId, AccessLevel.Configure)
            .ConfigureAwait(false);

        // A rule that applies to both sides has to resolve against both
        // registries, and the two sides name nothing alike: a condition on
        // the left's STATUS is not a condition the right can evaluate. This
        // is where that is found out, rather than in the run.
        var datasets = appliesToSide switch
        {
            "Left" => new[] { definition.Left },
            "Right" => [definition.Right],
            _ => [definition.Left, definition.Right],
        };

        foreach (var dataset in datasets)
        {
            var check = ConditionValidator.ValidateJson(conditionJson, dataset);

            if (!check.IsValid)
            {
                TempData["Error"] =
                    $"The condition was rejected against {dataset.Code}: "
                    + string.Join("; ", check.Errors)
                    + (appliesToSide == "Both"
                        ? " — a rule that applies to both sides must resolve against both registries."
                        : string.Empty);

                return RedirectToAction(nameof(Index), new { id = definitionId });
            }
        }

        if (actionType == "AutoPost")
        {
            TempData["Error"] =
                "AutoPost is a seam for future Failed Inward posting and is not implemented. A " +
                "rule that claimed to post would report an outcome nothing produced.";

            return RedirectToAction(nameof(Index), new { id = definitionId });
        }

        try
        {
            if (classificationRuleId is { } existing)
            {
                await Db.ExecuteAsync(
                    connection,
                    """
                    UPDATE cfg.ClassificationRule
                    SET ExceptionCode = @code, DisplayName = @label, AppliesToSide = @side,
                        ConditionJson = @json, ActionType = @action, Severity = @severity,
                        Sequence = @seq, IsActive = @active
                    WHERE ClassificationRuleId = @id;
                    """,
                    BindClassification(existing)).ConfigureAwait(false);

                await audit.RecordAsync(User, "ClassificationRule", existing, AuditAction.Update,
                    after: new { exceptionCode, appliesToSide, sequence, severity }).ConfigureAwait(false);
            }
            else
            {
                var id = await Db.ScalarAsync<int>(
                    connection,
                    """
                    INSERT cfg.ClassificationRule
                        (DefinitionId, ExceptionCode, DisplayName, AppliesToSide, ConditionJson,
                         ActionType, Severity, Sequence, IsActive)
                    VALUES (@def, @code, @label, @side, @json, @action, @severity, @seq, @active);
                    SELECT CAST(SCOPE_IDENTITY() AS INT);
                    """,
                    BindClassification(null)).ConfigureAwait(false);

                await audit.RecordAsync(User, "ClassificationRule", id, AuditAction.Create,
                    after: new { exceptionCode, appliesToSide, sequence }).ConfigureAwait(false);
            }

            TempData["Ok"] = $"Classification {exceptionCode} saved at sequence {sequence}.";
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            // Most likely the unique constraint on (definition, sequence):
            // two rules cannot share a position, or which one wins would
            // depend on the read order.
            TempData["Error"] = "The database refused this classification: " + ex.Message;
        }

        return RedirectToAction(nameof(Index), new { id = definitionId });

        Action<Microsoft.Data.SqlClient.SqlCommand> BindClassification(int? id) => command =>
        {
            if (id is { } value)
            {
                command.With("@id", value);
            }

            command.With("@def", definitionId)
                   .With("@code", exceptionCode?.Trim())
                   .With("@label", displayName?.Trim())
                   .With("@side", appliesToSide)
                   .With("@json", conditionJson)
                   .With("@action", actionType)
                   .With("@severity", severity)
                   .With("@seq", sequence)
                   .With("@active", isActive);
        };
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteClassification(int definitionId, int classificationRuleId)
    {
        var definition = await config.LoadDefinitionAsync(definitionId).ConfigureAwait(false);
        await access.RequireAsync(User, definition.CounterpartyId, AccessLevel.Configure)
            .ConfigureAwait(false);

        await Db.ExecuteAsync(
            connection, "DELETE cfg.ClassificationRule WHERE ClassificationRuleId = @id;",
            c => c.With("@id", classificationRuleId)).ConfigureAwait(false);

        await audit.RecordAsync(User, "ClassificationRule", classificationRuleId, AuditAction.Delete)
            .ConfigureAwait(false);

        TempData["Ok"] = "Classification removed.";
        return RedirectToAction(nameof(Index), new { id = definitionId });
    }

    /// <summary>
    /// Saves a control total: two aggregates that must agree.
    ///
    /// <para>
    /// Built from parts rather than typed as JSON, and then compiled straight
    /// away: a check that cannot compile is a check that fails the run it was
    /// added to protect, and finding that out here costs a message instead of
    /// a night.
    /// </para>
    ///
    /// <para>
    /// A non-zero difference on a failing check <b>fails the run</b>, however
    /// many rows matched. That is the design's position and this form says so:
    /// the alternative is a reconciliation that reports success while the
    /// money does not add up.
    /// </para>
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveControlTotal(
        int definitionId,
        int? controlTotalId,
        string checkCode,
        string displayName,
        string functionA,
        string sourceTypeA,
        string sideA,
        string? matchStatusA,
        string functionB,
        string sourceTypeB,
        string sideB,
        string? matchStatusB,
        string scope,
        int? periodDays,
        long toleranceMinor,
        bool failRunOnMismatch,
        bool isActive)
    {
        var definition = await config.LoadDefinitionAsync(definitionId).ConfigureAwait(false);
        await access.RequireAsync(User, definition.CounterpartyId, AccessLevel.Configure)
            .ConfigureAwait(false);

        if (scope == "Period" && (periodDays is null || periodDays <= 0))
        {
            TempData["Error"] = "A period-scoped check needs a window in days.";
            return RedirectToAction(nameof(Index), new { id = definitionId });
        }

        var specA = BuildSpec(definition, functionA, sideA, matchStatusA);
        var specB = BuildSpec(definition, functionB, sideB, matchStatusB);

        var check = new ControlTotalDefinition
        {
            ControlTotalId = controlTotalId ?? 0,
            CheckCode = checkCode?.Trim() ?? string.Empty,
            DisplayName = displayName?.Trim() ?? string.Empty,
            SourceA = specA,
            SourceB = specB,
            Scope = Db.ParseEnum<ControlTotalScope>(scope),
            SourceTypeA = Db.ParseEnum<ControlTotalSource>(sourceTypeA),
            SourceTypeB = Db.ParseEnum<ControlTotalSource>(sourceTypeB),
            PeriodDays = periodDays,
            ToleranceMinor = toleranceMinor,
            FailRunOnMismatch = failRunOnMismatch,
            IsActive = isActive,
        };

        // Compiled before it is stored. A check that cannot compile is worse
        // than no check: it fails the run it was added to protect.
        try
        {
            var businessDate = DateOnly.FromDateTime(DateTime.Today);
            var (from, to) = definition.WindowFor(businessDate);

            _ = Recon.Engine.Totals.ControlTotalEvaluator.Compile(
                check, definition, runId: 0, stagingRunId: 0, businessDate, from, to);
        }
        catch (SqlCompilationException ex)
        {
            TempData["Error"] = "That check does not compile: " + ex.Message;
            return RedirectToAction(nameof(Index), new { id = definitionId });
        }
        catch (FieldNotInRegistryException ex)
        {
            TempData["Error"] = "That check names a field outside the registry: " + ex.Message;
            return RedirectToAction(nameof(Index), new { id = definitionId });
        }

        try
        {
            if (controlTotalId is { } existing)
            {
                await Db.ExecuteAsync(
                    connection,
                    """
                    UPDATE cfg.ControlTotalDefinition
                    SET CheckCode = @code, DisplayName = @label,
                        SourceAExpressionJson = @jsonA, SourceBExpressionJson = @jsonB,
                        Scope = @scope, SourceTypeA = @typeA, SourceTypeB = @typeB,
                        PeriodDays = @period, ToleranceMinor = @tolerance,
                        FailRunOnMismatch = @fails, IsActive = @active
                    WHERE ControlTotalId = @id;
                    """,
                    BindTotal(existing)).ConfigureAwait(false);

                await audit.RecordAsync(User, "ControlTotalDefinition", existing, AuditAction.Update,
                    after: new { checkCode, scope, failRunOnMismatch }).ConfigureAwait(false);
            }
            else
            {
                var id = await Db.ScalarAsync<int>(
                    connection,
                    """
                    INSERT cfg.ControlTotalDefinition
                        (DefinitionId, CheckCode, DisplayName, SourceAExpressionJson,
                         SourceBExpressionJson, Scope, SourceTypeA, SourceTypeB, PeriodDays,
                         ToleranceMinor, FailRunOnMismatch, IsActive)
                    VALUES (@def, @code, @label, @jsonA, @jsonB, @scope, @typeA, @typeB,
                            @period, @tolerance, @fails, @active);
                    SELECT CAST(SCOPE_IDENTITY() AS INT);
                    """,
                    BindTotal(null)).ConfigureAwait(false);

                await audit.RecordAsync(User, "ControlTotalDefinition", id, AuditAction.Create,
                    after: new { checkCode, scope, failRunOnMismatch }).ConfigureAwait(false);
            }

            TempData["Ok"] = $"Control total {checkCode} saved, and it compiles."
                + (failRunOnMismatch
                    ? " A difference beyond the tolerance will FAIL the run."
                    : " A difference is reported and does not fail the run.");
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            TempData["Error"] = "The database refused this check: " + ex.Message;
        }

        return RedirectToAction(nameof(Index), new { id = definitionId });

        Action<Microsoft.Data.SqlClient.SqlCommand> BindTotal(int? id) => command =>
        {
            if (id is { } value)
            {
                command.With("@id", value);
            }

            command.With("@def", definitionId)
                   .With("@code", check.CheckCode)
                   .With("@label", check.DisplayName)
                   .With("@jsonA", AggregateSpecJson.Write(specA))
                   .With("@jsonB", AggregateSpecJson.Write(specB))
                   .With("@scope", scope)
                   .With("@typeA", sourceTypeA)
                   .With("@typeB", sourceTypeB)
                   .With("@period", periodDays)
                   .With("@tolerance", toleranceMinor)
                   .With("@fails", failRunOnMismatch)
                   .With("@active", isActive);
        };
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteControlTotal(int definitionId, int controlTotalId)
    {
        var definition = await config.LoadDefinitionAsync(definitionId).ConfigureAwait(false);
        await access.RequireAsync(User, definition.CounterpartyId, AccessLevel.Configure)
            .ConfigureAwait(false);

        // A check that has produced results is kept: ops.ControlTotalResult
        // references it, and a past run's balance proof must stay
        // explainable. Deactivating stops it running.
        var used = await Db.ScalarAsync<int>(
            connection,
            """
            SELECT CASE WHEN EXISTS (SELECT 1 FROM ops.ControlTotalResult WHERE ControlTotalId = @id)
                        THEN 1 ELSE 0 END;
            """,
            c => c.With("@id", controlTotalId)).ConfigureAwait(false);

        if (used == 1)
        {
            TempData["Error"] =
                "This check has produced results, so it cannot be deleted — a past run's balance " +
                "proof would stop being explainable. Deactivate it instead.";

            return RedirectToAction(nameof(Index), new { id = definitionId });
        }

        await Db.ExecuteAsync(
            connection, "DELETE cfg.ControlTotalDefinition WHERE ControlTotalId = @id;",
            c => c.With("@id", controlTotalId)).ConfigureAwait(false);

        await audit.RecordAsync(User, "ControlTotalDefinition", controlTotalId, AuditAction.Delete)
            .ConfigureAwait(false);

        TempData["Ok"] = "Check removed.";
        return RedirectToAction(nameof(Index), new { id = definitionId });
    }

    /// <summary>
    /// One side of a control total, from the parts the form collects. The
    /// dataset is the chosen side's, so a spec never names a dataset outside
    /// this definition.
    /// </summary>
    private static AggregateSpec BuildSpec(
        ReconciliationDefinition definition, string function, string side, string? matchStatus)
    {
        var dataset = side == "Right" ? definition.Right : definition.Left;

        return new AggregateSpec
        {
            Function = function,
            DatasetId = dataset.DatasetId,
            Side = side == "Right" ? Side.Right : Side.Left,
            MatchStatus = string.IsNullOrEmpty(matchStatus)
                ? null
                : Db.ParseEnum<MatchStatus>(matchStatus),
        };
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

public sealed record ExclusionRow
{
    public required int ExclusionRuleId { get; init; }
    public required int DatasetId { get; init; }
    public required string DatasetCode { get; init; }
    public required string Name { get; init; }
    public required string ConditionJson { get; init; }
    public required string ReasonCode { get; init; }
    public required bool IsActive { get; init; }
}

public sealed record ClassificationRow
{
    public required int ClassificationRuleId { get; init; }
    public required string ExceptionCode { get; init; }
    public required string DisplayName { get; init; }
    public required string AppliesToSide { get; init; }
    public required string ConditionJson { get; init; }
    public required string ActionType { get; init; }
    public required string Severity { get; init; }
    public required int Sequence { get; init; }
    public required bool IsActive { get; init; }
}

/// <summary>
/// A control total as the editor sees it — the definition, not a run's
/// result. <c>ControlTotalRow</c> is already taken by the run page, which
/// shows the two values and their difference.
/// </summary>
public sealed record ControlTotalRow2
{
    public required int ControlTotalId { get; init; }
    public required string CheckCode { get; init; }
    public required string DisplayName { get; init; }
    public required string SourceAJson { get; init; }
    public required string SourceBJson { get; init; }
    public required string Scope { get; init; }
    public required string SourceTypeA { get; init; }
    public required string SourceTypeB { get; init; }
    public int? PeriodDays { get; init; }
    public required long ToleranceMinor { get; init; }
    public required bool FailRunOnMismatch { get; init; }
    public required bool IsActive { get; init; }

    /// <summary>
    /// Whether any run has produced a result for this check. One that has
    /// cannot be deleted: a past run's balance proof must stay explainable.
    /// </summary>
    public required bool HasResults { get; init; }
}
