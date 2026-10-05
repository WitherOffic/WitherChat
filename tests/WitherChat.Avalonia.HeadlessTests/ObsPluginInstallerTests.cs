using System.IO.Compression;
using WitherChat.Desktop.Services;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed class ObsPluginInstallerTests
{
    [Fact]
    public void EmbeddedPayloadIncludesProductionX64DllAndCorrespondingSource()
    {
        var payload = ObsPluginPayload.LoadEmbedded();
        Assert.NotNull(payload);
        Assert.Equal(9, payload.Files.Count);
        Assert.Equal(0x8664, WindowsObsInstallation.GetPeMachine(payload.Files[ObsPluginPayload.DllPath]));
        using var source = new ZipArchive(new MemoryStream(payload.Files[ObsPluginPayload.DataPath + "plugin-source.zip"]));
        Assert.Contains(source.Entries, entry => entry.FullName.EndsWith("plugin.cpp", StringComparison.Ordinal));
        Assert.Null(ObsPluginPayload.CurrentSingleFileExe()); // Test runner is not a distributable EXE.
    }

    [Theory]
    [InlineData("../outside.dll")]
    [InlineData("obs-plugins/64bit/witherchat-obs.dll")]
    [InlineData("unrelated.txt")]
    public void PayloadRejectsExtraDuplicateOrEscapingEntries(string extra)
    {
        var payload = ObsPluginPayload.LoadEmbedded()!;
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in payload.Files)
                using (var entry = archive.CreateEntry(file.Key).Open()) entry.Write(file.Value);
            using var unexpected = archive.CreateEntry(extra).Open();
            unexpected.WriteByte(1);
        }
        stream.Position = 0;
        Assert.Throws<InvalidDataException>(() => ObsPluginPayload.Read(stream));
    }

    [Fact]
    public void InstallsUpdatesAndBacksUpOnlyOwnedFiles()
    {
        using var fixture = new InstallFixture();
        var payload = fixture.Payload;
        var result = fixture.Installer.Install(fixture.Root);
        Assert.Equal(ObsPluginInstallCode.Success, result.Code);
        foreach (var file in payload.Files)
            Assert.True(payload.Matches(file.Key, Path.Combine(fixture.Root, file.Key)));
        var exe = Path.Combine(fixture.Root, ObsPluginPayload.ExePath);
        Assert.Equal("test executable", File.ReadAllText(exe));
        File.WriteAllText(exe, "old executable");
        File.WriteAllText(Path.Combine(fixture.Root, ObsPluginPayload.DllPath), "old dll");
        Assert.Equal(ObsPluginInstallCode.Success, fixture.Installer.Install(fixture.Root).Code);
        Assert.Contains(Directory.GetFiles(Path.Combine(fixture.Root, "data/obs-plugins/witherchat-obs-backups"), "WitherChat.exe", SearchOption.AllDirectories),
            file => File.ReadAllText(file) == "old executable");
        Assert.Equal("keep scenes", File.ReadAllText(fixture.Sentinel));
    }

    [Fact]
    public void FailedUpdateRestoresAllTouchedFiles()
    {
        using var fixture = new InstallFixture();
        Assert.Equal(ObsPluginInstallCode.Success, fixture.Installer.Install(fixture.Root).Code);
        foreach (var file in fixture.Payload.Files)
            File.WriteAllText(Path.Combine(fixture.Root, file.Key), "old-" + file.Key);
        var result = fixture.Installer.Install(fixture.Root, index =>
        {
            if (index == 3) throw new IOException("simulated interrupted update");
        });
        Assert.Equal(ObsPluginInstallCode.Failed, result.Code);
        foreach (var file in fixture.Payload.Files)
            Assert.Equal("old-" + file.Key, File.ReadAllText(Path.Combine(fixture.Root, file.Key)));
        Assert.Equal("keep scenes", File.ReadAllText(fixture.Sentinel));
    }

    [Fact]
    public void PermissionFailureCanRequestElevationOnlyAfterCompleteRollback()
    {
        using var fixture = new InstallFixture();
        Assert.Equal(ObsPluginInstallCode.Success, fixture.Installer.Install(fixture.Root).Code);
        var original = fixture.Payload.Files.ToDictionary(file => file.Key, file => File.ReadAllBytes(Path.Combine(fixture.Root, file.Key)));
        var result = fixture.Installer.Install(fixture.Root, index =>
        {
            if (index == 2) throw new UnauthorizedAccessException("simulated protected file");
        });
        Assert.Equal(ObsPluginInstallCode.AccessDenied, result.Code);
        foreach (var file in original) Assert.Equal(file.Value, File.ReadAllBytes(Path.Combine(fixture.Root, file.Key)));
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Root, "data/obs-plugins/witherchat-obs-installer"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void FailedFirstInstallRemovesOnlyNewlyCreatedFiles()
    {
        using var fixture = new InstallFixture();
        Assert.Equal(ObsPluginInstallCode.Failed, fixture.Installer.Install(fixture.Root, index =>
        {
            if (index == 2) throw new IOException("simulated failure");
        }).Code);
        foreach (var file in fixture.Payload.Files)
            Assert.False(File.Exists(Path.Combine(fixture.Root, file.Key)));
        Assert.Equal("keep scenes", File.ReadAllText(fixture.Sentinel));
    }

    [Fact]
    public void RejectsRunningObsBeforeAnyWrite()
    {
        using var fixture = new InstallFixture();
        fixture.Probe.Running = true;
        Assert.Equal(ObsPluginInstallCode.ObsRunning, fixture.Installer.Install(fixture.Root).Code);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "obs-plugins")));
    }

    [Fact]
    public void RejectsObsStartedBetweenStagingAndCommit()
    {
        using var fixture = new InstallFixture();
        fixture.Probe.StartOnSecondCheck = true;
        Assert.Equal(ObsPluginInstallCode.ObsRunning, fixture.Installer.Install(fixture.Root).Code);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "obs-plugins")));
    }

    [Theory]
    [InlineData("wrong-version")]
    [InlineData("wrong-architecture")]
    [InlineData("missing-qt")]
    public void RejectsIncompatibleObs(string scenario)
    {
        using var fixture = new InstallFixture();
        if (scenario == "wrong-version") fixture.Probe.Version = "31.0.0";
        if (scenario == "wrong-architecture") fixture.Probe.Machine = 0xaa64;
        if (scenario == "missing-qt") File.Delete(Path.Combine(fixture.Root, "bin/64bit/Qt6Core.dll"));
        Assert.Equal(ObsPluginInstallCode.InvalidTarget, fixture.Installer.Install(fixture.Root).Code);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "obs-plugins")));
    }

    [Fact]
    public void RejectsDirectoryAtFileTargetWithoutPartialInstallation()
    {
        using var fixture = new InstallFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, ObsPluginPayload.DllPath));
        Assert.Equal(ObsPluginInstallCode.Failed, fixture.Installer.Install(fixture.Root).Code);
        foreach (var file in fixture.Payload.Files) Assert.False(File.Exists(Path.Combine(fixture.Root, file.Key)));
    }

    [Fact]
    public void RejectsPathsOutsideInstallation()
    {
        using var fixture = new InstallFixture();
        Assert.Throws<InvalidDataException>(() => WindowsObsInstallation.OwnedPath(fixture.Root, "../outside"));
        Assert.Equal(ObsPluginInstallCode.InvalidTarget, fixture.Installer.Install("relative/obs").Code);
        Assert.Equal(ObsPluginInstallCode.InvalidTarget, fixture.Installer.Install(@"\\server\obs").Code);
    }

    [Fact]
    public void DoesNotOverwriteTheCurrentlyRunningBundledExe()
    {
        using var fixture = new InstallFixture();
        var exe = Path.Combine(fixture.Root, ObsPluginPayload.ExePath);
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "current bundled executable");
        var installer = new ObsPluginInstaller(fixture.Payload, exe, fixture.Probe);
        Assert.Equal(ObsPluginInstallCode.Success, installer.Install(fixture.Root).Code);
        Assert.Equal("current bundled executable", File.ReadAllText(exe));
    }

    [Fact]
    public void MalformedPeHeadersAreNotAccepted()
    {
        Assert.Equal(0, WindowsObsInstallation.GetPeMachine([]));
        var data = new byte[100]; data[0] = 0x4d; data[1] = 0x5a;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(60), int.MaxValue);
        Assert.Equal(0, WindowsObsInstallation.GetPeMachine(data));
    }

    private sealed class FakeProbe : IObsInstallationProbe
    {
        public string Version { get; set; } = "32.2.2";
        public ushort Machine { get; set; } = 0x8664;
        public bool Running { get; set; }
        public bool StartOnSecondCheck { get; set; }
        private int _checks;
        public string ProductVersion(string file) => file.EndsWith("Qt6Core.dll", StringComparison.Ordinal) ? "6.11.1.0" : Version;
        public ushort PeMachine(string file) => Machine;
        public bool ObsRunning() => Running || StartOnSecondCheck && ++_checks >= 2;
    }

    private sealed class InstallFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "WitherChat-install-test-" + Guid.NewGuid().ToString("N"));
        public ObsPluginPayload Payload { get; } = ObsPluginPayload.LoadEmbedded()!;
        public FakeProbe Probe { get; } = new();
        public ObsPluginInstaller Installer { get; }
        public string Sentinel => Path.Combine(Root, "config/scenes.txt");
        public InstallFixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, "bin/64bit"));
            File.WriteAllText(Path.Combine(Root, "bin/64bit/obs64.exe"), "OBS");
            File.WriteAllText(Path.Combine(Root, "bin/64bit/Qt6Core.dll"), "Qt");
            Directory.CreateDirectory(Path.GetDirectoryName(Sentinel)!);
            File.WriteAllText(Sentinel, "keep scenes");
            var source = Path.Combine(Root, "source.exe");
            File.WriteAllText(source, "test executable");
            Installer = new(Payload, source, Probe);
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
