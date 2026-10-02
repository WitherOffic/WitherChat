using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WitherChat.Core.Models;

namespace WitherChat.Core.Services;

public sealed class DonationAlertsAuthService : IDisposable
{
    private const int MaxOAuthCallbackBytes = 16 * 1024;
    private static readonly Uri UserEndpoint = new("https://www.donationalerts.com/api/v1/user/oauth");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private string _clientId = string.Empty;

    public DonationAlertsAuthService(HttpMessageHandler? handler = null)
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
    }

    public void Configure(string? clientId)
    {
        var value = clientId?.Trim() ?? string.Empty;
        if (value.Length > 64 || value.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new ArgumentException("DonationAlerts Client ID has an invalid format.", nameof(clientId));
        }
        _clientId = value;
    }

    public async Task<DonationAlertsAuthSession> SignInWithBrowserAsync(
        Action<Uri> openBrowser,
        bool useEnglish,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(openBrowser);
        if (string.IsNullOrWhiteSpace(_clientId))
        {
            throw new InvalidOperationException(useEnglish
                ? "Enter the DonationAlerts application Client ID first."
                : "Сначала укажите Client ID приложения DonationAlerts.");
        }

        var redirectUri = new Uri(DonationAlertsApplication.RedirectUri, UriKind.Absolute);
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
            var values = ParseUrlEncoded(fragment.TrimStart('#'));
            if (values.TryGetValue("error", out var error))
            {
                var description = values.TryGetValue("error_description", out var detail) ? detail : error;
                throw new InvalidOperationException(useEnglish
                    ? "DonationAlerts sign-in failed: " + description
                    : "DonationAlerts отклонил вход: " + description);
            }
            if (!values.TryGetValue("state", out var returnedState) ||
                !string.Equals(returnedState, state, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(useEnglish
                    ? "DonationAlerts OAuth state validation failed."
                    : "Не удалось проверить безопасность ответа DonationAlerts OAuth.");
            }
            if (!values.TryGetValue("access_token", out var accessToken) ||
                string.IsNullOrWhiteSpace(accessToken))
            {
                throw new InvalidOperationException(useEnglish
                    ? "DonationAlerts did not return an access token."
                    : "DonationAlerts не вернул токен доступа.");
            }

            var expiresAt = DateTimeOffset.UtcNow.AddDays(365);
            if (values.TryGetValue("expires_in", out var rawExpiresIn) &&
                long.TryParse(rawExpiresIn, NumberStyles.None, CultureInfo.InvariantCulture, out var expiresIn))
            {
                expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, expiresIn));
            }
            var grantedScopes = values.TryGetValue("scope", out var rawScope)
                ? ParseGrantedScopes(rawScope)
                : DonationAlertsApplication.RequiredScopes;
            if (!DonationAlertsApplication.HasRequiredScopes(grantedScopes))
            {
                throw new InvalidOperationException(useEnglish
                    ? "DonationAlerts did not grant all permissions required for donations and history."
                    : "DonationAlerts не выдал все права для донатов и истории. Подключите аккаунт повторно.");
            }
            return await ValidateAsync(
                accessToken,
                expiresAt,
                grantedScopes,
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(useEnglish
                ? "DonationAlerts sign-in timed out. Please try again."
                : "Время ожидания входа в DonationAlerts истекло. Повторите попытку.");
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

    public Task<DonationAlertsAuthSession> ValidateAsync(
        DonationAlertsAuthSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!string.Equals(session.ClientId, _clientId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The stored DonationAlerts session belongs to another Client ID.");
        }
        return ValidateAsync(session.AccessToken, session.ExpiresAtUtc, session.Scopes, cancellationToken);
    }

    public void Dispose() => _httpClient.Dispose();

    internal static Uri BuildAuthorizeUri(Uri redirectUri, string state, string clientId)
    {
        ArgumentNullException.ThrowIfNull(redirectUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        if (!IsTrustedLoopbackRedirectUri(redirectUri))
        {
            throw new ArgumentException("OAuth redirect URI must use a local HTTP loopback address.", nameof(redirectUri));
        }

        var query = new Dictionary<string, string>
        {
            ["response_type"] = "token",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["scope"] = string.Join(' ', DonationAlertsApplication.RequiredScopes),
            ["state"] = state
        };
        return new Uri(
            "https://www.donationalerts.com/oauth/authorize?" +
            string.Join("&", query.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")));
    }

    internal static bool IsTrustedLoopbackRedirectUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp &&
        (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)) &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        uri.AbsolutePath == "/" &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment);

    internal static IReadOnlyList<string> ParseGrantedScopes(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(
                    [' ', '\t', '\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

    private async Task<DonationAlertsAuthSession> ValidateAsync(
        string accessToken,
        DateTimeOffset expiresAtUtc,
        IReadOnlyList<string> scopes,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UserEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var envelope = await ReadPayloadAsync<UserEnvelope>(response, cancellationToken).ConfigureAwait(false);
        if (envelope.Data is null || envelope.Data.Id <= 0)
        {
            throw new InvalidDataException("DonationAlerts returned an invalid user profile.");
        }

        Uri? avatarUri = null;
        if (Uri.TryCreate(envelope.Data.Avatar, UriKind.Absolute, out var parsedAvatar) &&
            parsedAvatar.Scheme is "https")
        {
            avatarUri = parsedAvatar;
        }
        return new DonationAlertsAuthSession(
            accessToken,
            _clientId,
            envelope.Data.Id,
            envelope.Data.Code ?? string.Empty,
            envelope.Data.Name ?? envelope.Data.Code ?? string.Empty,
            avatarUri,
            scopes.Distinct(StringComparer.Ordinal).ToArray(),
            expiresAtUtc);
    }

    private static async Task<T> ReadPayloadAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"DonationAlerts API returned {(int)response.StatusCode}: {body}",
                null,
                response.StatusCode);
        }
        return JsonSerializer.Deserialize<T>(body, JsonOptions)
               ?? throw new InvalidDataException("DonationAlerts returned an empty response.");
    }

    private static string GenerateState()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static void EnsureOAuthPortAvailable(Uri redirectUri, bool useEnglish)
    {
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
                ? $"OAuth callback port {redirectUri.Port} is unavailable."
                : $"Порт OAuth {redirectUri.Port} занят другим приложением.",
            exception);

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
            if (request.RemoteEndPoint is not { Address: { } address } || !IPAddress.IsLoopback(address))
            {
                context.Response.StatusCode = 403;
                await WriteResponseAsync(context.Response, "Forbidden", "text/plain", cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath.Equals("/oauth-complete", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (request.ContentLength64 is < 0 or > MaxOAuthCallbackBytes)
                {
                    context.Response.StatusCode = 400;
                    await WriteResponseAsync(context.Response, "Invalid callback", "text/plain", cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }
                using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
                var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                var bodyValues = ParseUrlEncoded(body);
                var fragment = bodyValues.TryGetValue("hash", out var hash) ? hash : body;
                var values = ParseUrlEncoded(fragment.TrimStart('#'));
                if (!values.TryGetValue("state", out var state) ||
                    !string.Equals(state, expectedState, StringComparison.Ordinal))
                {
                    context.Response.StatusCode = 400;
                    await WriteResponseAsync(context.Response, "Invalid callback", "text/plain", cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }
                await WriteResponseAsync(context.Response, "OK", "text/plain", cancellationToken)
                    .ConfigureAwait(false);
                return fragment;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath.Equals("/", StringComparison.Ordinal) == true)
            {
                await WriteResponseAsync(
                    context.Response,
                    BuildCallbackPage(useEnglish),
                    "text/html; charset=utf-8",
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            context.Response.StatusCode = 404;
            await WriteResponseAsync(context.Response, "Not found", "text/plain", cancellationToken)
                .ConfigureAwait(false);
        }
        throw new OperationCanceledException(cancellationToken);
    }

    private static async Task WriteResponseAsync(
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
        var connecting = useEnglish ? "Connecting DonationAlerts" : "Подключаем DonationAlerts";
        var wait = useEnglish ? "Please wait while sign-in completes." : "Подождите, пока завершается вход.";
        var connected = useEnglish ? "Account connected" : "Аккаунт подключён";
        var returnText = useEnglish
            ? "You can close this tab and return to WitherChat."
            : "Эту вкладку можно закрыть и вернуться в WitherChat.";
        var failed = useEnglish ? "Sign-in failed" : "Не удалось выполнить вход";
        const string template = """
<!doctype html><html lang="__LANG__"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>WitherChat DonationAlerts</title>
<style>body{margin:0;min-height:100vh;display:grid;place-items:center;background:#090a10;color:#f7f8ff;font-family:system-ui,"Segoe UI",sans-serif}main{max-width:560px;margin:24px;padding:32px;border:1px solid #ffffff2e;border-radius:28px;background:#1c2030b8}p{color:#aeb4c4;line-height:1.5}.ok{color:#ff8a34}.bad{color:#ff6b7a}</style></head>
<body><main><h1 id="title">__CONNECTING__</h1><p id="message">__WAIT__</p></main>
<script>(async()=>{const t=document.getElementById('title'),m=document.getElementById('message');try{const h=(location.hash||'').replace(/^#/,'');history.replaceState(null,document.title,location.pathname);if(!h)throw new Error('DonationAlerts did not return authorization data.');const r=await fetch('/oauth-complete',{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded'},body:'hash='+encodeURIComponent(h)});if(!r.ok)throw new Error('WitherChat rejected the authorization response.');t.textContent=__CONNECTED_JSON__;t.className='ok';m.textContent=__RETURN_JSON__}catch(e){t.textContent=__FAILED_JSON__;t.className='bad';m.textContent=e.message||String(e)}})();</script></body></html>
""";
        return template
            .Replace("__LANG__", useEnglish ? "en" : "ru", StringComparison.Ordinal)
            .Replace("__CONNECTING__", WebUtility.HtmlEncode(connecting), StringComparison.Ordinal)
            .Replace("__WAIT__", WebUtility.HtmlEncode(wait), StringComparison.Ordinal)
            .Replace("__CONNECTED_JSON__", JsonSerializer.Serialize(connected), StringComparison.Ordinal)
            .Replace("__RETURN_JSON__", JsonSerializer.Serialize(returnText), StringComparison.Ordinal)
            .Replace("__FAILED_JSON__", JsonSerializer.Serialize(failed), StringComparison.Ordinal);
    }

    private static Dictionary<string, string> ParseUrlEncoded(string value)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in value.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var key = separator < 0 ? part : part[..separator];
            var data = separator < 0 ? string.Empty : part[(separator + 1)..];
            result[WebUtility.UrlDecode(key)] = WebUtility.UrlDecode(data);
        }
        return result;
    }

    private sealed class UserEnvelope
    {
        public UserData? Data { get; set; }
    }

    private sealed class UserData
    {
        public long Id { get; set; }
        public string? Code { get; set; }
        public string? Name { get; set; }
        public string? Avatar { get; set; }

        [JsonPropertyName("socket_connection_token")]
        public string? SocketConnectionToken { get; set; }
    }
}
