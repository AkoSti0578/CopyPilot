using System.IO;
using System.Text.Json;
using CopyPilot.Core.Services;

namespace CopyPilot.Services;

/// <summary>Per-user settings stored in %APPDATA%\CopyPilot.</summary>
public sealed class AppSettings
{
    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CopyPilot");

    private static string SettingsFile => Path.Combine(Folder, "settings.json");
    private static string LastSessionFile => Path.Combine(Folder, "last-session.json");

    /// <summary>Explicit azcopy.exe to use instead of the bundled one.</summary>
    public string AzCopyPath { get; set; } = "";

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // fall back to defaults
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // settings are best-effort
        }
    }

    public static Profile? LoadLastSession()
    {
        try
        {
            return File.Exists(LastSessionFile) ? Profile.FromJson(File.ReadAllText(LastSessionFile)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Remembers the last session, never including secrets.</summary>
    public static void SaveLastSession(Profile profile)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(LastSessionFile, profile.ToJson(includeSecrets: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best-effort
        }
    }
}
