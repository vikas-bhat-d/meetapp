using System.Security.Claims;
using livekitmeet.Data;
using Microsoft.EntityFrameworkCore;

namespace livekitmeet.Services;

public static class UserPresenceStatuses
{
    public const string Reachable = "Reachable";
    public const string InCall = "In call";
    public const string DoNotDisturb = "Do Not Disturb";
    public const string Unavailable = "Not reachable";
}

public sealed record UserPresenceSnapshot(
    Guid UserId,
    string Status,
    bool IsDoNotDisturb,
    bool IsInCall,
    bool IsReachable);

public interface IUserPresenceService
{
    Task<UserPresenceSnapshot?> GetAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, UserPresenceSnapshot>> GetManyAsync(
        IEnumerable<Guid> userIds,
        CancellationToken cancellationToken = default);

    Task<bool> SetDoNotDisturbAsync(
        ClaimsPrincipal user,
        bool enabled,
        CancellationToken cancellationToken = default);
}

public sealed class UserPresenceService : IUserPresenceService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly CallInvitationConnectionTracker _tracker;

    public UserPresenceService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        CallInvitationConnectionTracker tracker)
    {
        _dbContextFactory = dbContextFactory;
        _tracker = tracker;
    }

    public async Task<UserPresenceSnapshot?> GetAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var statuses = await GetManyAsync(new[] { userId }, cancellationToken);
        return statuses.TryGetValue(userId, out var status) ? status : null;
    }

    public async Task<IReadOnlyDictionary<Guid, UserPresenceSnapshot>> GetManyAsync(
        IEnumerable<Guid> userIds,
        CancellationToken cancellationToken = default)
    {
        var requestedUserIds = userIds.Distinct().ToArray();
        if (requestedUserIds.Length == 0)
        {
            return new Dictionary<Guid, UserPresenceSnapshot>();
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var users = await db.Users
            .AsNoTracking()
            .Where(user => requestedUserIds.Contains(user.Id))
            .Select(user => new
            {
                user.Id,
                user.IsDoNotDisturb,
                IsInCall = db.CallParticipantSessions.Any(session =>
                    session.UserId == user.Id &&
                    session.LeftAtUtc == null &&
                    session.CallRoomLog.EndedAtUtc == null),
                HasFcmDevice = db.PushDevices.Any(device =>
                    device.UserId == user.Id &&
                    device.Platform == "android" &&
                    device.IsActive)
            })
            .ToListAsync(cancellationToken);

        var snapshots = new Dictionary<Guid, UserPresenceSnapshot>(users.Count);
        foreach (var user in users)
        {
            var isReachable = _tracker.IsConnected(user.Id) || user.HasFcmDevice;
            var status = GetStatus(user.IsDoNotDisturb, user.IsInCall, isReachable);
            snapshots[user.Id] = new UserPresenceSnapshot(
                user.Id,
                status,
                user.IsDoNotDisturb,
                user.IsInCall,
                isReachable);
        }

        return snapshots;
    }

    public async Task<bool> SetDoNotDisturbAsync(
        ClaimsPrincipal user,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var userIdValue = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdValue, out var userId))
        {
            return false;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var updated = await db.Users
            .Where(candidate => candidate.Id == userId && candidate.IsActive)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(candidate => candidate.IsDoNotDisturb, enabled),
                cancellationToken);
        return updated > 0;
    }

    private static string GetStatus(bool isDoNotDisturb, bool isInCall, bool isReachable) =>
        isInCall
            ? UserPresenceStatuses.InCall
            : isDoNotDisturb
                ? UserPresenceStatuses.DoNotDisturb
                : isReachable
                    ? UserPresenceStatuses.Reachable
                    : UserPresenceStatuses.Unavailable;
}