using System.Net;
using System.Net.Http;
using System.Globalization;
using System.Text.Json;

namespace WitherChat.Services;

public sealed class TwitchApiException : Exception
{
    public string EndpointName { get; }
    public string HttpMethod { get; }
    public HttpStatusCode StatusCode { get; }
    public string TwitchError { get; }
    public string TwitchMessage { get; }
    public string ResponseBody { get; }
    public TimeSpan? RetryAfter { get; }
    public bool IsMissingScope => StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden &&
                                  TwitchMessage.Contains("scope", StringComparison.OrdinalIgnoreCase);
    public bool IsExpiredToken => StatusCode == HttpStatusCode.Unauthorized &&
                                  (TwitchMessage.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                                   TwitchMessage.Contains("oauth", StringComparison.OrdinalIgnoreCase));
    public bool IsPermissionDenied => StatusCode == HttpStatusCode.Forbidden;

    public TwitchApiException(
        HttpStatusCode statusCode,
        string twitchMessage,
        string responseBody = "",
        string endpointName = "Twitch API",
        string httpMethod = "",
        string twitchError = "",
        TimeSpan? retryAfter = null)
        : base(ToFriendlyMessage(statusCode, twitchMessage))
    {
        EndpointName = endpointName;
        HttpMethod = httpMethod;
        StatusCode = statusCode;
        TwitchError = twitchError;
        TwitchMessage = twitchMessage;
        ResponseBody = responseBody;
        RetryAfter = retryAfter;
    }

    public static async Task<TwitchApiException> FromResponseAsync(
        HttpResponseMessage response,
        string endpointName = "Twitch API",
        string? httpMethod = null)
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

        var (error, message) = ExtractError(text);
        if (string.IsNullOrWhiteSpace(message))
        {
            message = response.ReasonPhrase ?? L("TwitchEmptyError");
        }

        var retryAfter = response.Headers.RetryAfter?.Delta;
        return new TwitchApiException(
            response.StatusCode,
            message,
            text,
            endpointName,
            httpMethod ?? response.RequestMessage?.Method.Method ?? string.Empty,
            error,
            retryAfter);
    }

    private static (string Error, string Message) ExtractError(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (string.Empty, string.Empty);
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var error = doc.RootElement.TryGetProperty("error", out var errorElement)
                ? errorElement.GetString() ?? string.Empty
                : string.Empty;
            var message = doc.RootElement.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString() ?? string.Empty
                : string.Empty;
            return (error, string.IsNullOrWhiteSpace(message) ? error : message);
        }
        catch
        {
            var safe = text.Length > 500 ? text[..500] : text;
            return (string.Empty, safe);
        }
    }

    private static string ToFriendlyMessage(HttpStatusCode statusCode, string message)
    {
        return statusCode switch
        {
            HttpStatusCode.Unauthorized => L("TwitchApiUnauthorized"),
            HttpStatusCode.Forbidden => Format("TwitchApiForbiddenFormat", message),
            (HttpStatusCode)429 => L("TwitchRateLimited"),
            (HttpStatusCode)422 => L("TwitchApiUnprocessable"),
            HttpStatusCode.BadRequest => Format("TwitchApiBadRequestFormat", message),
            _ => Format("TwitchApiHttpErrorFormat", (int)statusCode, message)
        };
    }

    private static string L(string key) =>
        LocalizationService.Get(LocalizationService.CurrentLanguage, key);

    private static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, L(key), args);
}
