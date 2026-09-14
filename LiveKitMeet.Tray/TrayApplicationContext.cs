using System.Diagnostics;
using System.Media;

namespace LiveKitMeet.Tray;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _openMeetItem;
    private readonly ToolStripMenuItem _signInItem;
    private readonly ToolStripMenuItem _signOutItem;
    private readonly TraySettings _settings;
    private readonly AuthClient _authClient = new();
    private readonly SynchronizationContext _uiContext;
    private SignalRInvitationClient? _invitationClient;

    public TrayApplicationContext()
    {
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _settings = TraySettings.Load();

        _statusItem = new ToolStripMenuItem("Not connected") { Enabled = false };
        _openMeetItem = new ToolStripMenuItem("Open Meet", null, (_, _) => ShowMeetBrowser());
        _signInItem = new ToolStripMenuItem("Sign in...", null, (_, _) => ShowLogin());
        _signOutItem = new ToolStripMenuItem("Sign out", null, (_, _) => SignOut()) { Enabled = false };
        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => ExitThread());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(_openMeetItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_signInItem);
        menu.Items.Add(_signOutItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "LiveKit Meet",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => ShowMeetBrowser();

        if (!string.IsNullOrWhiteSpace(_settings.GetRefreshToken()))
        {
            _ = ConnectSavedSessionAsync();
        }
    }

    private void ShowLogin()
    {
        using var form = new LoginForm(_authClient, _settings.ServerUrl);
        if (form.ShowDialog() != DialogResult.OK || form.Tokens is null)
        {
            return;
        }

        _settings.ServerUrl = form.ServerUrl;
        _settings.SetRefreshToken(form.Tokens.RefreshToken);
        _settings.Save();
        _ = ConnectAsync(form.Tokens);
    }

    private async Task ConnectSavedSessionAsync()
    {
        try
        {
            var refreshToken = _settings.GetRefreshToken();
            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                return;
            }

            var tokens = await _authClient.RefreshAsync(_settings.ServerUrl, refreshToken);
            _settings.SetRefreshToken(tokens.RefreshToken);
            _settings.Save();
            await ConnectAsync(tokens);
        }
        catch (Exception ex)
        {
            _settings.SetRefreshToken(null);
            _settings.Save();
            SetStatus($"Sign in required: {ShortError(ex.Message)}", false);
        }
    }

    private async Task ConnectAsync(AuthTokenResponse tokens)
    {
        await DisconnectAsync();
        try
        {
            _invitationClient = new SignalRInvitationClient(
                _authClient,
                _settings.ServerUrl,
                tokens,
                HandleInvitationAsync,
                status => SetStatus(status, status is "Connected"),
                refreshToken =>
                {
                    _settings.SetRefreshToken(refreshToken);
                    _settings.Save();
                });
            await _invitationClient.StartAsync();
            SetStatus("Connected", true);
            SetSignInState(true);
        }
        catch (Exception ex)
        {
            await DisconnectAsync();
            SetStatus($"Connection failed: {ShortError(ex.Message)}", false);
        }
    }

    private Task HandleInvitationAsync(CallInvitationMessage invitation)
    {
        if (invitation.ExpiresAtUtc <= DateTime.UtcNow)
        {
            return Task.CompletedTask;
        }

        _uiContext.Post(_ => ShowIncomingCall(invitation), null);
        return Task.CompletedTask;
    }

    private void ShowIncomingCall(CallInvitationMessage invitation)
    {
        SystemSounds.Exclamation.Play();
        _notifyIcon.ShowBalloonTip(3000, "Incoming LiveKit call", $"{invitation.FromDisplayName} is calling you.", ToolTipIcon.Info);

        using var form = new IncomingCallForm(invitation);
        if (form.ShowDialog() == DialogResult.OK)
        {
            OpenRoomUrl(invitation.RoomUrl);
        }
    }

    private void OpenRoomUrl(string roomUrl)
    {
        if (!Uri.TryCreate(roomUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            MessageBox.Show("The invitation URL is invalid.", "LiveKit Meet", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        OpenInBrowser(uri.ToString());
    }

    private void ShowMeetBrowser()
    {
        OpenInBrowser(_settings.ServerUrl);
    }

    private static void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"The meeting page could not be opened.\r\n\r\n{ex.Message}",
                "LiveKit Meet",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void SignOut()
    {
        _ = SignOutAsync();
    }

    private async Task SignOutAsync()
    {
        await DisconnectAsync();
        _settings.SetRefreshToken(null);
        _settings.Save();
        SetSignInState(false);
        SetStatus("Not connected", false);
    }

    private async Task DisconnectAsync()
    {
        if (_invitationClient is not null)
        {
            await _invitationClient.DisposeAsync();
            _invitationClient = null;
        }
    }

    private void SetStatus(string status, bool connected)
    {
        _uiContext.Post(_ =>
        {
            _statusItem.Text = status;
            _notifyIcon.Text = status.Length > 63 ? status[..63] : $"LiveKit Meet - {status}";
            _signOutItem.Enabled = connected;
        }, null);
    }

    private void SetSignInState(bool signedIn)
    {
        _uiContext.Post(_ =>
        {
            _signInItem.Enabled = !signedIn;
            _signOutItem.Enabled = signedIn;
        }, null);
    }

    private static string ShortError(string message) => message.Length > 80 ? message[..80] : message;

    protected override void ExitThreadCore()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _invitationClient?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _authClient.Dispose();
        base.ExitThreadCore();
    }
}
