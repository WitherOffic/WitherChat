using System.Text.RegularExpressions;

namespace WitherChat.Desktop.Services;

public sealed record ChatLinkSegment(string Text, Uri? Uri = null, string FullUrl = "")
{
    public bool IsLink => Uri is not null;
}

public static partial class ChatLinkParser
{
    private static readonly IReadOnlyDictionary<string, string> KnownSites =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["t.me"] = "Telegram",
            ["telegram.me"] = "Telegram",
            ["youtube.com"] = "YouTube",
            ["youtu.be"] = "YouTube",
            ["twitch.tv"] = "Twitch",
            ["discord.com"] = "Discord",
            ["discord.gg"] = "Discord",
            ["vk.com"] = "VK",
            ["boosty.to"] = "Boosty",
            ["donationalerts.com"] = "DonationAlerts",
            ["github.com"] = "GitHub",
            ["x.com"] = "X",
            ["twitter.com"] = "X (Twitter)",
            ["instagram.com"] = "Instagram",
            ["tiktok.com"] = "TikTok",
            ["reddit.com"] = "Reddit",
            ["steamcommunity.com"] = "Steam",
            ["steampowered.com"] = "Steam"
        };

    public static IReadOnlyList<ChatLinkSegment> Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        try
        {
            return ParseCore(text);
        }
        catch (RegexMatchTimeoutException)
        {
            // Preserve the entire message; partial link parsing must not lose text.
            return [new ChatLinkSegment(text)];
        }
    }

    private static IReadOnlyList<ChatLinkSegment> ParseCore(string text)
    {
        var result = new List<ChatLinkSegment>();
        var position = 0;
        foreach (Match match in UrlRegex().Matches(text))
        {
            var candidate = TrimTrailingPunctuation(match.Value);
            if (candidate.Length == 0 ||
                !TryCreateSafeUri(candidate, out var uri))
            {
                continue;
            }

            if (match.Index > position)
            {
                result.Add(new ChatLinkSegment(text[position..match.Index]));
            }

            result.Add(new ChatLinkSegment(CreateDisplayLabel(uri), uri, candidate));
            position = match.Index + candidate.Length;
        }

        if (position < text.Length)
        {
            result.Add(new ChatLinkSegment(text[position..]));
        }

        return result.Count == 0 ? [new ChatLinkSegment(text)] : result;
    }

    private static bool TryCreateSafeUri(string candidate, out Uri uri)
    {
        var hasHttpScheme = candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                            candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        var absoluteCandidate = hasHttpScheme ? candidate : "https://" + candidate;
        if (Uri.TryCreate(absoluteCandidate, UriKind.Absolute, out var parsed) &&
            parsed.Host.Length > 0 &&
            parsed.Scheme is "http" or "https")
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    private static string CreateDisplayLabel(Uri uri)
    {
        var host = uri.IdnHost.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host[4..];
        }

        var knownSite = KnownSites.FirstOrDefault(pair =>
            string.Equals(host, pair.Key, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + pair.Key, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(knownSite.Value)
            ? host
            : knownSite.Value + " · " + host;
    }

    private static string TrimTrailingPunctuation(string value)
    {
        var length = value.Length;
        while (length > 0 && value[length - 1] is '.' or ',' or '!' or '?' or ';' or ':')
        {
            length--;
        }

        length = TrimUnbalancedClosingCharacter(value, length, '(', ')');
        length = TrimUnbalancedClosingCharacter(value, length, '[', ']');
        return value[..length];
    }

    private static int TrimUnbalancedClosingCharacter(
        string value,
        int length,
        char opening,
        char closing)
    {
        var span = value.AsSpan(0, length);
        var excessClosing = span.Count(closing) - span.Count(opening);
        while (length > 0 && excessClosing > 0 && value[length - 1] == closing)
        {
            length--;
            excessClosing--;
        }
        return length;
    }

    [GeneratedRegex(
        @"(?i)(?<![\p{L}\p{N}_@])(?:(?:https?://|www\.)[^\s<>{}\[\]""']+|(?:[\p{L}\p{N}](?:[\p{L}\p{N}-]{0,61}[\p{L}\p{N}])?\.)+(?:[\p{L}]{2,24}|xn--[a-z0-9-]{2,59})(?::\d{2,5})?(?:[/?#][^\s<>{}\[\]""']*)?)(?![\p{L}\p{N}_-])",
        RegexOptions.CultureInvariant, 100)]
    private static partial Regex UrlRegex();
}
