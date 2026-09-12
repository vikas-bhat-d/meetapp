using Microsoft.EntityFrameworkCore;

namespace livekitmeet.Data;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.HasKey(user => user.Id);
            entity.Property(user => user.UserName).HasMaxLength(100).IsRequired();
            entity.Property(user => user.NormalizedUserName).HasMaxLength(100).IsRequired();
            entity.Property(user => user.DisplayName).HasMaxLength(200).IsRequired();
            entity.Property(user => user.PasswordHash).HasMaxLength(500).IsRequired();
            entity.HasIndex(user => user.NormalizedUserName).IsUnique();
        });

        modelBuilder.Entity<AuthSession>(entity =>
        {
            entity.HasKey(session => session.Id);
            entity.Property(session => session.AccessTokenHash).HasMaxLength(64).IsRequired();
            entity.Property(session => session.RefreshTokenHash).HasMaxLength(64).IsRequired();
            entity.HasIndex(session => session.AccessTokenHash).IsUnique();
            entity.HasIndex(session => session.RefreshTokenHash).IsUnique();
            entity.HasOne(session => session.User)
                .WithMany(user => user.AuthSessions)
                .HasForeignKey(session => session.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
