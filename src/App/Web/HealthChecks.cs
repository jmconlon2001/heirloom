using FamilyMemories.Configuration;
using FamilyMemories.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FamilyMemories.Web;

public sealed class DatabaseHealthCheck(AppDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        await db.Database.CanConnectAsync(ct)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Cannot connect to PostgreSQL");
}

public sealed class GotenbergHealthCheck(IHttpClientFactory http, AppOptions options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        if (options.GotenbergUrl is null)
            return HealthCheckResult.Degraded("GOTENBERG_URL is not set; PDF exports are unavailable");

        try
        {
            using var response = await http.CreateClient("gotenberg").GetAsync(
                new Uri(new Uri(options.GotenbergUrl.TrimEnd('/') + "/"), "health"), ct);
            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"Gotenberg returned {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return HealthCheckResult.Unhealthy("Gotenberg is unreachable", ex);
        }
    }
}
