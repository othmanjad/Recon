using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Recon.Web.Services;

namespace Recon.Web.Controllers;

// CA1848 asks for LoggerMessage delegates. Setup happens once and logs twice:
// who pointed the portal at which server, and who claimed a counterparty. The
// allocation the rule guards against does not arise, and both messages belong
// beside the action that produced them.
#pragma warning disable CA1848

/// <summary>
/// First run: point the portal at a SQL Server, let it build its own database,
/// and get the first operator in.
///
/// <para>
/// This screen exists because every other answer to "where is the database"
/// requires somebody with a shell on the server. An operator who has been
/// handed a machine with SQL Server on it can open the portal, type a server
/// name, press a button and have a working platform — and the same screen
/// re-points it at a different server later without a redeploy.
/// </para>
///
/// <para>
/// Sign-in is required even here. Creating a database and installing a schema
/// are the two most consequential things anyone does to this platform, and
/// both are written to the audit log with a name against them — which means
/// there has to be a name. Sign-in works without a database, so the order
/// holds.
/// </para>
/// </summary>
[Authorize]
public sealed class SetupController(
    PlatformConfiguration platform,
    DatabaseInstaller installer,
    ILogger<SetupController> logger) : Controller
{
    // Deliberately NOT AccessService: constructing one needs a database
    // connection, and this is the one controller that has to answer without
    // one. Found by running the first-run path — every /setup request was a
    // 500 because the screen that configures the database could not be built
    // without the database.
    private string Operator => AccessService.NameOf(User);

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        ViewData["Title"] = "Setup";

        var status = await installer.StatusAsync(cancellationToken).ConfigureAwait(false);

        ViewData["Status"] = status;
        ViewData["Platform"] = platform;
        ViewData["User"] = Operator;

        return View();
    }

    /// <summary>
    /// Tests a connection without saving it, so the operator finds out about
    /// a wrong password before it is written to disk.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Test(
        string server,
        string database,
        bool integratedSecurity,
        string? userId,
        string? password,
        bool encrypt,
        bool trustServerCertificate,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(database))
        {
            return Json(new { ok = false, message = "A server and a database name are required." });
        }

        var connectionString = PlatformConfiguration.Build(
            server.Trim(), database.Trim(), integratedSecurity, userId, password,
            encrypt, trustServerCertificate);

        // The database may legitimately not exist yet — that is what the
        // install button is for — so the server itself is tested through
        // master, and the named database separately.
        var masterString = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = "master",
        }.ConnectionString;

        var serverTest = await PlatformConfiguration.TestAsync(masterString, cancellationToken)
            .ConfigureAwait(false);

        if (!serverTest.Ok)
        {
            return Json(new { ok = false, message = serverTest.Message });
        }

        var databaseTest = await PlatformConfiguration.TestAsync(connectionString, cancellationToken)
            .ConfigureAwait(false);

        return Json(new
        {
            ok = true,
            version = serverTest.Version,
            edition = serverTest.Edition,
            login = serverTest.Login,
            databaseExists = databaseTest.Ok,
            message = databaseTest.Ok
                ? $"Connected to [{database.Trim()}] as {serverTest.Login}."
                : $"The server answered as {serverTest.Login}, but [{database.Trim()}] does not exist yet. " +
                  "Saving and then installing will create it.",
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Save(
        string server,
        string database,
        bool integratedSecurity,
        string? userId,
        string? password,
        bool encrypt,
        bool trustServerCertificate,
        string? storageRoot)
    {
        if (platform.IsFromHost)
        {
            TempData["Error"] =
                "The connection string comes from the host's configuration, which the portal does " +
                "not overwrite. Change it there, or remove ConnectionStrings:Recon to manage it here.";

            return RedirectToAction(nameof(Index));
        }

        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(database))
        {
            TempData["Error"] = "A server and a database name are required.";
            return RedirectToAction(nameof(Index));
        }

        var connectionString = PlatformConfiguration.Build(
            server.Trim(), database.Trim(), integratedSecurity, userId, password,
            encrypt, trustServerCertificate);

        platform.Save(connectionString, storageRoot);

        // Redacted, always: a log line with a password in it is a password in
        // every log aggregator the deployment has.
        logger.LogInformation(
            "{User} pointed the portal at {ConnectionString}.",
            Operator, PlatformConfiguration.Redact(connectionString));

        TempData["Ok"] = $"Saved. The portal will use [{database.Trim()}] on {server.Trim()}.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Creates the database if it is missing and applies the schema scripts
    /// that have not been applied.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Install(CancellationToken cancellationToken)
    {
        var result = await installer.InstallAsync(Operator, cancellationToken).ConfigureAwait(false);

        TempData[result.Ok ? "Ok" : "Error"] = result.Ok
            ? "Install complete: " + string.Join("; ", result.Log)
            : "Install failed: " + result.Message
              + (result.Log.Count > 0 ? " (got as far as: " + string.Join("; ", result.Log) + ")" : "");

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Records the schema scripts as applied, for a database somebody else
    /// built — the test harness, the demo script, a DBA, a restored backup.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Adopt(CancellationToken cancellationToken)
    {
        var result = await installer.AdoptAsync(Operator, cancellationToken).ConfigureAwait(false);

        TempData[result.Ok ? "Ok" : "Error"] = result.Ok
            ? "Recorded: " + string.Join("; ", result.Log)
            : result.Message;

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Applies the demo configuration — a complete worked reconciliation — and
    /// grants the caller access to it.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Demo(CancellationToken cancellationToken)
    {
        var result = await installer.SeedDemoAsync(Operator, cancellationToken).ConfigureAwait(false);

        TempData[result.Ok ? "Ok" : "Error"] = result.Ok
            ? "Demo configuration loaded: " + string.Join("; ", result.Log)
            : "Could not load the demo configuration: " + result.Message;

        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Grants the caller Configure access to every counterparty that has no
    /// administrator — the first-run escape hatch.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Claim(CancellationToken cancellationToken)
    {
        var user = Operator;
        var granted = await installer.ClaimUnadministeredAsync(user, cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "{User} claimed {Count} unadministered counterparty(ies).", user, granted);

        TempData[granted > 0 ? "Ok" : "Error"] = granted > 0
            ? $"{user} now has Configure access to {granted} counterparty(ies)."
            : "Nothing to claim: every counterparty already has an administrator. Ask one of them " +
              "for a grant — this button only ever fills a vacancy, so it cannot be used to get " +
              "into a counterparty somebody already administers.";

        return RedirectToAction(nameof(Index));
    }
}
#pragma warning restore CA1848
