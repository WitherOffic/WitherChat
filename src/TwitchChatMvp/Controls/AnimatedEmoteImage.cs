using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TwitchChatMvp.Models;
using TwitchChatMvp.Services;

namespace TwitchChatMvp.Controls;

public sealed class AnimatedEmoteImage : Image
{
    public static readonly DependencyProperty MediaProperty = DependencyProperty.Register(
        nameof(Media),
        typeof(EmoteMedia),
        typeof(AnimatedEmoteImage),
        new PropertyMetadata(null, OnMediaChanged));

    private static readonly HashSet<AnimatedEmoteImage> ActiveImages = [];
    private static bool _renderingHooked;
    private TimeSpan _lastRenderTime;
    private TimeSpan _frameElapsed;
    private int _frameIndex;

    public AnimatedEmoteImage()
    {
        Loaded += (_, _) => UpdateRegistration();
        Unloaded += (_, _) => Unregister(this);
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
        image.Source = image.Media?.FirstFrame;
        image.UpdateRegistration();
    }

    private void UpdateRegistration()
    {
        if (IsLoaded && Media?.IsAnimated == true)
        {
            ActiveImages.Add(this);
            if (!_renderingHooked)
            {
                CompositionTarget.Rendering += OnRendering;
                _renderingHooked = true;
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
        if (_renderingHooked && ActiveImages.Count == 0)
        {
            CompositionTarget.Rendering -= OnRendering;
            _renderingHooked = false;
        }
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs rendering)
        {
            return;
        }

        foreach (var image in ActiveImages.ToArray())
        {
            image.Advance(rendering.RenderingTime);
        }
    }

    private void Advance(TimeSpan renderingTime)
    {
        var media = Media;
        if (!IsVisible || media is null || !media.IsAnimated)
        {
            return;
        }

        if (AnimationService.ReduceMotion)
        {
            Source = media.FirstFrame;
            _lastRenderTime = renderingTime;
            return;
        }

        if (_lastRenderTime == TimeSpan.Zero)
        {
            _lastRenderTime = renderingTime;
            return;
        }

        _frameElapsed += renderingTime - _lastRenderTime;
        _lastRenderTime = renderingTime;
        var delay = media.FrameDelays[_frameIndex];
        while (_frameElapsed >= delay)
        {
            _frameElapsed -= delay;
            _frameIndex = (_frameIndex + 1) % media.Frames.Count;
            delay = media.FrameDelays[_frameIndex];
        }

        Source = media.Frames[_frameIndex];
    }
}
