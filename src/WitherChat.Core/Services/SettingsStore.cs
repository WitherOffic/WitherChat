using System.Globalization;
using System.Text.Json;
using WitherChat.Core.Models;

namespace WitherChat.Core.Services;

public sealed class SettingsStore(AppDataPaths paths) : IDisposable, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly object _lifetimeGate = new();
    private readonly TaskCompletionSource _savesDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _pendingSaves;
    private readonly AppDataPaths _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    private bool _savesBlockedAfterLoadFailure;
    private bool _disposed;

    public WitherChatSettings Load()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        _paths.EnsureCreated();
        if (!File.Exists(_paths.SettingsFile))
        {
            var migrated = TryLoadLegacySettings();
            if (migrated is not null)
            {
                migrated.Normalize();
                return migrated;
            }
            return new WitherChatSettings();
        }

        try
        {
            var settings = ReadSettingsFile(_paths.SettingsFile);
            _savesBlockedAfterLoadFailure = false;
            return settings;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return RecoverCorruptSettings();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A transient read/permission failure must not be followed by an automatic
            // save that overwrites the user's existing file with defaults.
            _savesBlockedAfterLoadFailure = true;
            return new WitherChatSettings();
        }
    }

    private WitherChatSettings RecoverCorruptSettings()
    {
        var backupFile = _paths.SettingsFile + ".bak";
        WitherChatSettings? backup = null;
        if (File.Exists(backupFile))
        {
            try
            {
                backup = ReadSettingsFile(backupFile);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                // Keep both files for diagnostics. The primary file is still quarantined below.
            }
        }

        if (!TryQuarantine(_paths.SettingsFile))
        {
            _savesBlockedAfterLoadFailure = true;
        }
        return backup ?? new WitherChatSettings();
    }

    private static WitherChatSettings ReadSettingsFile(string file)
    {
        var json = File.ReadAllText(file);
        var settings = JsonSerializer.Deserialize<WitherChatSettings>(json, JsonOptions)
                       ?? throw new InvalidDataException("The settings file contains no settings object.");
        settings.Normalize();
        return settings;
    }

    private static bool TryQuarantine(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return true;
            }
            var suffix = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
            var quarantine = file + ".corrupt-" + suffix;
            for (var index = 1; File.Exists(quarantine); index++)
            {
                quarantine = file + ".corrupt-" + suffix + "-" +
                             index.ToString(CultureInfo.InvariantCulture);
            }
            File.Move(file, quarantine);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private WitherChatSettings? TryLoadLegacySettings()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(_paths.LegacySettingsFile))
        {
            return null;
        }
        try
        {
            var json = File.ReadAllText(_paths.LegacySettingsFile);
            var legacy = JsonSerializer.Deserialize<LegacySettings>(json, JsonOptions);
            if (legacy is null)
            {
                return null;
            }
            var activeChannel = string.IsNullOrWhiteSpace(legacy.LastActiveChannelLogin)
                ? legacy.LastReadOnlyChannel
                : legacy.LastActiveChannelLogin;
            return new WitherChatSettings
            {
                Channel = activeChannel,
                SavedChannels = legacy.SavedChannelLogins ?? [],
                Theme = legacy.Theme,
                Language = legacy.Language,
                AlwaysOnTop = legacy.AlwaysOnTop,
                ToastNotifications = legacy.ToastNotifications,
                CloseToTray = true,
                MessageLimit = legacy.MessageLimit,
                ViewerCountRefreshIntervalSeconds = legacy.ViewerCountRefreshIntervalSeconds,
                ChatFontSize = legacy.FontSize,
                ShowTimestamps = legacy.ShowTimestamps,
                ShowBadges = legacy.EnableBadges,
                EnableTwitchEmotes = legacy.EnableTwitchEmotes,
                EnableBttvEmotes = legacy.EnableBttvEmotes,
                EnableSevenTvEmotes = legacy.EnableSevenTvEmotes,
                ShowChannelPointRedemptions = legacy.ShowChannelPointRedemptions,
                MessageVisualTheme = legacy.MessageVisualTheme,
                UiFontFamily = legacy.UiFontFamily,
                WindowControlsOnRight = string.Equals(legacy.WindowControlsPosition, "Right", StringComparison.OrdinalIgnoreCase),
                WindowControlsStyle = legacy.WindowControlsStyle,
                ReduceMotion = legacy.ReduceMotion,
                UseCustomClientId = legacy.UseCustomClientId,
                ClientId = legacy.ClientId,
                RedirectUri = legacy.RedirectUri,
                EnableObsOverlay = legacy.EnableObsOverlay,
                OverlayPort = legacy.OverlayPort,
                OverlayMaxMessages = legacy.OverlayMaxMessages,
                OverlayFontSize = legacy.OverlayFontSize,
                OverlayShowTimestamps = legacy.OverlayShowTimestamps,
                OverlayShowBadges = legacy.OverlayShowBadges,
                OverlayShowEmotes = legacy.OverlayShowEmotes,
                OverlayFadeOutSeconds = legacy.OverlayFadeOutSeconds,
                OverlayTextShadow = legacy.OverlayTextShadow,
                OverlayTextOutline = legacy.OverlayTextOutline,
                OverlayDarkBackground = legacy.OverlayDarkBackground,
                OverlayBackgroundOpacity = legacy.OverlayBackgroundOpacity,
                OverlayAlign = legacy.OverlayAlign,
                EnableChatLogging = legacy.EnableChatLogging,
                SaveChatLogTxt = legacy.SaveChatLogTxt,
                LogChatBadges = legacy.LogChatBadges,
                LogChannelPointRedemptions = legacy.LogChannelPointRedemptions,
                ChatLogsFolder = string.IsNullOrWhiteSpace(legacy.ChatLogsFolder) &&
                                 Directory.Exists(_paths.LegacyChatLogsDirectory)
                    ? _paths.LegacyChatLogsDirectory
                    : legacy.ChatLogsFolder,
                MaxLogViewerMessages = legacy.MaxLogViewerMessages
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public async Task SaveAsync(WitherChatSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pendingSaves++;
        }
        var gateHeld = false;
        try
        {
            if (_savesBlockedAfterLoadFailure)
            {
                throw new IOException(
                    "Settings were not saved because the existing settings file could not be read or preserved.");
            }
            settings.Normalize();
            _paths.EnsureCreated();
            await _saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateHeld = true;
            var temporaryFile = _paths.SettingsFile + ".tmp";
            try
            {
                var backupFile = _paths.SettingsFile + ".bak";
                var json = JsonSerializer.Serialize(settings, JsonOptions);
                await File.WriteAllTextAsync(temporaryFile, json, cancellationToken).ConfigureAwait(false);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(temporaryFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                if (File.Exists(_paths.SettingsFile))
                {
                    File.Copy(_paths.SettingsFile, backupFile, overwrite: true);
                }
                File.Move(temporaryFile, _paths.SettingsFile, overwrite: true);
            }
            finally
            {
                TryDeleteTemporaryFile(temporaryFile);
            }
        }
        finally
        {
            if (gateHeld) _saveLock.Release();
            lock (_lifetimeGate)
            {
                if (--_pendingSaves == 0 && _disposed)
                {
                    _saveLock.Dispose();
                    _savesDrained.TrySetResult();
                }
            }
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        lock (_lifetimeGate)
        {
            if (_disposed) return;
            _disposed = true;
            // Accepted saves keep ownership of their gate until they finish.
            // Synchronous disposal must never block the UI or strand queued writers.
            if (_pendingSaves == 0)
            {
                _saveLock.Dispose();
                _savesDrained.TrySetResult();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return new ValueTask(_savesDrained.Task);
    }

    private sealed class LegacySettings
    {
        public bool UseCustomClientId { get; set; }
        public string ClientId { get; set; } = string.Empty;
        public string RedirectUri { get; set; } = TwitchApplication.RedirectUri;
        public string LastReadOnlyChannel { get; set; } = string.Empty;
        public List<string> SavedChannelLogins { get; set; } = [];
        public string LastActiveChannelLogin { get; set; } = string.Empty;
        public double FontSize { get; set; } = 17;
        public int MessageLimit { get; set; } = 500;
        public int ViewerCountRefreshIntervalSeconds { get; set; } =
            WitherChatSettings.DefaultViewerCountRefreshIntervalSeconds;
        public bool ShowTimestamps { get; set; } = true;
        public bool EnableTwitchEmotes { get; set; } = true;
        public bool EnableBttvEmotes { get; set; } = true;
        public bool EnableSevenTvEmotes { get; set; } = true;
        public bool ShowChannelPointRedemptions { get; set; } = true;
        public string MessageVisualTheme { get; set; } = "TornBlack";
        public string UiFontFamily { get; set; } = "SegoeUIVariable";
        public string Theme { get; set; } = "Dark";
        public string Language { get; set; } = "ru";
        public string WindowControlsPosition { get; set; } = "Left";
        public bool AlwaysOnTop { get; set; }
        public bool ToastNotifications { get; set; } = true;
        public bool ReduceMotion { get; set; }
        public bool EnableBadges { get; set; } = true;
        public bool EnableObsOverlay { get; set; }
        public int OverlayPort { get; set; } = 17655;
        public int OverlayMaxMessages { get; set; } = 12;
        public double OverlayFontSize { get; set; } = 22;
        public bool OverlayShowTimestamps { get; set; } = true;
        public bool OverlayShowBadges { get; set; } = true;
        public bool OverlayShowEmotes { get; set; } = true;
        public int OverlayFadeOutSeconds { get; set; }
        public bool OverlayTextShadow { get; set; } = true;
        public bool OverlayTextOutline { get; set; } = true;
        public bool OverlayDarkBackground { get; set; } = true;
        public double OverlayBackgroundOpacity { get; set; }
        public string OverlayAlign { get; set; } = "left";
        public string WindowControlsStyle { get; set; } = "Mac";
        public bool EnableChatLogging { get; set; } = true;
        public bool SaveChatLogTxt { get; set; } = true;
        public bool LogChatBadges { get; set; } = true;
        public bool LogChannelPointRedemptions { get; set; } = true;
        public string ChatLogsFolder { get; set; } = string.Empty;
        public int MaxLogViewerMessages { get; set; } = 3000;
    }
}
