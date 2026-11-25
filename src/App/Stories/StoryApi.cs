using FamilyMemories.Auth;
using FamilyMemories.Data;
using FamilyMemories.Media;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Stories;

/// <summary>
/// Small JSON endpoints used by the answer page's scripts: autosave, chunked uploads,
/// attachment status and ordering. Every call checks antiforgery and story access.
/// </summary>
public static class StoryApi
{
    public sealed record WhenDto(int? Year, int? Month, int? Day, bool Circa, int? Decade, string? Label);
    public sealed record AutosaveRequest(string? Title, string? Body, string? Place, WhenDto? When, int? Revision,
        Dictionary<Guid, string?>? Captions = null);
    public sealed record StartUploadRequest(string? FileName, long Size, bool Voice);
    public sealed record OrderRequest(List<Guid> Ids);

    public static void MapStoryApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization().AddEndpointFilter(ValidateAntiforgery);

        api.MapPost("/memories/{id:guid}/autosave", async (Guid id, AutosaveRequest req, HttpContext http,
            AppDbContext db, StoryAccess access, MemoryService memories, AttachmentService attachments, TimeProvider clock) =>
        {
            var memory = await db.Memories.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id);
            if (memory is null || !await access.CanEditAsync(http.User, memory)) return Results.NotFound();

            var w = req.When;
            var when = w is null ? memory.When
                : FuzzyDate.Normalize(w.Year, w.Month, w.Day, w.Circa, w.Decade, w.Label, clock.GetUtcNow().Year);
            var result = await memories.SaveAsync(id, new MemoryEdit(req.Title, req.Body, when, req.Place),
                http.User.UserId(), VersionKind.Autosave, req.Revision);
            if (result.Outcome is SaveOutcome.Saved or SaveOutcome.Unchanged && req.Captions is { Count: > 0 })
            {
                var current = await db.Attachments.AsNoTracking().Where(a => a.MemoryId == id && a.RemovedAt == null)
                    .ToDictionaryAsync(a => a.Id, a => a.Caption);
                foreach (var (attachmentId, caption) in req.Captions)
                    if (current.TryGetValue(attachmentId, out var old) && (old ?? "") != (caption?.Trim() ?? ""))
                        await attachments.SetCaptionAsync(id, attachmentId, caption);
            }
            return result.Outcome switch
            {
                SaveOutcome.Conflict => Results.Conflict(new { revision = result.Revision }),
                SaveOutcome.NotFound => Results.NotFound(),
                _ => Results.Ok(new { revision = result.Revision, savedAt = result.SavedAt, changed = result.Outcome == SaveOutcome.Saved }),
            };
        });

        api.MapPost("/memories/{id:guid}/uploads", async (Guid id, StartUploadRequest req, HttpContext http,
            AppDbContext db, StoryAccess access, AttachmentService attachments) =>
        {
            var memory = await db.Memories.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id);
            if (memory is null || !await access.CanEditAsync(http.User, memory)) return Results.NotFound();
            var status = await attachments.StartAsync(http.User.UserId(), id, req.FileName ?? "file", req.Size, req.Voice);
            return status.Error == UploadError.None
                ? Results.Ok(new { uploadId = status.UploadId, received = 0L, chunkSize = AttachmentService.ChunkBytes })
                : UploadProblem(status);
        });

        api.MapGet("/uploads/{uploadId:guid}", async (Guid uploadId, HttpContext http, AttachmentService attachments) =>
            await attachments.FindAsync(uploadId, http.User.UserId()) is { } s
                ? Results.Ok(new { received = s.ReceivedBytes, total = s.TotalBytes })
                : Results.NotFound());

        api.MapPut("/uploads/{uploadId:guid}", async (Guid uploadId, long offset, HttpContext http,
            AttachmentService attachments, CancellationToken ct) =>
        {
            var session = await attachments.FindAsync(uploadId, http.User.UserId());
            if (session is null) return Results.NotFound();
            var status = await attachments.AppendAsync(session, offset, http.Request.Body, http.Request.ContentLength, ct);
            return status.Error == UploadError.None
                ? Results.Ok(new { received = status.Received, total = status.Total })
                : UploadProblem(status);
        });

        api.MapPost("/uploads/{uploadId:guid}/complete", async (Guid uploadId, HttpContext http,
            AttachmentService attachments, AppDbContext db) =>
        {
            var session = await attachments.FindAsync(uploadId, http.User.UserId());
            if (session is null) return Results.NotFound();
            var status = await attachments.CompleteAsync(session, http.User.UserId());
            if (status.Error != UploadError.None) return UploadProblem(status);
            var a = await db.Attachments.AsNoTracking().SingleAsync(x => x.Id == status.Attachment!.Id);
            return Results.Ok(AttachmentJson(a));
        });

        api.MapGet("/attachments/{id:guid}", async (Guid id, HttpContext http, AppDbContext db, StoryAccess access) =>
        {
            var a = await db.Attachments.AsNoTracking().Include(x => x.Memory).SingleOrDefaultAsync(x => x.Id == id && x.RemovedAt == null);
            if (a is null || !await access.CanViewAsync(http.User, a.Memory!)) return Results.NotFound();
            return Results.Ok(AttachmentJson(a));
        });

        api.MapPost("/memories/{id:guid}/attachments/order", async (Guid id, OrderRequest req, HttpContext http,
            AppDbContext db, StoryAccess access, AttachmentService attachments) =>
        {
            var memory = await db.Memories.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id);
            if (memory is null || !await access.CanEditAsync(http.User, memory)) return Results.NotFound();
            await attachments.ReorderAsync(id, req.Ids);
            return Results.NoContent();
        });
    }

    public static object AttachmentJson(Attachment a) => new
    {
        id = a.Id,
        kind = a.Kind.ToString().ToLowerInvariant(),
        state = a.State.ToString().ToLowerInvariant(),
        fileName = a.OriginalFileName,
        caption = a.Caption,
        voice = a.IsVoiceRecording,
        durationSeconds = a.DurationSeconds,
        thumbUrl = $"/media/{a.Id}/thumb",
        webUrl = $"/media/{a.Id}/web",
        audioUrl = $"/media/{a.Id}/audio",
        originalUrl = $"/media/{a.Id}/original",
    };

    private static IResult UploadProblem(UploadStatus s) => s.Error switch
    {
        UploadError.WrongOffset => Results.Conflict(new { received = s.Received, total = s.Total }),
        UploadError.TooLarge => Results.Json(new { error = "too-large" }, statusCode: StatusCodes.Status413PayloadTooLarge),
        UploadError.Unsupported => Results.Json(new { error = "unsupported" }, statusCode: StatusCodes.Status415UnsupportedMediaType),
        UploadError.Incomplete => Results.BadRequest(new { error = "incomplete", received = s.Received }),
        _ => Results.NotFound(),
    };

    private static async ValueTask<object?> ValidateAntiforgery(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        if (!HttpMethods.IsGet(http.Request.Method))
        {
            var antiforgery = http.RequestServices.GetRequiredService<IAntiforgery>();
            if (!await antiforgery.IsRequestValidAsync(http)) return Results.BadRequest(new { error = "antiforgery" });
        }
        return await next(ctx);
    }
}

public static class MediaEndpoints
{
    /// <summary>Serves originals and derivatives, only to people allowed to see the story. Supports range requests (Safari audio/video).</summary>
    public static void MapMediaEndpoints(this IEndpointRouteBuilder app) =>
        app.MapGet("/media/{id:guid}/{variant}", async (Guid id, string variant, HttpContext http,
            AppDbContext db, StoryAccess access, MediaStorage storage) =>
        {
            if (!Enum.TryParse<MediaVariant>(variant, ignoreCase: true, out var v)) return Results.NotFound();
            var a = await db.Attachments.AsNoTracking().Include(x => x.Memory).SingleOrDefaultAsync(x => x.Id == id);
            if (a is null || !await access.CanViewAsync(http.User, a.Memory!)) return Results.NotFound();

            var path = storage.PathFor(a, v);
            if (!File.Exists(path)) return Results.NotFound();

            http.Response.Headers.CacheControl = "private, max-age=86400";
            return v == MediaVariant.Original
                ? Results.File(path, MediaStorage.ContentTypeFor(a, v), a.OriginalFileName, enableRangeProcessing: true)
                : Results.File(path, MediaStorage.ContentTypeFor(a, v), enableRangeProcessing: true);
        }).RequireAuthorization();
}
