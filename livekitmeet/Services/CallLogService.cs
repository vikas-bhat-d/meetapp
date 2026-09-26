using System.Globalization;
using System.Security.Claims;
using System.Text;
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

public sealed record AdminLogDeletionResult(int CallLogsDeleted, int RoomLogsDeleted);

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

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
}

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

    Task<int> ExpirePendingInvitationsAsync(CancellationToken cancellationToken = default);

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

    Task<PagedResult<CallLogItem>> GetRecentAsync(
        ClaimsPrincipal user,
        int page = 1,
        int pageSize = 20,
        string? searchTerm = null,
        CancellationToken cancellationToken = default);

    Task<PagedResult<CallRoomHistoryItem>> GetRecentRoomLogsAsync(
        ClaimsPrincipal user,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default);

    Task<PagedResult<AdminCallRoomLogItem>> GetAdminRoomLogsAsync(
        ClaimsPrincipal user,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default);

    Task<string> ExportAdminCallLogsCsvAsync(
        ClaimsPrincipal user,
        DateTime fromUtc,
        DateTime toUtcExclusive,
        CancellationToken cancellationToken = default);

    Task<string> ExportAdminRoomLogsCsvAsync(
        ClaimsPrincipal user,
        DateTime fromUtc,
        DateTime toUtcExclusive,
        CancellationToken cancellationToken = default);

    Task<AdminLogDeletionResult> DeleteAdminLogsAsync(
        ClaimsPrincipal user,
        DateTime fromUtc,
        DateTime toUtcExclusive,
        CancellationToken cancellationToken = default);
}

