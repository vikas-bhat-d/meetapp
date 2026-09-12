using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace livekitmeet.Services;

public sealed record CallInvitationMessage(
    Guid InvitationId,
    string RoomName,
    string RoomUrl,
    string FromUserName,
    string FromDisplayName,
    bool AudioEnabled,
    bool VideoEnabled,
    DateTime ExpiresAtUtc);

public sealed class CallInvitationConnectionTracker
{
    private readonly ConcurrentDictionary<Guid, int> _connections = new();

    public void Connected(Guid userId) => _connections.AddOrUpdate(userId, 1, (_, count) => count + 1);

    public void Disconnected(Guid userId)
    {
        while (_connections.TryGetValue(userId, out var count))
        {
            if (count <= 1)
            {
                if (_connections.TryRemove(new KeyValuePair<Guid, int>(userId, count)))
                {
                    return;
                }

                continue;
            }

            if (_connections.TryUpdate(userId, count - 1, count))
            {
                return;
            }
        }
    }

    public bool IsConnected(Guid userId) => _connections.ContainsKey(userId);
}

[Authorize]
public sealed class CallInvitationHub : Hub
{
    private readonly CallInvitationConnectionTracker _tracker;

    public CallInvitationHub(CallInvitationConnectionTracker tracker)
    {
        _tracker = tracker;
    }

    public static string UserGroup(Guid userId) => $"call-invitations:user:{userId:N}";

    public override async Task OnConnectedAsync()
    {
        if (TryGetUserId(Context.User, out var userId))
        {
            _tracker.Connected(userId);
            await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userId));
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (TryGetUserId(Context.User, out var userId))
        {
            _tracker.Disconnected(userId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    private static bool TryGetUserId(ClaimsPrincipal? user, out Guid userId)
    {
        var value = user?.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out userId);
    }
}
