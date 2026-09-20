using System.Security.Claims;
using livekitmeet.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace livekitmeet.Services;

public sealed record CallLogItem(
    Guid Id,
    Guid InvitationId,
    string ContactDisplayName,
    string ContactUserName,
    bool IsIncoming,
    string RoomName,
    string Status,
    DateTime CreatedAtUtc,
    DateTime? AnsweredAtUtc,
    DateTime? EndedAtUtc,
    int? DurationSeconds);

public sealed record CallParticipantLogItem(
    Guid UserId,
    string UserName,
    string DisplayName,
    DateTime JoinedAtUtc,
    DateTime? LeftAtUtc,
    int DurationSeconds,
    bool IsActive);

public sealed record CallInvitationLogItem(
    Guid InvitationId,
    string CallerUserName,
    string CallerDisplayName,
    string RecipientUserName,
    string RecipientDisplayName,
    string Status,
    DateTime CreatedAtUtc,
    DateTime? AnsweredAtUtc,
    DateTime? EndedAtUtc,
    int? DurationSeconds);

public sealed record AdminCallRoomLogItem(
    Guid Id,
    string RoomName,
    string RoomUrl,
    DateTime CreatedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? EndedAtUtc,
    IReadOnlyList<CallParticipantLogItem> Participants,
    IReadOnlyList<CallInvitationLogItem> Invitations);

public sealed record CallRoomHistoryItem(
    Guid Id,
    string RoomName,
    DateTime CreatedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? EndedAtUtc,
    int? DurationSeconds,
    int ParticipantCount,
    string Status,
    IReadOnlyList<CallParticipantLogItem> Participants,
    IReadOnlyList<CallInvitationLogItem> Invitations);

public interface ICallLogService
{
    Task<CallLog> CreateAsync(
        Guid invitationId,
        Guid callerId,
        Guid recipientId,
        string roomName,
        string roomUrl,
        CancellationToken cancellationToken = default);

    Task<Guid?> JoinParticipantAsync(
        ClaimsPrincipal user,
        string roomName,
        string roomUrl,
        Guid? invitationId,
        CancellationToken cancellationToken = default);

    Task<bool> LeaveParticipantAsync(
        ClaimsPrincipal user,
        Guid participantSessionId,
        CancellationToken cancellationToken = default);

    Task MarkFailedAsync(Guid invitationId, CancellationToken cancellationToken = default);

    Task<bool> MarkAnsweredAsync(
        ClaimsPrincipal user,
        Guid invitationId,
        CancellationToken cancellationToken = default);

    Task<bool> MarkDeclinedAsync(
        ClaimsPrincipal user,
        Guid invitationId,
        CancellationToken cancellationToken = default);

