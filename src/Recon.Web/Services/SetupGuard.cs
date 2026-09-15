using Recon.Web.Services;

namespace Recon.Web;

// CA1848: the guard logs only on a state transition, not per request.
#pragma warning disable CA1848

/// <summary>
/// Sends every request to <c>/setup</c> until the portal has a database with a
/// schema in it.
///
/// <para>
/// Without this, a fresh install answers every screen with a connection error
/// — which is accurate and useless. The guard is also what makes the setup
/// screens reachable at all on an unconfigured portal, since every other
/// controller needs a connection to render.
/// </para>
///
/// <para>
/// The readiness check is cached until it first succeeds. It is a round trip
/// to the server, and doing it per request on a working portal would put a
/// query in front of every page for a state that changes once. Once ready, the
/// guard is out of the way for good — re-pointing at a different database
/// restarts the check by clearing it.
/// </para>
/// </summary>
public sealed class SetupGuard(
    RequestDelegate next,
    PlatformConfiguration configuration,
    DatabaseInstaller installer,
    ILogger<SetupGuard> logger)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    private readonly PlatformConfiguration _configuration =
        configuration ?? throw new ArgumentNullException(nameof(configuration));

    private readonly DatabaseInstaller _installer =
        installer ?? throw new ArgumentNullException(nameof(installer));

    private readonly ILogger<SetupGuard> _log = logger ?? throw new ArgumentNullException(nameof(logger));

    private volatile bool _ready;

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var path = context.Request.Path.Value ?? "/";

        // The setup screens themselves, sign-in, and static files must always
        // answer: the first two are how the portal gets configured and the
        // third is how those pages render.
        if (_ready
            || path.StartsWith("/setup", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/account", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/css", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/js", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/vendor", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/favicon", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (!_configuration.IsConfigured)
        {
            Redirect(context, "not configured");
            return;
        }

        var status = await _installer.StatusAsync(context.RequestAborted).ConfigureAwait(false);

        if (!status.Ready)
        {
            Redirect(context, status.Message ?? "the schema is not installed");
            return;
        }

        _log.LogInformation("The platform is configured and the schema is installed.");
        _ready = true;

        await _next(context).ConfigureAwait(false);
    }

    private void Redirect(HttpContext context, string reason)
    {
        // An XHR gets an answer it can show rather than the HTML of a page it
        // did not ask for.
        if (context.Request.Headers.XRequestedWith == "XMLHttpRequest"
            || context.Request.Headers.Accept.ToString().Contains("application/json", StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.ContentType = "application/json";

            _log.LogWarning("Refused {Path}: {Reason}.", context.Request.Path, reason);

            context.Response.WriteAsJsonAsync(new { error = "setup incomplete", reason });
            return;
        }

        context.Response.Redirect("/setup");
    }
}

public static class SetupGuardExtensions
{
    public static IApplicationBuilder UseSetupGuard(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<SetupGuard>();
    }
}
#pragma warning restore CA1848
