using Avalonia.Media.Imaging;

namespace WitherChat.Desktop.Services;

public sealed class ChatImageMedia : IDisposable
{
    public IReadOnlyList<Bitmap> Frames { get; }
    public IReadOnlyList<TimeSpan> FrameDelays { get; }
    public Bitmap? FirstFrame => Frames.Count > 0 ? Frames[0] : null;
    public bool IsAnimated => Frames.Count > 1;
    internal long DecodedByteCount { get; }
    private long _memoryPressureBytes;
    private int _disposed;

    public ChatImageMedia(IReadOnlyList<Bitmap> frames, IReadOnlyList<TimeSpan> frameDelays)
    {
        Frames = frames;
        FrameDelays = frameDelays;
        // The decoder creates BGRA8888 frames, whose native pixel storage can be
        // orders of magnitude larger than the small managed Bitmap wrappers.
        DecodedByteCount = frames.Sum(frame =>
            checked((long)frame.PixelSize.Width * frame.PixelSize.Height * 4));
        if (DecodedByteCount > 0)
        {
            GC.AddMemoryPressure(DecodedByteCount);
            _memoryPressureBytes = DecodedByteCount;
        }
    }

    ~ChatImageMedia() => ReleaseMemoryPressure();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            foreach (var frame in Frames) frame.Dispose();
        }
        finally
        {
            ReleaseMemoryPressure();
            GC.SuppressFinalize(this);
        }
    }

    private void ReleaseMemoryPressure()
    {
        var bytes = Interlocked.Exchange(ref _memoryPressureBytes, 0);
        if (bytes > 0) GC.RemoveMemoryPressure(bytes);
        // On the finalizer path the existing bitmap/native finalizers own frame
        // destruction; never touch live UI objects from the finalizer thread.
    }
}
