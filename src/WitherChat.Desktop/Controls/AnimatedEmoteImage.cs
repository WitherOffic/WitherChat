using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using WitherChat.Desktop.Services;

namespace WitherChat.Desktop.Controls;

public sealed class AnimatedEmoteImage : Image
{
    private static readonly HashSet<AnimatedEmoteImage> ActiveImages = [];
    private static readonly HashSet<TopLevel> ScheduledTopLevels = [];
    private static bool _reduceMotion;
    private static bool _fastScrolling;

    public static readonly StyledProperty<ChatImageResource?> ResourceProperty =
        AvaloniaProperty.Register<AnimatedEmoteImage, ChatImageResource?>(nameof(Resource));

    private TimeSpan _lastFrameTime;
    private TimeSpan _frameElapsed;
    private int _frameIndex;
    private ChatImageResource? _subscribedResource;
    private bool _isAttached;

    public ChatImageResource? Resource
    {
        get => GetValue(ResourceProperty);
        set => SetValue(ResourceProperty, value);
    }

    internal static int ActiveCount => ActiveImages.Count;

    internal static void SetReduceMotion(bool reduceMotion)
    {
        if (_reduceMotion == reduceMotion)
        {
            return;
        }

        _reduceMotion = reduceMotion;
        RefreshAll();
    }

    internal static void SetFastScrolling(bool fastScrolling)
    {
        if (_fastScrolling == fastScrolling)
        {
            return;
        }

        _fastScrolling = fastScrolling;
        RefreshAll();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Property == ResourceProperty)
        {
            UpdateResourceSubscription();
        }

        base.OnPropertyChanged(change);
        if (change.Property == ResourceProperty)
        {
            RefreshMedia();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs eventArgs)
    {
        base.OnAttachedToVisualTree(eventArgs);
        _isAttached = true;
        UpdateResourceSubscription();
        RefreshMedia();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs eventArgs)
    {
        _isAttached = false;
        UpdateResourceSubscription();
        ActiveImages.Remove(this);
        ResetClock();
        base.OnDetachedFromVisualTree(eventArgs);
    }

    private void UpdateResourceSubscription()
    {
        var resource = _isAttached ? Resource : null;
        if (ReferenceEquals(resource, _subscribedResource))
        {
            return;
        }
        if (_subscribedResource is { } previous)
        {
            previous.PropertyChanged -= OnResourcePropertyChanged;
        }
        _subscribedResource = resource;
        if (resource is not null)
        {
            // Cached emotes outlive virtualized message rows. Subscribe only while
            // attached, otherwise each discarded inline keeps its whole row alive.
            resource.PropertyChanged += OnResourcePropertyChanged;
        }
    }

    private void OnResourcePropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(ChatImageResource.Media) or nameof(ChatImageResource.Image))
        {
            RefreshMedia();
        }
    }

    private void RefreshMedia()
    {
        var media = Resource?.Media;
        Source = media?.FirstFrame;
        _frameIndex = 0;
        ResetClock();
        if (TopLevel.GetTopLevel(this) is not null && media?.IsAnimated == true)
        {
            ActiveImages.Add(this);
            ScheduleFrame();
        }
        else
        {
            ActiveImages.Remove(this);
        }
    }

    private static void RefreshAll()
    {
        foreach (var image in ActiveImages.ToArray())
        {
            image.Source = image.Resource?.Media?.FirstFrame;
            image._frameIndex = 0;
            image.ResetClock();
        }

        if (!_reduceMotion && !_fastScrolling)
        {
            foreach (var image in ActiveImages)
            {
                image.ScheduleFrame();
            }
        }
    }

    private void ScheduleFrame()
    {
        if (_reduceMotion || _fastScrolling || Resource?.Media?.IsAnimated != true)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null || !ScheduledTopLevels.Add(topLevel))
        {
            return;
        }

        topLevel.RequestAnimationFrame(time => AdvanceTopLevel(topLevel, time));
    }

    private static void AdvanceTopLevel(TopLevel topLevel, TimeSpan renderingTime)
    {
        ScheduledTopLevels.Remove(topLevel);
        if (_reduceMotion || _fastScrolling)
        {
            return;
        }

        var hasActiveImages = false;
        foreach (var image in ActiveImages.ToArray())
        {
            if (!ReferenceEquals(TopLevel.GetTopLevel(image), topLevel))
            {
                continue;
            }

            image.Advance(renderingTime);
            hasActiveImages = true;
        }

        if (hasActiveImages)
        {
            var owner = ActiveImages.FirstOrDefault(image => ReferenceEquals(TopLevel.GetTopLevel(image), topLevel));
            owner?.ScheduleFrame();
        }
    }

    private void Advance(TimeSpan renderingTime)
    {
        var media = Resource?.Media;
        if (media?.IsAnimated != true || media.Frames.Count != media.FrameDelays.Count)
        {
            RefreshMedia();
            return;
        }

        if (_lastFrameTime == TimeSpan.Zero)
        {
            _lastFrameTime = renderingTime;
            return;
        }

        var delta = renderingTime - _lastFrameTime;
        _lastFrameTime = renderingTime;
        if (delta <= TimeSpan.Zero || delta > TimeSpan.FromMilliseconds(250))
        {
            delta = TimeSpan.FromMilliseconds(250);
        }

        _frameElapsed += delta;
        var delay = media.FrameDelays[_frameIndex];
        for (var advanced = 0; advanced < media.Frames.Count && _frameElapsed >= delay; advanced++)
        {
            _frameElapsed -= delay;
            _frameIndex = (_frameIndex + 1) % media.Frames.Count;
            delay = media.FrameDelays[_frameIndex];
        }

        var frame = media.Frames[_frameIndex];
        if (!ReferenceEquals(Source, frame))
        {
            Source = frame;
        }
    }

    private void ResetClock()
    {
        _lastFrameTime = TimeSpan.Zero;
        _frameElapsed = TimeSpan.Zero;
    }
}
