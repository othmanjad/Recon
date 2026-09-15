using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Data.SqlClient;
using Recon.Web.Filters;
using Recon.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------
// Server-rendered MVC with Razor views. No SPA framework and no build
// step: the client side is Bootstrap and jQuery served from wwwroot,
// vendored rather than pulled from a CDN, because a reconciliation portal
// inside a bank's network must not depend on an outbound request to a
// third party to render its own page (design §13.1).
// ---------------------------------------------------------------------
builder.Services.AddControllersWithViews(options =>
{
    // AccessService.RequireAsync throws rather than returning false, so that
    // a call site which carried on cannot write data the user may not write.
    // This turns those refusals into a page, once, instead of a try/catch
    // around every action.
    options.Filters.Add<AccessDeniedFilter>();
});

// The audit log records the caller's IP, and the access checks read the
// current user outside a controller. Both take IHttpContextAccessor as an
// optional dependency — without it registered they silently record nothing,
// which is the failure mode an audit log must not have.
builder.Services.AddHttpContextAccessor();

// A scoped connection per request. Each request is one unit of work, and
// the engine's repositories take a connection rather than a factory so
// that a run and its steps share one session — which the application lock
// depends on, since sp_getapplock is session-scoped.
builder.Services.AddScoped(_ =>
{
    var connectionString = builder.Configuration.GetConnectionString("Recon")
        ?? throw new InvalidOperationException(
            "ConnectionStrings:Recon is not configured. The portal cannot start without a database.");

    var connection = new SqlConnection(connectionString);
    connection.Open();
    return connection;
});

builder.Services.AddScoped<Recon.Data.ConfigurationRepository>();
builder.Services.AddScoped<Recon.Data.RunRepository>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<AccessService>();
builder.Services.AddScoped<PortalQueries>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<SandboxService>();

// The scheduler. Runs in-process for a single-node deployment; the
// application lock in RunRepository is what makes it safe to run more
// than one node, because the second node's attempt is refused rather
// than duplicated (review item B2).
builder.Services.AddHostedService<SchedulerService>();

// Development authentication only: a cookie naming the operator, so the
// audit log and the per-counterparty access checks have a real subject.
// A deployment replaces this with the bank's identity provider — the
// authorization logic reads claims and does not care where they came
// from.
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/account/signin";
        options.AccessDeniedPath = "/account/denied";
        options.Cookie.Name = "recon.operator";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.ExpireTimeSpan = TimeSpan.FromHours(9);
    });

builder.Services.AddAuthorization();
builder.Services.AddAntiforgery();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/home/error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");

await app.RunAsync();
