using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace WitherChat.Desktop.Controls;

public sealed class CompactLoadingBar : Control
{
    public static readonly StyledProperty<IBrush?> BackgroundBrushProperty =
        AvaloniaProperty.Register<CompactLoadingBar, IBrush?>(nameof(BackgroundBrush));

    public static readonly StyledProperty<IBrush?> BorderBrushProperty =
        AvaloniaProperty.Register<CompactLoadingBar, IBrush?>(nameof(BorderBrush));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<CompactLoadingBar, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> IndicatorBrushProperty =
        AvaloniaProperty.Register<CompactLoadingBar, IBrush?>(nameof(IndicatorBrush));

    public static readonly StyledProperty<bool> ReduceMotionProperty =
        AvaloniaProperty.Register<CompactLoadingBar, bool>(nameof(ReduceMotion));

    private bool _frameScheduled;
    private int _frameGeneration;
    private TimeSpan _animationTime;
    private readonly List<Visual> _visibilityAncestors = [];

    public IBrush? BackgroundBrush
    {
        get => GetValue(BackgroundBrushProperty);
        set => SetValue(BackgroundBrushProperty, value);
    }

    public IBrush? BorderBrush
    {
        get => GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? IndicatorBrush
    {
        get => GetValue(IndicatorBrushProperty);
        set => SetValue(IndicatorBrushProperty, value);
    }

    public bool ReduceMotion
    {
        get => GetValue(ReduceMotionProperty);
        set => SetValue(ReduceMotionProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        base.Render(context);
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var outerRect = new Rect(0.5, 0.5, Math.Max(0, Bounds.Width - 1), Math.Max(0, Bounds.Height - 1));
        context.DrawRectangle(
            BackgroundBrush,
            BorderBrush is null ? null : new Pen(BorderBrush, 1),
            new RoundedRect(outerRect, Bounds.Height / 2));

        const double trackWidth = 112;
        const double trackHeight = 4;
        const double segmentWidth = 34;
        var actualTrackWidth = Math.Min(trackWidth, Math.Max(0, Bounds.Width - 28));
        var trackX = (Bounds.Width - actualTrackWidth) / 2;
        var trackY = (Bounds.Height - trackHeight) / 2;
        var trackRect = new RoundedRect(
            new Rect(trackX, trackY, actualTrackWidth, trackHeight),
            trackHeight / 2);
        context.DrawRectangle(TrackBrush, null, trackRect);

        var availableTravel = Math.Max(0, actualTrackWidth - segmentWidth);
        var phase = ReduceMotion
            ? 0.5
            : (Math.Sin((_animationTime.TotalMilliseconds / 1150d) * Math.Tau) + 1) / 2;
        var segmentX = trackX + (availableTravel * phase);
        context.DrawRectangle(
            IndicatorBrush,
            null,
            new RoundedRect(
                new Rect(segmentX, trackY, Math.Min(segmentWidth, actualTrackWidth), trackHeight),
                trackHeight / 2));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && change.NewValue is true)
        {
            ScheduleFrame();
        }
        else if (change.Property == ReduceMotionProperty ||
                 change.Property == BackgroundBrushProperty ||
                 change.Property == BorderBrushProperty ||
                 change.Property == TrackBrushProperty ||
                 change.Property == IndicatorBrushProperty)
        {
            InvalidateVisual();
            if (!ReduceMotion)
            {
                ScheduleFrame();
            }
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs eventArgs)
    {
        base.OnAttachedToVisualTree(eventArgs);
        foreach (var ancestor in this.GetVisualAncestors())
        {
            _visibilityAncestors.Add(ancestor);
            ancestor.PropertyChanged += OnAncestorVisibilityChanged;
        }
        ScheduleFrame();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs eventArgs)
    {
        foreach (var ancestor in _visibilityAncestors)
            ancestor.PropertyChanged -= OnAncestorVisibilityChanged;
        _visibilityAncestors.Clear();
        _frameGeneration++;
        _frameScheduled = false;
        base.OnDetachedFromVisualTree(eventArgs);
    }

    private void OnAncestorVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == IsVisibleProperty && IsEffectivelyVisible) ScheduleFrame();
    }

    private void ScheduleFrame()
    {
        if (_frameScheduled || !IsEffectivelyVisible || ReduceMotion)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        _frameScheduled = true;
        var generation = _frameGeneration;
        topLevel.RequestAnimationFrame(renderingTime => OnAnimationFrame(generation, renderingTime));
    }

    private void OnAnimationFrame(int generation, TimeSpan renderingTime)
    {
        // A callback from a previous owner must not restart or duplicate the new loop.
        if (generation != _frameGeneration) return;
        _frameScheduled = false;
        if (!IsEffectivelyVisible || TopLevel.GetTopLevel(this) is null)
        {
            return;
        }

        _animationTime = renderingTime;
        InvalidateVisual();
        ScheduleFrame();
    }
}
