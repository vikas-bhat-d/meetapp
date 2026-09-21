using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using H.NotifyIcon;

namespace LiveKitMeet.Tray;

public sealed class TrayApplicationContext : IDisposable
{
    private readonly TaskbarIcon _taskbarIcon;
    private readonly MenuItem _statusItem;
    private readonly MenuItem _signInItem;
    private readonly MenuItem _signOutItem;
    private readonly TraySettings _settings;
    private readonly AuthClient _authClient = new();
    private readonly TrayAudioPlayer _audioPlayer;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly SemaphoreSlim _webSessionLock = new(1, 1);
    private readonly Dictionary<Guid, IncomingCallWindow> _incomingCallWindows = new();
    private SignalRInvitationClient? _invitationClient;
    private MeetWindow? _meetWindow;
    private string? _webSessionFingerprint;
    private Task? _savedSessionTask;
    private Task? _webSessionTask;
    private bool _exitStarted;
    private bool _disposed;

    public TrayApplicationContext()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _settings = TraySettings.Load();
        _audioPlayer = new TrayAudioPlayer(_settings);

        _statusItem = new MenuItem { Header = "Not connected", IsEnabled = false };
        var openMeetItem = new MenuItem { Header = "Open Meet" };
        openMeetItem.Click += (_, _) => ShowLogin();
        _signInItem = new MenuItem { Header = "Sign in..." };
        _signInItem.Click += (_, _) => ShowLogin();
        _signOutItem = new MenuItem { Header = "Sign out", IsEnabled = false };
        _signOutItem.Click += (_, _) => _ = SignOutAsync();
        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => _ = ExitAsync();

        var menu = new ContextMenu();
        menu.Items.Add(_statusItem);
        menu.Items.Add(openMeetItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_signInItem);
        menu.Items.Add(_signOutItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);

        _taskbarIcon = new TaskbarIcon
        {
            IconSource = CreateTrayIcon(),
            Visibility = System.Windows.Visibility.Visible,
            ToolTipText = "LiveKit Meet - Not connected",
            ContextMenu = menu
        };
        _taskbarIcon.TrayMouseDoubleClick += (_, _) => ShowLogin();
        _taskbarIcon.ForceCreate(enablesEfficiencyMode: false);
        TrayDiagnosticLog.Write(
            $"Tray icon created isCreated={_taskbarIcon.TrayIcon.IsCreated} visibility={_taskbarIcon.TrayIcon.Visibility}");

