using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Automation;
using Avalonia.LogicalTree;

namespace WitherChat.Desktop.Views;

public partial class DonationAlertsWindow
{
    private bool _isObsDockLayout;
    private bool _preDockTopmost;

    internal void ConfigureObsDockLayout(bool enabled)
    {
        if (_isObsDockLayout == enabled) return;
        if (enabled) _preDockTopmost = Topmost;
        _isObsDockLayout = enabled;
        ShowInTaskbar = !enabled;
        CanResize = !enabled;
        DonationWindowResizeMarker.IsVisible = !enabled;
        Topmost = enabled ? false : _preDockTopmost;
        ExtendClientAreaToDecorationsHint = !enabled;
        ExtendClientAreaTitleBarHeightHint = enabled ? 0 : 44;
        MinWidth = enabled ? 280 : 420;
        MinHeight = enabled ? 280 : 420;
        DonationDockScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        foreach (var button in this.GetLogicalDescendants().OfType<Button>())
        {
            if (AutomationProperties.GetAutomationId(button) is
                "DonationWindowMinimizeButton" or "DonationWindowCloseButton") button.IsVisible = !enabled;
        }
        UpdateObsDockLayout();
    }

    internal void UpdateObsDockLayout()
    {
        var narrow = Bounds.Width < 420;
        WindowRoot.RowDefinitions[0].Height = new GridLength(44);
        DonationWindowTitleText.IsVisible = true;
        DonationWindowTitleText.MaxWidth = narrow ? 160 : double.PositiveInfinity;
        DonationWindowTitleText.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        DonationContentRoot.Margin = narrow ? new Thickness(8, 14, 8, 16) : new Thickness(16, 14, 16, 16);
        DonationContentRoot.Height = Math.Max(460, Bounds.Height - 74);
        DonationDockScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        DonationConnectPanel.Padding = new Thickness(narrow ? 12 : 22);
        DonationControlStatusGrid.ColumnDefinitions = new ColumnDefinitions(narrow ? "Auto,*" : "Auto,*,Auto");
        DonationControlStatusGrid.RowDefinitions = new RowDefinitions(narrow ? "Auto,Auto" : "*");
        DonationControlStatusGrid.RowSpacing = narrow ? 8 : 0;
        Grid.SetColumn(DonationRefreshControlButton, narrow ? 1 : 2);
        Grid.SetRow(DonationRefreshControlButton, narrow ? 1 : 0);
        DonationRefreshControlButton.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        DonationActionsGrid.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "*,Auto");
        DonationActionsGrid.RowDefinitions = new RowDefinitions(narrow ? "Auto,Auto" : "*");
        DonationActionsGrid.RowSpacing = narrow ? 6 : 0;
        Grid.SetColumn(DonationActionButtons, narrow ? 0 : 1);
        Grid.SetRow(DonationActionButtons, narrow ? 1 : 0);
        DonationActionButtons.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        DonationTutorialContent.Margin = narrow ? new Thickness(12) : new Thickness(21,17,21,19);
        DonationTutorialNextButton.MinWidth = narrow ? 76 : 104;
        PositionDonationTutorial();
    }
}
