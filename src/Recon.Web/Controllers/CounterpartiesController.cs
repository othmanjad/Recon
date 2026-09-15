using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Recon.Data;
using Recon.Web.Models;
using Recon.Web.Services;

namespace Recon.Web.Controllers;

/// <summary>
/// Counterparties, their reconciliation definitions, and who may see them.
///
/// <para>
/// The counterparty is the unit access is scoped to (design §13), so this is
/// also where grants are managed. A user with <see cref="AccessLevel.Read"/>
/// sees the page and can change nothing; a grant may only be given by someone
/// who already holds <see cref="AccessLevel.Configure"/> on that same
/// counterparty — a user cannot widen their own reach, and cannot widen
/// anyone's reach into a counterparty they do not administer themselves.
/// </para>
/// </summary>
[Authorize]
public sealed class CounterpartiesController(
    PortalQueries queries,
    Microsoft.Data.SqlClient.SqlConnection connection,
    AccessService access,
    AuditService audit) : Controller
{
    public async Task<IActionResult> Index(int? id)
    {
        ViewData["Title"] = "Counterparties";

        var grants = await access.GrantsAsync(User).ConfigureAwait(false);
        var counterparties = await queries.CounterpartiesAsync(User).ConfigureAwait(false);
        var selectedId = id ?? counterparties.FirstOrDefault()?.CounterpartyId;

        CounterpartyRow? selected = null;
        if (selectedId is { } counterpartyId)
        {
            selected = counterparties.FirstOrDefault(c => c.CounterpartyId == counterpartyId);
        }

        ViewData["Counterparties"] = counterparties;
        ViewData["Selected"] = selected;

        ViewData["Definitions"] = selected is null
            ? new List<DefinitionRow>()
            : (await queries.DefinitionsAsync([selected.CounterpartyId]).ConfigureAwait(false));

        ViewData["Datasets"] = selected is null
            ? new List<DatasetRow>()
            : (await queries.DatasetsAsync(User).ConfigureAwait(false))
                .Where(d => d.CounterpartyId == selected.CounterpartyId)
                .ToList();

        // Grants are only listed for a counterparty the viewer administers.
        // "Who else can see this partner" is itself information about the
        // partner's operations.
        ViewData["Grants"] = selected is not null
            && grants.TryGetValue(selected.CounterpartyId, out var level)
            && level >= AccessLevel.Configure
                ? await GrantsForAsync(selected.CounterpartyId).ConfigureAwait(false)
                : null;

        ViewData["MyGrants"] = grants;
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveCounterparty(
        int? counterpartyId, string code, string name, string? description, bool isActive)
    {
        if (counterpartyId is { } existing)
        {
            await access.RequireAsync(User, existing, AccessLevel.Configure).ConfigureAwait(false);

            await Db.ExecuteAsync(
                connection,
                """
                UPDATE cfg.Counterparty
                SET Code = @code, Name = @name, Description = @description, IsActive = @active
                WHERE CounterpartyId = @id;
                """,
                c => c.With("@id", existing)
                      .With("@code", code?.Trim())
                      .With("@name", name?.Trim())
                      .With("@description", string.IsNullOrWhiteSpace(description) ? null : description.Trim())
                      .With("@active", isActive)).ConfigureAwait(false);

            await audit.RecordAsync(User, "Counterparty", existing, AuditAction.Update,
                after: new { code, name, isActive }).ConfigureAwait(false);

            TempData["Ok"] = $"{code} saved.";
            return RedirectToAction(nameof(Index), new { id = existing });
        }

        // Creating a counterparty is a platform-level act rather than one
        // scoped to a counterparty that does not exist yet, so the check is
        // "administers something" — and the creator is granted Configure on
        // what they just created, or they would be unable to see it.
        var myGrants = await access.GrantsAsync(User).ConfigureAwait(false);
        if (!myGrants.Values.Any(l => l >= AccessLevel.Configure))
        {
            TempData["Error"] = "Creating a counterparty requires Configure access on the platform.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            var id = await Db.ScalarAsync<int>(
                connection,
                """
                INSERT cfg.Counterparty (Code, Name, Description, IsActive)
                VALUES (@code, @name, @description, @active);
                SELECT CAST(SCOPE_IDENTITY() AS INT);
                """,
                c => c.With("@code", code?.Trim())
                      .With("@name", name?.Trim())
                      .With("@description", string.IsNullOrWhiteSpace(description) ? null : description.Trim())
                      .With("@active", isActive)).ConfigureAwait(false);

            await Db.ExecuteAsync(
                connection,
                """
                INSERT cfg.UserCounterpartyAccess (UserName, CounterpartyId, AccessLevel, GrantedBy)
                VALUES (@user, @cp, 'Configure', @user);
                """,
                c => c.With("@user", access.UserName(User)).With("@cp", id)).ConfigureAwait(false);

            await audit.RecordAsync(User, "Counterparty", id, AuditAction.Create,
                after: new { code, name, isActive }).ConfigureAwait(false);

            TempData["Ok"] = $"{code} created, and you were granted Configure access to it.";
            return RedirectToAction(nameof(Index), new { id });
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            TempData["Error"] = "The database refused this counterparty: " + ex.Message;
            return RedirectToAction(nameof(Index));
        }
    }

    /// <summary>
    /// Creates or edits a reconciliation definition — the left dataset, the
    /// right dataset and the matching window.
    ///
    /// <para>
    /// The window is not cosmetic: late arrivals mean "= BusinessDate" is
    /// never the right range (finding C5), and a window of zero on both sides
    /// silently drops every transaction that crossed midnight.
    /// </para>
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveDefinition(
        int counterpartyId,
        int? definitionId,
        string code,
        string name,
        int leftDatasetId,
        int rightDatasetId,
        int windowBefore,
        int windowAfter)
    {
        await access.RequireAsync(User, counterpartyId, AccessLevel.Configure).ConfigureAwait(false);

        if (leftDatasetId == rightDatasetId)
        {
            TempData["Error"] = "A definition cannot reconcile a dataset against itself.";
            return RedirectToAction(nameof(Index), new { id = counterpartyId });
        }

        // Both datasets must belong to this counterparty. Without the check a
        // definition could straddle two partners and its runs would appear
        // under whichever one the access filter happened to allow.
        var owned = await Db.ScalarAsync<int>(
            connection,
            """
            SELECT COUNT(*) FROM cfg.Dataset
            WHERE DatasetId IN (@left, @right) AND CounterpartyId = @cp;
            """,
            c => c.With("@left", leftDatasetId)
                  .With("@right", rightDatasetId)
                  .With("@cp", counterpartyId)).ConfigureAwait(false);

        if (owned != 2)
        {
            TempData["Error"] =
                "Both datasets must belong to this counterparty. A definition that straddles " +
                "two partners cannot be access-scoped to either.";

            return RedirectToAction(nameof(Index), new { id = counterpartyId });
        }

        try
        {
            if (definitionId is { } existing)
            {
                // Version is incremented on every edit: the snapshot stored
                // with each run names the version it ran, so "what did this
                // definition look like in June" stays answerable.
                await Db.ExecuteAsync(
                    connection,
                    """
                    UPDATE cfg.ReconciliationDefinition
                    SET Code = @code, Name = @name,
                        LeftDatasetId = @left, RightDatasetId = @right,
                        MatchingWindowDaysBefore = @before, MatchingWindowDaysAfter = @after,
                        Version = Version + 1
                    WHERE DefinitionId = @id;
                    """,
                    Bind(existing)).ConfigureAwait(false);

                await audit.RecordAsync(User, "ReconciliationDefinition", existing,
                    AuditAction.Update, after: new { code, name, windowBefore, windowAfter })
                    .ConfigureAwait(false);

                TempData["Ok"] = $"{code} saved; its version was incremented.";
            }
            else
            {
                var id = await Db.ScalarAsync<int>(
                    connection,
                    """
                    INSERT cfg.ReconciliationDefinition
                        (CounterpartyId, Code, Name, LeftDatasetId, RightDatasetId,
                         MatchingWindowDaysBefore, MatchingWindowDaysAfter, IsActive)
                    VALUES (@cp, @code, @name, @left, @right, @before, @after, 0);
                    SELECT CAST(SCOPE_IDENTITY() AS INT);
                    """,
                    Bind(null)).ConfigureAwait(false);

                await audit.RecordAsync(User, "ReconciliationDefinition", id, AuditAction.Create,
                    after: new { code, name, leftDatasetId, rightDatasetId }).ConfigureAwait(false);

                TempData["Ok"] =
                    $"{code} created, inactive. Add its passes in the rule builder, then activate it.";
            }
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            TempData["Error"] = "The database refused this definition: " + ex.Message;
        }

        return RedirectToAction(nameof(Index), new { id = counterpartyId });

        Action<Microsoft.Data.SqlClient.SqlCommand> Bind(int? id) => command =>
        {
            if (id is { } value)
            {
                command.With("@id", value);
            }

            command.With("@cp", counterpartyId)
                   .With("@code", code?.Trim())
                   .With("@name", name?.Trim())
                   .With("@left", leftDatasetId)
                   .With("@right", rightDatasetId)
                   .With("@before", windowBefore)
                   .With("@after", windowAfter);
        };
    }

    /// <summary>
    /// Activates or deactivates a definition, refusing activation while the
    /// definition could not produce trustworthy numbers (review item B6).
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ActivateDefinition(
        int counterpartyId, int definitionId, bool active,
        [FromServices] ConfigurationRepository config)
    {
        ArgumentNullException.ThrowIfNull(config);
        await access.RequireAsync(User, counterpartyId, AccessLevel.Configure).ConfigureAwait(false);

        var definition = await config.LoadDefinitionAsync(definitionId).ConfigureAwait(false);

        if (active)
        {
            var problems = definition.ActivationProblems();
            if (problems.Count > 0)
            {
                TempData["Error"] = "Cannot activate: " + string.Join("; ", problems);
                return RedirectToAction(nameof(Index), new { id = counterpartyId });
            }
        }

        await Db.ExecuteAsync(
            connection,
            "UPDATE cfg.ReconciliationDefinition SET IsActive = @active WHERE DefinitionId = @id;",
            c => c.With("@id", definitionId).With("@active", active)).ConfigureAwait(false);

        await audit.RecordAsync(
            User, "ReconciliationDefinition", definitionId,
            active ? AuditAction.Activate : AuditAction.Deactivate,
            after: new { definition.Code, active }).ConfigureAwait(false);

        TempData["Ok"] = $"{definition.Code} {(active ? "activated" : "deactivated")}.";
        return RedirectToAction(nameof(Index), new { id = counterpartyId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Grant(int counterpartyId, string userName, string level)
    {
        await access.RequireAsync(User, counterpartyId, AccessLevel.Configure).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(userName))
        {
            TempData["Error"] = "A user name is required.";
            return RedirectToAction(nameof(Index), new { id = counterpartyId });
        }

        try
        {
            // MERGE rather than INSERT: the unique constraint on
            // (UserName, CounterpartyId) means a second grant is an edit, and
            // an operator raising someone from Read to Operate expects that
            // to work rather than to fail on a duplicate key.
            await Db.ExecuteAsync(
                connection,
                """
                UPDATE cfg.UserCounterpartyAccess
                SET AccessLevel = @level, GrantedAt = SYSDATETIME(), GrantedBy = @by
                WHERE UserName = @user AND CounterpartyId = @cp;

                IF @@ROWCOUNT = 0
                    INSERT cfg.UserCounterpartyAccess (UserName, CounterpartyId, AccessLevel, GrantedBy)
                    VALUES (@user, @cp, @level, @by);
                """,
                c => c.With("@cp", counterpartyId)
                      .With("@user", userName.Trim())
                      .With("@level", level)
                      .With("@by", access.UserName(User))).ConfigureAwait(false);

            await audit.RecordAsync(
                User, "UserCounterpartyAccess", $"{userName.Trim()}@{counterpartyId}",
                AuditAction.Create, after: new { userName, counterpartyId, level })
                .ConfigureAwait(false);

            TempData["Ok"] = $"{userName.Trim()} now has {level} access.";
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            TempData["Error"] = "The database refused this grant: " + ex.Message;
        }

        return RedirectToAction(nameof(Index), new { id = counterpartyId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Revoke(int counterpartyId, string userName)
    {
        await access.RequireAsync(User, counterpartyId, AccessLevel.Configure).ConfigureAwait(false);

        // Revoking your own last Configure grant would lock the counterparty
        // out of administration entirely, and nobody could grant it back.
        if (string.Equals(userName, access.UserName(User), StringComparison.Ordinal))
        {
            var administrators = await Db.ScalarAsync<int>(
                connection,
                """
                SELECT COUNT(*) FROM cfg.UserCounterpartyAccess
                WHERE CounterpartyId = @cp AND AccessLevel = 'Configure';
                """,
                c => c.With("@cp", counterpartyId)).ConfigureAwait(false);

            if (administrators <= 1)
            {
                TempData["Error"] =
                    "You are the only user with Configure access to this counterparty. " +
                    "Revoking it would leave nobody able to grant it back.";

                return RedirectToAction(nameof(Index), new { id = counterpartyId });
            }
        }

        await Db.ExecuteAsync(
            connection,
            """
            DELETE cfg.UserCounterpartyAccess
            WHERE CounterpartyId = @cp AND UserName = @user;
            """,
            c => c.With("@cp", counterpartyId).With("@user", userName)).ConfigureAwait(false);

        await audit.RecordAsync(
            User, "UserCounterpartyAccess", $"{userName}@{counterpartyId}", AuditAction.Delete,
            before: new { userName, counterpartyId }).ConfigureAwait(false);

        TempData["Ok"] = $"Access for {userName} revoked.";
        return RedirectToAction(nameof(Index), new { id = counterpartyId });
    }

    private Task<List<GrantRow>> GrantsForAsync(int counterpartyId) =>
        Db.QueryAsync(
            connection,
            """
            SELECT UserName, AccessLevel, GrantedAt, GrantedBy
            FROM cfg.UserCounterpartyAccess
            WHERE CounterpartyId = @cp
            ORDER BY UserName;
            """,
            r => new GrantRow(r.GetString(0), r.GetString(1), r.GetDateTime(2), r.GetString(3)),
            c => c.With("@cp", counterpartyId));
}

public sealed record GrantRow(string UserName, string Level, DateTime GrantedAt, string GrantedBy);
