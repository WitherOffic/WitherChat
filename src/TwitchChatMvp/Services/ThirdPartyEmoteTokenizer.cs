using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TwitchChatMvp.Models;

namespace TwitchChatMvp.Services;

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
                result.Add(ChatMessagePartModel.TextPart(token));
                continue;
            }

            var exact = findEmote(token);
            if (exact is not null)
            {
                result.Add(ChatMessagePartModel.ThirdPartyEmote(exact));
                continue;
            }

            var (leading, core, trailing) = TrimBoundaryPunctuation(token);
            var trimmed = string.IsNullOrEmpty(core) ? null : findEmote(core);
            if (trimmed is null)
            {
                result.Add(ChatMessagePartModel.TextPart(token));
                continue;
            }

            if (!string.IsNullOrEmpty(leading))
            {
                result.Add(ChatMessagePartModel.TextPart(leading));
            }

            result.Add(ChatMessagePartModel.ThirdPartyEmote(trimmed));
            if (!string.IsNullOrEmpty(trailing))
            {
                result.Add(ChatMessagePartModel.TextPart(trailing));
            }
        }

        return result;
    }

    [Conditional("DEBUG")]
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
        AssertKinds("привет ф", Find, ChatMessagePartKind.Text, ChatMessagePartKind.Text, ChatMessagePartKind.ThirdPartyEmote);
        AssertKinds("фраза", Find, ChatMessagePartKind.Text);
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

    private static bool IsBoundary(Rune rune)
    {
        return Rune.GetUnicodeCategory(rune) is
            UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation or
            UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or
            UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation or
            UnicodeCategory.OtherPunctuation or UnicodeCategory.MathSymbol or
            UnicodeCategory.CurrencySymbol or UnicodeCategory.ModifierSymbol or UnicodeCategory.OtherSymbol;
    }

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
}
