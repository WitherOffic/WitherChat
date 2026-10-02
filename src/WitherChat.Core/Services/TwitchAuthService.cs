using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WitherChat.Core.Models;

namespace WitherChat.Core.Services;

public sealed class TwitchAuthService : IDisposable
{
    private static readonly Uri TokenEndpoint = new("https://id.twitch.tv/oauth2/token");
    private static readonly Uri ValidateEndpoint = new("https://id.twitch.tv/oauth2/validate");
    private const int MaxOAuthCallbackBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private string _clientId;
    private Uri _redirectUri;

    private readonly HttpClient _httpClient;

    public TwitchAuthService(
        string? clientId = null,
        string? redirectUri = null,
        HttpMessageHandler? handler = null)
    {
        _httpClient = handler is null
            ? new HttpClient(
                new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    CheckCertificateRevocationList = true
                },
                disposeHandler: true)
            : new HttpClient(handler, disposeHandler: true);
        _httpClient.Timeout = TimeSpan.FromSeconds(25);
        _clientId = TwitchApplication.ClientId;
        _redirectUri = new Uri(TwitchApplication.RedirectUri, UriKind.Absolute);
        Configure(clientId, redirectUri);
    }

    public void Configure(string? clientId, string? redirectUri)
    {
        _clientId = string.IsNullOrWhiteSpace(clientId) ? TwitchApplication.ClientId : clientId.Trim();
        var value = string.IsNullOrWhiteSpace(redirectUri) ? TwitchApplication.RedirectUri : redirectUri.Trim();
        if (!value.EndsWith('/'))
        {
            value += "/";
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !IsTrustedLoopbackRedirectUri(uri))
        {
            throw new ArgumentException(
                "OAuth redirect URI must use a local HTTP loopback address and root path.",
                nameof(redirectUri));
        }

        _redirectUri = uri;
    }

    public async Task<TwitchAuthSession> SignInWithBrowserAsync(
        Action<Uri> openBrowser,
        bool useEnglish,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(openBrowser);
        var redirectUri = _redirectUri;
        EnsureOAuthPortAvailable(redirectUri, useEnglish);
        var state = GenerateState();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri.AbsoluteUri);

        try
        {
            listener.Start();
        }
        catch (Exception exception) when (exception is HttpListenerException or PlatformNotSupportedException)
        {
            throw CreatePortUnavailableException(redirectUri, useEnglish, exception);
        }

        try
        {
            openBrowser(BuildAuthorizeUri(redirectUri, state, _clientId));
            var fragment = await WaitForOAuthFragmentAsync(listener, state, useEnglish, timeout.Token)
                .ConfigureAwait(false);
            return await BuildValidatedSessionFromFragmentAsync(fragment, state, useEnglish, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(useEnglish
                ? "Twitch sign-in timed out. Please try again."
                : "Время ожидания входа в Twitch истекло. Повторите попытку.");
        }
        finally
        {
            try
            {
                listener.Stop();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public async Task<TwitchAuthSession> RefreshAsync(
        TwitchAuthSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(session.RefreshToken))
        {
            throw new InvalidOperationException("The Twitch session cannot be refreshed.");
        }

        using var content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("client_id", _clientId),
            new KeyValuePair<string, string>("grant_type", "refresh_token"),
            new KeyValuePair<string, string>("refresh_token", session.RefreshToken)
        ]);
        using var response = await _httpClient.PostAsync(TokenEndpoint, content, cancellationToken).ConfigureAwait(false);
        var token = await ReadPayloadAsync<TokenResponse>(response, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            token = token with { RefreshToken = session.RefreshToken };
        }
        return await BuildValidatedSessionAsync(token, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TwitchAuthSession> ValidateAsync(
        TwitchAuthSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        using var request = new HttpRequestMessage(HttpMethod.Get, ValidateEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", session.AccessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var validation = await ReadPayloadAsync<ValidationResponse>(response, cancellationToken).ConfigureAwait(false);
        EnsureValidationMatchesApplication(validation);
        var grantedScopes = validation.Scopes.ToHashSet(StringComparer.Ordinal);
        var missingScopes = TwitchApplication.RequiredScopes
            .Where(scope => !grantedScopes.Contains(scope))
            .ToArray();
        if (missingScopes.Length > 0)
        {
            throw new InvalidOperationException(
                "The Twitch session is missing required permissions: " + string.Join(", ", missingScopes));
        }

        return session with
        {
            UserId = validation.UserId,
            Login = validation.Login,
            Scopes = validation.Scopes,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, validation.ExpiresIn)),
            ValidatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    public void Dispose() => _httpClient.Dispose();

    internal static bool IsTrustedLoopbackRedirectUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp &&
        (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)) &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        uri.AbsolutePath == "/" &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment);

    internal static Uri BuildAuthorizeUri(Uri redirectUri, string state)
        => BuildAuthorizeUri(redirectUri, state, TwitchApplication.ClientId);

    private static Uri BuildAuthorizeUri(Uri redirectUri, string state, string clientId)
    {
        ArgumentNullException.ThrowIfNull(redirectUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        if (!IsTrustedLoopbackRedirectUri(redirectUri))
        {
            throw new ArgumentException("OAuth redirect URI must use a local HTTP loopback address.", nameof(redirectUri));
        }

        var query = new Dictionary<string, string>
        {
            ["response_type"] = "token",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["scope"] = string.Join(' ', TwitchApplication.ChatScopes),
            ["state"] = state,
            ["force_verify"] = "false"
        };
        return new Uri(
            "https://id.twitch.tv/oauth2/authorize?" +
            string.Join("&", query.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")));
    }

    private async Task<TwitchAuthSession> BuildValidatedSessionAsync(
        TokenResponse token,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new InvalidDataException("Twitch returned an empty access token.");
        }

        var provisional = new TwitchAuthSession(
            token.AccessToken,
            token.RefreshToken ?? string.Empty,
            _clientId,
            string.Empty,
            string.Empty,
            token.Scope ?? [],
            DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, token.ExpiresIn)),
            DateTimeOffset.MinValue);
        return await ValidateAsync(provisional, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TwitchAuthSession> BuildValidatedSessionFromFragmentAsync(
        string fragment,
        string expectedState,
        bool useEnglish,
        CancellationToken cancellationToken)
    {
        var values = ParseUrlEncoded(fragment.TrimStart('#'));
        if (values.TryGetValue("error", out var error))
        {
            var description = values.TryGetValue("error_description", out var detail) ? detail : error;
            throw new InvalidOperationException(useEnglish
                ? "Twitch sign-in failed: " + description
                : "Twitch отклонил вход: " + description);
        }

        if (!values.TryGetValue("state", out var state) ||
            !string.Equals(state, expectedState, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(useEnglish
                ? "Twitch OAuth state validation failed."
                : "Не удалось проверить безопасность ответа Twitch OAuth.");
        }

        if (!values.TryGetValue("access_token", out var accessToken) || string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException(useEnglish
                ? "Twitch did not return an access token."
                : "Twitch не вернул токен доступа.");
        }

        var provisional = new TwitchAuthSession(
            accessToken,
            string.Empty,
            _clientId,
            string.Empty,
            string.Empty,
            [],
            DateTimeOffset.UtcNow.AddHours(4),
            DateTimeOffset.MinValue);
        return await ValidateAsync(provisional, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> WaitForOAuthFragmentAsync(
        HttpListener listener,
        string expectedState,
        bool useEnglish,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var context = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            var request = context.Request;
            if (request.RemoteEndPoint is not { Address: { } remoteAddress } || !IPAddress.IsLoopback(remoteAddress))
            {
                context.Response.StatusCode = 403;
                await WriteTextResponseAsync(context.Response, "Forbidden", "text/plain", cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath.Equals("/oauth-complete", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (request.ContentLength64 is < 0 or > MaxOAuthCallbackBytes ||
                    !string.Equals(
                        request.ContentType?.Split(';', 2)[0].Trim(),
                        "application/x-www-form-urlencoded",
                        StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = 400;
                    await WriteTextResponseAsync(context.Response, "Invalid callback", "text/plain", cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                var fragment = await ReadOAuthCompleteBodyAsync(request, cancellationToken).ConfigureAwait(false);
                var values = ParseUrlEncoded(fragment.TrimStart('#'));
                if (!values.TryGetValue("state", out var state) ||
                    !string.Equals(state, expectedState, StringComparison.Ordinal))
                {
                    context.Response.StatusCode = 400;
                    await WriteTextResponseAsync(context.Response, "Invalid callback", "text/plain", cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                await WriteTextResponseAsync(context.Response, "OK", "text/plain", cancellationToken)
                    .ConfigureAwait(false);
                return fragment;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath.Equals("/", StringComparison.Ordinal) == true)
            {
                await WriteTextResponseAsync(
                    context.Response,
                    BuildCallbackPage(useEnglish),
                    "text/html; charset=utf-8",
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            context.Response.StatusCode = 404;
            await WriteTextResponseAsync(context.Response, "Not found", "text/plain", cancellationToken)
                .ConfigureAwait(false);
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private static void EnsureOAuthPortAvailable(Uri redirectUri, bool useEnglish)
    {
        if (!IsTrustedLoopbackRedirectUri(redirectUri))
        {
            throw new InvalidOperationException("OAuth redirect URI must use a local HTTP loopback address.");
        }

        try
        {
            using var probe = new TcpListener(IPAddress.Loopback, redirectUri.Port);
            probe.Start();
        }
        catch (SocketException exception)
        {
            throw CreatePortUnavailableException(redirectUri, useEnglish, exception);
        }
    }

    private static InvalidOperationException CreatePortUnavailableException(
        Uri redirectUri,
        bool useEnglish,
        Exception exception) =>
        new(useEnglish
                ? $"OAuth callback port {redirectUri.Port} is unavailable. Close the other application using it and try again."
                : $"Порт OAuth {redirectUri.Port} занят. Закройте использующее его приложение и повторите попытку.",
            exception);

    private static string GenerateState()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static async Task<string> ReadOAuthCompleteBodyAsync(
        HttpListenerRequest request,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
        var buffer = new char[MaxOAuthCallbackBytes + 1];
        var length = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        if (length > MaxOAuthCallbackBytes)
        {
            throw new InvalidOperationException("The OAuth callback is too large.");
        }

        var body = new string(buffer, 0, length);
        var values = ParseUrlEncoded(body);
        return values.TryGetValue("hash", out var hash) ? hash : body;
    }

    private static async Task WriteTextResponseAsync(
        HttpListenerResponse response,
        string text,
        string contentType,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        response.ContentType = contentType;
        response.ContentEncoding = Encoding.UTF8;
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["Pragma"] = "no-cache";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["X-Frame-Options"] = "DENY";
        response.Headers["Content-Security-Policy"] =
            "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; " +
            "connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        response.Close();
    }

    private static string BuildCallbackPage(bool useEnglish)
    {
        var pageTitle = useEnglish ? "WitherChat sign-in" : "Вход в WitherChat";
        var connecting = useEnglish ? "Connecting WitherChat" : "Подключаем WitherChat";
        var wait = useEnglish ? "Please wait while Twitch sign-in completes." : "Подождите, пока завершается вход через Twitch.";
        var noToken = useEnglish ? "Twitch did not return authorization data." : "Twitch не вернул данные авторизации.";
        var rejected = useEnglish ? "WitherChat rejected the authorization response." : "WitherChat отклонил ответ авторизации.";
        var connectedTitle = useEnglish ? "Account connected" : "Аккаунт подключён";
        var connectedMessage = useEnglish
            ? "You can close this tab and return to WitherChat."
            : "Эту вкладку можно закрыть и вернуться в WitherChat.";
        var failed = useEnglish ? "Sign-in failed" : "Не удалось выполнить вход";
        return $$"""
<!doctype html>
<html lang="{{(useEnglish ? "en" : "ru")}}">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1">
  <title>{{WebUtility.HtmlEncode(pageTitle)}}</title>
  <style>
    body{margin:0;min-height:100vh;display:grid;place-items:center;background:#090a10;color:#f7f8ff;font-family:system-ui,"Segoe UI",sans-serif}
    main{max-width:560px;margin:24px;padding:32px;border:1px solid rgba(255,255,255,.18);border-radius:28px;background:rgba(28,32,48,.72);box-shadow:0 28px 80px rgba(0,0,0,.45)}
    h1{margin:0 0 10px;font-size:28px}p{color:#aeb4c4;line-height:1.5}.ok{color:#6fe7b4}.bad{color:#ff6b7a}
  </style>
</head>
<body>
<main><h1 id="title">{{WebUtility.HtmlEncode(connecting)}}</h1><p id="message">{{WebUtility.HtmlEncode(wait)}}</p></main>
<script>
(async function () {
  const title = document.getElementById('title');
  const message = document.getElementById('message');
  try {
    const hash = (window.location.hash || '').replace(/^#/, '');
    history.replaceState(null, document.title, window.location.pathname);
    if (!hash) throw new Error({{JsonSerializer.Serialize(noToken)}});
    const response = await fetch('/oauth-complete', {
      method: 'POST', headers: {'Content-Type': 'application/x-www-form-urlencoded'},
      body: 'hash=' + encodeURIComponent(hash)
    });
    if (!response.ok) throw new Error({{JsonSerializer.Serialize(rejected)}});
    title.textContent = {{JsonSerializer.Serialize(connectedTitle)}};
    title.className = 'ok';
    message.textContent = {{JsonSerializer.Serialize(connectedMessage)}};
  } catch (error) {
    title.textContent = {{JsonSerializer.Serialize(failed)}};
    title.className = 'bad';
    message.textContent = error.message || String(error);
  }
})();
</script>
</body>
</html>
""";
    }

    private static Dictionary<string, string> ParseUrlEncoded(string value)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in value.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var rawKey = separator >= 0 ? part[..separator] : part;
            var rawValue = separator >= 0 ? part[(separator + 1)..] : string.Empty;
            result[Uri.UnescapeDataString(rawKey.Replace('+', ' '))] =
                Uri.UnescapeDataString(rawValue.Replace('+', ' '));
        }

        return result;
    }

    private void EnsureValidationMatchesApplication(ValidationResponse validation)
    {
        if (!string.Equals(validation.ClientId, _clientId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The Twitch token belongs to another application.");
        }

        if (string.IsNullOrWhiteSpace(validation.UserId) || string.IsNullOrWhiteSpace(validation.Login))
        {
            throw new InvalidOperationException("Twitch did not return a user identity for this token.");
        }

        var missingScope = TwitchApplication.RequiredScopes.FirstOrDefault(
            required => !validation.Scopes.Contains(required, StringComparer.Ordinal));
        if (missingScope is not null)
        {
            throw new InvalidOperationException("The Twitch token is missing scope: " + missingScope);
        }
    }

    private static async Task<T> ReadPayloadAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var error = await TryReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                "Twitch request failed: " + error,
                null,
                response.StatusCode);
        }

        return await DeserializeAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> DeserializeAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidDataException("Twitch returned an empty JSON response.");
    }

    private static async Task<string> TryReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = await DeserializeAsync<ErrorResponse>(response, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(payload.Message)
                ? response.ReasonPhrase ?? response.StatusCode.ToString()
                : payload.Message;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return response.ReasonPhrase ?? response.StatusCode.ToString();
        }
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("scope")] string[]? Scope);

    private sealed record ValidationResponse(
        [property: JsonPropertyName("client_id")] string ClientId,
        [property: JsonPropertyName("login")] string Login,
        [property: JsonPropertyName("scopes")] string[] Scopes,
        [property: JsonPropertyName("user_id")] string UserId,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record ErrorResponse(
        [property: JsonPropertyName("message")] string Message);
}
