using System.Text.Json;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using Xunit;

namespace WitherChat.Core.ApiTests;

public sealed class SettingsStoreTests
{
    [Theory]
    [InlineData("{")]
    [InlineData("{\"theme\":\"Light\",")]
    [InlineData("null")]
    public async Task MalformedOrEmptyPrimaryIsQuarantinedWithoutLosingItsContents(string corruptJson)
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var paths = new AppDataPaths(directory);
            await File.WriteAllTextAsync(
                paths.SettingsFile,
                corruptJson,
                TestContext.Current.CancellationToken);
            using var store = new SettingsStore(paths);

            var recovered = store.Load();

            Assert.Equal("Dark", recovered.Theme);
            Assert.False(File.Exists(paths.SettingsFile));
            var quarantine = Assert.Single(Directory.GetFiles(directory, "settings.json.corrupt-*"));
            Assert.Equal(
                corruptJson,
                await File.ReadAllTextAsync(quarantine, TestContext.Current.CancellationToken));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task CorruptPrimaryIsQuarantinedAndRecoveredFromBackup()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var paths = new AppDataPaths(directory);
            const string corruptPrimary = "{\"theme\":\"Light\",";
            await File.WriteAllTextAsync(
                paths.SettingsFile,
                corruptPrimary,
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                paths.SettingsFile + ".bak",
                JsonSerializer.Serialize(new WitherChatSettings { Theme = "System", Language = "en" }),
                TestContext.Current.CancellationToken);
            using var store = new SettingsStore(paths);

            var recovered = store.Load();

            Assert.Equal("System", recovered.Theme);
            Assert.Equal("en", recovered.Language);
            Assert.False(File.Exists(paths.SettingsFile));
            var quarantine = Assert.Single(Directory.GetFiles(directory, "settings.json.corrupt-*"));
            Assert.Equal(
                corruptPrimary,
                await File.ReadAllTextAsync(quarantine, TestContext.Current.CancellationToken));

            await store.SaveAsync(recovered, TestContext.Current.CancellationToken);
            using var verifier = new SettingsStore(paths);
            Assert.Equal("System", verifier.Load().Theme);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task InvalidBackupIsPreservedAndDefaultsAreUsed()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var paths = new AppDataPaths(directory);
            const string corruptPrimary = "{\"theme\":\"Light\",";
            const string corruptBackup = "{\"language\":";
            await File.WriteAllTextAsync(
                paths.SettingsFile,
                corruptPrimary,
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                paths.SettingsFile + ".bak",
                corruptBackup,
                TestContext.Current.CancellationToken);
            using var store = new SettingsStore(paths);

            var recovered = store.Load();

