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
        await EnsureCallLogsTableAsync(db);

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

    private static async Task EnsureCallLogsTableAsync(AppDbContext db)
    {
        if (db.Database.IsSqlite())
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS "CallLogs" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_CallLogs" PRIMARY KEY,
                    "InvitationId" TEXT NOT NULL,
                    "CallerId" TEXT NOT NULL,
                    "RecipientId" TEXT NOT NULL,
                    "RoomName" TEXT NOT NULL,
                    "RoomUrl" TEXT NOT NULL,
                    "Status" TEXT NOT NULL,
                    "CreatedAtUtc" TEXT NOT NULL,
                    "AnsweredAtUtc" TEXT NULL,
                    "EndedAtUtc" TEXT NULL,
                    "DurationSeconds" INTEGER NULL,
                    CONSTRAINT "FK_CallLogs_Caller" FOREIGN KEY ("CallerId")
                        REFERENCES "Users" ("Id") ON DELETE RESTRICT,
                    CONSTRAINT "FK_CallLogs_Recipient" FOREIGN KEY ("RecipientId")
                        REFERENCES "Users" ("Id") ON DELETE RESTRICT
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_CallLogs_InvitationId"
                    ON "CallLogs" ("InvitationId");
                CREATE INDEX IF NOT EXISTS "IX_CallLogs_CallerId"
                    ON "CallLogs" ("CallerId");
                CREATE INDEX IF NOT EXISTS "IX_CallLogs_RecipientId"
                    ON "CallLogs" ("RecipientId");
                CREATE INDEX IF NOT EXISTS "IX_CallLogs_CreatedAtUtc"
                    ON "CallLogs" ("CreatedAtUtc");
                """);
            await EnsureSqliteCallLogDurationColumnAsync(db);
            return;
        }

        if (db.Database.IsSqlServer())
        {
            await db.Database.ExecuteSqlRawAsync("""
                IF OBJECT_ID(N'[CallLogs]', N'U') IS NULL
                BEGIN
                    CREATE TABLE [CallLogs] (
                        [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_CallLogs] PRIMARY KEY,
                        [InvitationId] uniqueidentifier NOT NULL,
                        [CallerId] uniqueidentifier NOT NULL,
                        [RecipientId] uniqueidentifier NOT NULL,
                        [RoomName] nvarchar(200) NOT NULL,
                        [RoomUrl] nvarchar(2048) NOT NULL,
                        [Status] nvarchar(32) NOT NULL,
                        [CreatedAtUtc] datetime2 NOT NULL,
                        [AnsweredAtUtc] datetime2 NULL,
                        [EndedAtUtc] datetime2 NULL,
                        [DurationSeconds] int NULL,
                        CONSTRAINT [FK_CallLogs_Caller]
                            FOREIGN KEY ([CallerId]) REFERENCES [Users] ([Id]) ON DELETE NO ACTION,
                        CONSTRAINT [FK_CallLogs_Recipient]
                            FOREIGN KEY ([RecipientId]) REFERENCES [Users] ([Id]) ON DELETE NO ACTION
                    );
                    CREATE UNIQUE INDEX [IX_CallLogs_InvitationId]
                        ON [CallLogs] ([InvitationId]);
                    CREATE INDEX [IX_CallLogs_CallerId]
                        ON [CallLogs] ([CallerId]);
                    CREATE INDEX [IX_CallLogs_RecipientId]
                        ON [CallLogs] ([RecipientId]);
                    CREATE INDEX [IX_CallLogs_CreatedAtUtc]
                        ON [CallLogs] ([CreatedAtUtc]);
                END
                """);

            await db.Database.ExecuteSqlRawAsync("""
                IF COL_LENGTH(N'CallLogs', N'DurationSeconds') IS NULL
                BEGIN
                    ALTER TABLE [CallLogs] ADD [DurationSeconds] int NULL;
                END
                """);
        }
    }

    private static async Task EnsureSqliteCallLogDurationColumnAsync(AppDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen)
        {
            await connection.OpenAsync();
        }

        try
        {
            await using var checkCommand = connection.CreateCommand();
            checkCommand.CommandText = "SELECT COUNT(*) FROM pragma_table_info('CallLogs') WHERE name = 'DurationSeconds';";
            var columnExists = Convert.ToInt32(await checkCommand.ExecuteScalarAsync()) > 0;
            if (columnExists)
            {
                return;
            }

            await using var alterCommand = connection.CreateCommand();
            alterCommand.CommandText = "ALTER TABLE \"CallLogs\" ADD COLUMN \"DurationSeconds\" INTEGER NULL;";
            await alterCommand.ExecuteNonQueryAsync();
        }
        finally
        {
            if (!wasOpen)
            {
                await connection.CloseAsync();
            }
        }
    }
}

public static class UserNameNormalizer
{
    public static string Normalize(string userName) => userName.Trim().ToUpperInvariant();
}
