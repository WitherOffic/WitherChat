using Avalonia;
using Avalonia.Headless;
using WitherChat.Desktop;

[assembly: AvaloniaTestApplication(typeof(WitherChat.Avalonia.HeadlessTests.TestAppBuilder))]

namespace WitherChat.Avalonia.HeadlessTests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false
            });
}
