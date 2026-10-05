using WitherChat.Desktop.Services;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed class ObsPluginRemovalTests
{
    [Fact]
    public void RemovalDeletesOnlyKnownPluginFilesAndKeepsForeignFilesEverywhere()
    {
        using var fixture = new RemovalFixture();
        var id = "20261005-121314-" + new string('a', 32);
        var backup = "data/obs-plugins/witherchat-obs-backups/" + id + "/";
        var staging = "data/obs-plugins/witherchat-obs-installer/" + id + "/";
        foreach (var file in ObsPluginRemover.ActiveFiles) fixture.Write(backup + file, "old owned file");
        foreach (var file in ObsPluginPayload.RequiredPaths) fixture.Write(staging + file, "owned staging");
        fixture.Write(staging + "WitherChat.exe", "staged exe");
        fixture.Foreign(backup + "user-notes.txt");
        fixture.Foreign(staging + "unknown.txt");
        fixture.Foreign("data/obs-plugins/witherchat-obs-backups/my-personal-backup/WitherChat.exe");
        var result = fixture.Remover.Remove(fixture.Root);
        Assert.Equal(ObsPluginInstallCode.Success, result.Code);
        Assert.Empty(ObsPluginRemover.FindFiles(fixture.Root));
        fixture.AssertForeignIntact();
        Assert.True(Directory.Exists(Path.Combine(fixture.Root, ObsPluginPayload.DataPath)));
    }

    [Fact]
    public void RemovalClearsEmptyOwnedFoldersButNeverSharedObsFolders()
    {
        using var fixture = new RemovalFixture(addForeignInsidePlugin: false);
        Assert.Equal(ObsPluginInstallCode.Success, fixture.Remover.Remove(fixture.Root).Code);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ObsPluginPayload.DataPath)));
        Assert.True(Directory.Exists(Path.Combine(fixture.Root, "obs-plugins/64bit")));
        Assert.True(Directory.Exists(Path.Combine(fixture.Root, "data/obs-plugins")));
        fixture.AssertForeignIntact();
    }

    [Fact]
    public void RunningObsPreventsAnyRemoval()
    {
        using var fixture = new RemovalFixture();
        fixture.Probe.Running = true;
        Assert.Equal(ObsPluginInstallCode.ObsRunning, fixture.Remover.Remove(fixture.Root).Code);
        foreach (var file in ObsPluginRemover.ActiveFiles) Assert.True(File.Exists(Path.Combine(fixture.Root, file)));
        fixture.AssertForeignIntact();
    }

    [Fact]
    public void RunningBundledExeIsNotDeleted()
    {
        using var fixture = new RemovalFixture();
        var remover = new ObsPluginRemover(fixture.Probe, Path.Combine(fixture.Root, ObsPluginPayload.ExePath));
        Assert.Equal(ObsPluginInstallCode.RunningFromPlugin, remover.Remove(fixture.Root).Code);
        Assert.Equal(10, ObsPluginRemover.FindFiles(fixture.Root).Count);
    }

    [Fact]
    public void RemovalIsAllowedAfterObsVersionChangesAndWithoutQt6()
    {
        using var fixture = new RemovalFixture();
        fixture.Probe.Version = "33.0.0";
        File.Delete(Path.Combine(fixture.Root, "bin/64bit/Qt6Core.dll"));
        Assert.Equal(ObsPluginInstallCode.Success, fixture.Remover.Remove(fixture.Root).Code);
        fixture.AssertForeignIntact();
    }

    [Fact]
    public void RemovalRefusesNonObsFolderBeforeChangingAnything()
    {
        using var fixture = new RemovalFixture();
        File.Delete(Path.Combine(fixture.Root, "bin/64bit/obs64.exe"));
        Assert.Equal(ObsPluginInstallCode.InvalidTarget, fixture.Remover.Remove(fixture.Root).Code);
        Assert.Equal(10, ObsPluginRemover.FindFiles(fixture.Root).Count);
        fixture.AssertForeignIntact();
    }

    [Fact]
    public void FailureDoesNotReportSuccessAndNeverDeletesForeignFiles()
    {
        using var fixture = new RemovalFixture();
        var result = fixture.Remover.Remove(fixture.Root, index =>
        {
            if (index == 2) throw new IOException("simulated locked file");
        });
        Assert.Equal(ObsPluginInstallCode.Failed, result.Code);
        Assert.NotEmpty(ObsPluginRemover.FindFiles(fixture.Root));
        fixture.AssertForeignIntact();
        Assert.Equal(ObsPluginInstallCode.Success, fixture.Remover.Remove(fixture.Root).Code);
        fixture.AssertForeignIntact();
    }

    [Fact]
    public void AccessDeniedRequestsElevationAndRetryIsSafe()
    {
        using var fixture = new RemovalFixture();
        var result = fixture.Remover.Remove(fixture.Root, index =>
        {
            if (index == 1) throw new UnauthorizedAccessException("simulated permission failure");
        });
        Assert.Equal(ObsPluginInstallCode.AccessDenied, result.Code);
        fixture.AssertForeignIntact();
        Assert.Equal(ObsPluginInstallCode.Success, fixture.Remover.Remove(fixture.Root).Code);
    }

    [Fact]
    public void ReadOnlyOwnedFilesCanBeRemoved()
    {
        using var fixture = new RemovalFixture();
        File.SetAttributes(Path.Combine(fixture.Root, ObsPluginPayload.DllPath), FileAttributes.ReadOnly);
        Assert.Equal(ObsPluginInstallCode.Success, fixture.Remover.Remove(fixture.Root).Code);
        fixture.AssertForeignIntact();
    }

    [Fact]
    public void RemovalIsIdempotent()
    {
        using var fixture = new RemovalFixture();
        Assert.Equal(ObsPluginInstallCode.Success, fixture.Remover.Remove(fixture.Root).Code);
        Assert.Equal(ObsPluginInstallCode.Success, fixture.Remover.Remove(fixture.Root).Code);
        fixture.AssertForeignIntact();
    }

    [Fact]
    public void DirectoryInPlaceOfPluginFileRefusesEntirePlan()
    {
        using var fixture = new RemovalFixture();
        var dll = Path.Combine(fixture.Root, ObsPluginPayload.DllPath);
        File.Delete(dll); Directory.CreateDirectory(dll);
        Assert.Equal(ObsPluginInstallCode.Failed, fixture.Remover.Remove(fixture.Root).Code);
        foreach (var file in ObsPluginRemover.ActiveFiles.Where(file => file != ObsPluginPayload.DllPath))
            Assert.True(File.Exists(Path.Combine(fixture.Root, file)));
        fixture.AssertForeignIntact();
    }

    private sealed class FakeRemovalProbe : IObsInstallationProbe
    {
        public bool Running { get; set; }
        public string Version { get; set; } = "32.2.2";
        public string ProductVersion(string file) => Version;
        public ushort PeMachine(string file) => 0x8664;
        public bool ObsRunning() => Running;
    }
    private sealed class RemovalFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "WitherChat-remove-test-" + Guid.NewGuid().ToString("N"));
        public FakeRemovalProbe Probe { get; } = new();
        public ObsPluginRemover Remover { get; }
        private readonly List<string> _foreign = [];
        public RemovalFixture(bool addForeignInsidePlugin = true)
        {
            foreach (var file in ObsPluginRemover.ActiveFiles) Write(file, "owned");
            Write("bin/64bit/obs64.exe", "OBS");
            Write("bin/64bit/Qt6Core.dll", "Qt");
            Foreign("obs-plugins/64bit/other-plugin.dll");
            Foreign("data/obs-plugins/other-plugin/settings.json");
            Foreign("config/profiles/profile.ini");
            Foreign("config/scenes/scene.json");
            Foreign("user-token.dat");
            if (addForeignInsidePlugin) Foreign(ObsPluginPayload.DataPath + "my-notes.json");
            Remover = new(Probe, Path.Combine(Root, "outside-standalone.exe"));
        }
        public void Write(string relative, string text)
        {
            var file = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
        }
        public void Foreign(string relative) { Write(relative, "foreign sentinel " + relative); _foreign.Add(relative); }
        public void AssertForeignIntact()
        {
            foreach (var relative in _foreign) Assert.Equal("foreign sentinel " + relative, File.ReadAllText(Path.Combine(Root, relative)));
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