public sealed class CallLogService : ICallLogService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IHubContext<CallInvitationHub> _hub;
    private readonly CallInvitationStatusNotifier _statusNotifier;
    private readonly IFirebasePushNotificationService _pushNotifications;
    private readonly ILogger<CallLogService> _logger;

    public CallLogService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IHubContext<CallInvitationHub> hub,
        CallInvitationStatusNotifier statusNotifier,
        IFirebasePushNotificationService pushNotifications,
        ILogger<CallLogService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _hub = hub;
        _statusNotifier = statusNotifier;
        _pushNotifications = pushNotifications;
        _logger = logger;
    }

    public async Task<CallLog> CreateAsync(
        Guid invitationId,
        Guid callerId,
        Guid recipientId,
        string roomName,
        string roomUrl,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await GetOrCreateRoomAsync(db, roomName, roomUrl, cancellationToken);
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

        db.CallLogs.Add(log);
        await db.SaveChangesAsync(cancellationToken);
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
            _logger.LogWarning(
                "Call invitation join rejected before lookup. InvitationId={InvitationId} RoomName={RoomName}",
                invitationId,
                roomName);
            return null;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        _logger.LogInformation(
            "Call invitation join requested. InvitationId={InvitationId} UserId={UserId} RoomName={RoomName}",
            invitationId,
            userId,
            roomName);

        if (invitationId is Guid incomingInvitationId)
        {
            await ExpirePendingInvitationsAsync(cancellationToken);
            var invitation = await db.CallLogs
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.InvitationId == incomingInvitationId,
                    cancellationToken);
            if (invitation is null)
            {
                _logger.LogWarning(
                    "Call invitation join rejected because the invitation was not found. InvitationId={InvitationId} UserId={UserId}",
                    incomingInvitationId,
                    userId);
                return null;
            }

            if (invitation.RecipientId != userId)
            {
                _logger.LogWarning(
                    "Call invitation join rejected because the user is not the recipient. InvitationId={InvitationId} UserId={UserId} RecipientId={RecipientId} Status={Status}",
                    incomingInvitationId,
                    userId,
                    invitation.RecipientId,
                    invitation.Status);
                return null;
            }

            if (invitation.Status is not (CallLogStatuses.Ringing or CallLogStatuses.Answered))
            {
                _logger.LogWarning(
                    "Call invitation join rejected because its status is not joinable. InvitationId={InvitationId} UserId={UserId} Status={Status}",
                    incomingInvitationId,
                    userId,
                    invitation.Status);
                return null;
            }
        }

        var room = await GetOrCreateRoomAsync(db, roomName, roomUrl, cancellationToken);
        var existingSession = room.EndedAtUtc is null
            ? await db.CallParticipantSessions
                .Where(session => session.CallRoomLogId == room.Id &&
                                  session.UserId == userId &&
                                  session.LeftAtUtc == null)
                .OrderBy(session => session.JoinedAtUtc)
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        if (existingSession is not null)
        {
            room.EndedAtUtc = null;
            await db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Call invitation reused an active participant session. InvitationId={InvitationId} UserId={UserId} SessionId={SessionId}",
                invitationId,
                userId,
                existingSession.Id);
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
        db.CallParticipantSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Call participant session created. InvitationId={InvitationId} UserId={UserId} SessionId={SessionId} RoomName={RoomName}",
            invitationId,
            userId,
            session.Id,
            roomName);
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

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var session = await db.CallParticipantSessions
            .Include(candidate => candidate.CallRoomLog)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == participantSessionId && candidate.UserId == userId,
                cancellationToken);
        if (session is null)
        {
            return false;
        }

        var activeSessions = await db.CallParticipantSessions
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

            var anotherParticipantIsActive = await db.CallParticipantSessions
                .AnyAsync(
                    candidate => candidate.CallRoomLogId == session.CallRoomLogId &&
                                candidate.UserId != userId &&
                                candidate.LeftAtUtc == null,
                    cancellationToken);
            session.CallRoomLog.EndedAtUtc = anotherParticipantIsActive ? null : leftAtUtc;
            await db.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    public async Task MarkFailedAsync(Guid invitationId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var log = await db.CallLogs
            .AsNoTracking()
            .SingleOrDefaultAsync(
            candidate => candidate.InvitationId == invitationId,
            cancellationToken);
        if (log is null)
        {
            return;
        }

        await db.CallLogs
            .Where(candidate => candidate.InvitationId == invitationId &&
                                candidate.Status == CallLogStatuses.Ringing)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.Status, CallLogStatuses.Failed)
                    .SetProperty(candidate => candidate.EndedAtUtc, DateTime.UtcNow),
                cancellationToken);
    }

    public async Task<int> ExpirePendingInvitationsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var cutoff = now.Subtract(CallInvitationPolicy.Lifetime);
        var candidates = await db.CallLogs
            .AsNoTracking()
            .Where(log => log.Status == CallLogStatuses.Ringing && log.CreatedAtUtc <= cutoff)
            .Select(log => new { log.InvitationId, log.CallerId, log.RecipientId, log.CreatedAtUtc })
            .ToListAsync(cancellationToken);
        var expiredCount = 0;

        foreach (var candidate in candidates)
        {
            var expiredAtUtc = candidate.CreatedAtUtc.Add(CallInvitationPolicy.Lifetime);
            var updated = await db.CallLogs
                .Where(log => log.InvitationId == candidate.InvitationId &&
                              log.Status == CallLogStatuses.Ringing &&
                              log.CreatedAtUtc <= cutoff)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(log => log.Status, CallLogStatuses.NotReceived)
                        .SetProperty(log => log.EndedAtUtc, expiredAtUtc),
                    cancellationToken);
            if (updated == 0)
            {
                continue;
            }

            expiredCount++;
            await _statusNotifier.NotifyAsync(
                candidate.CallerId,
                candidate.InvitationId,
                CallLogStatuses.NotReceived);
            await _hub.Clients
                .Group(CallInvitationHub.UserGroup(candidate.RecipientId))
                .SendAsync("CallExpired", candidate.InvitationId, cancellationToken);
            try
            {
                await _pushNotifications.SendCancelAsync(
                    candidate.RecipientId,
                    candidate.InvitationId,
                    cancellationToken);
            }
            catch
            {
                // The database outcome remains authoritative if push cleanup fails.
            }
        }

        return expiredCount;
    }

    public async Task<bool> MarkAnsweredAsync(
        ClaimsPrincipal user,
        Guid invitationId,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetUserId(user, out var userId))
        {
            _logger.LogWarning(
                "Call invitation answer rejected because the user could not be identified. InvitationId={InvitationId}",
                invitationId);
            return false;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await ExpirePendingInvitationsAsync(cancellationToken);
        var log = await db.CallLogs
            .AsNoTracking()
            .SingleOrDefaultAsync(
            candidate => candidate.InvitationId == invitationId,
            cancellationToken);
        if (log is null)
        {
            _logger.LogWarning(
                "Call invitation answer rejected because the invitation was not found. InvitationId={InvitationId} UserId={UserId}",
                invitationId,
                userId);
            return false;
        }
        if (log.RecipientId != userId)
        {
            _logger.LogWarning(
                "Call invitation answer rejected because the user is not the recipient. InvitationId={InvitationId} UserId={UserId} RecipientId={RecipientId} Status={Status}",
                invitationId,
                userId,
                log.RecipientId,
                log.Status);
            return false;
        }
        if (log.Status == CallLogStatuses.Answered)
        {
            _logger.LogInformation(
                "Call invitation was already answered. InvitationId={InvitationId} UserId={UserId}",
                invitationId,
                userId);
            return true;
        }
        if (log.Status != CallLogStatuses.Ringing)
        {
            _logger.LogWarning(
                "Call invitation answer rejected because its status is not Ringing. InvitationId={InvitationId} UserId={UserId} Status={Status}",
                invitationId,
                userId,
                log.Status);
            return false;
        }

        var answeredAtUtc = DateTime.UtcNow;
        var updated = await db.CallLogs
            .Where(candidate => candidate.InvitationId == invitationId &&
                                candidate.RecipientId == userId &&
                                candidate.Status == CallLogStatuses.Ringing)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.Status, CallLogStatuses.Answered)
                    .SetProperty(candidate => candidate.AnsweredAtUtc, answeredAtUtc),
                cancellationToken);
        if (updated == 0)
        {
            var currentStatus = await db.CallLogs
                .AsNoTracking()
                .Where(candidate => candidate.InvitationId == invitationId &&
                                    candidate.RecipientId == userId)
                .Select(candidate => candidate.Status)
                .SingleOrDefaultAsync(cancellationToken);
            _logger.LogWarning(
                "Call invitation answer lost a concurrent transition. InvitationId={InvitationId} UserId={UserId} CurrentStatus={CurrentStatus}",
                invitationId,
                userId,
                currentStatus);
            return currentStatus == CallLogStatuses.Answered;
        }

        await _hub.Clients
            .Group(CallInvitationHub.UserGroup(log.CallerId))
            .SendAsync("CallAnswered", invitationId, cancellationToken);
        await _hub.Clients
            .Group(CallInvitationHub.UserGroup(log.RecipientId))
            .SendAsync("CallAnswered", invitationId, cancellationToken);
        await _statusNotifier.NotifyAsync(log.CallerId, invitationId, CallLogStatuses.Answered);
        _logger.LogInformation(
            "Call invitation answered. InvitationId={InvitationId} UserId={UserId} CallerId={CallerId}",
            invitationId,
            userId,
            log.CallerId);
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

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await ExpirePendingInvitationsAsync(cancellationToken);
        var log = await db.CallLogs
            .AsNoTracking()
            .SingleOrDefaultAsync(
            candidate => candidate.InvitationId == invitationId && candidate.RecipientId == userId,
            cancellationToken);
        if (log is null)
        {
            return false;
        }

        var endedAtUtc = DateTime.UtcNow;
        var updated = await db.CallLogs
            .Where(candidate => candidate.InvitationId == invitationId &&
                                candidate.RecipientId == userId &&
                                candidate.Status == CallLogStatuses.Ringing)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.Status, CallLogStatuses.Declined)
                    .SetProperty(candidate => candidate.EndedAtUtc, endedAtUtc),
                cancellationToken);
        if (updated == 0)
        {
            return false;
        }

        await _hub.Clients
            .Group(CallInvitationHub.UserGroup(log.CallerId))
            .SendAsync("CallDeclined", invitationId, cancellationToken);
        await _hub.Clients
            .Group(CallInvitationHub.UserGroup(log.RecipientId))
            .SendAsync("CallDeclined", invitationId, cancellationToken);
        await _statusNotifier.NotifyAsync(log.CallerId, invitationId, CallLogStatuses.Declined);
        return true;
    }

    public async Task<bool> MarkEndedAsync(
        ClaimsPrincipal user,
        Guid invitationId,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetUserId(user, out var userId))
        {
            _logger.LogWarning(
                "Call log end rejected because the user could not be identified. InvitationId={InvitationId}",
                invitationId);
            return false;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var log = await db.CallLogs
            .AsNoTracking()
            .SingleOrDefaultAsync(
            candidate => candidate.InvitationId == invitationId &&
                        (candidate.CallerId == userId || candidate.RecipientId == userId),
            cancellationToken);
        if (log is null)
        {
            _logger.LogWarning(
                "Call log end rejected because the invitation was not found. InvitationId={InvitationId} UserId={UserId}",
                invitationId,
                userId);
            return false;
        }

        var endedAtUtc = DateTime.UtcNow;
        var durationSeconds = CalculateDurationSeconds(log.AnsweredAtUtc, endedAtUtc);
        var recipientJoined = await db.CallParticipantSessions
            .AsNoTracking()
            .AnyAsync(
                session => session.InvitationId == invitationId &&
                           session.UserId == log.RecipientId,
                cancellationToken);
        var endedStatus = recipientJoined
            ? CallLogStatuses.Ended
            : CallLogStatuses.NotConnected;
        _logger.LogInformation(
            "Call log finalization evaluated. InvitationId={InvitationId} UserId={UserId} CurrentStatus={CurrentStatus} RecipientJoined={RecipientJoined} NextStatus={NextStatus}",
            invitationId,
            userId,
            log.Status,
            recipientJoined,
            endedStatus);
        var updated = await db.CallLogs
            .Where(candidate => candidate.InvitationId == invitationId &&
                                candidate.Status == CallLogStatuses.Answered)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.Status, endedStatus)
                    .SetProperty(candidate => candidate.EndedAtUtc, endedAtUtc)
                    .SetProperty(candidate => candidate.DurationSeconds, recipientJoined ? durationSeconds : null),
                cancellationToken);
        if (updated > 0)
        {
            await _statusNotifier.NotifyAsync(log.CallerId, invitationId, endedStatus);
            _logger.LogInformation(
                "Call log finalization applied. InvitationId={InvitationId} Status={Status}",
                invitationId,
                endedStatus);
        }
        else
        {
            _logger.LogWarning(
                "Call log finalization did not apply because the status changed concurrently. InvitationId={InvitationId} CurrentStatus={CurrentStatus}",
                invitationId,
                log.Status);
        }

        return updated > 0;
    }

    public async Task<PagedResult<CallLogItem>> GetRecentAsync(
        ClaimsPrincipal user,
        int page = 1,
        int pageSize = 20,
        string? searchTerm = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetUserId(user, out var userId))
        {
            return new PagedResult<CallLogItem>(Array.Empty<CallLogItem>(), 1, 20, 0);
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        searchTerm = searchTerm?.Trim();
        var query = db.CallLogs
            .AsNoTracking()
            .Where(log => log.CallerId == userId || log.RecipientId == userId);
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var searchPattern = $"%{searchTerm}%";
            var status = searchTerm.ToLowerInvariant() switch
            {
                "declined" => CallLogStatuses.Declined,
                "cancelled" => CallLogStatuses.Cancelled,
                "not connected" => CallLogStatuses.NotConnected,
                "not received" => CallLogStatuses.NotReceived,
                "delivery failed" => CallLogStatuses.Failed,
                "calling" => CallLogStatuses.Ringing,
                "accepted" => CallLogStatuses.Answered,
                "completed" => CallLogStatuses.Ended,
                _ => null
            };

            query = query.Where(log =>
                EF.Functions.Like(log.RoomName, searchPattern) ||
                EF.Functions.Like(log.Caller.DisplayName, searchPattern) ||
                EF.Functions.Like(log.Caller.UserName, searchPattern) ||
                EF.Functions.Like(log.Recipient.DisplayName, searchPattern) ||
                EF.Functions.Like(log.Recipient.UserName, searchPattern) ||
                (status != null && log.Status == status));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(log => log.CreatedAtUtc)
            .Skip((int)Math.Min(int.MaxValue, (long)(page - 1) * pageSize))
            .Take(pageSize)
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

        return new PagedResult<CallLogItem>(items, page, pageSize, totalCount);
    }

    public async Task<PagedResult<CallRoomHistoryItem>> GetRecentRoomLogsAsync(
        ClaimsPrincipal user,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetUserId(user, out var userId))
        {
            return new PagedResult<CallRoomHistoryItem>(Array.Empty<CallRoomHistoryItem>(), 1, 20, 0);
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var roomsQuery = db.CallRoomLogs
            .AsNoTracking()
            .Include(room => room.Participants)
                .ThenInclude(session => session.User)
            .Where(room => room.Participants.Any(session => session.UserId == userId) ||
                           db.CallLogs.Any(log => log.RoomName == room.RoomName &&
                                                   (log.CallerId == userId || log.RecipientId == userId)));
        var totalCount = await roomsQuery.CountAsync(cancellationToken);
        var rooms = await roomsQuery
            .OrderByDescending(room => room.CreatedAtUtc)
            .Skip((int)Math.Min(int.MaxValue, (long)(page - 1) * pageSize))
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var roomNames = rooms
            .Select(room => room.RoomName)
            .ToArray();
        var invitations = roomNames.Length == 0
            ? new List<CallLog>()
            : await db.CallLogs
                .AsNoTracking()
                .Include(log => log.Caller)
                .Include(log => log.Recipient)
                .Where(log => roomNames.Contains(log.RoomName))
                .OrderByDescending(log => log.CreatedAtUtc)
                .ToListAsync(cancellationToken);

        var items = rooms
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

        return new PagedResult<CallRoomHistoryItem>(items, page, pageSize, totalCount);
    }

    public async Task<PagedResult<AdminCallRoomLogItem>> GetAdminRoomLogsAsync(
        ClaimsPrincipal user,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (!user.IsInRole("Admin"))
        {
            return new PagedResult<AdminCallRoomLogItem>(Array.Empty<AdminCallRoomLogItem>(), 1, 20, 0);
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var roomsQuery = db.CallRoomLogs
            .AsNoTracking()
            .Include(room => room.Participants)
                .ThenInclude(session => session.User);
        var totalCount = await roomsQuery.CountAsync(cancellationToken);
        var rooms = await roomsQuery
            .OrderByDescending(room => room.CreatedAtUtc)
            .Skip((int)Math.Min(int.MaxValue, (long)(page - 1) * pageSize))
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var roomNames = rooms
            .Select(room => room.RoomName)
            .ToArray();
        var invitations = roomNames.Length == 0
            ? new List<CallLog>()
            : await db.CallLogs
                .AsNoTracking()
                .Include(log => log.Caller)
                .Include(log => log.Recipient)
                .Where(log => roomNames.Contains(log.RoomName))
                .OrderByDescending(log => log.CreatedAtUtc)
                .ToListAsync(cancellationToken);

        var items = rooms
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

                return new PagedResult<AdminCallRoomLogItem>(items, page, pageSize, totalCount);
    }

            public async Task<string> ExportAdminCallLogsCsvAsync(
                ClaimsPrincipal user,
                DateTime fromUtc,
                DateTime toUtcExclusive,
                CancellationToken cancellationToken = default)
            {
                EnsureAdmin(user);
                ValidateDateRange(fromUtc, toUtcExclusive);

                await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
                var logs = await db.CallLogs
                    .AsNoTracking()
                    .Include(log => log.Caller)
                    .Include(log => log.Recipient)
                    .Where(log => log.CreatedAtUtc >= fromUtc && log.CreatedAtUtc < toUtcExclusive)
                    .OrderBy(log => log.CreatedAtUtc)
                    .ToListAsync(cancellationToken);

                var csv = new StringBuilder();
                AppendCsvRow(csv,
                    "Id",
                    "InvitationId",
                    "CallerUserName",
                    "CallerDisplayName",
                    "RecipientUserName",
                    "RecipientDisplayName",
                    "RoomName",
                    "RoomUrl",
                    "Status",
                    "CreatedAtUtc",
                    "AnsweredAtUtc",
                    "EndedAtUtc",
                    "DurationSeconds");

                foreach (var log in logs)
                {
                    AppendCsvRow(csv,
                        log.Id,
                        log.InvitationId,
                        log.Caller.UserName,
                        log.Caller.DisplayName,
                        log.Recipient.UserName,
                        log.Recipient.DisplayName,
                        log.RoomName,
                        log.RoomUrl,
                        log.Status,
                        log.CreatedAtUtc,
                        log.AnsweredAtUtc,
                        log.EndedAtUtc,
                        log.DurationSeconds);
                }

                return csv.ToString();
            }

            public async Task<string> ExportAdminRoomLogsCsvAsync(
                ClaimsPrincipal user,
                DateTime fromUtc,
                DateTime toUtcExclusive,
                CancellationToken cancellationToken = default)
            {
                EnsureAdmin(user);
                ValidateDateRange(fromUtc, toUtcExclusive);

                await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
                var rooms = await db.CallRoomLogs
                    .AsNoTracking()
                    .Include(room => room.Participants)
                        .ThenInclude(session => session.User)
                    .Where(room => room.CreatedAtUtc >= fromUtc && room.CreatedAtUtc < toUtcExclusive)
                    .OrderBy(room => room.CreatedAtUtc)
                    .ToListAsync(cancellationToken);

                var invitations = await db.CallLogs
                    .AsNoTracking()
                    .Include(log => log.Caller)
                    .Include(log => log.Recipient)
                    .Where(log => db.CallRoomLogs.Any(room =>
                        room.CreatedAtUtc >= fromUtc &&
                        room.CreatedAtUtc < toUtcExclusive &&
                        room.RoomName == log.RoomName))
                    .OrderBy(log => log.CreatedAtUtc)
                    .ToListAsync(cancellationToken);

                var invitationsByRoom = invitations.ToLookup(log => log.RoomName, StringComparer.Ordinal);
                var csv = new StringBuilder();
                AppendCsvRow(csv,
                    "RecordType",
                    "RoomId",
                    "RoomName",
                    "RoomUrl",
                    "RoomCreatedAtUtc",
                    "RoomStartedAtUtc",
                    "RoomEndedAtUtc",
                    "UserId",
                    "UserName",
                    "DisplayName",
                    "JoinedAtUtc",
                    "LeftAtUtc",
                    "ParticipantDurationSeconds",
                    "ParticipantStatus",
                    "InvitationId",
                    "CallerUserName",
                    "CallerDisplayName",
                    "RecipientUserName",
                    "RecipientDisplayName",
                    "InvitationStatus",
                    "InvitationCreatedAtUtc",
                    "InvitationAnsweredAtUtc",
                    "InvitationEndedAtUtc",
                    "InvitationDurationSeconds");

                foreach (var room in rooms)
                {
                    AppendCsvRow(csv,
                        "Room",
                        room.Id,
                        room.RoomName,
                        room.RoomUrl,
                        room.CreatedAtUtc,
                        room.StartedAtUtc,
                        room.EndedAtUtc,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null);

                    foreach (var participant in room.Participants.OrderBy(session => session.JoinedAtUtc))
                    {
                        var effectiveLeftAtUtc = participant.LeftAtUtc ?? room.EndedAtUtc;
                        var isActive = participant.LeftAtUtc is null && room.EndedAtUtc is null;
                        var durationSeconds = participant.DurationSeconds ??
                            CalculateDurationSeconds(participant.JoinedAtUtc, effectiveLeftAtUtc ?? DateTime.UtcNow) ?? 0;

                        AppendCsvRow(csv,
                            "Participant",
                            room.Id,
                            room.RoomName,
                            room.RoomUrl,
                            room.CreatedAtUtc,
                            room.StartedAtUtc,
                            room.EndedAtUtc,
                            participant.UserId,
                            participant.User.UserName,
                            participant.User.DisplayName,
                            participant.JoinedAtUtc,
                            effectiveLeftAtUtc,
                            durationSeconds,
                            isActive ? "Active" : "Ended",
                            participant.InvitationId,
                            null,
                            null,
                            null,
                            null,
                            null,
                            null,
                            null,
                            null,
                            null);
                    }

                    foreach (var invitation in invitationsByRoom[room.RoomName])
                    {
                        AppendCsvRow(csv,
                            "Invitation",
                            room.Id,
                            room.RoomName,
                            room.RoomUrl,
                            room.CreatedAtUtc,
                            room.StartedAtUtc,
                            room.EndedAtUtc,
                            null,
                            null,
                            null,
                            null,
                            null,
                            null,
                            null,
                            invitation.InvitationId,
                            invitation.Caller.UserName,
                            invitation.Caller.DisplayName,
                            invitation.Recipient.UserName,
                            invitation.Recipient.DisplayName,
                            invitation.Status,
                            invitation.CreatedAtUtc,
                            invitation.AnsweredAtUtc,
                            invitation.EndedAtUtc,
                            invitation.DurationSeconds);
                    }
                }

                return csv.ToString();
            }

            public async Task<AdminLogDeletionResult> DeleteAdminLogsAsync(
                ClaimsPrincipal user,
                DateTime fromUtc,
                DateTime toUtcExclusive,
                CancellationToken cancellationToken = default)
            {
                EnsureAdmin(user);
                ValidateDateRange(fromUtc, toUtcExclusive);

                await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                var callLogsDeleted = await db.CallLogs
                    .Where(log => log.CreatedAtUtc >= fromUtc && log.CreatedAtUtc < toUtcExclusive)
                    .ExecuteDeleteAsync(cancellationToken);
                var roomLogsDeleted = await db.CallRoomLogs
                    .Where(room => room.CreatedAtUtc >= fromUtc && room.CreatedAtUtc < toUtcExclusive)
                    .ExecuteDeleteAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                return new AdminLogDeletionResult(callLogsDeleted, roomLogsDeleted);
            }

            private static void EnsureAdmin(ClaimsPrincipal user)
            {
                if (user.Identity?.IsAuthenticated != true || !user.IsInRole("Admin"))
                {
                    throw new UnauthorizedAccessException("Administrator access is required.");
                }
            }

            private static void ValidateDateRange(DateTime fromUtc, DateTime toUtcExclusive)
            {
                if (fromUtc >= toUtcExclusive)
                {
                    throw new ArgumentException("The log date range must have an end after its start.");
                }
            }

            private static void AppendCsvRow(StringBuilder csv, params object?[] values)
            {
                csv.AppendLine(string.Join(",", values.Select(FormatCsvValue)));
            }

            private static string FormatCsvValue(object? value)
            {
                var text = value switch
                {
                    null => string.Empty,
                    DateTime dateTime => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture),
                    IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
                    _ => value.ToString() ?? string.Empty
                };

                return $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
            }

    private async Task<CallRoomLog> GetOrCreateRoomAsync(
        AppDbContext db,
        string roomName,
        string roomUrl,
        CancellationToken cancellationToken)
    {
        var normalizedRoomName = roomName.Trim();
        var room = await db.CallRoomLogs
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
        db.CallRoomLogs.Add(room);
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
            CallLogStatuses.NotConnected => "Not connected",
            CallLogStatuses.NotReceived => "Not received",
            CallLogStatuses.Answered => "Accepted",
            CallLogStatuses.Ended => "Completed",
            CallLogStatuses.Failed => "Delivery failed",
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