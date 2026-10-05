using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using WitherChat.Desktop.ViewModels;

namespace WitherChat.Desktop.Views;

// One responsive layout for both the standalone application and the embedded OBS client.
// Only the space occupied by the host's caption is different.
public partial class MainWindow
{
    private readonly List<Action> _responsivePanelRestores = [];
    private readonly Dictionary<Border, (double Width, double Height, double MaxWidth, double MaxHeight)> _responsivePanelSizes = [];

    private Border[] ResponsivePanelCards() => [StreamEventsCard, ProtectionPanelCard,
        LogViewerCard, SettingsCard, ConnectPanelCard, MomentsPanelCard, MomentEditorCard,
        ModerationPanelCard, ModerationDialogCard, RecentMessagesCard, DeleteLogCard, ClearChatConfirmationCard];

    private void CaptureResponsivePanelLayout()
    {
        void GridSnapshot(Grid grid)
        {
            var columns = grid.ColumnDefinitions; var rows = grid.RowDefinitions; var spacing = grid.RowSpacing;
            _responsivePanelRestores.Add(() =>
            { grid.ColumnDefinitions = columns; grid.RowDefinitions = rows; grid.RowSpacing = spacing; });
            foreach (var child in grid.Children)
            {
                var column = Grid.GetColumn(child); var row = Grid.GetRow(child); var span = Grid.GetColumnSpan(child);
                _responsivePanelRestores.Add(() =>
                { Grid.SetColumn(child, column); Grid.SetRow(child, row); Grid.SetColumnSpan(child, span); });
            }
        }
        void TextSnapshot(TextBlock text, bool visibility = false)
        {
            var wrap = text.TextWrapping; var trim = text.TextTrimming; var size = text.FontSize; var visible = text.IsVisible;
            _responsivePanelRestores.Add(() =>
            {
                text.TextWrapping = wrap; text.TextTrimming = trim; text.FontSize = size;
                if (visibility) text.IsVisible = visible;
            });
        }
        foreach (var card in ResponsivePanelCards())
        {
            _responsivePanelSizes[card] = (card.Width, card.Height,
                card.MaxWidth > 0 ? card.MaxWidth : double.PositiveInfinity,
                card.MaxHeight > 0 ? card.MaxHeight : double.PositiveInfinity);
            var padding = card.Padding;
            _responsivePanelRestores.Add(() => card.Padding = padding);
            foreach (var text in HeaderTexts(card)) TextSnapshot(text,
                card == StreamEventsCard || card == ProtectionPanelCard || card == ConnectPanelCard || card == LogViewerCard);
        }
        foreach (var text in ProtectionPanelCard.GetLogicalDescendants().OfType<TextBlock>()) TextSnapshot(text);
        foreach (var grid in ProtectionPanelCard.GetLogicalDescendants().OfType<Grid>()
            .Where(grid => grid.ColumnDefinitions.Count > 1)) GridSnapshot(grid);
        foreach (var grid in new[] { LogViewerHeader, LogViewerBodyGrid, LogSelectedHeaderGrid, LogViewerFilters }) GridSnapshot(grid);
        foreach (var filter in StreamEventFiltersPanel.Children)
        {
            var margin = filter.Margin; _responsivePanelRestores.Add(() => filter.Margin = margin);
        }
        var sidebarHeight = LogViewerSidebar.Height; var contentHeight = LogViewerContent.Height;
        var headerMargin = LogSelectedHeaderGrid.Margin; var filterMargin = LogViewerFilters.Margin;
        var actionsOrientation = LogViewerActions.Orientation;
        var openWidth = OpenLogFolderButton.Width; var openMinWidth = OpenLogFolderButton.MinWidth;
        var openPadding = OpenLogFolderButton.Padding; var labelVisible = OpenLogFolderLabel.IsVisible;
        _responsivePanelRestores.Add(() =>
        {
            LogViewerSidebar.Height = sidebarHeight; LogViewerContent.Height = contentHeight;
            LogSelectedHeaderGrid.Margin = headerMargin; LogViewerFilters.Margin = filterMargin;
            LogViewerActions.Orientation = actionsOrientation;
            OpenLogFolderButton.Width = openWidth; OpenLogFolderButton.MinWidth = openMinWidth;
            OpenLogFolderButton.Padding = openPadding; OpenLogFolderLabel.IsVisible = labelVisible;
            LogDockBodyScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        });
    }

    private static TextBlock[] HeaderTexts(Border card) =>
        card.Child is Grid root && root.Children.FirstOrDefault() is Grid header
            ? header.Children.OfType<StackPanel>().FirstOrDefault()?.GetLogicalDescendants().OfType<TextBlock>().ToArray() ?? [] : [];

