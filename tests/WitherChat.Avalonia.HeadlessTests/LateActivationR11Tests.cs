using System.Reflection;
using Avalonia.Headless.XUnit;
using WitherChat.Desktop;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaFact]
    public async Task R11LateActivationCannotReopenWindowDuringExit()
    {
        await using var fixture = new WindowFixture();
        var application = new App();
        typeof(App).GetField("_mainWindow", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(application, fixture.Window);
        typeof(App).GetProperty(nameof(App.IsExitRequested))!.SetValue(application, true);
        Assert.False(fixture.Window.IsVisible);
        application.ShowMainWindow();
        Assert.False(fixture.Window.IsVisible, "A SHOW already queued before shutdown must not reopen the closing window.");
    }
}
