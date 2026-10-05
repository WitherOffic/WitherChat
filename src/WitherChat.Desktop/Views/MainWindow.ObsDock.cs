using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Threading;
using WitherChat.Desktop.ViewModels;

namespace WitherChat.Desktop.Views;

public partial class MainWindow
{
    private bool _isObsDockLayout;
    private bool _preDockCompact;
    private bool _preDockHeader;
    private bool _preDockComposer;


    internal void ConfigureObsDockLayout(bool enabled)
    {
        if (_isObsDockLayout == enabled || DataContext is not MainWindowViewModel vm) return;
        if (enabled)
        {
            _preDockCompact = vm.IsCompactMode;
            _preDockHeader = vm.IsHeaderExpanded;
            _preDockComposer = vm.IsComposerExpanded;

        }
        ObsDockMoreButton.Flyout?.Hide();
        _isObsDockLayout = enabled;
        vm.IsObsDockMode = enabled;
        ChatLayoutRoot.RowDefinitions[3].Height = new GridLength(enabled ? 0 : 10);
        TitleBarPanel.IsVisible = !enabled;
        ShowInTaskbar = !enabled;
        CanResize = !enabled;
        ExtendClientAreaToDecorationsHint = !enabled;
        ExtendClientAreaTitleBarHeightHint = enabled ? 0 : 42;
        if (enabled)
        {
            Topmost = false;
            MinWidth = 280;
            MinHeight = 280;
            vm.IsHeaderExpanded = true;
            vm.IsComposerExpanded = true;
            ResetWindowVisual();
            UpdateObsDockLayout();
        }
        else
        {
            this.Bind(TopmostProperty, new Binding(nameof(MainWindowViewModel.AlwaysOnTop)));
            vm.IsCompactMode = _preDockCompact;
            vm.IsHeaderExpanded = _preDockHeader;
            vm.IsComposerExpanded = _preDockComposer;
            MinWidth = vm.IsCompactMode ? 280 : 860;
            MinHeight = vm.IsCompactMode ? 340 : 560;
            ObsDockToolbar.IsVisible = false;
            UpdateObsFilterLayout(false);

            ResetWindowVisual();
        }
        UpdateSettingsCardSize();
        if (vm.IsFollowingLatest) QueueScrollToEnd();
    }

    internal void UpdateObsDockLayout()
    {
        if (!_isObsDockLayout || DataContext is not MainWindowViewModel vm) return;
        var width = Bounds.Width > 0 ? Bounds.Width : Width;
        var narrow = width < 860;
        var changed = vm.IsCompactMode != narrow;
        vm.IsCompactMode = narrow;
        ObsDockToolbar.IsVisible = true;
        ObsDockMenuCard.Width = Math.Min(320, Math.Max(1, width - 16));
        var height = Bounds.Height > 0 ? Bounds.Height : Height;
        ObsDockMenuCard.MaxHeight = Math.Max(1, height - 48);
        UpdateObsFilterLayout(true);
        TitleBarPanel.IsVisible = false;

        if (changed && vm.IsFollowingLatest) QueueScrollToEnd();
    }


    private void UpdateObsFilterLayout(bool enabled)
    {
        var width = Bounds.Width > 0 ? Bounds.Width : Width;
        var height = Bounds.Height > 0 ? Bounds.Height : Height;
        // Keep a readable message area even at the dock's minimum size.
        MainFiltersPanel.MaxHeight = enabled ? Math.Clamp(height * .32, 84, 160) : double.PositiveInfinity;
        MainFiltersScrollViewer.VerticalScrollBarVisibility = enabled ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        SmartChatFiltersPanel.HorizontalScrollBarVisibility = enabled ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;
        MainMessageSearchBox.Width = enabled ? Math.Min(240, Math.Max(1, width - 80)) : 240;
        MainUserFilterBox.Width = enabled ? Math.Min(170, Math.Max(1, width - 80)) : 170;
    }

    private void ObsDockMenuItem_OnClick(object? sender, RoutedEventArgs args)
    {
        // Keep bindings attached until Avalonia has invoked the row command.
        Dispatcher.UIThread.Post(() => ObsDockMoreButton.Flyout?.Hide(), DispatcherPriority.Background);
    }
}
