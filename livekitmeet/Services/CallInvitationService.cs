using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using livekitmeet.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace livekitmeet.Services;

public sealed record CallInvitationResult(bool Success, string? Error, Guid? InvitationId)
{
    public static CallInvitationResult Failed(string error) => new(false, error, null);
}

public interface ICallInvitationService
{
    Task<CallInvitationResult> SendAsync(
        ClaimsPrincipal caller,
        string targetUserName,
        string roomName,
        string roomUrl,
        bool audioEnabled,
        bool videoEnabled,
        CancellationToken cancellationToken = default);

    Task<bool> CancelAsync(
        ClaimsPrincipal caller,
        Guid invitationId,
        CancellationToken cancellationToken = default);

    Task<bool> DeclineWithActionTokenAsync(
        Guid invitationId,
        string actionToken,
        CancellationToken cancellationToken = default);
}

public sealed class CallInvitationService : ICallInvitationService
{
    private readonly AppDbContext _db;
    private readonly IHubContext<CallInvitationHub> _hub;
    private readonly CallInvitationConnectionTracker _tracker;
    private readonly IFirebasePushNotificationService _pushNotifications;
    private readonly ICallLogService _callLogs;
    private readonly string _actionTokenSecret;

    public CallInvitationService(
        AppDbContext db,
        IHubContext<CallInvitationHub> hub,
        CallInvitationConnectionTracker tracker,
        IFirebasePushNotificationService pushNotifications,
        ICallLogService callLogs,
        IConfiguration configuration)
    {
        _db = db;
        _hub = hub;
        _tracker = tracker;
        _pushNotifications = pushNotifications;
        _callLogs = callLogs;
        _actionTokenSecret = configuration["Auth:JwtSecret"] ?? throw new InvalidOperationException("Auth:JwtSecret must be configured.");
    }

    public async Task<CallInvitationResult> SendAsync(
        ClaimsPrincipal caller,
        string targetUserName,
        string roomName,
        string roomUrl,
        bool audioEnabled,
        bool videoEnabled,
        CancellationToken cancellationToken = default)
    {
        var callerIdValue = caller.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(callerIdValue, out var callerId))
        {
            return CallInvitationResult.Failed("The caller could not be identified.");
        }

        targetUserName = targetUserName.Trim();
        if (string.IsNullOrWhiteSpace(targetUserName))
        {
            return CallInvitationResult.Failed("Enter a username to call.");
        }

        var target = await _db.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(
                user => user.NormalizedUserName == UserNameNormalizer.Normalize(targetUserName) && user.IsActive,
                cancellationToken);
        if (target is null)
        {
            return CallInvitationResult.Failed("That user does not exist or is not active.");
        }
        if (target.Id == callerId)
        {
            return CallInvitationResult.Failed("You cannot call yourself.");
        }

        roomName = roomName.Trim();
        if (await _db.CallParticipantSessions.AnyAsync(
                session => session.UserId == target.Id &&
                           session.LeftAtUtc == null &&
                           session.CallRoomLog.RoomName == roomName &&
                           session.CallRoomLog.EndedAtUtc == null,
                cancellationToken))
        {
            return CallInvitationResult.Failed("That user is already in this room.");
        }

        var fromUserName = caller.FindFirstValue(ClaimTypes.Name) ?? "User";
        var fromDisplayName = caller.FindFirst("display_name")?.Value ?? fromUserName;
        var invitationId = Guid.NewGuid();
        var invitation = new CallInvitationMessage(
            invitationId,
            roomName,
            QueryHelpers.AddQueryString(roomUrl, "invitationId", invitationId.ToString()),
            fromUserName,
            fromDisplayName,
            audioEnabled,
            videoEnabled,
            DateTime.UtcNow.AddMinutes(2));

        await _callLogs.CreateAsync(
            invitation.InvitationId,
            callerId,
            target.Id,
            invitation.RoomName,
            invitation.RoomUrl,
            cancellationToken);

        var declineToken = CreateDeclineActionToken(invitation.InvitationId, target.Id, invitation.ExpiresAtUtc);

        var deliveredToTray = _tracker.IsConnected(target.Id);
        if (deliveredToTray)
        {
            await _hub.Clients
                .Group(CallInvitationHub.UserGroup(target.Id))
                .SendAsync("IncomingCall", invitation, cancellationToken);
        }

