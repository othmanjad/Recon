using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Recon.Data;
using Recon.Web.Services;

namespace Recon.Web.Controllers;

/// <summary>
/// Platform settings — <c>cfg.PlatformSetting</c>.
///
/// <para>
/// The table exists because the design's retention periods, batch sizes and
/// thresholds were scattered through prose as numbers nobody could change
/// without a deployment. Two of them are still open questions rather than
/// decisions, and the screen says which: <c>ResultsMonthsOnline</c> is a
/// seven-year placeholder standing in for a regulatory retention period
/// nobody has confirmed.
/// </para>
///
/// <para>
/// Values are validated against the declared data type before the write. The
/// column is <c>NVARCHAR</c> for all four types, so "abc" would store happily
/// in an <c>Int</c> setting and fail at 3am inside the scheduler's parse
/// instead — where it would fall back to a default and look like nothing
/// happened.
/// </para>
/// </summary>
[Authorize]
public sealed class SettingsController(
    PortalQueries queries,
    Microsoft.Data.SqlClient.SqlConnection connection,
    AccessService access,
    AuditService audit) : Controller
{
    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Platform settings";

        var grants = await access.GrantsAsync(User).ConfigureAwait(false);

        ViewData["CanConfigure"] = grants.Values.Any(l => l >= AccessLevel.Configure);
        ViewData["Currencies"] = await Db.QueryAsync(
            connection,
            """
            SELECT CurrencyCode, Name, MinorUnits FROM cfg.Currency ORDER BY CurrencyCode;
            """,
            r => new CurrencyRow(r.GetString(0), r.GetString(1), r.GetByte(2))).ConfigureAwait(false);

        ViewData["Slots"] = await Db.QueryAsync(
            connection,
            """
            SELECT SlotType, COUNT(*), SUM(CASE WHEN IsNormalizedOnly = 1 THEN 1 ELSE 0 END)
            FROM cfg.StorageSlotCatalogue
            GROUP BY SlotType
            ORDER BY SlotType;
            """,
            r => new SlotSummaryRow(r.GetString(0), r.GetInt32(1), r.GetInt32(2))).ConfigureAwait(false);

        return View(await queries.SettingsAsync().ConfigureAwait(false));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(string settingKey, string settingValue)
    {
        var grants = await access.GrantsAsync(User).ConfigureAwait(false);
        if (!grants.Values.Any(l => l >= AccessLevel.Configure))
        {
            TempData["Error"] = "Changing a platform setting requires Configure access.";
            return RedirectToAction(nameof(Index));
        }

        var settings = await queries.SettingsAsync().ConfigureAwait(false);
        var setting = settings.FirstOrDefault(s => s.Key == settingKey);

        if (setting is null)
        {
            // Keys are not created here. A setting the code does not read is a
            // row nothing consults, which is worse than no row at all.
            TempData["Error"] = $"'{settingKey}' is not a known setting.";
            return RedirectToAction(nameof(Index));
        }

        var value = (settingValue ?? string.Empty).Trim();

        if (!IsValid(setting.DataType, value, out var expected))
        {
            TempData["Error"] = $"{settingKey} is declared {setting.DataType}: {expected}";
            return RedirectToAction(nameof(Index));
        }

        if (string.Equals(value, setting.Value, StringComparison.Ordinal))
        {
            TempData["Ok"] = $"{settingKey} is already {value}.";
            return RedirectToAction(nameof(Index));
        }

        await Db.ExecuteAsync(
            connection,
            """
            UPDATE cfg.PlatformSetting
            SET SettingValue = @value, ModifiedAt = SYSDATETIME(), ModifiedBy = @by
            WHERE SettingKey = @key;
            """,
            c => c.With("@key", settingKey)
                  .With("@value", value)
                  .With("@by", access.UserName(User))).ConfigureAwait(false);

        await audit.RecordAsync(
            User, "PlatformSetting", settingKey, AuditAction.Update,
            before: new { setting.Value },
            after: new { Value = value }).ConfigureAwait(false);

        TempData["Ok"] = $"{settingKey}: {setting.Value} → {value}.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Validation against the declared type, with the message saying what was
    /// expected rather than that something was wrong.
    /// </summary>
    internal static bool IsValid(string dataType, string value, out string expected)
    {
        switch (dataType)
        {
            case "Int":
                expected = "a whole number, for example 3";
                return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                    && i >= 0;

            case "Decimal":
                expected = "a decimal number with a dot, for example 15.5";
                return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _);

            case "Bool":
                expected = "true or false";
                return value is "true" or "false";

            case "String":
                expected = "a non-empty value of at most 300 characters";
                return value.Length is > 0 and <= 300;

            default:
                expected = $"an unknown data type '{dataType}' — the row itself is wrong";
                return false;
        }
    }
}

public sealed record CurrencyRow(string Code, string Name, int MinorUnits);

public sealed record SlotSummaryRow(string SlotType, int Total, int NormalizedOnly);