    private void ApplyResponsivePanelLayout()
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        foreach (var restore in _responsivePanelRestores) restore();
        var narrow = Bounds.Width < 600;
        var edge = narrow ? 8 : 18;
        var caption = _isObsDockLayout ? 36 : 42;
        var availableWidth = Math.Max(1, Bounds.Width - edge * 2);
        var availableHeight = Math.Max(1, Bounds.Height - caption - edge * 2);
        foreach (var (card, original) in _responsivePanelSizes)
        {
            if (card.Parent is Border overlay)
            {
                overlay.Margin = new Thickness(0, caption, 0, 0);
                overlay.Padding = new Thickness(edge);
                overlay.ZIndex = card == ClearChatConfirmationCard || card == DeleteLogCard ? 90 :
                    card == RecentMessagesCard ? 80 : card == MomentEditorCard || card == ModerationDialogCard ? 70 : 50;
            }
            card.Margin = default;
            card.MinWidth = card.MinHeight = 0;
            card.MaxWidth = Math.Min(original.MaxWidth, availableWidth);
            card.MaxHeight = Math.Min(original.MaxHeight, availableHeight);
            card.Width = double.IsFinite(original.Width) ? Math.Min(original.Width, card.MaxWidth) : double.NaN;
            card.Height = double.IsFinite(original.Height) ? Math.Min(original.Height, card.MaxHeight) : double.NaN;
            if (narrow) card.Padding = new Thickness(12);
        }
        UpdateDockToolbarInteractivity();
        if (!narrow) return;
        foreach (var filter in StreamEventFiltersPanel.Children) filter.Margin = new Thickness(0, 0, 6, 6);
        foreach (var card in ResponsivePanelCards())
        {
            var texts = HeaderTexts(card);
            if (texts.Length == 0) continue;
            texts[0].FontSize = 20;
            texts[0].TextTrimming = TextTrimming.None;
            texts[0].TextWrapping = TextWrapping.Wrap;
        }
        if (Bounds.Height < 450)
            foreach (var card in new[] { StreamEventsCard, ProtectionPanelCard, ConnectPanelCard })
                foreach (var description in HeaderTexts(card).Skip(1)) description.IsVisible = false;

        var protectionRoot = (Grid)ProtectionPanelCard.Child!;
        var protectionHeader = protectionRoot.Children[0];
        foreach (var grid in ProtectionPanelCard.GetLogicalDescendants().OfType<Grid>()
            .Where(grid => grid != protectionHeader && grid.ColumnDefinitions.Count > 1 && grid.Children.Count > 1))
        {
            if (grid.Children[0] is CheckBox && grid.Children.Count <= 3)
            {
                grid.ColumnDefinitions = new ColumnDefinitions("Auto,8,*");
                grid.RowDefinitions = new RowDefinitions("Auto,8,Auto");
                Grid.SetColumn(grid.Children[0], 0); Grid.SetRow(grid.Children[0], 0);
                Grid.SetColumn(grid.Children[1], 2); Grid.SetRow(grid.Children[1], 0);
                if (grid.Children.Count == 3)
                { Grid.SetColumn(grid.Children[2], 2); Grid.SetRow(grid.Children[2], 2); }
            }
            else
            {
                grid.ColumnDefinitions = new ColumnDefinitions("*");
                grid.RowDefinitions = new RowDefinitions(string.Join(",", grid.Children.Select(_ => "Auto")));
                grid.RowSpacing = 8;
                for (var index = 0; index < grid.Children.Count; index++)
                { Grid.SetColumn(grid.Children[index], 0); Grid.SetRow(grid.Children[index], index); Grid.SetColumnSpan(grid.Children[index], 1); }
            }
        }
        foreach (var text in ProtectionPanelCard.GetLogicalDescendants().OfType<TextBlock>()
            .Except(HeaderTexts(ProtectionPanelCard))) text.TextWrapping = TextWrapping.Wrap;

        LogViewerHeader.ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto");
        Grid.SetColumn(ChatLogsHelpButton, 1); Grid.SetColumn(OpenLogFolderButton, 2); Grid.SetColumn(CloseLogsButton, 3);
        OpenLogFolderLabel.IsVisible = false;
        OpenLogFolderButton.Width = OpenLogFolderButton.MinWidth = 32;
        OpenLogFolderButton.Padding = new Thickness(6);
        LogDockBodyScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        LogViewerBodyGrid.ColumnDefinitions = new ColumnDefinitions("*");
        LogViewerBodyGrid.RowDefinitions = new RowDefinitions("Auto,12,Auto");
        Grid.SetColumn(LogViewerContent, 0); Grid.SetRow(LogViewerContent, 2);
        LogViewerSidebar.Height = 250; LogViewerContent.Height = 460;
        LogSelectedHeaderGrid.ColumnDefinitions = new ColumnDefinitions("*");
        LogSelectedHeaderGrid.RowDefinitions = new RowDefinitions("Auto,8,Auto");
        LogSelectedHeaderGrid.Margin = new Thickness(8);
        Grid.SetColumn(LogViewerActions, 0); Grid.SetRow(LogViewerActions, 2);
        LogViewerActions.Orientation = Orientation.Vertical;
        LogViewerFilters.ColumnDefinitions = new ColumnDefinitions("*");
        LogViewerFilters.RowDefinitions = new RowDefinitions("Auto,8,Auto,8,Auto");
        LogViewerFilters.Margin = new Thickness(8);
        for (var index = 0; index < LogViewerFilters.Children.Count; index++)
        { Grid.SetColumn(LogViewerFilters.Children[index], 0); Grid.SetRow(LogViewerFilters.Children[index], index * 2); }
    }

    private void UpdateDockToolbarInteractivity()
    {
        if (!_isObsDockLayout) return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(UpdateDockToolbarInteractivity);
            return;
        }
        if (DataContext is not MainWindowViewModel vm) return;
        ObsDockToolbar.IsEnabled = !(vm.IsSettingsOpen || vm.IsConnectPanelOpen || vm.IsLogViewerOpen ||
            vm.IsModerationPanelOpen || vm.IsModerationDialogOpen || vm.IsRecentMessagesOpen ||
            vm.IsDeleteLogConfirmationOpen || vm.IsClearChatConfirmationOpen || vm.IsStreamEventsOpen || vm.IsProtectionPanelOpen ||
            vm.IsMomentsPanelOpen || vm.IsMomentEditorOpen || vm.IsOnboardingOpen);
    }
}
