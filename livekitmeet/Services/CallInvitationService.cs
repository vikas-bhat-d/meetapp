using System.Security.Claims;
using livekitmeet.Data;
using Microsoft.AspNetCore.SignalR;
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
        string targetUserName,
        Guid invitationId,
        CancellationToken cancellationToken = default);
}

public sealed class CallInvitationService : ICallInvitationService
{
    private readonly AppDbContext _db;
    private readonly IHubContext<CallInvitationHub> _hub;
    private readonly CallInvitationConnectionTracker _tracker;
    private readonly IFirebasePushNotificationService _pushNotifications;

    public CallInvitationService(
        AppDbContext db,
        IHubContext<CallInvitationHub> hub,
        CallInvitationConnectionTracker tracker,
        IFirebasePushNotificationService pushNotifications)
    {
        _db = db;
        _hub = hub;
        _tracker = tracker;
        _pushNotifications = pushNotifications;
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
        var fromUserName = caller.FindFirstValue(ClaimTypes.Name) ?? "User";
        var fromDisplayName = caller.FindFirst("display_name")?.Value ?? fromUserName;
        var invitation = new CallInvitationMessage(
            Guid.NewGuid(),
            roomName,
            roomUrl,
            fromUserName,
            fromDisplayName,
            audioEnabled,
            videoEnabled,
            DateTime.UtcNow.AddMinutes(2));

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
                ["videoEnabled"] = videoEnabled.ToString().ToLowerInvariant()
            },
            cancellationToken);

        if (!deliveredToTray && pushResult.DeliveredCount == 0)
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

        return new CallInvitationResult(true, null, invitation.InvitationId);
    }

    public async Task<bool> CancelAsync(
        ClaimsPrincipal caller,
        string targetUserName,
        Guid invitationId,
        CancellationToken cancellationToken = default)
    {
        targetUserName = targetUserName.Trim();
        if (string.IsNullOrWhiteSpace(targetUserName))
        {
            return false;
        }

        var target = await _db.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(
                user => user.NormalizedUserName == UserNameNormalizer.Normalize(targetUserName) && user.IsActive,
                cancellationToken);
        if (target is null)
        {
            return false;
        }

        await _hub.Clients
            .Group(CallInvitationHub.UserGroup(target.Id))
            .SendAsync("CallCancelled", invitationId, cancellationToken);

        await _pushNotifications.SendCancelAsync(target.Id, invitationId, cancellationToken);
        return true;
    }
}
