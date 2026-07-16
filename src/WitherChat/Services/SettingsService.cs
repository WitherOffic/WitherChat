using System.IO;
using System.Text;
using System.Text.Json;
using WitherChat.Models;

namespace WitherChat.Services;

public sealed class SettingsService
{
    private static readonly object FileGate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private static bool _writesBlockedUntilSuccessfulLoad;
    private static bool _blockedWriteWarningLogged;

    public AppSettings Load()
    {
        lock (FileGate)
        {
            AppPaths.TryMigrateLegacyFile(AppPaths.LegacySettingsFile, AppPaths.SettingsFile);
            if (!File.Exists(AppPaths.SettingsFile))
            {
                _writesBlockedUntilSuccessfulLoad = false;
                return new AppSettings();
            }

            AppSettings? settings;
            try
            {
                var json = ReadSettingsWithRetry();
                settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                _writesBlockedUntilSuccessfulLoad = !PreserveCorruptSettings();
                new FileLogger().Warn($"Settings load failed; corrupt file preserved: {ex.GetType().Name}");
                return new AppSettings();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _writesBlockedUntilSuccessfulLoad = true;
                new FileLogger().Warn($"Settings read failed without modifying the original file: {ex.GetType().Name}");
                return new AppSettings();
            }

            if (settings is null)
            {
                _writesBlockedUntilSuccessfulLoad = !PreserveCorruptSettings();
                new FileLogger().Warn("Settings load failed; null document preserved as corrupt.");
                return new AppSettings();
            }

            _writesBlockedUntilSuccessfulLoad = false;
            _blockedWriteWarningLogged = false;

            var languageBeforeNormalize = settings.Language;
            var channelMigrationBeforeNormalize = settings.ChannelSettingsMigrationVersion;
            settings.Normalize();
            if (!string.Equals(languageBeforeNormalize, settings.Language, StringComparison.Ordinal) ||
                channelMigrationBeforeNormalize != settings.ChannelSettingsMigrationVersion)
            {
                try
                {
                    Save(settings);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    new FileLogger().Warn($"Normalized settings could not be persisted: {ex.GetType().Name}");
                }
            }

            return settings;
        }
    }

    public void Save(AppSettings settings)
    {
        lock (FileGate)
        {
            if (_writesBlockedUntilSuccessfulLoad && File.Exists(AppPaths.SettingsFile))
            {
                if (!_blockedWriteWarningLogged)
                {
                    _blockedWriteWarningLogged = true;
                    new FileLogger().Warn("Settings save skipped because the existing file was not read successfully.");
                }
                return;
            }

            settings.Normalize();
            Directory.CreateDirectory(AppPaths.AppDataDirectory);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            var tempPath = AppPaths.SettingsFile + ".tmp";
            try
            {
                File.WriteAllText(tempPath, json, new UTF8Encoding(false));
                File.Move(tempPath, AppPaths.SettingsFile, overwrite: true);
                _writesBlockedUntilSuccessfulLoad = false;
                _blockedWriteWarningLogged = false;
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

    private static string ReadSettingsWithRetry()
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return File.ReadAllText(AppPaths.SettingsFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                if (attempt < 2)
                {
                    Thread.Sleep(40 * (attempt + 1));
                }
            }
        }

        throw lastError ?? new IOException("Settings could not be read.");
    }

    private static bool PreserveCorruptSettings()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile))
            {
                return true;
            }

            var backupPath = AppPaths.SettingsFile +
                             ".corrupt-" +
                             DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
            File.Move(AppPaths.SettingsFile, backupPath);
            return true;
        }
        catch
        {
            // Preserve the original file in place if it cannot be renamed.
            return false;
        }
    }
}