    Task<bool> MarkEndedAsync(
        ClaimsPrincipal user,
        Guid invitationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CallLogItem>> GetRecentAsync(
        ClaimsPrincipal user,
        int limit = 50,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CallRoomHistoryItem>> GetRecentRoomLogsAsync(
        ClaimsPrincipal user,
        int limit = 50,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AdminCallRoomLogItem>> GetAdminRoomLogsAsync(
        ClaimsPrincipal user,
        int limit = 100,
        CancellationToken cancellationToken = default);
}

public sealed class CallLogService : ICallLogService
{
    private readonly AppDbContext _db;
    private readonly IHubContext<CallInvitationHub> _hub;

    public CallLogService(AppDbContext db, IHubContext<CallInvitationHub> hub)
    {
        _db = db;
        _hub = hub;
    }

    public async Task<CallLog> CreateAsync(
        Guid invitationId,
        Guid callerId,
        Guid recipientId,
        string roomName,
        string roomUrl,
        CancellationToken cancellationToken = default)
    {
        await GetOrCreateRoomAsync(roomName, roomUrl, cancellationToken);
        var log = new CallLog
        {
            InvitationId = invitationId,
            CallerId = callerId,
            RecipientId = recipientId,
            RoomName = roomName.Trim(),
            RoomUrl = roomUrl.Trim(),
            Status = CallLogStatuses.Ringing,
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.CallLogs.Add(log);
        await _db.SaveChangesAsync(cancellationToken);
        return log;
    }

    public async Task<Guid?> JoinParticipantAsync(
        ClaimsPrincipal user,
        string roomName,
        string roomUrl,
        Guid? invitationId,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetUserId(user, out var userId) || string.IsNullOrWhiteSpace(roomName))
        {
            return null;
        }

        var room = await GetOrCreateRoomAsync(roomName, roomUrl, cancellationToken);
        var existingSession = room.EndedAtUtc is null
            ? await _db.CallParticipantSessions
                .Where(session => session.CallRoomLogId == room.Id &&
                                  session.UserId == userId &&
                                  session.LeftAtUtc == null)
                .OrderBy(session => session.JoinedAtUtc)
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        if (existingSession is not null)
        {
            room.EndedAtUtc = null;
            await _db.SaveChangesAsync(cancellationToken);
            return existingSession.Id;
        }

        var joinedAtUtc = DateTime.UtcNow;
        room.StartedAtUtc ??= joinedAtUtc;
        room.EndedAtUtc = null;

        var session = new CallParticipantSession
        {
            CallRoomLogId = room.Id,
            UserId = userId,
            InvitationId = invitationId,
            JoinedAtUtc = joinedAtUtc
        };
        _db.CallParticipantSessions.Add(session);
        await _db.SaveChangesAsync(cancellationToken);
        return session.Id;
    }

    public async Task<bool> LeaveParticipantAsync(
        ClaimsPrincipal user,
        Guid participantSessionId,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetUserId(user, out var userId))
        {
            return false;
        }

        var session = await _db.CallParticipantSessions
            .Include(candidate => candidate.CallRoomLog)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == participantSessionId && candidate.UserId == userId,
                cancellationToken);
        if (session is null)
        {
            return false;
        }

        var activeSessions = await _db.CallParticipantSessions
            .Where(candidate => candidate.CallRoomLogId == session.CallRoomLogId &&
                                candidate.UserId == userId &&
                                candidate.LeftAtUtc == null)
            .ToListAsync(cancellationToken);
        if (activeSessions.Count > 0)
        {
            var leftAtUtc = DateTime.UtcNow;
            foreach (var activeSession in activeSessions)
            {
                activeSession.LeftAtUtc = leftAtUtc;
                activeSession.DurationSeconds = CalculateDurationSeconds(activeSession.JoinedAtUtc, leftAtUtc) ?? 0;
            }

            var anotherParticipantIsActive = await _db.CallParticipantSessions
                .AnyAsync(
                    candidate => candidate.CallRoomLogId == session.CallRoomLogId &&
                                candidate.UserId != userId &&
                                candidate.LeftAtUtc == null,
                    cancellationToken);
            session.CallRoomLog.EndedAtUtc = anotherParticipantIsActive ? null : leftAtUtc;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    public async Task MarkFailedAsync(Guid invitationId, CancellationToken cancellationToken = default)
    {
        var log = await _db.CallLogs.SingleOrDefaultAsync(
            candidate => candidate.InvitationId == invitationId,
            cancellationToken);
        if (log is null || log.Status != CallLogStatuses.Ringing)
        {
            return;
        }

        log.Status = CallLogStatuses.Failed;
        log.EndedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> MarkAnsweredAsync(
        ClaimsPrincipal user,
        Guid invitationId,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetUserId(user, out var userId))
        {
            return false;
        }

        var log = await _db.CallLogs.SingleOrDefaultAsync(
            candidate => candidate.InvitationId == invitationId && candidate.RecipientId == userId,
            cancellationToken);
        if (log is null || log.Status != CallLogStatuses.Ringing)
        {
            return false;
        }

        log.Status = CallLogStatuses.Answered;
        log.AnsweredAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> MarkDeclinedAsync(
        ClaimsPrincipal user,
        Guid invitationId,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetUserId(user, out var userId))
        {
            return false;
        }

        var log = await _db.CallLogs.SingleOrDefaultAsync(
            candidate => candidate.InvitationId == invitationId && candidate.RecipientId == userId,
            cancellationToken);
        if (log is null || log.Status != CallLogStatuses.Ringing)
        {
            return false;
        }

        log.Status = CallLogStatuses.Declined;
        log.EndedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        await _hub.Clients
            .Group(CallInvitationHub.UserGroup(log.CallerId))
            .SendAsync("CallDeclined", invitationId, cancellationToken);
        return true;
    }

    public async Task<bool> MarkEndedAsync(
        ClaimsPrincipal user,
        Guid invitationId,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetUserId(user, out var userId))
        {
            return false;
        }

        var log = await _db.CallLogs.SingleOrDefaultAsync(
            candidate => candidate.InvitationId == invitationId &&
                        (candidate.CallerId == userId || candidate.RecipientId == userId),
            cancellationToken);
        if (log is null || log.Status != CallLogStatuses.Answered)
        {
            return false;
        }

        var endedAtUtc = DateTime.UtcNow;
        log.Status = CallLogStatuses.Ended;
        log.EndedAtUtc = endedAtUtc;
        log.DurationSeconds = CalculateDurationSeconds(log.AnsweredAtUtc, endedAtUtc);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<CallLogItem>> GetRecentAsync(
        ClaimsPrincipal user,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetUserId(user, out var userId))
        {
            return Array.Empty<CallLogItem>();
        }

        limit = Math.Clamp(limit, 1, 100);
        return await _db.CallLogs
            .AsNoTracking()
            .Where(log => log.CallerId == userId || log.RecipientId == userId)
            .OrderByDescending(log => log.CreatedAtUtc)
            .Take(limit)
            .Select(log => new CallLogItem(
                log.Id,
                log.InvitationId,
                log.CallerId == userId ? log.Recipient.DisplayName : log.Caller.DisplayName,
                log.CallerId == userId ? log.Recipient.UserName : log.Caller.UserName,
                log.RecipientId == userId,
                log.RoomName,
                log.Status,
                log.CreatedAtUtc,
                log.AnsweredAtUtc,
                log.EndedAtUtc,
                log.DurationSeconds))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CallRoomHistoryItem>> GetRecentRoomLogsAsync(
        ClaimsPrincipal user,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetUserId(user, out var userId))
        {
            return Array.Empty<CallRoomHistoryItem>();
        }

        limit = Math.Clamp(limit, 1, 100);
        var rooms = await _db.CallRoomLogs
            .AsNoTracking()
            .Include(room => room.Participants)
                .ThenInclude(session => session.User)
            .Where(room => room.Participants.Any(session => session.UserId == userId) ||
                           _db.CallLogs.Any(log => log.RoomName == room.RoomName &&
                                                   (log.CallerId == userId || log.RecipientId == userId)))
            .OrderByDescending(room => room.CreatedAtUtc)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var roomNames = rooms
            .Select(room => room.RoomName)
            .ToArray();
        var invitations = roomNames.Length == 0
            ? new List<CallLog>()
            : await _db.CallLogs
                .AsNoTracking()
                .Include(log => log.Caller)
                .Include(log => log.Recipient)
                .Where(log => roomNames.Contains(log.RoomName))
                .OrderByDescending(log => log.CreatedAtUtc)
                .ToListAsync(cancellationToken);

        return rooms
            .Select(room =>
            {
                var roomInvitations = invitations
                    .Where(log => string.Equals(log.RoomName, room.RoomName, StringComparison.Ordinal))
                    .ToList();
                var participantCount = room.Participants
                    .Select(session => session.UserId)
                    .Distinct()
                    .Count();
                var userStatus = GetUserRoomStatus(room, userId, participantCount, roomInvitations);
                var durationSeconds = participantCount >= 2 && room.Participants.Any(session => session.UserId == userId)
                    ? CalculateUserDurationSeconds(room, userId)
                    : null;

                return new CallRoomHistoryItem(
                    room.Id,
                    room.RoomName,
                    room.CreatedAtUtc,
                    room.StartedAtUtc,
                    room.EndedAtUtc,
                    durationSeconds,
                    participantCount,
                    userStatus,
                    BuildParticipantLogs(room),
                    roomInvitations
                        .Select(log => new CallInvitationLogItem(
                            log.InvitationId,
                            log.Caller.UserName,
                            log.Caller.DisplayName,
                            log.Recipient.UserName,
                            log.Recipient.DisplayName,
                            log.Status,
                            log.CreatedAtUtc,
                            log.AnsweredAtUtc,
                            log.EndedAtUtc,
                            log.DurationSeconds))
                        .ToList());
            })
            .ToList();
    }

    public async Task<IReadOnlyList<AdminCallRoomLogItem>> GetAdminRoomLogsAsync(
        ClaimsPrincipal user,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (!user.IsInRole("Admin"))
        {
            return Array.Empty<AdminCallRoomLogItem>();
        }

        limit = Math.Clamp(limit, 1, 250);
        var rooms = await _db.CallRoomLogs
            .AsNoTracking()
            .Include(room => room.Participants)
                .ThenInclude(session => session.User)
            .OrderByDescending(room => room.CreatedAtUtc)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var roomNames = rooms
            .Select(room => room.RoomName)
            .ToArray();
        var invitations = roomNames.Length == 0
            ? new List<CallLog>()
            : await _db.CallLogs
                .AsNoTracking()
                .Include(log => log.Caller)
                .Include(log => log.Recipient)
                .Where(log => roomNames.Contains(log.RoomName))
                .OrderByDescending(log => log.CreatedAtUtc)
                .ToListAsync(cancellationToken);

        return rooms
            .Select(room => new AdminCallRoomLogItem(
                room.Id,
                room.RoomName,
                room.RoomUrl,
                room.CreatedAtUtc,
                room.StartedAtUtc,
                room.EndedAtUtc,
                BuildParticipantLogs(room),
                invitations
                    .Where(log => string.Equals(log.RoomName, room.RoomName, StringComparison.Ordinal))
                    .Select(log => new CallInvitationLogItem(
                        log.InvitationId,
                        log.Caller.UserName,
                        log.Caller.DisplayName,
                        log.Recipient.UserName,
                        log.Recipient.DisplayName,
                        log.Status,
                        log.CreatedAtUtc,
                        log.AnsweredAtUtc,
                        log.EndedAtUtc,
                        log.DurationSeconds))
                    .ToList()))
            .ToList();
    }

    private async Task<CallRoomLog> GetOrCreateRoomAsync(
        string roomName,
        string roomUrl,
        CancellationToken cancellationToken)
    {
        var normalizedRoomName = roomName.Trim();
        var room = await _db.CallRoomLogs
            .SingleOrDefaultAsync(candidate => candidate.RoomName == normalizedRoomName, cancellationToken);
        if (room is not null)
        {
            if (!string.IsNullOrWhiteSpace(roomUrl) && room.RoomUrl != roomUrl.Trim())
            {
                room.RoomUrl = roomUrl.Trim();
            }

            return room;
        }

        room = new CallRoomLog
        {
            RoomName = normalizedRoomName,
            RoomUrl = roomUrl.Trim()
        };
        _db.CallRoomLogs.Add(room);
        return room;
    }

    private static IReadOnlyList<CallParticipantLogItem> BuildParticipantLogs(CallRoomLog room)
    {
        return room.Participants
            .OrderBy(session => session.JoinedAtUtc)
            .Select(session =>
            {
                var effectiveLeftAtUtc = session.LeftAtUtc ?? room.EndedAtUtc;
                var isActive = session.LeftAtUtc is null && room.EndedAtUtc is null;
                var durationSeconds = session.DurationSeconds ?? CalculateDurationSeconds(
                    session.JoinedAtUtc,
                    effectiveLeftAtUtc ?? DateTime.UtcNow) ?? 0;

                return new CallParticipantLogItem(
                    session.UserId,
                    session.User.UserName,
                    session.User.DisplayName,
                    session.JoinedAtUtc,
                    effectiveLeftAtUtc,
                    durationSeconds,
                    isActive);
            })
            .ToList();
    }

    private static string GetUserRoomStatus(
        CallRoomLog room,
        Guid userId,
        int participantCount,
        IReadOnlyList<CallLog> roomInvitations)
    {
        var userHasJoined = room.Participants.Any(session => session.UserId == userId);
        var userHasActiveSession = room.EndedAtUtc is null && room.Participants.Any(
            session => session.UserId == userId && session.LeftAtUtc is null);
        var latestUserInvitation = roomInvitations
            .Where(log => log.CallerId == userId || log.RecipientId == userId)
            .OrderByDescending(log => log.CreatedAtUtc)
            .FirstOrDefault();

        if (!userHasJoined && latestUserInvitation is not null)
        {
            return GetInvitationDisplayStatus(latestUserInvitation, userId, false);
        }

        if (participantCount >= 2 && userHasJoined)
        {
            return userHasActiveSession ? "In progress" : "Completed";
        }

        if (latestUserInvitation is not null)
        {
            return GetInvitationDisplayStatus(latestUserInvitation, userId, true);
        }

        return "No other participant";
    }

    private static string GetInvitationDisplayStatus(CallLog invitation, Guid userId, bool hasJoined)
    {
        if (hasJoined && invitation.Status is CallLogStatuses.Answered or CallLogStatuses.Ended)
        {
            return "No other participant";
        }

        return invitation.Status switch
        {
            CallLogStatuses.Declined => "Declined",
            CallLogStatuses.Answered or CallLogStatuses.Ended => "Not connected",
            CallLogStatuses.Failed => "Not connected",
            CallLogStatuses.Ringing => "Calling",
            _ => invitation.Status
        };
    }

    private static int? CalculateUserDurationSeconds(CallRoomLog room, Guid userId)
    {
        var userSessions = room.Participants
            .Where(session => session.UserId == userId)
            .ToList();
        if (userSessions.Count == 0)
        {
            return null;
        }

        long totalSeconds = 0;
        foreach (var session in userSessions)
        {
            if (session.DurationSeconds is int storedDuration)
            {
                totalSeconds += Math.Max(0, storedDuration);
                continue;
            }

            var effectiveLeftAtUtc = session.LeftAtUtc ?? room.EndedAtUtc ?? DateTime.UtcNow;
            var duration = CalculateDurationSeconds(session.JoinedAtUtc, effectiveLeftAtUtc);
            if (duration is int calculatedDuration)
            {
                totalSeconds += calculatedDuration;
            }
        }

        return totalSeconds >= int.MaxValue
            ? int.MaxValue
            : (int)Math.Max(0, totalSeconds);
    }

    private static int? CalculateDurationSeconds(DateTime? answeredAtUtc, DateTime endedAtUtc)
    {
        if (answeredAtUtc is not DateTime answered || endedAtUtc < answered)
        {
            return null;
        }

        var totalSeconds = (endedAtUtc - answered).TotalSeconds;
        return totalSeconds >= int.MaxValue
            ? int.MaxValue
            : Math.Max(0, (int)Math.Round(totalSeconds, MidpointRounding.AwayFromZero));
    }

    private static bool TryGetUserId(ClaimsPrincipal user, out Guid userId)
    {
        return Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
    }
}