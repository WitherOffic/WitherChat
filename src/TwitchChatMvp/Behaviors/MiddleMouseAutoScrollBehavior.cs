using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace TwitchChatMvp.Behaviors;

public static class MiddleMouseAutoScrollBehavior
{
    private const double DeadZone = 10;
    private const double MaxSpeed = 1100;

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(MiddleMouseAutoScrollBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(AutoScrollState),
        typeof(MiddleMouseAutoScrollBehavior));

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox listBox)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            var state = new AutoScrollState(listBox);
            listBox.SetValue(StateProperty, state);
            state.Attach();
        }
        else if (listBox.GetValue(StateProperty) is AutoScrollState state)
        {
            state.Dispose();
            listBox.ClearValue(StateProperty);
        }
    }

    private sealed class AutoScrollState
    {
        private readonly ListBox _owner;
        private ScrollViewer? _scrollViewer;
        private Window? _window;
        private Popup? _indicator;
        private Point _origin;
        private TimeSpan _lastRenderTime;
        private Cursor? _previousCursor;
        private Cursor? _previousOverrideCursor;
        private bool _active;

        public AutoScrollState(ListBox owner)
        {
            _owner = owner;
        }

        public void Attach()
        {
            _owner.Loaded += Owner_Loaded;
            _owner.Unloaded += Owner_Unloaded;
            _owner.PreviewMouseDown += Owner_PreviewMouseDown;
            _owner.LostMouseCapture += Owner_LostMouseCapture;
            if (_owner.IsLoaded)
            {
                Owner_Loaded(_owner, new RoutedEventArgs());
            }
        }

        public void Dispose()
        {
            Stop();
            _owner.Loaded -= Owner_Loaded;
            _owner.Unloaded -= Owner_Unloaded;
            _owner.PreviewMouseDown -= Owner_PreviewMouseDown;
            _owner.LostMouseCapture -= Owner_LostMouseCapture;
            UnhookWindow();
        }

        private void Owner_Loaded(object sender, RoutedEventArgs e)
        {
            _scrollViewer = FindVisualChild<ScrollViewer>(_owner);
            _window = Window.GetWindow(_owner);
            if (_window is not null)
            {
                _window.PreviewKeyDown -= Window_PreviewKeyDown;
                _window.PreviewKeyDown += Window_PreviewKeyDown;
                _window.Deactivated -= Window_Deactivated;
                _window.Deactivated += Window_Deactivated;
            }
        }

        private void Owner_Unloaded(object sender, RoutedEventArgs e)
        {
            Stop();
            UnhookWindow();
        }

        private void UnhookWindow()
        {
            if (_window is null)
            {
                return;
            }

            _window.PreviewKeyDown -= Window_PreviewKeyDown;
            _window.Deactivated -= Window_Deactivated;
            _window = null;
        }

        private void Owner_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_active)
            {
                if (e.ChangedButton is MouseButton.Middle or MouseButton.Left or MouseButton.Right)
                {
                    Stop();
                    e.Handled = e.ChangedButton == MouseButton.Middle;
                }

                return;
            }

            if (e.ChangedButton != MouseButton.Middle || IsInteractive(e.OriginalSource as DependencyObject))
            {
                return;
            }

            _scrollViewer ??= FindVisualChild<ScrollViewer>(_owner);
            if (_scrollViewer is null || !Mouse.Capture(_owner, CaptureMode.SubTree))
            {
                return;
            }

            _active = true;
            _origin = e.GetPosition(_owner);
            _lastRenderTime = TimeSpan.Zero;
            _previousCursor = _owner.Cursor;
            _previousOverrideCursor = Mouse.OverrideCursor;
            _owner.Cursor = Cursors.ScrollNS;
            Mouse.OverrideCursor = Cursors.ScrollNS;
            ShowIndicator();
            CompositionTarget.Rendering += OnRendering;
            e.Handled = true;
        }

        private void Owner_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_active)
            {
                Stop(releaseCapture: false);
            }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_active && e.Key == Key.Escape)
            {
                Stop();
                e.Handled = true;
            }
        }

        private void Window_Deactivated(object? sender, EventArgs e) => Stop();

        private void OnRendering(object? sender, EventArgs e)
        {
            if (!_active || _scrollViewer is null || e is not RenderingEventArgs rendering)
            {
                return;
            }

            if (_lastRenderTime == TimeSpan.Zero)
            {
                _lastRenderTime = rendering.RenderingTime;
                return;
            }

            var elapsed = Math.Min(0.05, (rendering.RenderingTime - _lastRenderTime).TotalSeconds);
            _lastRenderTime = rendering.RenderingTime;
            var delta = Mouse.GetPosition(_owner).Y - _origin.Y;
            var distance = Math.Abs(delta);
            if (distance <= DeadZone)
            {
                return;
            }

            var speed = Math.Sign(delta) * Math.Min(MaxSpeed, (distance - DeadZone) * 12);
            var target = Math.Clamp(
                _scrollViewer.VerticalOffset + (speed * elapsed),
                0,
                _scrollViewer.ScrollableHeight);
            _scrollViewer.ScrollToVerticalOffset(target);
        }

        private void Stop(bool releaseCapture = true)
        {
            if (!_active)
            {
                return;
            }

            _active = false;
            CompositionTarget.Rendering -= OnRendering;
            _indicator?.SetCurrentValue(Popup.IsOpenProperty, false);
            _owner.Cursor = _previousCursor;
            Mouse.OverrideCursor = _previousOverrideCursor;
            if (releaseCapture && Mouse.Captured == _owner)
            {
                Mouse.Capture(null);
            }
        }

        private void ShowIndicator()
        {
            _indicator ??= CreateIndicator();
            _indicator.PlacementTarget = _owner;
            _indicator.HorizontalOffset = _origin.X - 20;
            _indicator.VerticalOffset = _origin.Y - 20;
            _indicator.IsOpen = true;

            if (!Services.AnimationService.ReduceMotion && _indicator.Child is UIElement child)
            {
                child.Opacity = 0;
                child.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
            }
        }

        private Popup CreateIndicator()
        {
            var icon = new Path
            {
                Data = Geometry.Parse("M12,15 L20,7 L28,15 M12,25 L20,33 L28,25"),
                StrokeThickness = 1.7,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                IsHitTestVisible = false
            };
            icon.SetResourceReference(Shape.StrokeProperty, "PrimaryText");

            var border = new Border
            {
                Width = 40,
                Height = 40,
                CornerRadius = new CornerRadius(20),
                BorderThickness = new Thickness(1),
                Child = icon,
                IsHitTestVisible = false
            };
            border.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
            border.SetResourceReference(Border.BorderBrushProperty, "BorderBrushBright");
            border.SetResourceReference(UIElement.EffectProperty, "SoftShadow");

            return new Popup
            {
                Placement = PlacementMode.Relative,
                AllowsTransparency = true,
                StaysOpen = true,
                IsHitTestVisible = false,
                Child = border
            };
        }

        private bool IsInteractive(DependencyObject? source)
        {
            for (var current = source; current is not null && current != _owner; current = GetParent(current))
            {
                if (current is ButtonBase or Hyperlink or TextBoxBase or ScrollBar or Thumb or MenuItem or Selector)
                {
                    return true;
                }
            }

            return false;
        }

        private static DependencyObject? GetParent(DependencyObject child)
        {
            if (child is Visual)
            {
                return VisualTreeHelper.GetParent(child);
            }

            if (child is ContentElement content)
            {
                return ContentOperations.GetParent(content) ?? LogicalTreeHelper.GetParent(child);
            }

            return LogicalTreeHelper.GetParent(child);
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match)
                {
                    return match;
                }

                var nested = FindVisualChild<T>(child);
                if (nested is not null)
                {
                    return nested;
                }
            }

            return null;
        }
    }
}