        if (string.IsNullOrWhiteSpace(_settings.GetRefreshToken()))
        {
            SetStatus("Sign in required", false);
        }
        else
        {
            _savedSessionTask = ConnectSavedSessionAsync();
        }
    }

    private void ShowLogin()
    {
        if (_exitStarted)
        {
            return;
        }

        ShowMeetWindow(_settings.ServerUrl);
    }

    private MeetWindow EnsureMeetWindow()
    {
        if (_meetWindow is not null)
        {
            return _meetWindow;
        }

        _meetWindow = new MeetWindow(_settings.ServerUrl);
        _meetWindow.WebSessionChanged += HandleWebSessionChanged;
        _meetWindow.Closed += (_, _) => _meetWindow = null;
        return _meetWindow;
    }

    private void ShowMeetWindow(string url)
    {
        var window = EnsureMeetWindow();
        if (!window.IsVisible)
        {
            window.Show();
        }

        window.NavigateTo(url);
        window.Activate();
    }

    private void HandleWebSessionChanged(object? sender, WebSessionChangedEventArgs args)
    {
        if (_exitStarted)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(args.RefreshToken))
        {
            if (_invitationClient is null && !_exitStarted)
            {
                _webSessionFingerprint = null;
                SetSignInState(false);
                SetStatus("Sign in required", false);
            }

            return;
        }

        _webSessionTask = ConnectWebSessionAsync(args.RefreshToken);
    }

    private async Task ConnectWebSessionAsync(string refreshToken)
    {
        var lockHeld = false;
        try
        {
            await _webSessionLock.WaitAsync(_lifetimeCts.Token).ConfigureAwait(false);
            lockHeld = true;
            var fingerprint = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
            if (string.Equals(fingerprint, _webSessionFingerprint, StringComparison.Ordinal))
            {
                return;
            }

            var retryCount = 0;
            while (!_lifetimeCts.IsCancellationRequested)
            {
                try
                {
                    SetStatus("Signing in...", false);
                    var tokens = await _authClient.ExchangeWebSessionAsync(
                        _settings.ServerUrl,
                        refreshToken,
                        _lifetimeCts.Token).ConfigureAwait(false);
                    _settings.SetRefreshToken(tokens.RefreshToken);
                    _settings.Save();
                    _webSessionFingerprint = fingerprint;
                    await ConnectAsync(tokens).ConfigureAwait(false);
                    return;
                }
                catch (AuthRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    SetSignInState(false);
                    SetStatus("Sign in required", false);
                    return;
                }
                catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    retryCount++;
                    TrayDiagnosticLog.Write($"Web session exchange failed attempt={retryCount} error={ex.Message}");
                    SetStatus("Waiting for server...", false);
                    if (!await WaitForRetryAsync(retryCount).ConfigureAwait(false))
                    {
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        finally
        {
            if (lockHeld)
            {
                _webSessionLock.Release();
            }
        }
    }

    private async Task ConnectSavedSessionAsync()
    {
        var retryCount = 0;
        while (!_lifetimeCts.IsCancellationRequested)
        {
            try
            {
                var refreshToken = _settings.GetRefreshToken();
                if (string.IsNullOrWhiteSpace(refreshToken))
                {
                    SetSignInState(false);
                    return;
                }

                var tokens = await _authClient.RefreshAsync(
                    _settings.ServerUrl,
                    refreshToken,
                    _lifetimeCts.Token).ConfigureAwait(false);
                _settings.SetRefreshToken(tokens.RefreshToken);
                _settings.Save();
                await ConnectAsync(tokens).ConfigureAwait(false);
                return;
            }
            catch (AuthRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _settings.SetRefreshToken(null);
                _settings.Save();
                SetSignInState(false);
                SetStatus("Sign in required", false);
                return;
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                retryCount++;
                TrayDiagnosticLog.Write($"Saved session refresh failed attempt={retryCount} error={ex.Message}");
                SetSignInState(true);
                SetStatus("Waiting for server...", false);
                if (!await WaitForRetryAsync(retryCount).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
    }

    private async Task ConnectAsync(AuthTokenResponse tokens)
    {
        await DisconnectAsync().ConfigureAwait(false);
        if (_lifetimeCts.IsCancellationRequested)
        {
            return;
        }

        var client = new SignalRInvitationClient(
            _authClient,
            _settings.ServerUrl,
            tokens,
            HandleInvitationAsync,
            HandleInvitationClosed,
            status => SetStatus(status, status is "Connected"),
            refreshToken =>
            {
                _settings.SetRefreshToken(refreshToken);
                _settings.Save();
            });
        _invitationClient = client;
        SetSignInState(true);
        var connectionStarted = false;

        try
        {
            await client.StartAsync(_lifetimeCts.Token).ConfigureAwait(false);
            connectionStarted = true;
            SetStatus("Connected", true);
        }
        catch (AuthRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            _settings.SetRefreshToken(null);
            _settings.Save();
            SetSignInState(false);
            SetStatus("Sign in required", false);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            TrayDiagnosticLog.Write($"SignalR connection failed error={ex.Message}");
            SetStatus("Connection failed", false);
        }
        finally
        {
            if (!connectionStarted && ReferenceEquals(_invitationClient, client))
            {
                Interlocked.CompareExchange(ref _invitationClient, null, client);
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private Task HandleInvitationAsync(CallInvitationMessage invitation)
    {
        if (invitation.ExpiresAtUtc <= DateTime.UtcNow || _exitStarted)
        {
            return Task.CompletedTask;
        }

        _dispatcher.BeginInvoke(new Action(() => _ = ShowIncomingCallAsync(invitation)));
        return Task.CompletedTask;
    }

    private async Task ShowIncomingCallAsync(CallInvitationMessage invitation)
    {
        TrayDiagnosticLog.Write($"Incoming invitation shown invitation={invitation.InvitationId:D} room={invitation.RoomName}");

        var window = new IncomingCallWindow(invitation);
        _incomingCallWindows[invitation.InvitationId] = window;
        _audioPlayer.StartRingtone(invitation.InvitationId);
        try
        {
            bool accepted;
            try
            {
                accepted = window.ShowDialog() == true;
            }
            finally
            {
                _audioPlayer.StopRingtone(invitation.InvitationId);
            }

            if (accepted)
            {
                TrayDiagnosticLog.Write($"Accept selected invitation={invitation.InvitationId:D}");
                _audioPlayer.PlayAcceptSound();
                if (await ReportCallOutcomeAsync(invitation, "accept"))
                {
                    TrayDiagnosticLog.Write($"Accept succeeded; opening room invitation={invitation.InvitationId:D} url={invitation.RoomUrl}");
                    ShowMeetWindow(invitation.RoomUrl);
                }
                else
                {
                    TrayDiagnosticLog.Write($"Accept failed invitation={invitation.InvitationId:D}");
                    MessageBox.Show(
                        "This invitation is no longer available.",
                        "LiveKit Meet",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            else if (!_exitStarted && !window.ClosedByRemoteStatus)
            {
                TrayDiagnosticLog.Write($"Decline selected invitation={invitation.InvitationId:D}");
                _audioPlayer.PlayDeclineSound();
                await ReportCallOutcomeAsync(invitation, "decline");
            }
        }
        finally
        {
            _incomingCallWindows.Remove(invitation.InvitationId);
        }
    }

    private void HandleInvitationClosed(Guid invitationId, string status)
    {
        if (_exitStarted)
        {
            return;
        }

        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (_incomingCallWindows.TryGetValue(invitationId, out var window) && window.IsVisible)
            {
                window.CloseByRemoteStatus();
            }
        }));
    }

    private async Task<bool> ReportCallOutcomeAsync(CallInvitationMessage invitation, string outcome)
    {
        try
        {
            if (_invitationClient is not null)
            {
                return await _invitationClient.ReportCallOutcomeAsync(invitation.InvitationId, outcome)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            TrayDiagnosticLog.Write($"Outcome request threw invitation={invitation.InvitationId:D} outcome={outcome} error={ex.Message}");
        }

        return false;
    }

    private async Task SignOutAsync()
    {
        if (_exitStarted)
        {
            return;
        }

        await DisconnectAsync();
        _settings.SetRefreshToken(null);
        _settings.Save();
        _webSessionFingerprint = null;
        SetSignInState(false);
        SetStatus("Not connected", false);
        if (_meetWindow is not null)
        {
            await _meetWindow.ClearSessionAsync();
        }
    }

    private async Task DisconnectAsync()
    {
        var client = Interlocked.Exchange(ref _invitationClient, null);
        if (client is not null)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void SetStatus(string status, bool connected)
    {
        if (_exitStarted)
        {
            return;
        }

        void Update()
        {
            if (_exitStarted)
            {
                return;
            }

            _statusItem.Header = status;
            _taskbarIcon.ToolTipText = $"LiveKit Meet - {status}";
            if (connected)
            {
                _signOutItem.IsEnabled = true;
            }
        }

        if (_dispatcher.CheckAccess())
        {
            Update();
        }
        else
        {
            _dispatcher.BeginInvoke(Update);
        }
    }

    private void SetSignInState(bool signedIn)
    {
        if (_exitStarted)
        {
            return;
        }

        void Update()
        {
            if (_exitStarted)
            {
                return;
            }

            _signInItem.IsEnabled = !signedIn;
            _signOutItem.IsEnabled = signedIn;
        }

        if (_dispatcher.CheckAccess())
        {
            Update();
        }
        else
        {
            _dispatcher.BeginInvoke(Update);
        }
    }

    private async Task ExitAsync()
    {
        if (_exitStarted)
        {
            return;
        }

        _exitStarted = true;
        _taskbarIcon.Visibility = Visibility.Hidden;
        _lifetimeCts.Cancel();
        _audioPlayer.StopAll();
        foreach (var window in _incomingCallWindows.Values.ToArray())
        {
            window.CloseForApplicationExit();
        }

        _meetWindow?.CloseForApplicationExit();
        try
        {
            await DisconnectAsync();
        }
        catch (Exception ex)
        {
            TrayDiagnosticLog.Write($"Shutdown connection disposal failed error={ex.Message}");
        }

        var backgroundTasks = new[] { _savedSessionTask, _webSessionTask }
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();
        if (backgroundTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(backgroundTasks);
            }
            catch (Exception ex)
            {
                TrayDiagnosticLog.Write($"Shutdown background task failed error={ex.Message}");
            }
        }

        _authClient.Dispose();
        _audioPlayer.Dispose();
        _webSessionLock.Dispose();
        _taskbarIcon.Dispose();
        _lifetimeCts.Dispose();
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_exitStarted)
        {
            _lifetimeCts.Cancel();
            _taskbarIcon.Dispose();
            _audioPlayer.Dispose();
            _authClient.Dispose();
            _webSessionLock.Dispose();
            _lifetimeCts.Dispose();
        }
    }

    private static ImageSource CreateTrayIcon()
    {
        return new BitmapImage(new Uri("pack://application:,,,/assets/tray-icon.ico", UriKind.Absolute));
    }

    private static TimeSpan GetRetryDelay(int retryCount) => retryCount switch
    {
        1 => TimeSpan.Zero,
        2 => TimeSpan.FromSeconds(2),
        3 => TimeSpan.FromSeconds(5),
        4 => TimeSpan.FromSeconds(10),
        _ => TimeSpan.FromSeconds(30)
    };

    private async Task<bool> WaitForRetryAsync(int retryCount)
    {
        try
        {
            await Task.Delay(GetRetryDelay(retryCount), _lifetimeCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            return false;
        }
    }
}
