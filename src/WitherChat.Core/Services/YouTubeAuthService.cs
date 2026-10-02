using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WitherChat.Core.Models;

namespace WitherChat.Core.Services;

public sealed class YouTubeAuthService : IDisposable
{
    public const string ReadOnlyScope = "https://www.googleapis.com/auth/youtube.readonly";
    public const string ModerationScope = "https://www.googleapis.com/auth/youtube.force-ssl";
    private const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string ChannelsEndpoint = "https://www.googleapis.com/youtube/v3/channels";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private string _clientId = YouTubeApplication.ClientId;

    public YouTubeAuthService(HttpMessageHandler? handler = null)
    {
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public void Configure(string clientId)
    {
        clientId = (clientId ?? string.Empty).Trim();
        if (clientId.Length > 0 && !clientId.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal))
        {
            throw new ArgumentException("Google OAuth Client ID has an invalid format.", nameof(clientId));
        }
        _clientId = clientId;
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The loopback callback must always return a useful browser page and release its listener.")]
    public async Task<YouTubeAuthSession> SignInWithBrowserAsync(
        Action<Uri> openBrowser,
        bool useEnglish,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(openBrowser);
        if (string.IsNullOrWhiteSpace(_clientId))
        {
            throw new InvalidOperationException(useEnglish
                ? "Enter a Google OAuth Client ID for a Desktop application first."
                : "Сначала укажите Google OAuth Client ID приложения типа Desktop.");
        }

        var verifier = CreateBase64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = CreateBase64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = CreateBase64Url(RandomNumberGenerator.GetBytes(32));
        var redirectUri = ReserveLoopbackRedirectUri();
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri.AbsoluteUri);
        listener.Start();

        openBrowser(BuildAuthorizeUri(_clientId, redirectUri, state, challenge));
        var context = await WaitForCallbackAsync(listener, state, cancellationToken).ConfigureAwait(false);
        try
        {
            var error = context.Request.QueryString["error"];
            if (!string.IsNullOrWhiteSpace(error))
            {
                throw new InvalidOperationException(useEnglish
                    ? "YouTube sign-in was declined: " + error
                    : "YouTube отклонил вход: " + error);
            }

            var code = context.Request.QueryString["code"];
            if (string.IsNullOrWhiteSpace(code))
            {
                throw new InvalidOperationException(useEnglish
                    ? "Google did not return an authorization code."
                    : "Google не вернул код авторизации.");
            }

            var token = await ExchangeCodeAsync(code, verifier, redirectUri, cancellationToken).ConfigureAwait(false);
            var session = await BuildSessionAsync(token, cancellationToken).ConfigureAwait(false);
            await WriteCallbackPageAsync(
                    context.Response,
                    useEnglish,
                    success: true,
                    errorDetail: null,
                    cancellationToken)
                .ConfigureAwait(false);
            return session;
        }
        catch (Exception exception)
        {
            try
            {
                if (context.Response.OutputStream.CanWrite)
                {
                    await WriteCallbackPageAsync(
                            context.Response,
                            useEnglish,
                            success: false,
                            GetAuthorizationErrorMessage(exception, useEnglish),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
            }
            throw;
        }
    }

    public async Task<YouTubeAuthSession> RefreshAsync(
        YouTubeAuthSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(session.RefreshToken))
        {
            throw new InvalidOperationException("The YouTube session cannot be refreshed. Sign in again.");
        }
        if (!string.Equals(session.ClientId, _clientId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The stored YouTube session belongs to another Google OAuth Client ID.");
        }

        using var content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("client_id", _clientId),
            new KeyValuePair<string, string>("refresh_token", session.RefreshToken),
            new KeyValuePair<string, string>("grant_type", "refresh_token")
        ]);
        using var response = await _httpClient.PostAsync(TokenEndpoint, content, cancellationToken).ConfigureAwait(false);
        var token = await ReadPayloadAsync<TokenResponse>(response, cancellationToken).ConfigureAwait(false);
        token = token with
        {
            RefreshToken = string.IsNullOrWhiteSpace(token.RefreshToken) ? session.RefreshToken : token.RefreshToken,
            Scope = string.IsNullOrWhiteSpace(token.Scope) ? string.Join(' ', session.Scopes) : token.Scope
        };
        return await BuildSessionAsync(token, cancellationToken).ConfigureAwait(false);
    }

