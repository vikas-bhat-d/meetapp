using Microsoft.EntityFrameworkCore;

namespace livekitmeet.Data;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();
    public DbSet<PushDevice> PushDevices => Set<PushDevice>();
    public DbSet<CallLog> CallLogs => Set<CallLog>();
    public DbSet<CallRoomLog> CallRoomLogs => Set<CallRoomLog>();
    public DbSet<CallParticipantSession> CallParticipantSessions => Set<CallParticipantSession>();

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

        modelBuilder.Entity<PushDevice>(entity =>
        {
            entity.HasKey(device => device.Id);
            entity.Property(device => device.Platform).HasMaxLength(32).IsRequired();
            entity.Property(device => device.PushToken).HasMaxLength(512).IsRequired();
            entity.HasIndex(device => device.PushToken).IsUnique();
            entity.HasIndex(device => device.UserId);
            entity.HasOne(device => device.User)
                .WithMany()
                .HasForeignKey(device => device.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CallLog>(entity =>
        {
            entity.HasKey(log => log.Id);
            entity.Property(log => log.RoomName).HasMaxLength(200).IsRequired();
            entity.Property(log => log.RoomUrl).HasMaxLength(2048).IsRequired();
            entity.Property(log => log.Status).HasMaxLength(32).IsRequired();
            entity.HasIndex(log => log.InvitationId).IsUnique();
            entity.HasIndex(log => log.CallerId);
            entity.HasIndex(log => log.RecipientId);
            entity.HasIndex(log => log.CreatedAtUtc);
            entity.HasOne(log => log.Caller)
                .WithMany()
                .HasForeignKey(log => log.CallerId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(log => log.Recipient)
                .WithMany()
                .HasForeignKey(log => log.RecipientId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CallRoomLog>(entity =>
        {
            entity.HasKey(room => room.Id);
            entity.Property(room => room.RoomName).HasMaxLength(200).IsRequired();
            entity.Property(room => room.RoomUrl).HasMaxLength(2048).IsRequired();
            entity.HasIndex(room => room.RoomName).IsUnique();
        });

        modelBuilder.Entity<CallParticipantSession>(entity =>
        {
            entity.HasKey(session => session.Id);
            entity.HasIndex(session => session.CallRoomLogId);
            entity.HasIndex(session => session.UserId);
            entity.HasIndex(session => session.InvitationId);
            entity.HasOne(session => session.CallRoomLog)
                .WithMany(room => room.Participants)
                .HasForeignKey(session => session.CallRoomLogId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(session => session.User)
                .WithMany()
                .HasForeignKey(session => session.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
