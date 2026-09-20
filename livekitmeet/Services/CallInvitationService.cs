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

public sealed record CallInvitationAvailability(bool Available, string? Error);

public interface ICallInvitationService
{
    Task<CallInvitationAvailability> CheckAvailabilityAsync(
        ClaimsPrincipal caller,
        string targetUserName,
        CancellationToken cancellationToken = default);

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
    private readonly CallInvitationStatusNotifier _statusNotifier;
    private readonly IFirebasePushNotificationService _pushNotifications;
    private readonly ICallLogService _callLogs;
    private readonly string _actionTokenSecret;

    public CallInvitationService(
        AppDbContext db,
        IHubContext<CallInvitationHub> hub,
        CallInvitationConnectionTracker tracker,
        CallInvitationStatusNotifier statusNotifier,
        IFirebasePushNotificationService pushNotifications,
        ICallLogService callLogs,
        IConfiguration configuration)
    {
        _db = db;
        _hub = hub;
        _tracker = tracker;
        _statusNotifier = statusNotifier;
        _pushNotifications = pushNotifications;
        _callLogs = callLogs;
        _actionTokenSecret = configuration["Auth:JwtSecret"] ?? throw new InvalidOperationException("Auth:JwtSecret must be configured.");
    }

    public async Task<CallInvitationAvailability> CheckAvailabilityAsync(
        ClaimsPrincipal caller,
        string targetUserName,
        CancellationToken cancellationToken = default)
    {
        var callerIdValue = caller.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(callerIdValue, out var callerId))
        {
            return new CallInvitationAvailability(false, "The caller could not be identified.");
        }

        targetUserName = targetUserName.Trim();
        if (string.IsNullOrWhiteSpace(targetUserName))
        {
            return new CallInvitationAvailability(false, "Enter a username to call.");
        }

        var target = await _db.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(
                user => user.NormalizedUserName == UserNameNormalizer.Normalize(targetUserName) && user.IsActive,
                cancellationToken);
        if (target is null)
        {
            return new CallInvitationAvailability(false, "That user does not exist.");
        }
        if (target.Id == callerId)
        {
            return new CallInvitationAvailability(false, "You cannot call yourself.");
        }
        return new CallInvitationAvailability(true, null);
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
            return CallInvitationResult.Failed("That user does not exist.");
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
            DateTime.UtcNow.Add(CallInvitationPolicy.Lifetime));

        var declineToken = CreateDeclineActionToken(invitation.InvitationId, target.Id, invitation.ExpiresAtUtc);
        var declineUrl = CreateDeclineEndpoint(invitation.RoomUrl, invitation.InvitationId);
        var pushData = new Dictionary<string, string>
        {
            ["type"] = "INCOMING_CALL",
            ["callUUID"] = invitation.InvitationId.ToString(),
            ["invitationId"] = invitation.InvitationId.ToString(),
            ["roomName"] = roomName,
            ["roomUrl"] = invitation.RoomUrl,
            ["fromUserName"] = fromUserName,
            ["fromDisplayName"] = fromDisplayName,
            ["callerName"] = fromDisplayName,
            ["callerHandle"] = fromUserName,
            ["hasVideo"] = videoEnabled.ToString().ToLowerInvariant(),
            ["audioEnabled"] = audioEnabled.ToString().ToLowerInvariant(),
            ["videoEnabled"] = videoEnabled.ToString().ToLowerInvariant(),
            ["declineToken"] = declineToken,
            ["declineUrl"] = declineUrl
        };

        var deliveredToTray = _tracker.IsConnected(target.Id);
        FcmSendResult? pushResult = null;
        if (!deliveredToTray)
        {
            pushResult = await _pushNotifications.SendAsync(
                target.Id,
                $"Incoming call from {fromDisplayName}",
                $"{roomName} is ready to join.",
                pushData,
                cancellationToken);

            if (pushResult.DeliveredCount == 0)
            {
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
        }

        await _callLogs.CreateAsync(
            invitation.InvitationId,
            callerId,
            target.Id,
            invitation.RoomName,
            invitation.RoomUrl,
            cancellationToken);

        if (deliveredToTray)
        {
            await _hub.Clients
                .Group(CallInvitationHub.UserGroup(target.Id))
                .SendAsync("IncomingCall", invitation, cancellationToken);

            await _pushNotifications.SendAsync(
                target.Id,
                $"Incoming call from {fromDisplayName}",
                $"{roomName} is ready to join.",
                pushData,
                cancellationToken);
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

        await _callLogs.ExpirePendingInvitationsAsync(cancellationToken);
        var log = await _db.CallLogs
            .AsNoTracking()
            .SingleOrDefaultAsync(
            candidate => candidate.InvitationId == invitationId && candidate.CallerId == callerId,
            cancellationToken);
        if (log is null)
        {
            return false;
        }

        var endedAtUtc = DateTime.UtcNow;
        var updated = await _db.CallLogs
            .Where(candidate => candidate.InvitationId == invitationId &&
                                candidate.CallerId == callerId &&
                                candidate.Status == CallLogStatuses.Ringing)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.Status, CallLogStatuses.Cancelled)
                    .SetProperty(candidate => candidate.EndedAtUtc, endedAtUtc),
                cancellationToken);
        if (updated == 0)
        {
            return false;
        }

        await _hub.Clients
            .Group(CallInvitationHub.UserGroup(log.RecipientId))
            .SendAsync("CallCancelled", invitationId, cancellationToken);

        await _statusNotifier.NotifyAsync(log.CallerId, invitationId, CallLogStatuses.Cancelled);

        await _pushNotifications.SendCancelAsync(log.RecipientId, invitationId, cancellationToken);
        return true;
    }

    public async Task<bool> DeclineWithActionTokenAsync(
        Guid invitationId,
        string actionToken,
        CancellationToken cancellationToken = default)
    {
        await _callLogs.ExpirePendingInvitationsAsync(cancellationToken);
        var log = await _db.CallLogs
            .AsNoTracking()
            .SingleOrDefaultAsync(
            candidate => candidate.InvitationId == invitationId,
            cancellationToken);
        if (log is null || !ValidateDeclineActionToken(actionToken, invitationId, log.RecipientId))
        {
            return false;
        }

        var endedAtUtc = DateTime.UtcNow;
        var updated = await _db.CallLogs
            .Where(candidate => candidate.InvitationId == invitationId &&
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

    private static string CreateDeclineEndpoint(string roomUrl, Guid invitationId)
    {
        if (!Uri.TryCreate(roomUrl, UriKind.Absolute, out var roomUri))
        {
            throw new InvalidOperationException("The invitation room URL is invalid.");
        }

        var roomPath = roomUri.AbsolutePath;
        var roomsMarkerIndex = roomPath.IndexOf("/rooms/", StringComparison.OrdinalIgnoreCase);
        var applicationPath = roomsMarkerIndex >= 0 ? roomPath[..roomsMarkerIndex] : string.Empty;
        var builder = new UriBuilder(roomUri)
        {
            Path = $"{applicationPath.TrimEnd('/')}/api/call-invitations/{invitationId:D}/decline-native",
            Query = string.Empty
        };

        return builder.Uri.AbsoluteUri;
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
