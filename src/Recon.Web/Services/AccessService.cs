using System.Security.Claims;
using Microsoft.Data.SqlClient;
using Recon.Data;

namespace Recon.Web.Services;

/// <summary>
/// Per-counterparty access control (design §13).
///
/// <para>
/// With multiple counterparties on one platform, permissions are scoped per
/// counterparty: a user managing JoPACC need not see another partner's data.
/// The design is explicit that this must exist from Phase 3 rather than be
/// bolted on later, because retrofitting authorization means auditing every
/// query that already exists.
/// </para>
///
/// <para>
/// Every read in <see cref="PortalQueries"/> is filtered by the set this
/// returns. The filtering happens in SQL rather than after the fact: a page
/// that fetched everything and then hid rows would still have leaked them
/// through paging counts and totals.
/// </para>
/// </summary>
public sealed class AccessService(SqlConnection connection, IHttpContextAccessor? accessor = null)
{
    private readonly SqlConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    private readonly IHttpContextAccessor? _accessor = accessor;

    public string UserName(ClaimsPrincipal? user) =>
        user?.FindFirst(ClaimTypes.Name)?.Value
        ?? NameOf(_accessor?.HttpContext?.User);

    /// <summary>
    /// Who the caller is, without needing a database.
    ///
    /// <para>
    /// The setup screens need this: they run on a portal that has no
    /// connection yet, so they cannot take an <see cref="AccessService"/> —
    /// constructing one needs a connection, and the one screen that must
    /// answer without a database would be the one screen that cannot. It is
    /// static and here rather than a second copy, so "who is this" has one
    /// definition.
    /// </para>
    /// </summary>
    public static string NameOf(ClaimsPrincipal? user) =>
        user?.FindFirst(ClaimTypes.Name)?.Value ?? "anonymous";

    /// <summary>
    /// The counterparties this user may see, and at what level.
    ///
    /// An empty result means no access to anything — which is the correct
    /// default for a new account, and the reason every screen handles it
    /// rather than showing an empty table as though nothing existed.
    /// </summary>
    public async Task<Dictionary<int, AccessLevel>> GrantsAsync(
        ClaimsPrincipal? user, CancellationToken cancellationToken = default)
    {
        var name = UserName(user);

        var rows = await Db.QueryAsync(
            _connection,
            """
            SELECT CounterpartyId, AccessLevel
            FROM cfg.UserCounterpartyAccess
            WHERE UserName = @user;
            """,
            r => (Id: r.GetInt32(0), Level: Db.ParseEnum<AccessLevel>(r.GetString(1))),
            c => c.With("@user", name),
            cancellationToken).ConfigureAwait(false);

        return rows.ToDictionary(x => x.Id, x => x.Level);
    }

    public async Task<bool> CanAsync(
        ClaimsPrincipal? user,
        int counterpartyId,
        AccessLevel required,
        CancellationToken cancellationToken = default)
    {
        var grants = await GrantsAsync(user, cancellationToken).ConfigureAwait(false);

        return grants.TryGetValue(counterpartyId, out var level) && level >= required;
    }

    /// <summary>
    /// Throws rather than returning false, for the call sites where carrying
    /// on would mean writing data the user may not write. A missing grant and
    /// an insufficient one are the same refusal here: telling the user which
    /// counterparties exist is itself a disclosure.
    /// </summary>
    public async Task RequireAsync(
        ClaimsPrincipal? user,
        int counterpartyId,
        AccessLevel required,
        CancellationToken cancellationToken = default)
    {
        if (!await CanAsync(user, counterpartyId, required, cancellationToken).ConfigureAwait(false))
        {
            throw new AccessDeniedException(UserName(user), counterpartyId, required);
        }
    }

    /// <summary>
    /// A SQL fragment restricting a query to the user's counterparties, with
    /// the ids bound as parameters.
    ///
    /// Returns <c>1 = 0</c> for a user with no grants — a predicate that
    /// matches nothing, rather than an empty IN list which is a syntax error,
    /// and rather than omitting the filter which would show everything.
    /// </summary>
    public static string CounterpartyFilter(
        string alias, IReadOnlyCollection<int> counterpartyIds, SqlCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (counterpartyIds.Count == 0)
        {
            return "1 = 0";
        }

        var names = new List<string>(counterpartyIds.Count);
        var index = 0;

        foreach (var id in counterpartyIds)
        {
            var name = "@cp" + index++;
            command.Parameters.AddWithValue(name, id);
            names.Add(name);
        }

        return $"{alias}.CounterpartyId IN ({string.Join(", ", names)})";
    }
}

/// <summary>
/// Mirrors <c>cfg.UserCounterpartyAccess.AccessLevel</c>. Ordered so that
/// <c>&gt;=</c> means "at least", which is what every check wants.
/// </summary>
public enum AccessLevel
{
    Read = 1,
    Operate = 2,
    Configure = 3,
}

public sealed class AccessDeniedException(string userName, int counterpartyId, AccessLevel required)
    : Exception($"{userName} does not have {required} access to counterparty {counterpartyId}.")
{
    public string UserName { get; } = userName;
    public int CounterpartyId { get; } = counterpartyId;
    public AccessLevel Required { get; } = required;
}
