using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, AppRole, Guid>(options)
{
    public DbSet<MagicLinkToken> MagicLinkTokens => Set<MagicLinkToken>();
    public DbSet<UserDevice> UserDevices => Set<UserDevice>();
    public DbSet<Chapter> Chapters => Set<Chapter>();
    public DbSet<Prompt> Prompts => Set<Prompt>();
    public DbSet<PromptSet> PromptSets => Set<PromptSet>();
    public DbSet<PromptSetItem> PromptSetItems => Set<PromptSetItem>();
    public DbSet<StorytellerHelper> StorytellerHelpers => Set<StorytellerHelper>();
    public DbSet<Memory> Memories => Set<Memory>();
    public DbSet<MemoryVersion> MemoryVersions => Set<MemoryVersion>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<UploadSession> UploadSessions => Set<UploadSession>();
    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();
    public DbSet<BackupRun> BackupRuns => Set<BackupRun>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();
    public DbSet<Person> People => Set<Person>();
    public DbSet<ParentChild> ParentChildren => Set<ParentChild>();
    public DbSet<Partnership> Partnerships => Set<Partnership>();
    public DbSet<PersonFact> PersonFacts => Set<PersonFact>();
    public DbSet<MemorySource> MemorySources => Set<MemorySource>();
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<ArchiveItem> ArchiveItems => Set<ArchiveItem>();
    public DbSet<StoryDocument> StoryDocuments => Set<StoryDocument>();
    public DbSet<PersonLink> PersonLinks => Set<PersonLink>();
    public DbSet<MemoryPerson> MemoryPeople => Set<MemoryPerson>();
    public DbSet<AttachmentPerson> AttachmentPeople => Set<AttachmentPerson>();
    public DbSet<MemoryShare> MemoryShares => Set<MemoryShare>();
    public DbSet<LinkShare> LinkShares => Set<LinkShare>();
    public DbSet<MemoryReaction> Reactions => Set<MemoryReaction>();
    public DbSet<MemoryComment> Comments => Set<MemoryComment>();
    public DbSet<MemoryRead> Reads => Set<MemoryRead>();
    public DbSet<EmailLog> EmailLog => Set<EmailLog>();
    public DbSet<ReminderSettings> ReminderSettings => Set<ReminderSettings>();
    public DbSet<EmailTemplateOverride> EmailTemplates => Set<EmailTemplateOverride>();
    public DbSet<ExportJob> Exports => Set<ExportJob>();
    public DbSet<ReplyCode> ReplyCodes => Set<ReplyCode>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<AppUser>(e =>
        {
            e.Property(u => u.DisplayName).HasMaxLength(200).IsRequired();
            e.HasOne(u => u.PromptSet).WithMany().HasForeignKey(u => u.PromptSetId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<MagicLinkToken>(e =>
        {
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasIndex(t => new { t.UserId, t.CreatedAt });
            e.Property(t => t.TokenHash).HasMaxLength(32);
            e.Property(t => t.ReturnPath).HasMaxLength(2000);
            e.Property(t => t.CreatedByIp).HasMaxLength(64);
            e.Property(t => t.ConsumedByIp).HasMaxLength(64);
            e.HasOne(t => t.User).WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<UserDevice>(e =>
        {
            e.HasIndex(d => d.UserId);
            e.Property(d => d.UserAgent).HasMaxLength(500);
            e.Property(d => d.Label).HasMaxLength(200);
            e.HasOne(d => d.User).WithMany().HasForeignKey(d => d.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Chapter>(e =>
        {
            e.HasIndex(c => c.Key).IsUnique();
            e.Property(c => c.Key).HasMaxLength(100);
            e.Property(c => c.Title).HasMaxLength(200);
        });

        b.Entity<Prompt>(e =>
        {
            e.HasIndex(p => p.Key).IsUnique();
            e.HasIndex(p => p.ForUserId);
            e.Property(p => p.Key).HasMaxLength(100);
            e.Property(p => p.Title).HasMaxLength(300);
            e.Property(p => p.Question).HasMaxLength(2000);
            e.Property(p => p.SuggestedAttachments).HasMaxLength(500);
            e.HasOne(p => p.Chapter).WithMany(c => c.Prompts).HasForeignKey(p => p.ChapterId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(p => p.ForUser).WithMany().HasForeignKey(p => p.ForUserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(p => p.AskedByUser).WithMany().HasForeignKey(p => p.AskedByUserId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<PromptSet>(e => e.Property(s => s.Name).HasMaxLength(200));
        b.Entity<PromptSetItem>(e =>
        {
            e.HasKey(i => new { i.PromptSetId, i.PromptId });
            e.HasOne<PromptSet>().WithMany(s => s.Items).HasForeignKey(i => i.PromptSetId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(i => i.Prompt).WithMany().HasForeignKey(i => i.PromptId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<StorytellerHelper>(e =>
        {
            e.HasKey(h => new { h.StorytellerId, h.HelperId });
            e.HasOne(h => h.Storyteller).WithMany().HasForeignKey(h => h.StorytellerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(h => h.Helper).WithMany().HasForeignKey(h => h.HelperId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Memory>(e =>
        {
            e.Ignore(m => m.When);
            // One story per storyteller per prompt.
            e.HasIndex(m => new { m.StorytellerId, m.PromptId }).IsUnique().HasFilter("\"PromptId\" IS NOT NULL");
            e.Property(m => m.Title).HasMaxLength(300);
            e.Property(m => m.Body).HasMaxLength(200_000);
            e.Property(m => m.WhenLabel).HasMaxLength(200);
            e.Property(m => m.Place).HasMaxLength(300);
            e.Property(m => m.Revision).IsConcurrencyToken();
            e.HasOne(m => m.Storyteller).WithMany().HasForeignKey(m => m.StorytellerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(m => m.WrittenBy).WithMany().HasForeignKey(m => m.WrittenById).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(m => m.AboutPerson).WithMany().HasForeignKey(m => m.AboutPersonId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(m => m.Prompt).WithMany().HasForeignKey(m => m.PromptId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<MemoryVersion>(e =>
        {
            e.HasIndex(v => new { v.MemoryId, v.Number }).IsUnique();
            e.Property(v => v.Title).HasMaxLength(300);
            e.Property(v => v.Body).HasMaxLength(200_000);
            e.Property(v => v.WhenLabel).HasMaxLength(200);
            e.Property(v => v.Place).HasMaxLength(300);
            e.HasOne(v => v.Memory).WithMany(m => m.Versions).HasForeignKey(v => v.MemoryId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(v => v.SavedBy).WithMany().HasForeignKey(v => v.SavedById).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Attachment>(e =>
        {
            e.HasIndex(a => new { a.MemoryId, a.SortOrder });
            e.Property(a => a.OriginalFileName).HasMaxLength(300);
            e.Property(a => a.ContentType).HasMaxLength(100);
            e.Property(a => a.Extension).HasMaxLength(10);
            e.Property(a => a.Sha256).HasMaxLength(64);
            e.Property(a => a.Caption).HasMaxLength(1000);
            e.Property(a => a.ProcessingError).HasMaxLength(2000);
            e.HasOne(a => a.Memory).WithMany(m => m.Attachments).HasForeignKey(a => a.MemoryId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(a => a.ArchiveItem).WithMany(i => i.Files).HasForeignKey(a => a.ArchiveItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(a => new { a.ArchiveItemId, a.SortOrder });
            e.Property(a => a.Keywords).HasColumnType("text[]").HasDefaultValueSql("'{}'::text[]");
            // A file belongs to a story or to an archive item, never both and never neither (D-080).
            e.ToTable(t => t.HasCheckConstraint("CK_Attachments_OneOwner", "(\"MemoryId\" IS NULL) <> (\"ArchiveItemId\" IS NULL)"));
        });

        b.Entity<Collection>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(4000);
            e.HasIndex(x => new { x.ParentId, x.SortOrder });
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<ArchiveItem>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Description).HasMaxLength(8000);
            e.Property(x => x.Place).HasMaxLength(300);
            e.Property(x => x.WhenLabel).HasMaxLength(200);
            e.Property(x => x.Transcription).HasMaxLength(200_000);
            e.Property(x => x.Provenance).HasMaxLength(1000);
            e.Ignore(x => x.When);
            e.HasIndex(x => x.CollectionId);
            e.HasOne(x => x.Collection).WithMany().HasForeignKey(x => x.CollectionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<StoryDocument>(e =>
        {
            e.HasKey(x => new { x.MemoryId, x.ArchiveItemId });
            e.HasOne(x => x.Memory).WithMany().HasForeignKey(x => x.MemoryId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ArchiveItem).WithMany().HasForeignKey(x => x.ArchiveItemId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<UploadSession>(e =>
        {
            e.HasIndex(u => u.UserId);
            e.Property(u => u.FileName).HasMaxLength(300);
        });

        b.Entity<MemorySource>(e =>
        {
            e.Property(x => x.Text).HasMaxLength(200_000);
            e.HasIndex(x => new { x.MemoryId, x.CreatedAt });
            e.HasOne(x => x.Memory).WithMany().HasForeignKey(x => x.MemoryId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<PersonFact>(e =>
        {
            e.Property(f => f.Kind).HasMaxLength(20);
            e.Property(f => f.Label).HasMaxLength(120);
            e.Property(f => f.Place).HasMaxLength(300);
            e.Property(f => f.Value).HasMaxLength(1000);
            e.Property(f => f.Note).HasMaxLength(8000);
            e.Ignore(f => f.Date);
            e.Property(f => f.Citations).HasColumnType("jsonb").HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => System.Text.Json.JsonSerializer.Deserialize<List<FactCitation>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new List<FactCitation>(),
                new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<List<FactCitation>>(
                    (a, b) => a!.SequenceEqual(b!), v => v.Aggregate(0, (h, x) => HashCode.Combine(h, x.GetHashCode())), v => v.ToList()));
            e.HasIndex(f => new { f.PersonId, f.SortOrder });
            e.HasOne(f => f.Person).WithMany().HasForeignKey(f => f.PersonId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<PersonLink>(e =>
        {
            e.Property(l => l.Label).HasMaxLength(120);
            e.Property(l => l.Url).HasMaxLength(1000);
            e.HasIndex(l => l.PersonId);
            e.HasOne(l => l.Person).WithMany().HasForeignKey(l => l.PersonId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Person>(e =>
        {
            e.Property(p => p.GivenNames).HasMaxLength(200);
            e.Property(p => p.Surname).HasMaxLength(200);
            e.Property(p => p.BirthSurname).HasMaxLength(200);
            e.Property(p => p.Nickname).HasMaxLength(100);
            e.Property(p => p.BirthPlace).HasMaxLength(300);
            e.Property(p => p.DeathPlace).HasMaxLength(300);
            e.Property(p => p.Bio).HasMaxLength(4000);
            e.Property(p => p.GedcomId).HasMaxLength(50);
            e.Ignore(p => p.Birth);
            e.Ignore(p => p.Death);
            e.HasIndex(p => p.LinkedUserId).IsUnique().HasFilter("\"LinkedUserId\" IS NOT NULL");
            e.HasOne(p => p.LinkedUser).WithMany().HasForeignKey(p => p.LinkedUserId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(p => p.PhotoAttachment).WithMany().HasForeignKey(p => p.PhotoAttachmentId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<ParentChild>(e =>
        {
            e.HasKey(x => new { x.ParentId, x.ChildId });
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Child).WithMany().HasForeignKey(x => x.ChildId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Partnership>(e =>
        {
            e.HasOne(x => x.Person1).WithMany().HasForeignKey(x => x.Person1Id).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Person2).WithMany().HasForeignKey(x => x.Person2Id).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.Person1Id, x.Person2Id }).IsUnique();
        });
        b.Entity<MemoryPerson>(e =>
        {
            e.HasKey(x => new { x.MemoryId, x.PersonId });
            e.HasOne(x => x.Memory).WithMany().HasForeignKey(x => x.MemoryId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Person).WithMany().HasForeignKey(x => x.PersonId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<AttachmentPerson>(e =>
        {
            e.HasKey(x => new { x.AttachmentId, x.PersonId });
            e.HasOne(x => x.Attachment).WithMany().HasForeignKey(x => x.AttachmentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Person).WithMany().HasForeignKey(x => x.PersonId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<MemoryShare>(e =>
        {
            e.HasKey(x => new { x.MemoryId, x.UserId });
            e.HasOne<Memory>().WithMany().HasForeignKey(x => x.MemoryId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<LinkShare>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.Property(x => x.TokenHash).HasMaxLength(32);
            e.Property(x => x.PasscodeHash).HasMaxLength(200);
            e.Property(x => x.Label).HasMaxLength(200);
            e.HasOne(x => x.Memory).WithMany().HasForeignKey(x => x.MemoryId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<MemoryReaction>(e =>
        {
            e.HasKey(x => new { x.MemoryId, x.UserId });
            e.HasOne<Memory>().WithMany().HasForeignKey(x => x.MemoryId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<MemoryComment>(e =>
        {
            e.HasIndex(x => new { x.MemoryId, x.CreatedAt });
            e.Property(x => x.Body).HasMaxLength(2000);
            e.HasOne<Memory>().WithMany().HasForeignKey(x => x.MemoryId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<MemoryRead>(e =>
        {
            e.HasKey(x => new { x.MemoryId, x.UserId });
            e.HasOne<Memory>().WithMany().HasForeignKey(x => x.MemoryId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ReplyCode>(e =>
        {
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(16);
            e.HasIndex(x => new { x.UserId, x.PromptId });
        });
        b.Entity<Attachment>().Property(a => a.Transcript).HasMaxLength(100_000);

        b.Entity<ExportJob>(e =>
        {
            e.HasIndex(x => x.CreatedAt);
            e.Property(x => x.Message).HasMaxLength(4000);
            e.Property(x => x.FileName).HasMaxLength(300);
        });

        b.Entity<EmailLog>(e =>
        {
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => new { x.UserId, x.Kind });
            e.Property(x => x.ToAddress).HasMaxLength(320);
            e.Property(x => x.ToName).HasMaxLength(200);
            e.Property(x => x.Subject).HasMaxLength(500);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.Property(x => x.UnsubscribeUrl).HasMaxLength(2000);
            e.Property(x => x.ReplyTo).HasMaxLength(320);
        });
        b.Entity<ReminderSettings>(e =>
        {
            e.HasKey(x => x.UserId);
            e.Property(x => x.TimeZoneId).HasMaxLength(100);
            e.HasOne(x => x.User).WithOne().HasForeignKey<ReminderSettings>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<EmailTemplateOverride>(e =>
        {
            e.HasKey(x => x.Kind);
            e.Property(x => x.Subject).HasMaxLength(500);
        });

        b.Entity<BackupRun>(e =>
        {
            e.HasIndex(r => new { r.Kind, r.StartedAt });
            e.Property(r => r.SnapshotId).HasMaxLength(100);
            e.Property(r => r.OffsiteSnapshotId).HasMaxLength(100);
            e.Property(r => r.Message).HasMaxLength(4000);
        });
        b.Entity<AppSetting>(e =>
        {
            e.HasKey(s => s.Key);
            e.Property(s => s.Key).HasMaxLength(100);
        });

        b.Entity<AuditEntry>(e =>
        {
            e.HasIndex(a => a.At);
            e.Property(a => a.ActorName).HasMaxLength(200);
            e.Property(a => a.Action).HasMaxLength(100);
            e.Property(a => a.TargetType).HasMaxLength(50);
            e.Property(a => a.TargetId).HasMaxLength(100);
            e.Property(a => a.Summary).HasMaxLength(1000);
            e.Property(a => a.Ip).HasMaxLength(64);
        });
    }
}
