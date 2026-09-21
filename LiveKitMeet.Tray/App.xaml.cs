using System.Windows;
using System.Windows.Threading;

namespace LiveKitMeet.Tray;

public partial class App : Application
{
    private TrayApplicationContext? _context;
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, "Local\\WinCall", out var createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        _ownsSingleInstanceMutex = true;
        _context = new TrayApplicationContext();
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
}
