using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace FamilyMemories.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, AppRole, Guid>(options)
{
    public DbSet<MagicLinkToken> MagicLinkTokens => Set<MagicLinkToken>();
    public DbSet<UserDevice> UserDevices => Set<UserDevice>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<AppUser>(e =>
        {
            e.Property(u => u.DisplayName).HasMaxLength(200).IsRequired();
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
    }
}
