using FamilyMemories.Auth;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using Hangfire.Dashboard;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Media;

/// <summary>
/// Runs processing immediately, in its own scope, when Hangfire is off (JOBS_ENABLED=false):
/// tests and quick local runs.
/// </summary>
public sealed class InlineMediaJobs(IServiceScopeFactory scopes) : IMediaJobs
{
    public void EnqueueProcessing(Guid attachmentId)
    {
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<MediaProcessor>()
            .ProcessAsync(attachmentId, CancellationToken.None).GetAwaiter().GetResult();
    }

    public void EnqueueTranscription(Guid attachmentId)
    {
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<FamilyMemories.Extras.TranscriptionJob>()
            .RunAsync(attachmentId, CancellationToken.None).GetAwaiter().GetResult();
    }
}

/// <summary>Deletes uploads that were started but never finished (after 7 days).</summary>
public sealed class UploadCleanup(AppDbContext db, MediaStorage storage, TimeProvider clock, ILogger<UploadCleanup> log)
{
    public async Task RunAsync()
    {
        var cutoff = clock.GetUtcNow().UtcDateTime.AddDays(-7);
        var stale = await db.UploadSessions.Where(s => s.CompletedAt == null && s.UpdatedAt < cutoff).ToListAsync();
        foreach (var s in stale)
        {
            try { File.Delete(storage.UploadPartPath(s.Id)); } catch (IOException) { }
        }
        db.UploadSessions.RemoveRange(stale);
        await db.SaveChangesAsync();
        if (stale.Count > 0) log.LogInformation("Removed {Count} unfinished uploads", stale.Count);
    }
}

public sealed class AdminDashboardAuthorization : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context) =>
        context.GetHttpContext().User is { Identity.IsAuthenticated: true } user && user.IsAdmin();
}
