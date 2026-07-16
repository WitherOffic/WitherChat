using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using WitherChat.Models;

namespace WitherChat.Services;

public static class ThirdPartyEmoteTokenizer
{
    private static readonly Regex Segments = new(@"\s+|\S+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<ChatMessagePartModel> Tokenize(
        string text,
        Func<string, ThirdPartyEmote?> findEmote)
    {
        var result = new List<ChatMessagePartModel>();
        if (string.IsNullOrEmpty(text))
        {
            return result;
        }

        foreach (Match match in Segments.Matches(text))
        {
            var token = match.Value;
            if (string.IsNullOrWhiteSpace(token) || token.Contains("://", StringComparison.Ordinal))
            {
                AppendText(result, token);
                continue;
            }

            var exact = findEmote(token);
            if (exact is not null)
            {
                AppendEmote(result, exact, token, allowOverlay: true);
                continue;
            }

            var (leading, core, trailing) = TrimBoundaryPunctuation(token);
            var trimmed = string.IsNullOrEmpty(core) ? null : findEmote(core);
            if (trimmed is null)
            {
                AppendText(result, token);
                continue;
            }

            if (!string.IsNullOrEmpty(leading))
            {
                AppendText(result, leading);
            }

            AppendEmote(result, trimmed, core, allowOverlay: string.IsNullOrEmpty(leading));
            if (!string.IsNullOrEmpty(trailing))
            {
                AppendText(result, trailing);
            }
        }

        return result;
    }

    private static void AppendText(List<ChatMessagePartModel> result, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (result.Count > 0 && result[^1].Kind == ChatMessagePartKind.Text)
        {
            result[^1] = ChatMessagePartModel.TextPart(result[^1].Text + text);
            return;
        }

        result.Add(ChatMessagePartModel.TextPart(text));
    }

    private static void AppendEmote(
        List<ChatMessagePartModel> result,
        ThirdPartyEmote emote,
        string fallbackText,
        bool allowOverlay)
    {
        var part = ChatMessagePartModel.ThirdPartyEmote(emote);
        if (!part.IsZeroWidth)
        {
            result.Add(part);
            return;
        }

        if (allowOverlay && TryGetOverlayBase(result, out var basePart, out var whitespaceIndex))
        {
            if (whitespaceIndex >= 0)
            {
                result.RemoveAt(whitespaceIndex);
            }
            basePart.AddOverlay(part);
            return;
        }

        // Some 7TV zero-width emotes are intentionally sent on their own.
        // Without a preceding emote there is nothing to composite onto, so
        // render the media as a normal standalone emote instead of leaking its code as text.
        result.Add(part);
    }

    private static bool TryGetOverlayBase(
        IReadOnlyList<ChatMessagePartModel> result,
        out ChatMessagePartModel basePart,
        out int whitespaceIndex)
    {
        whitespaceIndex = -1;
        var index = result.Count - 1;
        if (index >= 0 &&
            result[index].Kind == ChatMessagePartKind.Text &&
            !string.IsNullOrEmpty(result[index].Text) &&
            string.IsNullOrWhiteSpace(result[index].Text))
        {
            whitespaceIndex = index;
            index--;
        }

        if (index >= 0 &&
            result[index].Kind is ChatMessagePartKind.TwitchEmote or ChatMessagePartKind.ThirdPartyEmote &&
            !result[index].IsZeroWidth)
        {
            basePart = result[index];
            return true;
        }

        basePart = null!;
        whitespaceIndex = -1;
        return false;
    }

#if DEBUG
    public static void RunSelfTests()
    {
        var emotes = new Dictionary<string, ThirdPartyEmote>(StringComparer.Ordinal)
        {
            ["ф"] = TestEmote("1", "ф"),
            ["0"] = TestEmote("2", "0"),
            ["f"] = TestEmote("3", "f"),
            ["KEKW"] = TestEmote("4", "KEKW")
        };
        ThirdPartyEmote? Find(string code) => emotes.TryGetValue(code, out var emote) ? emote : null;

        AssertKinds("ф", Find, ChatMessagePartKind.ThirdPartyEmote);
        AssertKinds("0", Find, ChatMessagePartKind.ThirdPartyEmote);
        AssertKinds("f", Find, ChatMessagePartKind.ThirdPartyEmote);
        AssertKinds("привет ф", Find, ChatMessagePartKind.Text, ChatMessagePartKind.ThirdPartyEmote);
        AssertKinds("фраза", Find, ChatMessagePartKind.Text);
        AssertKinds("обычное текстовое сообщение", Find, ChatMessagePartKind.Text);
        AssertKinds("100", Find, ChatMessagePartKind.Text);
        AssertKinds("0 0 0", Find,
            ChatMessagePartKind.ThirdPartyEmote, ChatMessagePartKind.Text,
            ChatMessagePartKind.ThirdPartyEmote, ChatMessagePartKind.Text,
            ChatMessagePartKind.ThirdPartyEmote);
        AssertKinds("KEKW!", Find, ChatMessagePartKind.ThirdPartyEmote, ChatMessagePartKind.Text);

        var spaced = Tokenize("0   ф", Find);
        if (string.Concat(spaced.Select(part => part.Text)) != "0   ф")
        {
            throw new InvalidOperationException("7TV tokenizer did not preserve message text.");
        }

        var baseEmote = TestEmote("base", "BASE");
        var overlay = new ThirdPartyEmote(
            "overlay", "OVERLAY", "https://cdn.7tv.app/emote/overlay/1x.png", "7TV", IsZeroWidth: true);
        ThirdPartyEmote? FindComposite(string code) => code switch
        {
            "BASE" => baseEmote,
            "OVERLAY" => overlay,
            _ => null
        };
        var composite = Tokenize("BASE OVERLAY OVERLAY", FindComposite);
        if (composite.Count != 1 || composite[0].OverlayParts.Count != 2 || composite[0].Text != "BASE")
        {
            throw new InvalidOperationException("7TV zero-width composition self-test failed.");
        }

        var orphan = Tokenize("OVERLAY", FindComposite);
        if (orphan.Count != 1 || orphan[0].Kind != ChatMessagePartKind.ThirdPartyEmote ||
            !orphan[0].IsZeroWidth || orphan[0].Text != "OVERLAY")
        {
            throw new InvalidOperationException("7TV standalone zero-width emote self-test failed.");
        }
    }
#endif

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

    private static bool IsBoundary(Rune rune)
    {
        return Rune.GetUnicodeCategory(rune) is
            UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation or
            UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or
            UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation or
            UnicodeCategory.OtherPunctuation or UnicodeCategory.MathSymbol or
            UnicodeCategory.CurrencySymbol or UnicodeCategory.ModifierSymbol or UnicodeCategory.OtherSymbol;
    }

#if DEBUG
    private static ThirdPartyEmote TestEmote(string id, string code) =>
        new(id, code, $"https://cdn.7tv.app/emote/{id}/2x.png", "7TV");

    private static void AssertKinds(
        string text,
        Func<string, ThirdPartyEmote?> findEmote,
        params ChatMessagePartKind[] expected)
    {
        var actual = Tokenize(text, findEmote).Select(part => part.Kind).ToArray();
        if (!actual.SequenceEqual(expected))
        {
            throw new InvalidOperationException($"7TV tokenizer self-test failed for '{text}'.");
        }
    }
#endif
}
