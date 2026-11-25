using System.Threading.RateLimiting;
using FamilyMemories.Admin;
using FamilyMemories.Auth;
using FamilyMemories.Backup;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using FamilyMemories.Email;
using FamilyMemories.Media;
using FamilyMemories.Stories;
using Hangfire;
using Hangfire.PostgreSql;
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
    // Scripts calling /api or loading /media get a status code, not an HTML login page.
    o.Events.OnRedirectToLogin = context => RedirectOrStatus(context, StatusCodes.Status401Unauthorized);
    o.Events.OnRedirectToAccessDenied = context => RedirectOrStatus(context, StatusCodes.Status403Forbidden);
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
    o.HeaderName = "X-CSRF-TOKEN";
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

builder.Services.Configure<Microsoft.AspNetCore.Mvc.CookieTempDataProviderOptions>(o =>
{
    o.Cookie.Name = "fm_tmp";
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

builder.Services.AddScoped<MagicLinkService>();
builder.Services.AddScoped<DeviceService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AuditLog>();
builder.Services.AddScoped<PromptSeeder>();
builder.Services.AddScoped<StoryPlanService>();
builder.Services.AddScoped<StoryAccess>();
builder.Services.AddScoped<MemoryService>();
builder.Services.AddScoped<UserAdminService>();
builder.Services.AddSingleton<MediaStorage>();
builder.Services.AddScoped<AttachmentService>();
builder.Services.AddScoped<MediaProcessor>();
builder.Services.AddScoped<AdminAlerts>();
builder.Services.AddSingleton<MaintenanceMode>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddScoped<BackupJobs>();
builder.Services.AddScoped<BackupStatusQuery>();
builder.Services.AddHttpClient("ntfy", c => c.Timeout = TimeSpan.FromSeconds(10));

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Admin", p => p.RequireRole(Roles.Admin));

// Uploads without JavaScript arrive as one multipart form; allow the largest file type.
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
    o.MultipartBodyLengthLimit = Math.Max(options.MaxVideoBytes, options.MaxAudioBytes) + 1024 * 1024);

// ---- Background jobs (media processing now; reminders, exports and backups later) ----
if (options.JobsEnabled)
{
    builder.Services.AddHangfire(c => c
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UsePostgreSqlStorage(pg => pg.UseNpgsqlConnection(options.ConnectionString),
            new PostgreSqlStorageOptions { SchemaName = "hangfire", PrepareSchemaIfNecessary = true, EnableLongPolling = true }));
    builder.Services.AddHangfireServer(o => o.WorkerCount = Math.Max(2, Environment.ProcessorCount / 2));
    builder.Services.AddScoped<IMediaJobs, HangfireMediaJobs>();
    builder.Services.AddScoped<IBackupQueue, HangfireBackupQueue>();
}
else
{
    builder.Services.AddScoped<IMediaJobs, InlineMediaJobs>();
    builder.Services.AddScoped<IBackupQueue, InlineBackupQueue>();
}
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
    o.Conventions.AuthorizeFolder("/Admin", "Admin");
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
app.UseMaintenancePage();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/healthz", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/readyz", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
app.MapRazorPages();
app.MapStoryApi();
app.MapMediaEndpoints();
if (options.JobsEnabled)
{
    app.MapHangfireDashboard("/admin/jobs", new DashboardOptions
    {
        Authorization = [new AdminDashboardAuthorization()],
        DashboardTitle = $"{options.AppName}: background jobs",
        AppPath = "/admin",
    });
    app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<UploadCleanup>(
        "upload-cleanup", c => c.RunAsync(), Cron.Daily(3));
    if (options.BackupsConfigured)
        BackupJobs.Schedule(app.Services.GetRequiredService<IRecurringJobManager>(), options);
}

app.Run();

static Task RedirectOrStatus(Microsoft.AspNetCore.Authentication.RedirectContext<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions> context, int status)
{
    var path = context.Request.Path;
    if (path.StartsWithSegments("/api") || path.StartsWithSegments("/media"))
        context.Response.StatusCode = status;
    else
        context.Response.Redirect(context.RedirectUri);
    return Task.CompletedTask;
}

public partial class Program;
