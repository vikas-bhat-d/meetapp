using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace livekitmeet.Data;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, IConfiguration configuration)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();
        await EnsurePushDevicesTableAsync(db);

        var adminUserName = configuration["Admin:UserName"]?.Trim();
        var adminPassword = configuration["Admin:Password"];
        if (string.IsNullOrWhiteSpace(adminUserName) || string.IsNullOrWhiteSpace(adminPassword))
        {
            throw new InvalidOperationException(
                "Admin:UserName and Admin:Password must be configured so the initial administrator can be created.");
        }

        var normalizedUserName = UserNameNormalizer.Normalize(adminUserName);
        var admin = await db.Users.SingleOrDefaultAsync(user => user.NormalizedUserName == normalizedUserName);
        if (admin is null)
        {
            var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AppUser>>();
            admin = new AppUser
            {
                UserName = adminUserName,
                NormalizedUserName = normalizedUserName,
                DisplayName = configuration["Admin:DisplayName"]?.Trim() ?? adminUserName,
                IsAdmin = true,
                IsActive = true
            };
            admin.PasswordHash = passwordHasher.HashPassword(admin, adminPassword);
            db.Users.Add(admin);
            await db.SaveChangesAsync();
        }
        else if (!admin.IsAdmin)
        {
            admin.IsAdmin = true;
            admin.IsActive = true;
            await db.SaveChangesAsync();
        }
    }

    private static async Task EnsurePushDevicesTableAsync(AppDbContext db)
    {
        if (db.Database.IsSqlite())
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS "PushDevices" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_PushDevices" PRIMARY KEY,
                    "UserId" TEXT NOT NULL,
                    "Platform" TEXT NOT NULL,
                    "PushToken" TEXT NOT NULL,
                    "CreatedAtUtc" TEXT NOT NULL,
                    "LastSeenAtUtc" TEXT NOT NULL,
                    "IsActive" INTEGER NOT NULL,
                    CONSTRAINT "FK_PushDevices_Users_UserId" FOREIGN KEY ("UserId")
                        REFERENCES "Users" ("Id") ON DELETE CASCADE
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_PushDevices_PushToken"
                    ON "PushDevices" ("PushToken");
                CREATE INDEX IF NOT EXISTS "IX_PushDevices_UserId"
                    ON "PushDevices" ("UserId");
                """);
            return;
        }

        if (db.Database.IsSqlServer())
        {
            await db.Database.ExecuteSqlRawAsync("""
                IF OBJECT_ID(N'[PushDevices]', N'U') IS NULL
                BEGIN
                    CREATE TABLE [PushDevices] (
                        [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_PushDevices] PRIMARY KEY,
                        [UserId] uniqueidentifier NOT NULL,
                        [Platform] nvarchar(32) NOT NULL,
                        [PushToken] nvarchar(4096) NOT NULL,
                        [CreatedAtUtc] datetime2 NOT NULL,
                        [LastSeenAtUtc] datetime2 NOT NULL,
                        [IsActive] bit NOT NULL,
                        CONSTRAINT [FK_PushDevices_Users_UserId]
                            FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
                    );
                    CREATE UNIQUE INDEX [IX_PushDevices_PushToken]
                        ON [PushDevices] ([PushToken]);
                    CREATE INDEX [IX_PushDevices_UserId]
                        ON [PushDevices] ([UserId]);
                END
                """);
        }
    }
}

public static class UserNameNormalizer
{
    public static string Normalize(string userName) => userName.Trim().ToUpperInvariant();
}
