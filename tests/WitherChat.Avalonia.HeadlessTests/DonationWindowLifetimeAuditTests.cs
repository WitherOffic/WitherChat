using System.Reflection;
using Avalonia.Headless.XUnit;
using CommunityToolkit.Mvvm.ComponentModel;
using WitherChat.Desktop.Views;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaFact]
    public async Task ClosedDonationWindowReleasesItsViewModelSubscription()
    {
        await using var fixture = new WindowFixture();
        var window = new DonationAlertsWindow { DataContext = fixture.ViewModel };
        var field = typeof(ObservableObject).GetField("PropertyChanged",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        bool IsSubscribed() => ((Delegate?)field.GetValue(fixture.ViewModel))?
            .GetInvocationList().Any(handler => ReferenceEquals(handler.Target, window)) == true;
        window.Show();
        Assert.True(IsSubscribed());
        try
        {
            window.CloseForApplicationExit();
            Assert.False(IsSubscribed(), "Closed donation window remains subscribed to the live view model.");
        }
        finally
        {
            window.DataContext = null;
            window.CloseForApplicationExit();
        }
    }

    [AvaloniaFact]
    public async Task HidingDonationWindowKeepsItsViewModelSubscriptionUntilRealClose()
    {
        await using var fixture = new WindowFixture();
        var window = new DonationAlertsWindow { DataContext = fixture.ViewModel };
        var field = typeof(ObservableObject).GetField("PropertyChanged",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        bool IsSubscribed() => ((Delegate?)field.GetValue(fixture.ViewModel))?
            .GetInvocationList().Any(handler => ReferenceEquals(handler.Target, window)) == true;
        window.Show();
        try
        {
            window.Close();
            Assert.False(window.IsVisible);
            Assert.True(IsSubscribed());
            window.Show();
            Assert.True(window.IsVisible);
            Assert.True(IsSubscribed());
        }
        finally
        {
            window.DataContext = null;
            window.CloseForApplicationExit();
        }
    }
}
