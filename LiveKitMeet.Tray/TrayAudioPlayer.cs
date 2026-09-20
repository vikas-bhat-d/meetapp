using System.IO;
using System.Windows.Media;

namespace LiveKitMeet.Tray;

internal sealed class TrayAudioPlayer : IDisposable
{
    private readonly TraySettings _settings;
    private readonly MediaPlayer _player = new();
    private readonly HashSet<Guid> _ringingInvitations = new();
    private string? _currentPath;
    private bool _currentPlaybackLoops;
    private bool _playWhenOpened;
    private bool _disposed;

    public TrayAudioPlayer(TraySettings settings)
    {
        _settings = settings;
        _player.MediaOpened += HandleMediaOpened;
        _player.MediaEnded += HandleMediaEnded;
        _player.MediaFailed += HandleMediaFailed;
    }

    public void StartRingtone(Guid invitationId)
    {
        if (_disposed || !_ringingInvitations.Add(invitationId))
        {
            return;
        }

        if (_ringingInvitations.Count == 1)
        {
            StartRingtonePlayback();
        }
    }

    public void StopRingtone(Guid invitationId)
    {
        if (_disposed || !_ringingInvitations.Remove(invitationId))
        {
            return;
        }

        if (_ringingInvitations.Count == 0)
        {
            StopPlayback();
        }
    }

    public void PlayAcceptSound()
    {
        PlayOneShot(_settings.AcceptSoundPath, "accept");
    }

    public void PlayDeclineSound()
    {
        PlayOneShot(_settings.DeclineSoundPath, "decline");
    }

    public void StopAll()
    {
        if (_disposed)
        {
            return;
        }

        _ringingInvitations.Clear();
        StopPlayback();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ringingInvitations.Clear();
        StopPlayback();
        _player.MediaOpened -= HandleMediaOpened;
        _player.MediaEnded -= HandleMediaEnded;
        _player.MediaFailed -= HandleMediaFailed;
    }

    private void PlayOneShot(string? configuredPath, string soundName)
    {
        if (_disposed)
        {
            return;
        }

        StopPlayback();
        var path = ResolvePath(configuredPath, soundName);
        if (path is null)
        {
            if (_ringingInvitations.Count > 0)
            {
                StartRingtonePlayback();
            }

            return;
        }

        StartPlayback(path, loops: false);
    }

    private void StartRingtonePlayback()
    {
        var path = ResolvePath(_settings.RingtonePath, "ringtone");
        if (path is not null)
        {
            StartPlayback(path, loops: true);
        }
    }

    private void StartPlayback(string path, bool loops)
    {
        StopPlayback();
        _currentPath = path;
        _currentPlaybackLoops = loops;
        _playWhenOpened = true;

        try
        {
            _player.Open(new Uri(path, UriKind.Absolute));
        }
        catch (Exception ex)
        {
            TrayDiagnosticLog.Write($"Audio playback could not open path={path} error={ex.Message}");
            StopPlayback();
        }
    }

    private string? ResolvePath(string? configuredPath, string soundName)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return null;
        }

        var path = configuredPath.Trim();
        if (!Path.IsPathRooted(path))
        {
            path = Path.Combine(AppContext.BaseDirectory, path);
        }

        path = Path.GetFullPath(path);
        if (File.Exists(path))
        {
            return path;
        }

        TrayDiagnosticLog.Write($"Audio file not found type={soundName} path={path}");
        return null;
    }

    private void HandleMediaOpened(object? sender, EventArgs e)
    {
        if (_disposed || !_playWhenOpened)
        {
            return;
        }

        _playWhenOpened = false;
        _player.Position = TimeSpan.Zero;
        _player.Play();
    }

    private void HandleMediaEnded(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (_currentPlaybackLoops && _ringingInvitations.Count > 0)
        {
            _player.Position = TimeSpan.Zero;
            _player.Play();
            return;
        }

        if (_ringingInvitations.Count > 0)
        {
            StartRingtonePlayback();
            return;
        }

        StopPlayback();
    }

    private void HandleMediaFailed(object? sender, ExceptionEventArgs e)
    {
        TrayDiagnosticLog.Write(
            $"Audio playback failed path={_currentPath} error={e.ErrorException?.Message ?? "Unknown error"}");
        StopPlayback();
    }

    private void StopPlayback()
    {
        _playWhenOpened = false;
        _currentPlaybackLoops = false;
        _currentPath = null;
        _player.Stop();
        _player.Close();
    }
}