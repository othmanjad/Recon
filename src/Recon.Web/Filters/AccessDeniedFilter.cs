using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Recon.Domain.Configuration;
using Recon.Web.Services;

namespace Recon.Web.Filters;

// CA1848 asks for LoggerMessage delegates. A refused authorization is a rare
// event by construction, so the allocation the rule guards against does not
// arise, and the inlined template keeps the message beside its cause.
#pragma warning disable CA1848

/// <summary>
/// Turns the exceptions the configuration layer throws into pages.
///
/// <para>
/// <see cref="AccessService.RequireAsync"/> throws rather than returning
/// false, because a call site that carried on would write data the user may
/// not write. That is the right shape for the check and the wrong shape for
/// an HTTP response, so the translation happens here — once — rather than in
/// a try/catch around every action.
/// </para>
///
/// <para>
/// A refusal is logged with the user and the counterparty. "Somebody tried to
/// configure a partner they do not administer" is exactly the kind of event
/// that should not be silent.
/// </para>
/// </summary>
public sealed class AccessDeniedFilter(
    ILogger<AccessDeniedFilter> logger,
    Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataDictionaryFactory tempData) : IExceptionFilter
{
    private readonly ILogger<AccessDeniedFilter> _log =
        logger ?? throw new ArgumentNullException(nameof(logger));

    // An exception filter runs outside the controller, so there is no
    // controller instance to reach TempData through. The factory produces the
    // same dictionary the redirect target will read.
    private readonly Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataDictionaryFactory _tempData =
        tempData ?? throw new ArgumentNullException(nameof(tempData));

    public void OnException(ExceptionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        switch (context.Exception)
        {
            case AccessDeniedException denied:
                _log.LogWarning(
                    "{User} was refused {Level} access to counterparty {CounterpartyId}.",
                    denied.UserName, denied.Required, denied.CounterpartyId);

                context.Result = new RedirectToActionResult(
                    "Denied", "Account", new { level = denied.Required.ToString() });

                context.ExceptionHandled = true;
                break;

            // A field code that is not in the registry, or is in it but not
            // matchable, is a configuration error or an attempt — either way
            // the answer is a message, not a 500.
            case FieldNotInRegistryException registry:
                _log.LogWarning(registry, "A request cited a field outside the registry.");
                Explain(context, registry.Message);
                break;

            case FieldNotMatchableException matchable:
                _log.LogWarning(matchable, "A request cited a non-matchable field.");
                Explain(context, matchable.Message);
                break;

            case Recon.Engine.Sql.SqlCompilationException compilation:
                _log.LogWarning(compilation, "A rule could not be compiled.");
                Explain(context, "That rule could not be compiled: " + compilation.Message);
                break;

            default:
                // Anything else is a real fault and belongs to the error
                // handler, which logs it and shows the error page.
                break;
        }
    }

    private void Explain(ExceptionContext context, string message)
    {
        _tempData.GetTempData(context.HttpContext)["Error"] = message;

        // Back where they came from, with the message. A redirect to a generic
        // error page would lose the form they were filling in. The referer is
        // only followed when it points at this same host: following an
        // arbitrary one would be an open redirect off a trusted page.
        var referer = context.HttpContext.Request.Headers.Referer.ToString();

        context.Result = !string.IsNullOrEmpty(referer)
            && Uri.TryCreate(referer, UriKind.Absolute, out var uri)
            && string.Equals(uri.Host, context.HttpContext.Request.Host.Host, StringComparison.OrdinalIgnoreCase)
                ? new RedirectResult(uri.PathAndQuery)
                : new RedirectToActionResult("Index", "Home", null);

        context.ExceptionHandled = true;
    }
}
#pragma warning restore CA1848
