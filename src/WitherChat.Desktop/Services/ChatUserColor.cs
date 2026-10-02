using Avalonia.Media;

namespace WitherChat.Desktop.Services;

internal static class ChatUserColor
{
    private static readonly IBrush DefaultUserBrush = new SolidColorBrush(Color.Parse("#9BB6FF"));
    private static readonly IBrush DefaultLightUserBrush = new SolidColorBrush(Color.Parse("#3456A8"));

    public static IBrush Create(string value, bool light)
    {
        var fallback = light ? DefaultLightUserBrush : DefaultUserBrush;
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        Color color;
        try { color = Color.Parse(value); }
        catch (FormatException) { return fallback; }
        if (color.A == 0) return fallback;
        color = Color.FromRgb(color.R, color.G, color.B);
        // Use the highlighted row as the conservative background. Keep readable
        // Twitch colors unchanged; blend only low-contrast colors toward black/white.
        var background = light ? Color.FromRgb(215, 221, 240) : Color.FromRgb(37, 40, 51);
        if (Contrast(color, background) >= 4.5) return new SolidColorBrush(color);
        var target = light ? (byte)0 : (byte)255;
        var low = 0d;
        var high = 1d;
        for (var index = 0; index < 12; index++)
        {
            var amount = (low + high) / 2;
            if (Contrast(Blend(color, target, amount), background) >= 4.5) high = amount;
            else low = amount;
        }
        return new SolidColorBrush(Blend(color, target, high));

        static Color Blend(Color source, byte target, double amount) => Color.FromRgb(
            (byte)Math.Round(source.R + (target - source.R) * amount),
            (byte)Math.Round(source.G + (target - source.G) * amount),
            (byte)Math.Round(source.B + (target - source.B) * amount));
        static double Linear(byte value)
        {
            var channel = value / 255d;
            return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color value) =>
            0.2126 * Linear(value.R) + 0.7152 * Linear(value.G) + 0.0722 * Linear(value.B);
        static double Contrast(Color first, Color second)
        {
            var a = Luminance(first);
            var b = Luminance(second);
            return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        }
    }
}
