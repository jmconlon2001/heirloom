using FamilyMemories.Data;
using FamilyMemories.Email;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyMemories.Tests.Infrastructure;

/// <summary>
/// Runs the real app against a fresh PostgreSQL database, with email captured in memory.
/// </summary>
public class AppFactory(PostgresFixture postgres) : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminEmail = "admin@example.test";
    public const string AdminName = "Jacob Admin";
    public const string PublicBaseUrl = "https://memories.example.test";

    private string connectionString = "";
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "fm-tests", Guid.NewGuid().ToString("N"));
    public CapturingEmailSender Email { get; } = new();
    public TestClock Clock { get; } = new();

    /// <summary>Extra settings (UPPER_SNAKE keys, like the .env file) a test class can override.</summary>
    protected virtual IDictionary<string, string?> Settings => new Dictionary<string, string?>();

    public virtual async ValueTask InitializeAsync()
    {
        connectionString = await postgres.CreateDatabaseAsync();
        _ = Services; // build the host now (runs migrations + seed)
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = connectionString,
            ["DATA_DIR"] = DataDir,
            ["PUBLIC_BASE_URL"] = PublicBaseUrl,
            ["ADMIN_EMAIL"] = AdminEmail,
            ["ADMIN_NAME"] = AdminName,
            ["HELP_NAME"] = "Jacob",
            ["HELP_PHONE"] = "(555) 010-0199",
            ["AUTH_RATE_LIMIT"] = "1000",
            ["PRE_MIGRATION_BACKUP"] = "false",
            ["JOBS_ENABLED"] = "false",
        };
        foreach (var (k, v) in Settings) settings[k] = v;
        foreach (var (k, v) in settings) builder.UseSetting(k, v);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Email);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    /// <summary>Creates a person directly (bypassing the admin UI).</summary>
    public async Task<AppUser> CreateUserAsync(string name, string email, string[] roles,
        PromptSetKind? set = null, Pacing pacing = Pacing.AllAtOnce)
    {
        await using var scope = Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<AppUser>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Guid? setId = set is null ? null : (await db.PromptSets.FirstAsync(s => s.Kind == set)).Id;
        var user = new AppUser
        {
            UserName = email, Email = email, EmailConfirmed = true, DisplayName = name,
            CreatedAt = Clock.GetUtcNow().UtcDateTime, PromptSetId = setId, Pacing = pacing,
            PacingStartedAt = setId is null ? null : Clock.GetUtcNow().UtcDateTime,
        };
        var result = await users.CreateAsync(user);
        if (!result.Succeeded) throw new InvalidOperationException(string.Join(", ", result.Errors.Select(e => e.Description)));
        await users.AddToRolesAsync(user, roles);
        return user;
    }

    public async Task<T> WithServiceAsync<TService, T>(Func<TService, Task<T>> action) where TService : notnull
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    public async Task WithDbAsync(Func<AppDbContext, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try { Directory.Delete(DataDir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }
}
