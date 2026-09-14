using System.Globalization;
using Microsoft.AspNetCore.Html;
using Recon.Domain.Money;

namespace Recon.Web;

/// <summary>
/// Formatting the views share.
///
/// <para>
/// <see cref="Money"/> is the only place in the web layer a minor-unit integer
/// becomes a displayable string, and it delegates to
/// <see cref="MinorUnits.Format"/> — which inserts a separator into the digits
/// rather than dividing. Nothing here does arithmetic on an amount.
/// </para>
/// </summary>
public static class Fmt
{
    /// <summary>
    /// JOD has 3 minor units, USD and EUR have 2. The scale belongs to the
    /// currency, so a caller that does not know it gets JOD — and every call
    /// site that has the currency passes it.
    /// </summary>
    public static int Scale(string? currencyCode) => currencyCode switch
    {
        null or "JOD" => 3,
        "USD" or "EUR" or "SAR" or "AED" => 2,
        _ => 3,
    };

    public static string Money(long? minor, string? currencyCode = null) =>
        minor is { } m ? MinorUnits.Format(m, Scale(currencyCode)) : "—";

    public static string Count(long? n) =>
        n is { } v ? v.ToString("N0", CultureInfo.InvariantCulture) : "—";

    public static string Duration(TimeSpan? span) => span is { } s
        ? $"{(int)s.TotalMinutes}m {s.Seconds:00}s"
        : "—";

    public static string Seconds(double? seconds) =>
        seconds is { } s ? s.ToString("N1", CultureInfo.InvariantCulture) + "s" : "—";

    public static HtmlString StatusBadge(string status)
    {
        var tone = status switch
        {
            "Completed" or "Resolved" => "success",
            "Running" => "primary",
            "Failed" or "Open" => "danger",
            "Rejected" or "InProgress" => "warning",
            "Resuming" or "AutoClosed" => "info",
            _ => "secondary",
        };

        return new HtmlString(
            $"""<span class="badge text-bg-{tone}">{System.Net.WebUtility.HtmlEncode(status)}</span>""");
    }

    public static HtmlString RunTypeBadge(string type)
    {
        var cls = type switch
        {
            "Sandbox" => "badge text-bg-warning",
            "Rematch" or "Rerun" => "badge text-bg-info",
            _ => "badge text-bg-light border",
        };

        return new HtmlString(
            $"""<span class="{cls}">{System.Net.WebUtility.HtmlEncode(type)}</span>""");
    }

    public static HtmlString RoleBadge(string? role)
    {
        if (string.IsNullOrEmpty(role))
        {
            return new HtmlString("""<span class="text-muted">—</span>""");
        }

        var tone = role switch
        {
            "Reference" or "OriginalReference" => "primary",
            "Amount" => "success",
            "Date" => "info",
            "Direction" => "dark",
            "Status" => "warning",
            _ => "light",
        };

        var cls = tone == "light"
            ? "badge text-bg-light border rc-badge-role"
            : $"badge text-bg-{tone} rc-badge-role";

        return new HtmlString(
            $"""<span class="{cls}">{System.Net.WebUtility.HtmlEncode(role)}</span>""");
    }
}
