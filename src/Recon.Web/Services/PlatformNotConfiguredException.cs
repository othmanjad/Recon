namespace Recon.Web.Services;

/// <summary>
/// Thrown when something asks for a database connection before the portal has
/// one.
///
/// <para>
/// A distinct type rather than an <see cref="InvalidOperationException"/>
/// because the answer to it is a specific screen: the setup guard turns it
/// into a redirect to <c>/setup</c>, and a request that slips past the guard
/// (an XHR, say) gets a message naming the actual state rather than a
/// connection-string error.
/// </para>
/// </summary>
public sealed class PlatformNotConfiguredException()
    : Exception("The platform has no database configured yet. Open /setup to point it at one.");
