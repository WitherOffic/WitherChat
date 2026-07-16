using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WitherChat.Models;
using WitherChat.Services;

namespace WitherChat.Controls;

public sealed class AnimatedEmoteImage : Image
{
    private static readonly TimeSpan MaximumFrameCatchUp = TimeSpan.FromMilliseconds(250);
    public static readonly DependencyProperty MediaProperty = DependencyProperty.Register(
        nameof(Media),
        typeof(EmoteMedia),
        typeof(AnimatedEmoteImage),
        new PropertyMetadata(null, OnMediaChanged));

    private static readonly List<AnimatedEmoteImage> ActiveImages = [];
    private static bool _renderingHooked;
    private static bool _isFastScrolling;
    private TimeSpan _lastRenderTime;
    private TimeSpan _frameElapsed;
    private int _frameIndex;

    static AnimatedEmoteImage()
    {
        AnimationService.ReduceMotionChanged += (_, _) => RefreshRenderingHook();
    }

    public AnimatedEmoteImage()
    {
        Loaded += (_, _) =>
        {
            ResetFrameClock();
            UpdateRegistration();
        };
        IsVisibleChanged += (_, _) =>
        {
            ResetFrameClock();
            UpdateRegistration();
        };
        Unloaded += (_, _) =>
        {
            Unregister(this);
            Source = null;
            _frameIndex = 0;
            _lastRenderTime = TimeSpan.Zero;
            _frameElapsed = TimeSpan.Zero;
        };
    }

    internal static int ActiveCount => ActiveImages.Count;

    internal static void SetFastScrolling(bool isFastScrolling)
    {
        if (_isFastScrolling == isFastScrolling)
        {
            return;
        }

        _isFastScrolling = isFastScrolling;
        foreach (var image in ActiveImages)
        {
            image.ResetFrameClock();
        }

        RefreshRenderingHook();
    }

    public EmoteMedia? Media
    {
        get => (EmoteMedia?)GetValue(MediaProperty);
        set => SetValue(MediaProperty, value);
    }

    private static void OnMediaChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var image = (AnimatedEmoteImage)d;
        image._frameIndex = 0;
        image._frameElapsed = TimeSpan.Zero;
        image._lastRenderTime = TimeSpan.Zero;
        image.Opacity = 1;
        image.Source = image.Media?.FirstFrame;
        image.UpdateRegistration();
    }

    private void UpdateRegistration()
    {
        var media = Media;
        if (IsLoaded && IsVisible && media is not null)
        {
            Opacity = 1;
            Source = media.FirstFrame;
            if (media.IsAnimated)
            {
                if (!ActiveImages.Contains(this))
                {
                    ActiveImages.Add(this);
                }
                RefreshRenderingHook();
            }
            else
            {
                Unregister(this);
            }
        }
        else
        {
            Unregister(this);
        }
    }

    private static void Unregister(AnimatedEmoteImage image)
    {
        ActiveImages.Remove(image);
        RefreshRenderingHook();
    }

    private static void RefreshRenderingHook()
    {
        var shouldHook = ActiveImages.Count > 0 && !AnimationService.ReduceMotion && !_isFastScrolling;
        if (shouldHook == _renderingHooked)
        {
            return;
        }

        if (shouldHook)
        {
            foreach (var image in ActiveImages)
            {
                image.ResetFrameClock();
            }
            CompositionTarget.Rendering += OnRendering;
        }
        else
        {
            CompositionTarget.Rendering -= OnRendering;
            if (AnimationService.ReduceMotion)
            {
                foreach (var image in ActiveImages)
                {
                    var firstFrame = image.Media?.FirstFrame;
                    if (!ReferenceEquals(image.Source, firstFrame))
                    {
                        image.Source = firstFrame;
                    }
                    image.ResetFrameClock();
                }
            }
        }

        _renderingHooked = shouldHook;
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs rendering)
        {
            return;
        }

        for (var index = ActiveImages.Count - 1; index >= 0; index--)
        {
            if (index < ActiveImages.Count)
            {
                ActiveImages[index].Advance(rendering.RenderingTime);
            }
        }
    }

    private void Advance(TimeSpan renderingTime)
    {
        var media = Media;
        if (!IsVisible || media is null || !media.IsAnimated)
        {
            _lastRenderTime = renderingTime;
            _frameElapsed = TimeSpan.Zero;
            return;
        }

        if (_isFastScrolling)
        {
            _lastRenderTime = renderingTime;
            _frameElapsed = TimeSpan.Zero;
            return;
        }

        if (_lastRenderTime == TimeSpan.Zero)
        {
            _lastRenderTime = renderingTime;
            return;
        }

        var frameDelta = renderingTime - _lastRenderTime;
        _lastRenderTime = renderingTime;
        if (frameDelta <= TimeSpan.Zero || frameDelta > MaximumFrameCatchUp)
        {
            frameDelta = MaximumFrameCatchUp;
        }

        _frameElapsed += frameDelta;
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

    private void ResetFrameClock()
    {
        _lastRenderTime = TimeSpan.Zero;
        _frameElapsed = TimeSpan.Zero;
    }
}
