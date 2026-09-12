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
}

public static class UserNameNormalizer
{
    public static string Normalize(string userName) => userName.Trim().ToUpperInvariant();
}
