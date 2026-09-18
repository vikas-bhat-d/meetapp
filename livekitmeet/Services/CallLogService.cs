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

public interface ICallLogService
{
    Task<CallLog> CreateAsync(
        Guid invitationId,
        Guid callerId,
        Guid recipientId,
        string roomName,
        string roomUrl,
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