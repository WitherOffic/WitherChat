using System.ComponentModel;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CommunityToolkit.Mvvm.ComponentModel;
using WitherChat.Desktop.Controls;
using WitherChat.Desktop.Services;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed class AnimatedEmoteLifetimeTests
{
    private const string TwoFrameGif =
        "R0lGODlhAQABAHAAACH5BAEAAAEALAAAAAABAAEAgf8AAAAAAAAAAAAAAAICRAEA" +
        "IfkEAQAAAQAsAAAAAAEAAQCBAAD/AAAAAAAAAAAAAgJEAQA7";

    [AvaloniaFact]
    public void DetachedImagesReleaseCachedResourceSubscription()
    {
        using var media = ChatImageDecoder.Decode(Convert.FromBase64String(TwoFrameGif));
        var resource = new ChatImageResource(28);
        resource.SetMedia(media!, 28);
        var window = new Window { Width = 200, Height = 100 };
        window.Show();
        try
        {
            for (var index = 0; index < 50; index++)
            {
                var image = new AnimatedEmoteImage { Resource = resource };
                Assert.Equal(0, ImageSubscriberCount(resource));
                Assert.Same(media!.FirstFrame, image.Source);

                window.Content = image;
                Assert.Equal(1, ImageSubscriberCount(resource));
                window.Content = null;
                Assert.Equal(0, ImageSubscriberCount(resource));
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ImageReattachShowsMediaLoadedWhileDetached()
    {
        using var media = ChatImageDecoder.Decode(Convert.FromBase64String(TwoFrameGif));
        using var replacement = ChatImageDecoder.Decode(Convert.FromBase64String(TwoFrameGif));
        var resource = new ChatImageResource(28);
        var image = new AnimatedEmoteImage { Resource = resource };
        var window = new Window { Width = 200, Height = 100, Content = image };
        window.Show();
        try
        {
            Assert.Null(image.Source);
            window.Content = null;
            resource.SetMedia(media!, 28);
            Assert.Null(image.Source);
            Assert.Equal(0, ImageSubscriberCount(resource));

            window.Content = image;
            Assert.Same(media!.FirstFrame, image.Source);
            Assert.Equal(1, ImageSubscriberCount(resource));
            resource.SetMedia(replacement!, 28);
            Assert.Same(replacement!.FirstFrame, image.Source);

            window.Content = null;
            window.Content = image;
            Assert.Same(replacement.FirstFrame, image.Source);
            Assert.Equal(1, ImageSubscriberCount(resource));
        }
        finally
        {
            window.Close();
        }
        Assert.Equal(0, ImageSubscriberCount(resource));
    }

    [AvaloniaFact]
    public void ReplacingImageResourceMovesTheActiveSubscription()
    {
        using var firstMedia = ChatImageDecoder.Decode(Convert.FromBase64String(TwoFrameGif));
        using var secondMedia = ChatImageDecoder.Decode(Convert.FromBase64String(TwoFrameGif));
        var first = new ChatImageResource(28);
        var second = new ChatImageResource(28);
        first.SetMedia(firstMedia!, 28);
        second.SetMedia(secondMedia!, 28);
        var image = new AnimatedEmoteImage { Resource = first };
        var window = new Window { Width = 200, Height = 100, Content = image };
        window.Show();
        try
        {
            Assert.Equal(1, ImageSubscriberCount(first));
            image.Resource = second;
            Assert.Equal(0, ImageSubscriberCount(first));
            Assert.Equal(1, ImageSubscriberCount(second));
            Assert.Same(secondMedia!.FirstFrame, image.Source);

            image.Resource = null;
            Assert.Equal(0, ImageSubscriberCount(second));
            Assert.Null(image.Source);
        }
        finally
        {
            window.Close();
        }
    }

    private static int ImageSubscriberCount(ChatImageResource resource)
    {
        // Inspect only our explicit strong event handlers, avoiding nondeterministic
        // GC timing and unrelated weak Avalonia bindings in lifetime regressions.
        var field = typeof(ObservableObject).GetField(
            nameof(ObservableObject.PropertyChanged), BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var handlers = (PropertyChangedEventHandler?)field.GetValue(resource);
        return handlers?.GetInvocationList().Count(handler => handler.Target is AnimatedEmoteImage) ?? 0;
    }
}
