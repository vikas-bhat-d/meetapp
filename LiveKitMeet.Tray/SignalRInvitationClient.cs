using Microsoft.AspNetCore.SignalR.Client;
using System.Net;

namespace LiveKitMeet.Tray;

public sealed class SignalRInvitationClient : IAsyncDisposable
{
    private readonly AuthClient _authClient;
    private readonly string _serverUrl;
    private string _refreshToken;
    private readonly Func<CallInvitationMessage, Task> _onInvitation;
    private readonly Action<Guid, string> _onInvitationClosed;
    private readonly Action<string> _onStatusChanged;
    private readonly Action<string> _onRefreshTokenChanged;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private HubConnection? _connection;
    private string _accessToken;
    private DateTime _accessTokenExpiresAtUtc;
    private bool _disposed;

    public SignalRInvitationClient(
        AuthClient authClient,
        string serverUrl,
        AuthTokenResponse tokens,
        Func<CallInvitationMessage, Task> onInvitation,
        Action<Guid, string> onInvitationClosed,
        Action<string> onStatusChanged,
        Action<string> onRefreshTokenChanged)
    {
        _authClient = authClient;
        _serverUrl = AuthClient.NormalizeServerUrl(serverUrl);
        _accessToken = tokens.AccessToken;
        _accessTokenExpiresAtUtc = tokens.AccessTokenExpiresAtUtc;
        _refreshToken = tokens.RefreshToken;
        _onInvitation = onInvitation;
        _onInvitationClosed = onInvitationClosed;
        _onStatusChanged = onStatusChanged;
        _onRefreshTokenChanged = onRefreshTokenChanged;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);
        var retryCount = 0;

        _connection = new HubConnectionBuilder()
            .WithUrl($"{_serverUrl}/hubs/call-invitations", options =>
            {
                options.AccessTokenProvider = GetAccessTokenAsync;
            })
            .WithAutomaticReconnect(new InfiniteRetryPolicy())
            .Build();

        _connection.On<CallInvitationMessage>("IncomingCall", invitation =>
        {
            _ = _onInvitation(invitation);
        });
        foreach (var eventName in new[] { "CallAnswered", "CallDeclined", "CallCancelled", "CallExpired" })
        {
            _connection.On<Guid>(eventName, invitationId => _onInvitationClosed(invitationId, eventName));
        }
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
            if (!_disposed)
            {
                _onStatusChanged(error is null ? "Disconnected" : "Connection error");
            }

            return Task.CompletedTask;
        };

        while (!_disposed && !linkedCancellation.IsCancellationRequested)
        {
            try
            {
                _onStatusChanged("Connecting...");
                await _connection.StartAsync(linkedCancellation.Token).ConfigureAwait(false);
                _onStatusChanged("Connected");
                return;
            }
            catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (AuthRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw;
            }
            catch (Exception ex)
            {
                TrayDiagnosticLog.Write($"SignalR start failed attempt={retryCount + 1} error={ex.Message}");
                retryCount++;
                _onStatusChanged("Waiting for server...");
                await Task.Delay(GetInitialRetryDelay(retryCount), linkedCancellation.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task<string?> GetAccessTokenAsync()
    {
        if (_disposed)
        {
            return null;
        }

        await _tokenLock.WaitAsync(_lifetimeCts.Token).ConfigureAwait(false);
        try
        {
            if (_accessTokenExpiresAtUtc > DateTime.UtcNow.AddMinutes(1))
            {
                return _accessToken;
            }

            var refreshed = await _authClient.RefreshAsync(_serverUrl, _refreshToken, _lifetimeCts.Token)
                .ConfigureAwait(false);
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

    public async Task<bool> ReportCallOutcomeAsync(Guid invitationId, string outcome)
    {
        var accessToken = await GetAccessTokenAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return false;
        }

        return await _authClient.ReportCallOutcomeAsync(
            _serverUrl,
            accessToken,
            invitationId,
            outcome,
            _lifetimeCts.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetimeCts.Cancel();
        if (_connection is not null)
        {
            try
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _lifetimeCts.Dispose();
        _tokenLock.Dispose();
    }

    private static TimeSpan GetInitialRetryDelay(int retryCount) => retryCount switch
    {
        1 => TimeSpan.Zero,
        2 => TimeSpan.FromSeconds(2),
        3 => TimeSpan.FromSeconds(5),
        4 => TimeSpan.FromSeconds(10),
        _ => TimeSpan.FromSeconds(30)
    };

    private sealed class InfiniteRetryPolicy : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext) => retryContext.PreviousRetryCount switch
        {
            0 => TimeSpan.Zero,
            1 => TimeSpan.FromSeconds(2),
            2 => TimeSpan.FromSeconds(10),
            3 => TimeSpan.FromSeconds(30),
            _ => TimeSpan.FromSeconds(60)
        };
    }
}
