using System.IO;
using System.Text;
using System.Text.Json;
using TwitchChatMvp.Models;

namespace TwitchChatMvp.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public AppSettings Load()
    {
        try
        {
            AppPaths.TryMigrateLegacyFile(AppPaths.LegacySettingsFile, AppPaths.SettingsFile);
            if (!File.Exists(AppPaths.SettingsFile))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(AppPaths.SettingsFile);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            var languageBeforeNormalize = settings.Language;
            settings.Normalize();
            if (!string.Equals(languageBeforeNormalize, settings.Language, StringComparison.Ordinal))
            {
                Save(settings);
            }

            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        settings.Normalize();
        Directory.CreateDirectory(AppPaths.AppDataDirectory);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var tempPath = AppPaths.SettingsFile + ".tmp";
        try
        {
            File.WriteAllText(tempPath, json, new UTF8Encoding(false));
            File.Move(tempPath, AppPaths.SettingsFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
