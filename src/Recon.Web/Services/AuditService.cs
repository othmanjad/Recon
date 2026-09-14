using System.Security.Claims;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Recon.Data;

namespace Recon.Web.Services;

/// <summary>
/// Writes <c>aud.AuditLog</c>.
///
/// <para>
/// The design puts the audit log in Phase 1 even though Maker/Checker is
/// deferred (§14): with Operations editing rules that decide financial
/// outcomes, "who changed this rule and when" is not optional, and
/// retrofitting an audit trail always costs more than writing it.
/// </para>
///
/// <para>
/// The portal's database role has <c>INSERT</c> and <c>SELECT</c> on this
/// schema and nothing else — no role anywhere has <c>UPDATE</c> or
/// <c>DELETE</c> — so the application cannot rewrite the record it is the
/// subject of. That property is enforced by the grants, not by this class.
/// </para>
/// </summary>
public sealed class AuditService(SqlConnection connection, IHttpContextAccessor? accessor = null)
{
    private readonly SqlConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public Task RecordAsync(
        ClaimsPrincipal? user,
        string entityType,
        object entityId,
        AuditAction action,
        object? before = null,
        object? after = null,
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        var userName = user?.FindFirst(ClaimTypes.Name)?.Value
            ?? accessor?.HttpContext?.User.FindFirst(ClaimTypes.Name)?.Value
            ?? "anonymous";

        var ip = accessor?.HttpContext?.Connection.RemoteIpAddress?.ToString();

        return Db.ExecuteAsync(
            _connection,
            """
            INSERT aud.AuditLog
                (EntityType, EntityId, Action, OldValueJson, NewValueJson,
                 PerformedBy, IpAddress, Notes)
            VALUES (@type, @id, @action, @old, @new, @by, @ip, @notes);
            """,
            c => c.With("@type", entityType)
                  .With("@id", entityId.ToString())
                  .With("@action", action.ToString())
                  .With("@old", before is null ? null : JsonSerializer.Serialize(before, Options))
                  .With("@new", after is null ? null : JsonSerializer.Serialize(after, Options))
                  .With("@by", userName)
                  .With("@ip", ip)
                  .With("@notes", notes),
            cancellationToken);
    }
}

/// <summary>
/// Mirrors <c>CK_AuditLog_Action</c>, which the database collates
/// case-sensitively — so these names are the stored strings, exactly.
///
/// <see cref="Export"/> exists because of review item D3: the log captured
/// configuration changes but not READS of sensitive exports, and regulators
/// ask who downloaded which report.
/// </summary>
public enum AuditAction
{
    Create,
    Update,
    Delete,
    Activate,
    Deactivate,
    Execute,
    Resume,
    Close,
    Reopen,
    Export,
    Login,
}
