using System.Diagnostics;
using System.Reflection;
using Avalonia;
using WitherChat.Desktop.Views;
using Xunit;
namespace WitherChat.Avalonia.HeadlessTests;
public sealed partial class MainWindowLayoutTests
{
    private static async Task WaitForOnboardingFocusSettledAsync(MainWindow window, CancellationToken cancellationToken)
    {
        const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
        var pending=typeof(MainWindow).GetField("_onboardingFocusUpdatePending",flags)!;
        var target=typeof(MainWindow).GetField("_lastOnboardingTargetRect",flags)!;
        var rendered=typeof(MainWindow).GetField("_renderedOnboardingTargetRect",flags)!;
        var normalize=typeof(MainWindow).GetMethod("NormalizeOnboardingTargetRect",flags)!;
        var clock=Stopwatch.StartNew();
        while(clock.Elapsed < TimeSpan.FromSeconds(4))
        {
            var expected=(Rect?)normalize.Invoke(window,[target.GetValue(window)]);
            var actual=(Rect?)rendered.GetValue(window);
            if(!(bool)pending.GetValue(window)! && SameRect(expected,actual))return;
            await Task.Delay(20,cancellationToken);
        }
        Assert.Fail("Onboarding focus did not reach its exact target within the bounded wait.");
        static bool SameRect(Rect? expected,Rect? actual)
        {
            if(expected is null || actual is null)return expected is null && actual is null;
            return Math.Abs(expected.Value.X-actual.Value.X)<0.001 &&
                   Math.Abs(expected.Value.Y-actual.Value.Y)<0.001 &&
                   Math.Abs(expected.Value.Width-actual.Value.Width)<0.001 &&
                   Math.Abs(expected.Value.Height-actual.Value.Height)<0.001;
        }
    }
}