            Assert.Equal("Dark", recovered.Theme);
            Assert.Equal("ru", recovered.Language);
            var quarantine = Assert.Single(Directory.GetFiles(directory, "settings.json.corrupt-*"));
            Assert.Equal(
                corruptPrimary,
                await File.ReadAllTextAsync(quarantine, TestContext.Current.CancellationToken));
            Assert.Equal(
                corruptBackup,
                await File.ReadAllTextAsync(
                    paths.SettingsFile + ".bak",
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ReadFailureBlocksAutomaticOverwriteOfTheExistingSettings()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var paths = new AppDataPaths(directory);
            const string original = "{\"theme\":\"Light\",\"language\":\"en\"}";
            await File.WriteAllTextAsync(
                paths.SettingsFile,
                original,
                TestContext.Current.CancellationToken);
            using var exclusiveLock = new FileStream(
                paths.SettingsFile,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            using var store = new SettingsStore(paths);

            var fallback = store.Load();

            Assert.Equal("Dark", fallback.Theme);
            await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(
                fallback,
                TestContext.Current.CancellationToken));
            exclusiveLock.Position = 0;
            using var reader = new StreamReader(exclusiveLock, leaveOpen: true);
            Assert.Equal(original, await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task FailedQuarantineBlocksSavingOverTheCorruptPrimaryOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = CreateTemporaryDirectory();
        try
        {
            var paths = new AppDataPaths(directory);
            const string corrupt = "{\"theme\":";
            await File.WriteAllTextAsync(
                paths.SettingsFile,
                corrupt,
                TestContext.Current.CancellationToken);
            using var moveBlockingHandle = new FileStream(
                paths.SettingsFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            using var store = new SettingsStore(paths);

            _ = store.Load();

            Assert.True(File.Exists(paths.SettingsFile));
            Assert.Empty(Directory.GetFiles(directory, "settings.json.corrupt-*"));
            await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(
                new WitherChatSettings { Theme = "System" },
                TestContext.Current.CancellationToken));
            moveBlockingHandle.Position = 0;
            using var reader = new StreamReader(moveBlockingHandle, leaveOpen: true);
            Assert.Equal(corrupt, await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task SuccessfulSaveKeepsThePreviousCompleteFileAsBackup()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var paths = new AppDataPaths(directory);
            using var store = new SettingsStore(paths);
            await store.SaveAsync(
                new WitherChatSettings { Theme = "Dark", Language = "ru" },
                TestContext.Current.CancellationToken);
            await store.SaveAsync(
                new WitherChatSettings { Theme = "Light", Language = "en" },
                TestContext.Current.CancellationToken);

            Assert.False(File.Exists(paths.SettingsFile + ".tmp"));
            using var current = JsonDocument.Parse(await File.ReadAllTextAsync(
                paths.SettingsFile,
                TestContext.Current.CancellationToken));
            using var backup = JsonDocument.Parse(await File.ReadAllTextAsync(
                paths.SettingsFile + ".bak",
                TestContext.Current.CancellationToken));
            Assert.Equal("Light", current.RootElement.GetProperty("theme").GetString());
            Assert.Equal("Dark", backup.RootElement.GetProperty("theme").GetString());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ConcurrentSavesRemainValidAndLeaveNoTemporaryFile()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var paths = new AppDataPaths(directory);
            using var store = new SettingsStore(paths);
            var saves = Enumerable.Range(0, 32)
                .Select(index => store.SaveAsync(
                    new WitherChatSettings
                    {
                        Theme = (index % 3) switch
                        {
                            0 => "Dark",
                            1 => "Light",
                            _ => "System"
                        },
                        Language = index % 2 == 0 ? "ru" : "en",
                        MessageLimit = 250 + index
                    },
                    TestContext.Current.CancellationToken))
                .ToArray();

            await Task.WhenAll(saves);

            Assert.False(File.Exists(paths.SettingsFile + ".tmp"));
            Assert.True(File.Exists(paths.SettingsFile + ".bak"));
            using var current = JsonDocument.Parse(await File.ReadAllTextAsync(
                paths.SettingsFile,
                TestContext.Current.CancellationToken));
            using var backup = JsonDocument.Parse(await File.ReadAllTextAsync(
                paths.SettingsFile + ".bak",
                TestContext.Current.CancellationToken));
            Assert.Contains(
                current.RootElement.GetProperty("theme").GetString(),
                new[] { "Dark", "Light", "System" });
            Assert.Contains(
                backup.RootElement.GetProperty("theme").GetString(),
                new[] { "Dark", "Light", "System" });
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task WindowsLegacySettingsAreMigratedAndNormalized()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTemporaryDirectory();
        try
        {
            var profileDirectory = Path.Combine(root, "desktop");
            var paths = new AppDataPaths(profileDirectory);
            const string legacyJson = """
                                      {
                                        "lastActiveChannelLogin": "#Legacy_Channel",
                                        "savedChannelLogins": ["#Legacy_Channel", "SECOND_CHANNEL"],
                                        "theme": "Light",
                                        "language": "en",
                                        "fontSize": 19,
                                        "messageLimit": 900,
                                        "enableObsOverlay": true,
                                        "overlayPort": 18080,
                                        "enableChatLogging": false,
                                        "saveChatLogTxt": false,
                                        "windowControlsPosition": "Right"
                                      }
                                      """;
            await File.WriteAllTextAsync(
                paths.LegacySettingsFile,
                legacyJson,
                TestContext.Current.CancellationToken);
            using var store = new SettingsStore(paths);

            var migrated = store.Load();

            Assert.Equal("legacy_channel", migrated.Channel);
            Assert.Equal(new[] { "legacy_channel", "second_channel" }, migrated.SavedChannels);
            Assert.Equal("Light", migrated.Theme);
            Assert.Equal("en", migrated.Language);
            Assert.Equal(19, migrated.ChatFontSize);
            Assert.Equal(900, migrated.MessageLimit);
            Assert.True(migrated.EnableObsOverlay);
            Assert.Equal(18080, migrated.OverlayPort);
            Assert.False(migrated.EnableChatLogging);
            Assert.False(migrated.SaveChatLogTxt);
            Assert.True(migrated.WindowControlsOnRight);
            Assert.True(migrated.CloseToTray);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "WitherChat-settings-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
