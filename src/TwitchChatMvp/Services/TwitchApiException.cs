using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace TwitchChatMvp.Services;

public sealed class TwitchApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string TwitchMessage { get; }
    public string ResponseBody { get; }

    public TwitchApiException(HttpStatusCode statusCode, string twitchMessage, string responseBody = "")
        : base(ToFriendlyMessage(statusCode, twitchMessage))
    {
        StatusCode = statusCode;
        TwitchMessage = twitchMessage;
        ResponseBody = responseBody;
    }

    public static async Task<TwitchApiException> FromResponseAsync(HttpResponseMessage response)
    {
        var text = string.Empty;
        try
        {
            text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch
        {
            // Keep the original HTTP status if the response body cannot be read.
        }

        var message = ExtractMessage(text);
        if (string.IsNullOrWhiteSpace(message))
        {
            message = response.ReasonPhrase ?? "Twitch вернул ошибку без текста.";
        }

        return new TwitchApiException(response.StatusCode, message, text);
    }

    private static string ExtractMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("message", out var message))
            {
                return message.GetString() ?? string.Empty;
            }

            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                return error.GetString() ?? string.Empty;
            }
        }
        catch
        {
            return text.Length > 500 ? text[..500] : text;
        }

        return text.Length > 500 ? text[..500] : text;
    }

    private static string ToFriendlyMessage(HttpStatusCode statusCode, string message)
    {
        return statusCode switch
        {
            HttpStatusCode.Unauthorized => "Twitch не принял токен. Приложение попробовало обновить его; если ошибка повторится, подключите Twitch заново.",
            HttpStatusCode.Forbidden => "Не хватает прав: проверьте scopes приложения и роль модератора/владельца канала. Детали Twitch: " + message,
            (HttpStatusCode)429 => "Twitch ограничил частоту запросов. Подождите немного и повторите действие.",
            (HttpStatusCode)422 => "Сообщение слишком длинное или не прошло проверку Twitch.",
            HttpStatusCode.BadRequest => "Twitch отклонил запрос: " + message,
            _ => $"Twitch вернул {(int)statusCode}: {message}"
        };
    }
}