        var pushResult = await _pushNotifications.SendAsync(
            target.Id,
            $"Incoming call from {fromDisplayName}",
            $"{roomName} is ready to join.",
            new Dictionary<string, string>
            {
                ["type"] = "INCOMING_CALL",
                ["callUUID"] = invitation.InvitationId.ToString(),
                ["invitationId"] = invitation.InvitationId.ToString(),
                ["roomName"] = roomName,
                ["roomUrl"] = roomUrl,
                ["fromUserName"] = fromUserName,
                ["fromDisplayName"] = fromDisplayName,
                ["callerName"] = fromDisplayName,
                ["callerHandle"] = fromUserName,
                ["hasVideo"] = videoEnabled.ToString().ToLowerInvariant(),
                ["audioEnabled"] = audioEnabled.ToString().ToLowerInvariant(),
                ["videoEnabled"] = videoEnabled.ToString().ToLowerInvariant(),
                ["declineToken"] = declineToken
            },
            cancellationToken);

        if (!deliveredToTray && pushResult.DeliveredCount == 0)
        {
            await _callLogs.MarkFailedAsync(invitation.InvitationId, cancellationToken);
            if (pushResult.RegisteredCount == 0)
            {
                return CallInvitationResult.Failed(
                    "That user is not connected to the tray app and has no registered Android device. Make sure the APK is signed in with this exact username.");
            }

            if (!pushResult.Configured)
            {
                return CallInvitationResult.Failed(
                    pushResult.Error ?? "The Android device is registered, but Firebase server credentials are not configured.");
            }

            return CallInvitationResult.Failed(
                pushResult.Error ?? "The Android device is registered, but Firebase could not deliver the notification. Check the server logs.");
        }

        return new CallInvitationResult(true, null, invitation.InvitationId);
    }

    public async Task<bool> CancelAsync(
        ClaimsPrincipal caller,
        Guid invitationId,
        CancellationToken cancellationToken = default)
    {
        var callerIdValue = caller.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(callerIdValue, out var callerId))
        {
            return false;
        }

        var log = await _db.CallLogs.SingleOrDefaultAsync(
            candidate => candidate.InvitationId == invitationId && candidate.CallerId == callerId,
            cancellationToken);
        if (log is null || log.Status != CallLogStatuses.Ringing)
        {
            return false;
        }

        log.Status = CallLogStatuses.Cancelled;
        log.EndedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        await _hub.Clients
            .Group(CallInvitationHub.UserGroup(log.RecipientId))
            .SendAsync("CallCancelled", invitationId, cancellationToken);

        await _pushNotifications.SendCancelAsync(log.RecipientId, invitationId, cancellationToken);
        return true;
    }

    public async Task<bool> DeclineWithActionTokenAsync(
        Guid invitationId,
        string actionToken,
        CancellationToken cancellationToken = default)
    {
        var log = await _db.CallLogs.SingleOrDefaultAsync(
            candidate => candidate.InvitationId == invitationId,
            cancellationToken);
        if (log is null || log.Status != CallLogStatuses.Ringing ||
            !ValidateDeclineActionToken(actionToken, invitationId, log.RecipientId))
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

    private string CreateDeclineActionToken(Guid invitationId, Guid recipientId, DateTime expiresAtUtc)
    {
        var payload = string.Join(
            '|',
            invitationId.ToString("N"),
            recipientId.ToString("N"),
            new DateTimeOffset(expiresAtUtc).ToUnixTimeSeconds());
        var encodedPayload = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(payload));
        var signature = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(_actionTokenSecret),
            Encoding.UTF8.GetBytes(encodedPayload));
        return $"{encodedPayload}.{WebEncoders.Base64UrlEncode(signature)}";
    }

    private bool ValidateDeclineActionToken(string actionToken, Guid invitationId, Guid recipientId)
    {
        if (string.IsNullOrWhiteSpace(actionToken))
        {
            return false;
        }

        var tokenParts = actionToken.Split('.', 2, StringSplitOptions.None);
        if (tokenParts.Length != 2)
        {
            return false;
        }

        try
        {
            var payloadBytes = WebEncoders.Base64UrlDecode(tokenParts[0]);
            var payloadParts = Encoding.UTF8.GetString(payloadBytes).Split('|');
            if (payloadParts.Length != 3 ||
                !Guid.TryParseExact(payloadParts[0], "N", out var tokenInvitationId) ||
                !Guid.TryParseExact(payloadParts[1], "N", out var tokenRecipientId) ||
                !long.TryParse(payloadParts[2], out var expiresAtUnix))
            {
                return false;
            }

            var expectedSignature = HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(_actionTokenSecret),
                Encoding.UTF8.GetBytes(tokenParts[0]));
            var providedSignature = WebEncoders.Base64UrlDecode(tokenParts[1]);
            return tokenInvitationId == invitationId &&
                   tokenRecipientId == recipientId &&
                   expiresAtUnix >= DateTimeOffset.UtcNow.ToUnixTimeSeconds() &&
                   CryptographicOperations.FixedTimeEquals(expectedSignature, providedSignature);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
