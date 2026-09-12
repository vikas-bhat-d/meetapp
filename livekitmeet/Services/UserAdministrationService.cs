using System.Security.Claims;
using livekitmeet.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace livekitmeet.Services;

public sealed record UserListItem(
    Guid Id,
    string UserName,
    string DisplayName,
    bool IsAdmin,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime? LastLoginAtUtc);

public interface IUserAdministrationService
{
    Task<IReadOnlyList<UserListItem>> GetUsersAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> CreateUserAsync(
        ClaimsPrincipal actor,
        string userName,
        string displayName,
        string password,
        bool isAdmin,
        CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> SetActiveAsync(
        ClaimsPrincipal actor,
        Guid userId,
        bool isActive,
        CancellationToken cancellationToken = default);
}

public sealed class UserAdministrationService : IUserAdministrationService
{
    private readonly AppDbContext _db;
    private readonly IPasswordHasher<AppUser> _passwordHasher;

    public UserAdministrationService(AppDbContext db, IPasswordHasher<AppUser> passwordHasher)
    {
        _db = db;
        _passwordHasher = passwordHasher;
    }

    public async Task<IReadOnlyList<UserListItem>> GetUsersAsync(
        ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        EnsureAdmin(actor);
        return await _db.Users
            .AsNoTracking()
            .OrderBy(user => user.UserName)
            .Select(user => new UserListItem(
                user.Id,
                user.UserName,
                user.DisplayName,
                user.IsAdmin,
                user.IsActive,
                user.CreatedAtUtc,
                user.LastLoginAtUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<(bool Success, string? Error)> CreateUserAsync(
        ClaimsPrincipal actor,
        string userName,
        string displayName,
        string password,
        bool isAdmin,
        CancellationToken cancellationToken = default)
    {
        EnsureAdmin(actor);
        userName = userName.Trim();
        displayName = displayName.Trim();

        if (userName.Length < 3 || userName.Length > 100 ||
            userName.Any(character => !(char.IsLetterOrDigit(character) || character is '.' or '_' or '-')))
        {
            return (false, "Username must be 3-100 characters and contain only letters, numbers, '.', '_' or '-'.");
        }
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return (false, "Display name is required.");
        }
        if (password.Length < 8)
        {
            return (false, "Password must be at least 8 characters.");
        }

        var normalized = UserNameNormalizer.Normalize(userName);
        if (await _db.Users.AnyAsync(user => user.NormalizedUserName == normalized, cancellationToken))
        {
            return (false, "That username is already in use.");
        }

        var user = new AppUser
        {
            UserName = userName,
            NormalizedUserName = normalized,
            DisplayName = displayName,
            IsAdmin = isAdmin,
            IsActive = true
        };
        user.PasswordHash = _passwordHasher.HashPassword(user, password);
        _db.Users.Add(user);
        await _db.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> SetActiveAsync(
        ClaimsPrincipal actor,
        Guid userId,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        EnsureAdmin(actor);
        var actorId = GetUserId(actor);
        if (actorId == userId && !isActive)
        {
            return (false, "You cannot remove your own administrator account.");
        }

        var user = await _db.Users
            .Include(candidate => candidate.AuthSessions)
            .SingleOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken);
        if (user is null)
        {
            return (false, "User not found.");
        }

        if (user.IsAdmin && !isActive)
        {
            var activeAdminCount = await _db.Users.CountAsync(candidate => candidate.IsAdmin && candidate.IsActive, cancellationToken);
            if (activeAdminCount <= 1)
            {
                return (false, "The last active administrator cannot be removed.");
            }
        }

        user.IsActive = isActive;
        if (!isActive)
        {
            foreach (var session in user.AuthSessions)
            {
                session.RevokedAtUtc = DateTime.UtcNow;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    private static void EnsureAdmin(ClaimsPrincipal actor)
    {
        if (actor.Identity?.IsAuthenticated != true || !actor.IsInRole("Admin"))
        {
            throw new UnauthorizedAccessException("Administrator access is required.");
        }
    }

    private static Guid GetUserId(ClaimsPrincipal actor)
    {
        var value = actor.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var userId) ? userId : Guid.Empty;
    }
}
