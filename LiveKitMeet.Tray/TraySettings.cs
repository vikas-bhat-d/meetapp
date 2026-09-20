using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace LiveKitMeet.Tray;

public sealed class TraySettings
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LiveKitMeet");
    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "tray-settings.json");

    public string ServerUrl { get; set; } = "https://192.168.29.214:8443";
    public string? ProtectedRefreshToken { get; set; }
    public string RingtonePath { get; set; } = "./ringtone.mp3";
    public string? AcceptSoundPath { get; set; }
    public string? DeclineSoundPath { get; set; }

    public static TraySettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize<TraySettings>(File.ReadAllText(SettingsPath)) ?? new TraySettings();
            }
        }
        catch
        {
            // A corrupt local settings file is treated as a signed-out state.
        }

        return new TraySettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public string? GetRefreshToken()
    {
        if (string.IsNullOrWhiteSpace(ProtectedRefreshToken))
        {
            return null;
        }

        try
        {
            var encrypted = Convert.FromBase64String(ProtectedRefreshToken);
            var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    public void SetRefreshToken(string? refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            ProtectedRefreshToken = null;
            return;
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(refreshToken);
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        ProtectedRefreshToken = Convert.ToBase64String(encrypted);
    }
}
