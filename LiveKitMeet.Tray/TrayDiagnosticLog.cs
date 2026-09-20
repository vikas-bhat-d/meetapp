using System.IO;
using System.Text;

namespace LiveKitMeet.Tray;

internal static class TrayDiagnosticLog
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LiveKitMeet",
        "tray.log");

    public static void Write(string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(
                LogPath,
                $"{DateTime.UtcNow:O} {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
        }
    }
}