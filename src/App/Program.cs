using System.Threading.RateLimiting;
using FamilyMemories.Auth;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using FamilyMemories.Email;
using FamilyMemories.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);
var options = AppOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);

// ---- Logging: JSON to console, rolling file under /data/logs ----
builder.Services.AddSerilog((services, log) => log
    .ReadFrom.Configuration(builder.Configuration)
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(new RenderedCompactJsonFormatter())
    .WriteTo.File(new CompactJsonFormatter(), Path.Combine(options.LogsDir, "app-.log"),
        rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30));

// ---- Database ----
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(options.ConnectionString));
builder.Services.AddScoped<DatabaseInitializer>();

// ---- Data Protection: keys must survive container restarts or every cookie and link breaks ----
builder.Services.AddDataProtection()
    .SetApplicationName("FamilyMemories")
    .PersistKeysToFileSystem(new DirectoryInfo(options.KeysDir));

// ---- Identity: passwordless by default ----
builder.Services.AddIdentity<AppUser, AppRole>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.SignIn.RequireConfirmedAccount = false;
        // No complexity rules (BUILD_PROMPT §4). Optional passwords arrive later; only length matters.
        o.Password.RequireDigit = false;
        o.Password.RequireLowercase = false;
        o.Password.RequireUppercase = false;
        o.Password.RequireNonAlphanumeric = false;
        o.Password.RequiredUniqueChars = 1;
        o.Password.RequiredLength = 8;
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddClaimsPrincipalFactory<AppClaimsPrincipalFactory>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = "fm_auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    o.ExpireTimeSpan = options.RememberedDeviceLifetime;
    o.SlidingExpiration = true;
    o.LoginPath = "/account/login";
    o.LogoutPath = "/account/logout";
    o.AccessDeniedPath = "/account/access-denied";
    o.Events.OnValidatePrincipal = async context =>
    {
        await DeviceService.ValidateAsync(context);
        if (context.Principal is not null)
            await SecurityStampValidator.ValidatePrincipalAsync(context);
    };
});
builder.Services.Configure<SecurityStampValidatorOptions>(o =>
{
    // Keep the device claim when Identity periodically refreshes the principal.
    o.OnRefreshingPrincipal = ctx =>
    {
        var device = ctx.CurrentPrincipal?.FindFirst(DeviceService.DeviceClaim);
        if (device is not null && ctx.NewPrincipal?.Identity is System.Security.Claims.ClaimsIdentity id
            && !id.HasClaim(c => c.Type == DeviceService.DeviceClaim))
            id.AddClaim(device);
        return Task.CompletedTask;
    };
});
builder.Services.AddAntiforgery(o =>
{
    o.Cookie.Name = "fm_af";
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

builder.Services.Configure<Microsoft.AspNetCore.Mvc.CookieTempDataProviderOptions>(o =>
{
    o.Cookie.Name = "fm_tmp";
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

builder.Services.AddScoped<MagicLinkService>();
builder.Services.AddScoped<DeviceService>();
builder.Services.AddSingleton<EmailTemplates>();
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();

// ---- Reverse proxy (Caddy on OPNsense): trust X-Forwarded-For/Proto only from TRUSTED_PROXIES ----
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var network in options.TrustedProxies)
        o.KnownIPNetworks.Add(network);
});

// ---- Rate limiting: only sign-in form posts, per client IP ----
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
    {
        if (HttpMethods.IsPost(http.Request.Method)
            && http.Request.Path.StartsWithSegments("/account", StringComparison.OrdinalIgnoreCase)
            && !http.Request.Path.StartsWithSegments("/account/logout", StringComparison.OrdinalIgnoreCase))
        {
            return RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = options.AuthRateLimit, Window = TimeSpan.FromMinutes(10) });
        }
        return RateLimitPartition.GetNoLimiter("none");
    });
});

builder.Services.AddHttpClient("gotenberg", c => c.Timeout = TimeSpan.FromSeconds(3));
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"])
    .AddCheck<GotenbergHealthCheck>("gotenberg", tags: ["ready"]);

builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/");
    o.Conventions.AllowAnonymousToFolder("/Account");
    o.Conventions.AllowAnonymousToPage("/Error");
    o.Conventions.AllowAnonymousToPage("/Display");
});
builder.Services.Configure<RouteOptions>(o => o.LowercaseUrls = true);

var app = builder.Build();

// ---- Migrate (with a safety backup) and seed before taking traffic ----
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
}

app.UseForwardedHeaders();
app.UseSerilogRequestLogging(o => o.GetLevel = (http, _, ex) =>
    ex is not null || http.Response.StatusCode >= 500 ? LogEventLevel.Error
    : http.Request.Path.StartsWithSegments("/healthz") || http.Request.Path.StartsWithSegments("/readyz") ? LogEventLevel.Verbose
    : LogEventLevel.Information);

app.UseExceptionHandler("/error");
app.UseStatusCodePagesWithReExecute("/error/{0}");
app.UseSecurityHeaders();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/healthz", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/readyz", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
app.MapRazorPages();

app.Run();

public partial class Program;
