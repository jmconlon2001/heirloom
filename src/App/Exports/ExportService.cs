using System.Text.Json;
using FamilyMemories.Admin;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using FamilyMemories.Email;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Exports;

/// <summary>Starts exports and runs them as background jobs with a progress bar (§9).</summary>
public sealed class ExportService(AppDbContext db, IExportQueue queue, AuditLog audit, TimeProvider clock)
{
    public static readonly JsonSerializerOptions Json = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public async Task<ExportJob> StartAsync(ExportKind kind, ExportScope scope, BookOptions book, Guid userId)
    {
        var version = (await db.Exports.Where(e => e.Kind == kind).MaxAsync(e => (int?)e.Version) ?? 0) + 1;
        var job = new ExportJob
        {
            Id = Guid.CreateVersion7(), Kind = kind, Version = version, Status = ExportStatus.Queued, Message = "Waiting to start",
            ScopeJson = JsonSerializer.Serialize(scope, Json), OptionsJson = JsonSerializer.Serialize(book, Json),
            CreatedById = userId, CreatedAt = clock.GetUtcNow().UtcDateTime,
        };
        db.Exports.Add(job);
        audit.Add("export.start", $"Started export: {kind} v{version}{(scope.IncludePrivate ? " (including private stories)" : "")}", "export", job.Id);
        await db.SaveChangesAsync();
        await queue.EnqueueAsync(job.Id);
        return job;
    }
}

public interface IExportQueue { Task EnqueueAsync(Guid jobId); }

public sealed class HangfireExportQueue(IBackgroundJobClient jobs) : IExportQueue
{
    public Task EnqueueAsync(Guid jobId) { jobs.Enqueue<ExportRunner>(r => r.RunAsync(jobId, CancellationToken.None)); return Task.CompletedTask; }
}

public sealed class InlineExportQueue(IServiceScopeFactory scopes) : IExportQueue
{
    public async Task EnqueueAsync(Guid jobId)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ExportRunner>().RunAsync(jobId, CancellationToken.None);
    }
}

public sealed class ExportRunner(AppDbContext db, ExportPipeline pipeline, EmailService email, AppOptions options, TimeProvider clock, ILogger<ExportRunner> log)
{
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(Guid jobId, CancellationToken ct)
    {
        var job = await db.Exports.SingleOrDefaultAsync(e => e.Id == jobId, ct);
        if (job is null || job.Status is ExportStatus.Done) return;
        var scope = JsonSerializer.Deserialize<ExportScope>(job.ScopeJson, ExportService.Json) ?? new ExportScope();
        var book = JsonSerializer.Deserialize<BookOptions>(job.OptionsJson, ExportService.Json) ?? new BookOptions();

        async Task Progress(int percent, string message)
        {
            await db.Exports.Where(e => e.Id == jobId).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, ExportStatus.Running).SetProperty(e => e.Progress, percent).SetProperty(e => e.Message, message), ct);
        }

        try
        {
            await Progress(1, "Starting");
            var file = await pipeline.RunAsync(job, scope, book, Progress, ct);
            var size = new FileInfo(file).Length;
            await db.Exports.Where(e => e.Id == jobId).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, ExportStatus.Done).SetProperty(e => e.Progress, 100)
                .SetProperty(e => e.FileName, Path.GetFileName(file)).SetProperty(e => e.SizeBytes, size)
                .SetProperty(e => e.FinishedAt, clock.GetUtcNow().UtcDateTime), ct);

            var who = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == job.CreatedById, ct);
            if (who?.Email is not null)
                await email.SendAsync(EmailKind.ExportReady, new Recipient(who.Email, who.DisplayName, who.Id), new Dictionary<string, object?>
                {
                    ["export_name"] = Describe(job.Kind),
                    ["link"] = options.BuildUrl("admin/exports").ToString(),
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Export {ExportId} failed", jobId);
            var message = ex.Message.Length > 3900 ? ex.Message[..3900] : ex.Message;
            await db.Exports.Where(e => e.Id == jobId).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, ExportStatus.Failed).SetProperty(e => e.Message, "It didn't work: " + message)
                .SetProperty(e => e.FinishedAt, clock.GetUtcNow().UtcDateTime), CancellationToken.None);
        }
    }

    public static string Describe(ExportKind kind) => kind switch
    {
        ExportKind.Website => "family website",
        ExportKind.Pdf => "PDF of the family's stories",
        ExportKind.Book => "print-ready family book",
        _ => "portable archive",
    };
}