    public async Task<YouTubeAuthSession> ValidateAsync(
        YouTubeAuthSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!string.Equals(session.ClientId, _clientId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The stored YouTube session belongs to another Google OAuth Client ID.");
        }
        var identity = await LoadChannelIdentityAsync(session.AccessToken, cancellationToken).ConfigureAwait(false);
        return session with
        {
            ChannelId = identity.Id,
            ChannelTitle = identity.Snippet.Title,
            ChannelHandle = identity.Snippet.CustomUrl ?? string.Empty,
            ThumbnailUri = TryCreateHttpUri(identity.Snippet.Thumbnails?.Default?.Url)
        };
    }

    internal static Uri BuildAuthorizeUri(string clientId, Uri redirectUri, string state, string challenge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(redirectUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(challenge);
        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["response_type"] = "code",
            ["scope"] = string.Join(' ', ReadOnlyScope, ModerationScope),
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["include_granted_scopes"] = "true",
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        };
        return new Uri(AuthorizationEndpoint + "?" + string.Join('&', query.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")));
    }

    private async Task<YouTubeAuthSession> BuildSessionAsync(
        TokenResponse token,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new InvalidDataException("Google returned an empty YouTube access token.");
        }
        var identity = await LoadChannelIdentityAsync(token.AccessToken, cancellationToken).ConfigureAwait(false);
        var scopes = (token.Scope ?? ReadOnlyScope)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!scopes.Contains(ReadOnlyScope, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("The Google session is missing YouTube read-only permission.");
        }
        return new YouTubeAuthSession(
            token.AccessToken,
            token.RefreshToken ?? string.Empty,
            _clientId,
            identity.Id,
            identity.Snippet.Title,
            identity.Snippet.CustomUrl ?? string.Empty,
            TryCreateHttpUri(identity.Snippet.Thumbnails?.Default?.Url),
            scopes,
            DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, token.ExpiresIn)));
    }

    public static bool HasModerationScope(YouTubeAuthSession? session) =>
        session?.Scopes.Contains(ModerationScope, StringComparer.Ordinal) == true;

    private async Task<ChannelResource> LoadChannelIdentityAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            ChannelsEndpoint + "?part=id%2Csnippet&mine=true&maxResults=1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var payload = await ReadPayloadAsync<ChannelListResponse>(response, cancellationToken).ConfigureAwait(false);
        return payload.Items?.FirstOrDefault() ?? throw new YouTubeChannelUnavailableException();
    }

    private async Task<TokenResponse> ExchangeCodeAsync(
        string code,
        string verifier,
        Uri redirectUri,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(
            CreateAuthorizationCodeTokenParameters(_clientId, code, verifier, redirectUri));
        using var response = await _httpClient.PostAsync(TokenEndpoint, content, cancellationToken).ConfigureAwait(false);
        return await ReadPayloadAsync<TokenResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    internal static IReadOnlyList<KeyValuePair<string, string>> CreateAuthorizationCodeTokenParameters(
        string clientId,
        string code,
        string verifier,
        Uri redirectUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(verifier);
        ArgumentNullException.ThrowIfNull(redirectUri);
        return
        [
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("code", code),
            new KeyValuePair<string, string>("code_verifier", verifier),
            new KeyValuePair<string, string>("grant_type", "authorization_code"),
            new KeyValuePair<string, string>("redirect_uri", redirectUri.AbsoluteUri)
        ];
    }

    private static Uri ReserveLoopbackRedirectUri()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        return new Uri($"http://127.0.0.1:{port}/");
    }

    private static async Task<HttpListenerContext> WaitForCallbackAsync(
        HttpListener listener,
        string expectedState,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var context = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            if (context.Request.RemoteEndPoint is not { Address: { } address } || !IPAddress.IsLoopback(address))
            {
                context.Response.StatusCode = 403;
                context.Response.Close();
                continue;
            }
            var state = context.Request.QueryString["state"];
            if (!string.Equals(state, expectedState, StringComparison.Ordinal))
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                continue;
            }
            return context;
        }
    }

    private static async Task WriteCallbackPageAsync(
        HttpListenerResponse response,
        bool useEnglish,
        bool success,
        string? errorDetail,
        CancellationToken cancellationToken)
    {
        var title = success
            ? useEnglish ? "YouTube connected" : "YouTube подключён"
            : useEnglish ? "YouTube sign-in failed" : "Не удалось подключить YouTube";
        var message = success
            ? useEnglish ? "You can close this tab and return to WitherChat." : "Эту вкладку можно закрыть и вернуться в WitherChat."
            : (useEnglish ? "Reason: " : "Причина: ") +
              (string.IsNullOrWhiteSpace(errorDetail) ? "Unknown Google OAuth error." : errorDetail);
        var html = $$"""
                   <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
                   <title>{{WebUtility.HtmlEncode(title)}}</title><style>body{margin:0;min-height:100vh;display:grid;place-items:center;background:#090a10;color:#f7f8ff;font-family:system-ui,sans-serif}main{max-width:560px;margin:24px;padding:32px;border:1px solid #ffffff2b;border-radius:28px;background:#1c2030d9}p{color:#aeb4c4}</style></head>
                   <body><main><h1>{{WebUtility.HtmlEncode(title)}}</h1><p>{{WebUtility.HtmlEncode(message)}}</p></main>
                   <script>history.replaceState(null,document.title,location.pathname);</script></body></html>
                   """;
        var bytes = Encoding.UTF8.GetBytes(html);
        response.ContentType = "text/html; charset=utf-8";
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["Pragma"] = "no-cache";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["X-Frame-Options"] = "DENY";
        response.Headers["Content-Security-Policy"] =
            "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; " +
            "base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        response.Close();
    }

    private static async Task<T> ReadPayloadAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            string? errorCode = null;
            string? errorDescription = null;
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.TryGetProperty("error", out var error))
                {
                    if (error.ValueKind == JsonValueKind.String)
                    {
                        errorCode = error.GetString();
                    }
                    else if (error.ValueKind == JsonValueKind.Object &&
                             error.TryGetProperty("message", out var message))
                    {
                        errorDescription = message.GetString();
                    }
                }
                if (root.TryGetProperty("error_description", out var description))
                {
                    errorDescription = description.GetString();
                }
            }
            catch (JsonException)
            {
            }
            if (string.Equals(errorCode, "invalid_request", StringComparison.Ordinal) &&
                errorDescription?.Contains("client_secret", StringComparison.OrdinalIgnoreCase) == true)
            {
                throw new YouTubeOAuthClientConfigurationException();
            }
            var errorLabel = string.IsNullOrWhiteSpace(errorCode)
                ? string.Empty
                : " (" + errorCode + ")";
            throw new HttpRequestException(
                "Google request failed" + errorLabel + ": " +
                (errorDescription ?? response.ReasonPhrase),
                null,
                response.StatusCode);
        }
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidDataException("Google returned an empty JSON response.");
    }

    private static string CreateBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string GetAuthorizationErrorMessage(Exception exception, bool useEnglish) =>
        exception switch
        {
            YouTubeChannelUnavailableException channelUnavailable =>
                channelUnavailable.GetLocalizedMessage(useEnglish),
            YouTubeOAuthClientConfigurationException clientConfiguration =>
                clientConfiguration.GetLocalizedMessage(useEnglish),
            _ => exception.Message
        };

    private static Uri? TryCreateHttpUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null;

    public void Dispose() => _httpClient.Dispose();

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("scope")] string? Scope);

    private sealed record ChannelListResponse([property: JsonPropertyName("items")] ChannelResource[]? Items);
    private sealed record ChannelResource(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("snippet")] ChannelSnippet Snippet);
    private sealed record ChannelSnippet(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("customUrl")] string? CustomUrl,
        [property: JsonPropertyName("thumbnails")] ThumbnailCollection? Thumbnails);
    private sealed record ThumbnailCollection([property: JsonPropertyName("default")] Thumbnail? Default);
    private sealed record Thumbnail([property: JsonPropertyName("url")] string Url);
}
