using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace WitherChat.Desktop.Services;

internal static class ChatImageDecoder
{
    private const int MaximumDimension = 512;
    private const int MaximumAnimationFrames = 180;
    private const long MaximumDecodedPixels = 12L * 1024 * 1024;

    public static ChatImageMedia? Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.IsEmpty)
        {
            return null;
        }

        try
        {
            using var data = SKData.CreateCopy(encoded);
            using var codec = SKCodec.Create(data);
            if (codec is null)
            {
                return null;
            }

            var width = codec.Info.Width;
            var height = codec.Info.Height;
            var frameCount = Math.Max(1, codec.FrameCount);
            var decodedPixels = (long)width * height * frameCount;
            if (width is <= 0 or > MaximumDimension ||
                height is <= 0 or > MaximumDimension ||
                frameCount > MaximumAnimationFrames ||
                decodedPixels > MaximumDecodedPixels)
            {
                return null;
            }

            var info = new SKImageInfo(
                width,
                height,
                SKColorType.Bgra8888,
                SKAlphaType.Premul,
                codec.Info.ColorSpace);
            var frameInfo = codec.FrameInfo;
            var frames = new List<Bitmap>(frameCount);
            var delays = new List<TimeSpan>(frameCount);
            try
            {
                for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
                {
                    using var bitmap = new SKBitmap(info);
                    bitmap.Erase(SKColors.Transparent);
                    var result = codec.GetPixels(
                        info,
                        bitmap.GetPixels(),
                        bitmap.RowBytes,
                        new SKCodecOptions(frameIndex));
                    if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                    {
                        return null;
                    }

                    frames.Add(new Bitmap(
                        PixelFormat.Bgra8888,
                        AlphaFormat.Premul,
                        bitmap.GetPixels(),
                        new PixelSize(width, height),
                        new Vector(96, 96),
                        bitmap.RowBytes));
                    var duration = frameIndex < frameInfo.Length ? frameInfo[frameIndex].Duration : 100;
                    delays.Add(TimeSpan.FromMilliseconds(Math.Max(20, duration)));
                }

                return new ChatImageMedia(frames, delays);
            }
            catch
            {
                foreach (var frame in frames)
                {
                    frame.Dispose();
                }
                throw;
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or InvalidOperationException or OverflowException)
        {
            return null;
        }
    }
}
