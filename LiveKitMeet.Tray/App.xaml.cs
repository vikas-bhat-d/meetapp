using System.Windows;
using System.Windows.Threading;

namespace LiveKitMeet.Tray;

public partial class App : Application
{
    private TrayApplicationContext? _context;
    private TrayUrlActivationChannel? _activationChannel;
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private string? _pendingActivationUrl;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        base.OnStartup(e);

        var launchUrl = GetLaunchUrl(e.Args);
        _singleInstanceMutex = new Mutex(true, "Local\\WinCall", out var createdNew);
        if (!createdNew)
        {
            if (launchUrl is not null && !TrayUrlActivationChannel.TryForward(launchUrl))
            {
                TrayDiagnosticLog.Write($"Could not forward launch URL to the running tray instance url={launchUrl}");
            }

            Shutdown();
            return;
        }

        _ownsSingleInstanceMutex = true;
        _activationChannel = new TrayUrlActivationChannel(HandleActivationUrl);
        _activationChannel.Start();
        _context = new TrayApplicationContext(launchUrl);
        if (_pendingActivationUrl is not null)
        {
            _context.OpenUrl(_pendingActivationUrl);
            _pendingActivationUrl = null;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationChannel?.Dispose();
        _context?.Dispose();
        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }

        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(
        object? sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        TrayDiagnosticLog.Write($"Unhandled dispatcher exception type={e.Exception.GetType().FullName} error={e.Exception}");
        e.Handled = true;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            TrayDiagnosticLog.Write($"Unhandled application exception type={exception.GetType().FullName} error={exception}");
        }
    }

    private void HandleActivationUrl(string url)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_context is null)
            {
                _pendingActivationUrl = url;
                return;
            }

            _context.OpenUrl(url);
        }));
    }

    private static string? GetLaunchUrl(IEnumerable<string> args)
    {
        foreach (var argument in args)
        {
            if (Uri.TryCreate(argument, UriKind.Absolute, out var uri) &&
                uri.Scheme is "http" or "https")
            {
                return uri.ToString();
            }
        }

        return null;
    }
}
