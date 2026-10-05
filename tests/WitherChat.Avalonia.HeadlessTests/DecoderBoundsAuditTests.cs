using Avalonia.Headless.XUnit;
using SkiaSharp;
using WitherChat.Desktop.Services;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed class DecoderBoundsAuditTests
{
    private const string Gif = "R0lGODlhAQABAHAAACH5BAEAAAEALAAAAAABAAEAgf8AAAAAAAAAAAAAAAICRAEA" +
        "IfkEAQAAAQAsAAAAAAEAAQCBAAD/AAAAAAAAAAAAAgJEAQA7";

    [AvaloniaTheory]
    [InlineData(512, 1, true)]
    [InlineData(513, 1, false)]
    [InlineData(1, 513, false)]
    public void DimensionLimitIsCheckedBeforeAllocatingFrames(int width, int height, bool accepted)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Red);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var decoded = ChatImageDecoder.Decode(encoded.ToArray());
        Assert.Equal(accepted, decoded is not null);
        if (accepted) Assert.Equal(width, decoded!.FirstFrame!.PixelSize.Width);
    }

    [AvaloniaTheory]
    [InlineData(180, true)]
    [InlineData(181, false)]
    public void AnimationFrameLimitIsEnforced(int count, bool accepted)
    {
        var original = Convert.FromBase64String(Gif);
        var second = Array.IndexOf(original, (byte)0x21, 14);
        Assert.True(second > 13);
        var block = original.AsSpan(13, second - 13).ToArray();
        using var bytes = new MemoryStream();
        bytes.Write(original.AsSpan(0, 13));
        for (var index = 0; index < count; index++) bytes.Write(block);
        bytes.WriteByte(0x3b);
        using var decoded = ChatImageDecoder.Decode(bytes.ToArray());
        Assert.Equal(accepted, decoded is not null);
        if (accepted) Assert.Equal(count, decoded!.Frames.Count);
    }

    [AvaloniaFact]
    public void InvalidLateFrameIsRejected()
    {
        var original = Convert.FromBase64String(Gif);
        var first = Array.IndexOf(original, (byte)0x2c);
        var second = Array.IndexOf(original, (byte)0x2c, first + 1);
        var found = false;
        for (var value = 9; value <= 16; value++)
        {
            var corrupted = (byte[])original.Clone();
            corrupted[second + 22] = (byte)value;
            using var data = SKData.CreateCopy(corrupted);
            using var codec = SKCodec.Create(data);
            if (codec is null || codec.FrameCount != 2) continue;
            using var pixels = new SKBitmap(codec.Info);
            var firstResult = codec.GetPixels(codec.Info, pixels.GetPixels(), pixels.RowBytes, new SKCodecOptions(0));
            var lastResult = codec.GetPixels(codec.Info, pixels.GetPixels(), pixels.RowBytes, new SKCodecOptions(1));
            if (firstResult != SKCodecResult.Success ||
                lastResult is SKCodecResult.Success or SKCodecResult.IncompleteInput) continue;
            Assert.Null(ChatImageDecoder.Decode(corrupted));
            found = true;
            break;
        }
        Assert.True(found, "The fixture must reach a decode failure after its first valid frame.");
    }
}
