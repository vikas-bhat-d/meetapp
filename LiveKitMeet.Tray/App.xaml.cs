using System.Windows;
using System.Windows.Threading;

namespace LiveKitMeet.Tray;

public partial class App : Application
{
    private TrayApplicationContext? _context;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        base.OnStartup(e);
        _context = new TrayApplicationContext();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _context?.Dispose();
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
