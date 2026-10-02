using System.Globalization;
using System.Text;
using WitherChat.Core.Models;

namespace WitherChat.Core.Services;

public static class TwitchEmoteParser
{
    public static IReadOnlyList<ChatMessagePart> Parse(string text, string emotesTag)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var ranges = ParseRanges(emotesTag, text);
        if (ranges.Count == 0)
        {
            return [ChatMessagePart.PlainText(text)];
        }

        var parts = new List<ChatMessagePart>(ranges.Count * 2 + 1);
        var position = 0;
        foreach (var range in ranges)
        {
            // Ignore malformed and overlapping ranges instead of duplicating text.
            if (range.Start < position)
            {
                continue;
            }

            if (range.Start > position)
            {
                parts.Add(ChatMessagePart.PlainText(text[position..range.Start]));
            }

            var length = range.End - range.Start + 1;
            parts.Add(ChatMessagePart.TwitchEmote(text.Substring(range.Start, length), range.EmoteId));
            position = range.End + 1;
        }

        if (position < text.Length)
        {
            parts.Add(ChatMessagePart.PlainText(text[position..]));
        }

        return parts.Count == 0 ? [ChatMessagePart.PlainText(text)] : parts;
    }

    private static List<EmoteRange> ParseRanges(string emotesTag, string text)
    {
        var ranges = new List<EmoteRange>();
        if (string.IsNullOrWhiteSpace(emotesTag) || text.Length == 0)
        {
            return ranges;
        }

        // Twitch's IRC positions count Unicode scalar values. .NET string indexes
        // count UTF-16 code units, so every range after a supplementary character
        // (for example an emoji) must be translated before slicing the message.
        var utf16Offsets = new List<int> { 0 };
        var utf16Offset = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            utf16Offset += rune.Utf16SequenceLength;
            utf16Offsets.Add(utf16Offset);
        }

        var scalarCount = utf16Offsets.Count - 1;

        foreach (var entry in emotesTag.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0 || separator == entry.Length - 1)
            {
                continue;
            }

            var emoteId = entry[..separator];
            foreach (var value in entry[(separator + 1)..]
                         .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var dash = value.IndexOf('-', StringComparison.Ordinal);
                if (dash <= 0 || dash == value.Length - 1 ||
                    !int.TryParse(value[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out var start) ||
                    !int.TryParse(value[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var end) ||
                    start < 0 || end < start || end >= scalarCount)
                {
                    continue;
                }

                ranges.Add(new EmoteRange(
                    utf16Offsets[start],
                    utf16Offsets[end + 1] - 1,
                    emoteId));
            }
        }

        return ranges
            .OrderBy(range => range.Start)
            .ThenBy(range => range.End)
            .ThenBy(range => range.EmoteId, StringComparer.Ordinal)
            .ToList();
    }

    private sealed record EmoteRange(int Start, int End, string EmoteId);
}
