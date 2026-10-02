using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using WitherChat.Core.Models;

namespace WitherChat.Core.Services;

public static partial class ThirdPartyEmoteTokenizer
{
    public static IReadOnlyList<ChatMessagePart> Tokenize(
        IReadOnlyList<ChatMessagePart> source,
        IReadOnlyDictionary<string, ThirdPartyEmote>? catalog)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (catalog is null || catalog.Count == 0)
        {
            return source;
        }

        var result = new List<ChatMessagePart>(source.Count + 4);
        foreach (var part in source)
        {
            if (part.Kind != ChatMessagePartKind.Text)
            {
                result.Add(part);
                continue;
            }

            TokenizeText(part.Text, catalog, result);
        }

        return result;
    }

    private static void TokenizeText(
        string text,
        IReadOnlyDictionary<string, ThirdPartyEmote> catalog,
        List<ChatMessagePart> result)
    {
        foreach (Match match in SegmentRegex().Matches(text))
        {
            var token = match.Value;
            if (string.IsNullOrWhiteSpace(token) || token.Contains("://", StringComparison.Ordinal))
            {
                AppendText(result, token);
                continue;
            }

            if (catalog.TryGetValue(token, out var exact))
            {
                result.Add(CreateEmotePart(exact));
                continue;
            }

            var (leading, core, trailing) = TrimBoundaryPunctuation(token);
            if (core.Length == 0 || !catalog.TryGetValue(core, out var trimmed))
            {
                AppendText(result, token);
                continue;
            }

            AppendText(result, leading);
            result.Add(CreateEmotePart(trimmed));
            AppendText(result, trailing);
        }
    }

    private static ChatMessagePart CreateEmotePart(ThirdPartyEmote emote) => new()
    {
        Kind = ChatMessagePartKind.Emote,
        Text = emote.Code,
        ImageUri = emote.ImageUri,
        Provider = emote.Provider,
        EmoteId = emote.Id,
        IsZeroWidth = emote.IsZeroWidth,
        SourceWidth = emote.SourceWidth,
        SourceHeight = emote.SourceHeight
    };

    private static void AppendText(List<ChatMessagePart> result, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (result.Count > 0 && result[^1].Kind == ChatMessagePartKind.Text)
        {
            result[^1] = ChatMessagePart.PlainText(result[^1].Text + text);
        }
        else
        {
            result.Add(ChatMessagePart.PlainText(text));
        }
    }

    private static (string Leading, string Core, string Trailing) TrimBoundaryPunctuation(string token)
    {
        var runes = token.EnumerateRunes().ToArray();
        var first = 0;
        while (first < runes.Length && IsBoundary(runes[first]))
        {
            first++;
        }

        var last = runes.Length - 1;
        while (last >= first && IsBoundary(runes[last]))
        {
            last--;
        }

        var leadingLength = runes.Take(first).Sum(rune => rune.Utf16SequenceLength);
        var coreLength = runes.Skip(first).Take(last - first + 1).Sum(rune => rune.Utf16SequenceLength);
        return (
            token[..leadingLength],
            coreLength > 0 ? token.Substring(leadingLength, coreLength) : string.Empty,
            token[(leadingLength + coreLength)..]);
    }

    private static bool IsBoundary(Rune rune) => Rune.GetUnicodeCategory(rune) is
        UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation or
        UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or
        UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation or
        UnicodeCategory.OtherPunctuation or UnicodeCategory.MathSymbol or
        UnicodeCategory.CurrencySymbol or UnicodeCategory.ModifierSymbol or UnicodeCategory.OtherSymbol;

    [GeneratedRegex(@"\s+|\S+", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentRegex();
}
