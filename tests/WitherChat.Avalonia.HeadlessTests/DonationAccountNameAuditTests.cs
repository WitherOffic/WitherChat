using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using WitherChat.Desktop.ViewModels;
using Xunit;
namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaTheory]
    [InlineData(280,340,"ru",false)]
    [InlineData(280,340,"en",true)]
    [InlineData(1100,760,"ru",false)]
    [InlineData(1100,760,"en",true)]
    public async Task LongDonationAccountNameFitsSettingsCard(int width,int height,string language,bool light)
    {
        await using var fixture = new WindowFixture();
        ConfigureAuxiliaryAuditWindow(fixture,width,height,language,light);
        var vm=fixture.ViewModel;
        typeof(MainWindowViewModel).GetField("_donationAlertsAuthSession",BindingFlags.NonPublic|BindingFlags.Instance)!
            .SetValue(vm,new DonationAlertsAuthSession("synthetic-token","20400",1,"test",new string('W',128),
                null,DonationAlertsApplication.RequiredScopes,DateTimeOffset.UtcNow.AddHours(1)));
        vm.DonationAlertsAccountName=new string('W',128);
        vm.IsSettingsOpen=true;
        vm.SelectedSettingsSection=SettingsSection.Account;
        fixture.Window.Show();
        await SettleAuxiliaryAuditAsync(fixture.Window);
        var card=fixture.Window.FindControl<Border>("AccountSettingsCard")!;
        var label=Assert.Single(card.GetLogicalDescendants().OfType<TextBlock>(),
            block=>block.Text==vm.DonationAlertsAccountLabel);
        Assert.True(label.Bounds.Width <= card.Bounds.Width-card.Padding.Left-card.Padding.Right,
            $"Account label exceeds card: label={label.Bounds}, card={card.Bounds}");
        label.BringIntoView();
        await SettleAuxiliaryAuditAsync(fixture.Window);
        using var frame=fixture.Window.CaptureRenderedFrame();
        SaveAuditFrame(frame!,$"donation-account-name-{language}-{width}x{height}.png");
    }
}
