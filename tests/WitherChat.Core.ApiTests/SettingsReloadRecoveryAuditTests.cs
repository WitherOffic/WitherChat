using WitherChat.Core.Models;
using WitherChat.Core.Services;
using Xunit;

namespace WitherChat.Core.ApiTests;

public sealed class SettingsReloadRecoveryAuditTests
{
    [Fact]
    public async Task SuccessfulReloadUnblocksSavingOnlyAfterTheOriginalSettingsAreRecovered()
    {
        var root = Path.Combine(Path.GetTempPath(), "WitherChat-settings-reload-" + Guid.NewGuid().ToString("N"));
        var paths = new AppDataPaths(root);
        paths.EnsureCreated();
        try
        {
            await File.WriteAllTextAsync(paths.SettingsFile, "{\"theme\":\"Light\",\"messageLimit\":789}", TestContext.Current.CancellationToken);
            await using var store = new SettingsStore(paths);
            using (var locked = File.Open(paths.SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                _ = store.Load();
                await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(new WitherChatSettings(), TestContext.Current.CancellationToken));
            }
            var recovered = store.Load();
            Assert.Equal("Light", recovered.Theme);
            Assert.Equal(789, recovered.MessageLimit);
            recovered.Theme = "System";
            await store.SaveAsync(recovered, TestContext.Current.CancellationToken);
            using var reader = new SettingsStore(paths);
            Assert.Equal("System", reader.Load().Theme);
            Assert.Equal(789, reader.Load().MessageLimit);
        }
        finally { Directory.Delete(root, true); }
    }
}
