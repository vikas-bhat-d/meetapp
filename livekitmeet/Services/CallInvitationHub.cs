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

public sealed record CallInvitationStatusUpdate(Guid InvitationId, string Status);

public sealed class CallInvitationStatusNotifier
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Func<CallInvitationStatusUpdate, Task>>> _subscriptions = new();

    public IDisposable Subscribe(Guid userId, Func<CallInvitationStatusUpdate, Task> handler)
    {
        var userSubscriptions = _subscriptions.GetOrAdd(
            userId,
            _ => new ConcurrentDictionary<Guid, Func<CallInvitationStatusUpdate, Task>>());
        var subscriptionId = Guid.NewGuid();
        userSubscriptions[subscriptionId] = handler;
        return new Subscription(() => Unsubscribe(userId, subscriptionId));
    }

    public async Task NotifyAsync(Guid userId, Guid invitationId, string status)
    {
        if (!_subscriptions.TryGetValue(userId, out var userSubscriptions))
        {
            return;
        }

        var update = new CallInvitationStatusUpdate(invitationId, status);
        foreach (var handler in userSubscriptions.Values.ToArray())
        {
            try
            {
                await handler(update);
            }
            catch
            {
            }
        }
    }

    private void Unsubscribe(Guid userId, Guid subscriptionId)
    {
        if (!_subscriptions.TryGetValue(userId, out var userSubscriptions))
        {
            return;
        }

        userSubscriptions.TryRemove(subscriptionId, out _);
        if (userSubscriptions.IsEmpty)
        {
            _subscriptions.TryRemove(
                new KeyValuePair<Guid, ConcurrentDictionary<Guid, Func<CallInvitationStatusUpdate, Task>>>(userId, userSubscriptions));
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly Action _dispose;
        private int _disposed;

        public Subscription(Action dispose)
        {
            _dispose = dispose;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _dispose();
            }
        }
    }
}

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
