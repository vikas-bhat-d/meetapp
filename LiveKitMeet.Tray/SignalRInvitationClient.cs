using Microsoft.AspNetCore.SignalR.Client;

namespace LiveKitMeet.Tray;

public sealed class SignalRInvitationClient : IAsyncDisposable
{
    private readonly AuthClient _authClient;
    private readonly string _serverUrl;
    private string _refreshToken;
    private readonly Func<CallInvitationMessage, Task> _onInvitation;
    private readonly Action<string> _onStatusChanged;
    private readonly Action<string> _onRefreshTokenChanged;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private HubConnection? _connection;
    private string _accessToken;
    private DateTime _accessTokenExpiresAtUtc;
    private bool _disposed;

    public SignalRInvitationClient(
        AuthClient authClient,
        string serverUrl,
        AuthTokenResponse tokens,
        Func<CallInvitationMessage, Task> onInvitation,
        Action<string> onStatusChanged,
        Action<string> onRefreshTokenChanged)
    {
        _authClient = authClient;
        _serverUrl = AuthClient.NormalizeServerUrl(serverUrl);
        _accessToken = tokens.AccessToken;
        _accessTokenExpiresAtUtc = tokens.AccessTokenExpiresAtUtc;
        _refreshToken = tokens.RefreshToken;
        _onInvitation = onInvitation;
        _onStatusChanged = onStatusChanged;
        _onRefreshTokenChanged = onRefreshTokenChanged;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl($"{_serverUrl}/hubs/call-invitations", options =>
            {
                options.AccessTokenProvider = GetAccessTokenAsync;
            })
            .WithAutomaticReconnect(new[]
            {
                TimeSpan.Zero,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30)
            })
            .Build();

        _connection.On<CallInvitationMessage>("IncomingCall", invitation =>
        {
            _ = _onInvitation(invitation);
        });
        _connection.Reconnecting += error =>
        {
            _onStatusChanged("Reconnecting...");
            return Task.CompletedTask;
        };
        _connection.Reconnected += connectionId =>
        {
            _onStatusChanged("Connected");
            return Task.CompletedTask;
        };
        _connection.Closed += error =>
        {
            _onStatusChanged(error is null ? "Disconnected" : "Connection error");
            return Task.CompletedTask;
        };

        _onStatusChanged("Connecting...");
        await _connection.StartAsync(cancellationToken);
        _onStatusChanged("Connected");
    }

    private async Task<string?> GetAccessTokenAsync()
    {
        if (_disposed)
        {
            return null;
        }

        await _tokenLock.WaitAsync();
        try
        {
            if (_accessTokenExpiresAtUtc > DateTime.UtcNow.AddMinutes(1))
            {
                return _accessToken;
            }

            var refreshed = await _authClient.RefreshAsync(_serverUrl, _refreshToken);
            _accessToken = refreshed.AccessToken;
            _accessTokenExpiresAtUtc = refreshed.AccessTokenExpiresAtUtc;
            _refreshToken = refreshed.RefreshToken;
            _onRefreshTokenChanged(_refreshToken);
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _tokenLock.Dispose();
    }
}
