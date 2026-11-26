using System.Security.Claims;
using FamilyMemories.Admin;
using FamilyMemories.Auth;
using FamilyMemories.Data;
using FamilyMemories.Media;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Archive;

public sealed record ArchiveItemInput(string Title, string? Description, FuzzyDate When, string? Place, string? Transcription, string? Provenance, Guid? CollectionId);

public sealed record ArchiveFolderView(Collection? Folder, IReadOnlyList<Collection> Path, IReadOnlyList<Collection> Folders, IReadOnlyList<ArchiveItem> Items);

/// <summary>One keyword in the archive's keyword tree ("Family" → "Conlon" → "William"), with how many files carry it or anything under it.</summary>
public sealed record KeywordNode(string Name, string Path, int Count, IReadOnlyList<KeywordNode> Children);

/// <summary>
/// The family archive (D-080): folders of letters, certificates, album pages and recordings, each with its scans, a
/// transcription, a date and place, and where the original is now. Every signed-in family member can look and add;
/// changing or removing an item is for whoever added it and admins; folders are for admins.
/// </summary>
public sealed class ArchiveService(AppDbContext db, AttachmentService attachments, MediaStorage storage, AuditLog audit, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public static bool CanEdit(ClaimsPrincipal user, ArchiveItem item) =>
        user.IsAdmin() || (item.CreatedById is { } by && by == user.UserId());

    // ------------------------------------------------------------------ reading

    public async Task<ArchiveFolderView?> FolderAsync(Guid? folderId, string? keyword = null)
    {
        Collection? folder = null;
        if (folderId is { } id && (folder = await db.Collections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id)) is null) return null;
        var all = await db.Collections.AsNoTracking().ToListAsync();
        var path = new List<Collection>();
        for (var c = folder; c is not null; c = all.FirstOrDefault(x => x.Id == c.ParentId)) path.Insert(0, c);

        var query = db.ArchiveItems.AsNoTracking().Include(i => i.Files.Where(f => f.RemovedAt == null).OrderBy(f => f.SortOrder)).AsQueryable();
        // A keyword looks through the whole archive (the keyword itself or anything under it); a family's archive is small
        // enough to filter here, which keeps the "A|B|C" matching simple and the same as the keyword tree's.
        if (keyword is not { Length: > 0 }) query = query.Where(i => i.CollectionId == folderId);
        var items = await query.ToListAsync();
        if (keyword is { Length: > 0 })
            items = [.. items.Where(i => i.Files.Any(f => HasKeyword(f, keyword)))];

        return new ArchiveFolderView(folder, path,
            [.. all.Where(c => c.ParentId == folderId).OrderBy(c => c.SortOrder).ThenBy(c => c.Title)],
            [.. items.OrderBy(i => i.WhenYear ?? i.WhenDecade ?? int.MaxValue).ThenBy(i => i.WhenMonth).ThenBy(i => i.Title)]);
    }

    public Task<ArchiveItem?> ItemAsync(Guid id) =>
        db.ArchiveItems.AsNoTracking().Include(i => i.CreatedBy).Include(i => i.Collection)
            .Include(i => i.Files.Where(f => f.RemovedAt == null).OrderBy(f => f.SortOrder))
            .SingleOrDefaultAsync(i => i.Id == id);

    public Task<List<Collection>> AllFoldersAsync() => db.Collections.AsNoTracking().OrderBy(c => c.Title).ToListAsync();

    /// <summary>Every folder as "Letters › From Joe", for a "move to folder" list.</summary>
    public async Task<List<(Guid Id, string Name)>> FolderChoicesAsync()
    {
        var all = await AllFoldersAsync();
        string Name(Collection c) => c.ParentId is { } p && all.FirstOrDefault(x => x.Id == p) is { } parent ? Name(parent) + " › " + c.Title : c.Title;
        return [.. all.Select(c => (c.Id, Name(c))).OrderBy(x => x.Item2, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>The archive's keyword tree.</summary>
    public async Task<IReadOnlyList<KeywordNode>> KeywordTreeAsync()
    {
        var lists = await db.Attachments.AsNoTracking().Where(a => a.ArchiveItemId != null && a.RemovedAt == null && a.Keywords.Count > 0)
            .Select(a => a.Keywords).ToListAsync();
        return BuildKeywordTree(lists);
    }

    /// <summary>A keyword tree from each file's keywords ("A|B|C" paths and flat ones).</summary>
    public static IReadOnlyList<KeywordNode> BuildKeywordTree(IEnumerable<IReadOnlyCollection<string>> lists)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var keywords in lists)
            foreach (var path in keywords.SelectMany(Prefixes).Distinct())
                counts[path] = counts.GetValueOrDefault(path) + 1;

        IReadOnlyList<KeywordNode> Build(string? parent) =>
        [
            .. counts.Keys.Where(k => parent is null ? !k.Contains('|') : k.StartsWith(parent + "|") && !k[(parent.Length + 1)..].Contains('|'))
                .OrderBy(k => k, StringComparer.CurrentCultureIgnoreCase)
                .Select(k => new KeywordNode(k[(k.LastIndexOf('|') + 1)..], k, counts[k], Build(k))),
        ];
        return Build(null);
    }

    /// <summary>A file has the keyword itself or something under it.</summary>
    public static bool HasKeyword(Attachment a, string keyword) =>
        a.Keywords.Any(k => k == keyword || k.StartsWith(keyword + "|", StringComparison.Ordinal));

    private static IEnumerable<string> Prefixes(string keyword)
    {
        var parts = keyword.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i <= parts.Length; i++) yield return string.Join('|', parts[..i]);
    }

    public Task<List<ArchiveItem>> DocumentsForStoryAsync(Guid memoryId) =>
        db.ArchiveItems.AsNoTracking().Where(i => db.StoryDocuments.Any(d => d.MemoryId == memoryId && d.ArchiveItemId == i.Id))
            .Include(i => i.Files.Where(f => f.RemovedAt == null).OrderBy(f => f.SortOrder)).OrderBy(i => i.Title).ToListAsync();

    public Task<List<Memory>> StoriesForItemAsync(Guid itemId) =>
        db.Memories.AsNoTracking().Where(m => db.StoryDocuments.Any(d => d.ArchiveItemId == itemId && d.MemoryId == m.Id))
            .Include(m => m.Storyteller).OrderBy(m => m.Title).ToListAsync();

    // ------------------------------------------------------------------ folders (admins)

    public async Task<Collection> CreateFolderAsync(Guid? parentId, string title, string? description)
    {
        var order = await db.Collections.Where(c => c.ParentId == parentId).MaxAsync(c => (int?)c.SortOrder) ?? 0;
        var folder = new Collection { Id = Guid.CreateVersion7(), ParentId = parentId, Title = Clip(title, 200)!, Description = Clip(description, 4000), SortOrder = order + 1, CreatedAt = Now };
        db.Collections.Add(folder);
        audit.Add("archive.folder-add", $"Added the archive folder \"{folder.Title}\"", "collection", folder.Id);
        await db.SaveChangesAsync();
        return folder;
    }

    public async Task<bool> RenameFolderAsync(Guid id, string title, string? description)
    {
        var folder = await db.Collections.SingleOrDefaultAsync(c => c.Id == id);
        if (folder is null) return false;
        (folder.Title, folder.Description) = (Clip(title, 200)!, Clip(description, 4000));
        audit.Add("archive.folder-edit", $"Renamed an archive folder to \"{folder.Title}\"", "collection", id);
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>Only an empty folder can be removed, so nothing is ever lost by accident.</summary>
    public async Task<string?> DeleteFolderAsync(Guid id)
    {
        if (await db.ArchiveItems.AnyAsync(i => i.CollectionId == id) || await db.Collections.AnyAsync(c => c.ParentId == id))
            return "Only an empty folder can be removed. Move what's in it somewhere else first.";
        await db.Collections.Where(c => c.Id == id).ExecuteDeleteAsync();
        audit.Add("archive.folder-delete", "Removed an empty archive folder", "collection", id);
        await db.SaveChangesAsync();
        return null;
    }

    // ------------------------------------------------------------------ items

    /// <summary>Adds an item with its files; or, with <paramref name="eachFileSeparately"/>, one item per file (a box of photos).</summary>
    public async Task<(List<Guid> Items, List<string> Problems)> CreateAsync(ArchiveItemInput input, IReadOnlyList<(string Name, Stream Content)> files, bool eachFileSeparately, Guid userId)
    {
        var created = new List<Guid>();
        var problems = new List<string>();
        if (eachFileSeparately && files.Count > 0)
        {
            foreach (var (name, content) in files)
            {
                var item = NewItem(input with { Title = Path.GetFileNameWithoutExtension(name) }, userId);
                db.ArchiveItems.Add(item);
                await db.SaveChangesAsync();
                if (await AddFileAsync(item.Id, name, content, userId) is { } problem)
                {
                    problems.Add(problem);
                    db.ArchiveItems.Remove(item);
                    await db.SaveChangesAsync();
                    continue;
                }
                created.Add(item.Id);
            }
        }
        else
        {
            var item = NewItem(input, userId);
            db.ArchiveItems.Add(item);
            await db.SaveChangesAsync();
            foreach (var (name, content) in files)
                if (await AddFileAsync(item.Id, name, content, userId) is { } problem) problems.Add(problem);
            created.Add(item.Id);
        }
        audit.Add("archive.add", $"Added {created.Count} {(created.Count == 1 ? "item" : "items")} to the family archive");
        await db.SaveChangesAsync();
        return (created, problems);
    }

    private ArchiveItem NewItem(ArchiveItemInput input, Guid userId)
    {
        var item = new ArchiveItem { Id = Guid.CreateVersion7(), CreatedById = userId, CreatedAt = Now, UpdatedAt = Now };
        Apply(item, input);
        return item;
    }

    public async Task<bool> UpdateAsync(Guid id, ArchiveItemInput input)
    {
        var item = await db.ArchiveItems.SingleOrDefaultAsync(i => i.Id == id);
        if (item is null) return false;
        Apply(item, input);
        item.UpdatedAt = Now;
        audit.Add("archive.edit", $"Edited \"{item.Title}\" in the family archive", "archive-item", id);
        await db.SaveChangesAsync();
        return true;
    }

    private static void Apply(ArchiveItem item, ArchiveItemInput i)
    {
        item.Title = Clip(i.Title, 300) ?? "Untitled";
        item.Description = Clip(i.Description, 8000);
        item.When = i.When;
        item.Place = Clip(i.Place, 300);
        item.Transcription = Clip(i.Transcription?.Replace("\r\n", "\n"), 200_000);
        item.Provenance = Clip(i.Provenance, 1000);
        item.CollectionId = i.CollectionId;
    }

    /// <summary>Returns a problem in plain words, or null when the file was added.</summary>
    public async Task<string?> AddFileAsync(Guid itemId, string name, Stream content, Guid userId)
    {
        var (_, error) = await attachments.AddToArchiveAsync(itemId, name, content, userId);
        return error switch
        {
            UploadError.None => null,
            UploadError.TooLarge => $"{name} is too big to add.",
            _ => $"{name} isn't a kind of file we can keep (photos, scans, PDFs, recordings and videos are fine).",
        };
    }

    public async Task RemoveFileAsync(Guid itemId, Guid attachmentId)
    {
        await db.Attachments.Where(a => a.Id == attachmentId && a.ArchiveItemId == itemId && a.RemovedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.RemovedAt, Now));
        audit.Add("archive.file-remove", "Removed a file from an archive item", "archive-item", itemId);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Removes an item and its files for good (the people who can do this are asked to confirm). Earlier backups still hold
    /// them, so a restore can bring an item back.
    /// </summary>
    public async Task DeleteAsync(Guid id)
    {
        var item = await db.ArchiveItems.Include(i => i.Files).SingleOrDefaultAsync(i => i.Id == id);
        if (item is null) return;
        var folders = item.Files.Select(storage.FolderFor).ToList();
        db.ArchiveItems.Remove(item);   // its files' rows and story links go with it
        audit.Add("archive.delete", $"Removed \"{item.Title}\" and its {item.Files.Count} files from the family archive", "archive-item", id);
        await db.SaveChangesAsync();
        foreach (var folder in folders)
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); } catch (IOException) { }
    }

    public async Task<string?> LinkStoryAsync(Guid itemId, Guid memoryId)
    {
        if (!await db.Memories.AnyAsync(m => m.Id == memoryId)) return "That story couldn't be found.";
        if (await db.StoryDocuments.AnyAsync(d => d.ArchiveItemId == itemId && d.MemoryId == memoryId)) return null;
        db.StoryDocuments.Add(new StoryDocument { ArchiveItemId = itemId, MemoryId = memoryId });
        audit.Add("archive.link", "Linked an archive item to a story", "archive-item", itemId);
        await db.SaveChangesAsync();
        return null;
    }

    public async Task UnlinkStoryAsync(Guid itemId, Guid memoryId)
    {
        await db.StoryDocuments.Where(d => d.ArchiveItemId == itemId && d.MemoryId == memoryId).ExecuteDeleteAsync();
    }

    private static string? Clip(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length > max ? s[..max] : s;
    }
}
