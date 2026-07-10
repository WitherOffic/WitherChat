using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using TwitchChatMvp.Models;

namespace TwitchChatMvp.Services;

public sealed class AuthService
{
    private const int MaxOAuthCallbackBytes = 16 * 1024;
    public static readonly string[] RequiredScopes =
    [
        "user:read:chat",
        "user:write:chat",
        "moderator:manage:banned_users"
    ];

    private static readonly TimeSpan TokenSafetyWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ValidationInterval = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Func<string> _clientIdProvider;
    private readonly SecureTokenStore _tokenStore;
    private readonly FileLogger _logger;
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };
    private TwitchTokenSet? _currentToken;

    public AuthService(Func<string> clientIdProvider, SecureTokenStore tokenStore, FileLogger logger)
    {
        _clientIdProvider = clientIdProvider;
        _tokenStore = tokenStore;
        _logger = logger;
    }

    public string ScopesText => string.Join(' ', RequiredScopes);
    public TwitchTokenSet? CurrentToken => _currentToken;

    public async Task<TwitchTokenSet?> TryRestoreSessionAsync(CancellationToken cancellationToken = default)
    {
        var clientId = GetClientIdOrThrow();
        var token = _tokenStore.Load();
        if (token is null)
        {
            return null;
        }

        if (!token.IsForClient(clientId))
        {
            _logger.Warn("Stored token belongs to another client id. Clearing local token.");
            _tokenStore.Clear();
            return null;
        }

        _currentToken = token;
        try
        {
            await EnsureValidAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            return _currentToken;
        }
        catch (Exception ex)
        {
            _logger.Error("Stored Twitch session could not be restored", ex);
            _tokenStore.Clear();
            _currentToken = null;
            return null;
        }
    }

    public async Task<TwitchTokenSet> SignInWithImplicitGrantAsync(
        string redirectUri,
        Action<string> openBrowser,
        Action<string>? statusChanged,
        bool forceVerify = false,
        CancellationToken cancellationToken = default)
    {
        var clientId = GetClientIdOrThrow();
        var normalizedRedirectUri = NormalizeRedirectUri(redirectUri);
        EnsureOAuthPortAvailable(normalizedRedirectUri);
        var state = GenerateState();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

        using var listener = new HttpListener();
        listener.Prefixes.Add(normalizedRedirectUri);
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            throw CreatePortUnavailableException(normalizedRedirectUri, ex);
        }

        try
        {
            var authorizeUrl = BuildAuthorizeUrl(clientId, normalizedRedirectUri, state, forceVerify);
            statusChanged?.Invoke("Opening Twitch login in your browser...");
            openBrowser(authorizeUrl);

            var fragment = await WaitForOAuthFragmentAsync(listener, timeoutCts.Token).ConfigureAwait(false);
            var tokenSet = await BuildAndValidateTokenFromFragmentAsync(fragment, clientId, state, timeoutCts.Token).ConfigureAwait(false);

            _currentToken = tokenSet;
            _tokenStore.Save(tokenSet);
            _logger.Info("Twitch account connected through browser OAuth.");
            return tokenSet;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Twitch login timed out. Click Sign in with Twitch and try again.");
        }
        finally
        {
            try
            {
                listener.Stop();
            }
            catch
            {
                // Listener shutdown is best-effort after the OAuth session finishes.
            }
        }
    }

    public async Task<string> EnsureValidAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var clientId = GetClientIdOrThrow();
        _currentToken ??= _tokenStore.Load();
        if (_currentToken is null)
        {
            throw new InvalidOperationException("Twitch is not connected.");
        }

        if (!_currentToken.IsForClient(clientId))
        {
            _tokenStore.Clear();
            _currentToken = null;
            throw new InvalidOperationException("Client ID changed. Sign in with Twitch again.");
        }

        if (_currentToken.IsAccessTokenNearExpiry(TokenSafetyWindow))
        {
            ClearExpiredSession();
        }

        if (DateTimeOffset.UtcNow - _currentToken.LastValidatedAtUtc > ValidationInterval)
        {
            var valid = await ValidateAndUpdateAsync(_currentToken, cancellationToken).ConfigureAwait(false);
            if (!valid)
            {
                ClearExpiredSession();
            }
        }

        EnsureRequiredScopesPresent(_currentToken);
        return _currentToken.AccessToken;
    }

    public bool HasAccessToken
    {
        get
        {
            _currentToken ??= _tokenStore.Load();
            return !string.IsNullOrWhiteSpace(_currentToken?.AccessToken);
        }
    }

    public Task RefreshTokenAsync(CancellationToken cancellationToken = default)
    {
        ClearExpiredSession();
        return Task.CompletedTask;
    }

    public void SaveProfile(TwitchUser user)
    {
        _currentToken ??= _tokenStore.Load();
        if (_currentToken is null)
        {
            return;
        }

        _currentToken.UserId = user.Id;
        _currentToken.Login = user.Login;
        _currentToken.DisplayName = user.DisplayName;
        _currentToken.ProfileImageUrl = user.ProfileImageUrl;
        _tokenStore.Save(_currentToken);
    }

    public void Logout()
    {
        _currentToken = null;
        _tokenStore.Clear();
        _logger.Info("Twitch account disconnected locally.");
    }

    private async Task<string> WaitForOAuthFragmentAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var context = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            var request = context.Request;

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath.Equals("/oauth-complete", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (request.ContentLength64 < 0 || request.ContentLength64 > MaxOAuthCallbackBytes ||
                    !string.Equals(request.ContentType?.Split(';', 2)[0].Trim(),
                        "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = 400;
                    await WriteTextResponseAsync(context.Response, "Invalid callback", "text/plain").ConfigureAwait(false);
                    continue;
                }

                var fragment = await ReadOAuthCompleteBodyAsync(request).ConfigureAwait(false);
                await WriteTextResponseAsync(context.Response, "OK", "text/plain").ConfigureAwait(false);
                return fragment;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath.Equals("/", StringComparison.Ordinal) == true)
            {
                await WriteTextResponseAsync(context.Response, BuildCallbackPage(), "text/html; charset=utf-8").ConfigureAwait(false);
                continue;
            }

            context.Response.StatusCode = 404;
            await WriteTextResponseAsync(context.Response, "Not found", "text/plain").ConfigureAwait(false);
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private async Task<TwitchTokenSet> BuildAndValidateTokenFromFragmentAsync(
        string fragment,
        string expectedClientId,
        string expectedState,
        CancellationToken cancellationToken)
    {
        var values = ParseUrlEncoded(fragment.TrimStart('#'));
        if (values.TryGetValue("error", out var error))
        {
            var description = values.TryGetValue("error_description", out var errorDescription) ? errorDescription : error;
            throw new InvalidOperationException("Twitch login was not completed: " + description);
        }

        if (!values.TryGetValue("state", out var state) ||
            !string.Equals(state, expectedState, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("OAuth state check failed. Please try signing in again.");
        }

        if (!values.TryGetValue("access_token", out var accessToken) ||
            string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("Twitch did not return an access token.");
        }

        values.TryGetValue("token_type", out var tokenType);
        values.TryGetValue("scope", out var scopeText);
        var expiresIn = values.TryGetValue("expires_in", out var expiresText) &&
                        int.TryParse(expiresText, out var parsedExpires)
            ? parsedExpires
            : 0;

        var tokenSet = new TwitchTokenSet
        {
            ClientId = expectedClientId,
            AccessToken = accessToken,
            RefreshToken = null,
            TokenType = string.IsNullOrWhiteSpace(tokenType) ? "bearer" : tokenType,
            Scopes = ParseScopeText(scopeText),
            ExpiresAtUtc = expiresIn > 0
                ? DateTimeOffset.UtcNow.AddSeconds(expiresIn)
                : DateTimeOffset.UtcNow.AddHours(4)
        };

        var valid = await ValidateAndUpdateAsync(tokenSet, cancellationToken).ConfigureAwait(false);
        if (!valid)
        {
            throw new InvalidOperationException("Twitch token validation failed. Please sign in again.");
        }

        return tokenSet;
    }

    private async Task<bool> ValidateAndUpdateAsync(TwitchTokenSet tokenSet, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://id.twitch.tv/oauth2/validate");
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", tokenSet.AccessToken);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw await TwitchApiException.FromResponseAsync(response).ConfigureAwait(false);
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var validation = JsonSerializer.Deserialize<TokenValidationResponse>(json, JsonOptions)
            ?? throw new InvalidOperationException("Twitch returned an empty validate response.");

        if (!string.Equals(validation.ClientId, GetClientIdOrThrow(), StringComparison.Ordinal))
        {
            return false;
        }

        tokenSet.UserId = validation.UserId;
        tokenSet.Login = validation.Login;
        tokenSet.Scopes = validation.Scopes ?? [];
        tokenSet.ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(validation.ExpiresIn);
        tokenSet.LastValidatedAtUtc = DateTimeOffset.UtcNow;
        EnsureRequiredScopesPresent(tokenSet);
        _tokenStore.Save(tokenSet);
        _logger.Info($"OAuth validate success: user id={validation.UserId ?? "unknown"} login={validation.Login ?? "unknown"}");
        return true;
    }

    private string GetClientIdOrThrow()
    {
        var clientId = _clientIdProvider().Trim();
        if (string.IsNullOrWhiteSpace(clientId) ||
            string.Equals(clientId, "PASTE_YOUR_TWITCH_CLIENT_ID_HERE", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "This build does not have a Twitch Client ID configured. " +
                "For release builds, set AppTwitchDefaults.ClientId. Developers can enable a custom Client ID in Advanced settings.");
        }

        return clientId;
    }

    private void ClearExpiredSession()
    {
        _tokenStore.Clear();
        _currentToken = null;
        throw new InvalidOperationException("Twitch session expired. Click Sign in with Twitch and connect again.");
    }

    private string BuildAuthorizeUrl(string clientId, string redirectUri, string state, bool forceVerify)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "token",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = ScopesText,
            ["state"] = state,
            ["force_verify"] = forceVerify ? "true" : "false"
        };

        return "https://id.twitch.tv/oauth2/authorize?" + BuildQueryString(query);
    }

    private static string NormalizeRedirectUri(string redirectUri)
    {
        var value = string.IsNullOrWhiteSpace(redirectUri) ? AppTwitchDefaults.RedirectUri : redirectUri.Trim();
        if (!value.EndsWith("/", StringComparison.Ordinal))
        {
            value += "/";
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
            (!uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
             !uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException("Redirect URI must be a local HTTP URL, for example " + AppTwitchDefaults.RedirectUri);
        }

        return uri.AbsoluteUri;
    }

    private static void EnsureOAuthPortAvailable(string redirectUri)
    {
        var uri = new Uri(redirectUri);
        var port = uri.Port;
        try
        {
            using var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();
        }
        catch (SocketException ex)
        {
            throw new OAuthPortUnavailableException(redirectUri, port, ex);
        }
    }

    private static OAuthPortUnavailableException CreatePortUnavailableException(string redirectUri, Exception ex)
    {
        var uri = new Uri(redirectUri);
        return new OAuthPortUnavailableException(redirectUri, uri.Port, ex);
    }

    private static string GenerateState()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static async Task<string> ReadOAuthCompleteBodyAsync(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
        var buffer = new char[MaxOAuthCallbackBytes + 1];
        var length = await reader.ReadBlockAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
        if (length > MaxOAuthCallbackBytes)
        {
            throw new InvalidOperationException("OAuth callback is too large.");
        }

        var body = new string(buffer, 0, length);
        var values = ParseUrlEncoded(body);
        return values.TryGetValue("hash", out var hash) ? hash : body;
    }

    private static async Task WriteTextResponseAsync(HttpListenerResponse response, string text, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        response.ContentType = contentType;
        response.ContentEncoding = Encoding.UTF8;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }

    private static string BuildCallbackPage()
    {
        return """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1">
  <title>Twitch connected</title>
  <style>
    body{margin:0;min-height:100vh;display:grid;place-items:center;background:#090a10;color:#f7f8ff;font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif}
    main{max-width:560px;margin:24px;padding:32px;border:1px solid rgba(255,255,255,.18);border-radius:28px;background:rgba(28,32,48,.72);box-shadow:0 28px 80px rgba(0,0,0,.45)}
    h1{margin:0 0 10px;font-size:28px} p{color:#aeb4c4;line-height:1.5} .ok{color:#6fe7b4}.bad{color:#ff6b7a}
  </style>
</head>
<body>
<main>
  <h1 id="title">Connecting Twitch...</h1>
  <p id="message">Please wait while the desktop app receives the Twitch token.</p>
</main>
<script>
(async function () {
  const title = document.getElementById('title');
  const message = document.getElementById('message');
  try {
    const hash = (window.location.hash || '').replace(/^#/, '');
    if (!hash) {
      throw new Error('Twitch did not return token data.');
    }
    const response = await fetch('/oauth-complete', {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: 'hash=' + encodeURIComponent(hash)
    });
    if (!response.ok) {
      throw new Error('The desktop app rejected the OAuth response.');
    }
    title.textContent = 'Twitch connected';
    title.className = 'ok';
    message.textContent = 'Twitch is connected. You can close this tab.';
    history.replaceState(null, document.title, window.location.pathname);
  } catch (error) {
    title.textContent = 'Connection failed';
    title.className = 'bad';
    message.textContent = error.message || String(error);
  }
})();
</script>
</body>
</html>
""";
    }

    private static string BuildQueryString(IReadOnlyDictionary<string, string> values)
    {
        return string.Join("&", values.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    }

    private static Dictionary<string, string> ParseUrlEncoded(string value)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        foreach (var part in value.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var rawKey = separator >= 0 ? part[..separator] : part;
            var rawValue = separator >= 0 ? part[(separator + 1)..] : string.Empty;
            var key = Uri.UnescapeDataString(rawKey.Replace('+', ' '));
            var decodedValue = Uri.UnescapeDataString(rawValue.Replace('+', ' '));
            result[key] = decodedValue;
        }

        return result;
    }

    private static List<string> ParseScopeText(string? scopeText)
    {
        return string.IsNullOrWhiteSpace(scopeText)
            ? []
            : scopeText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static void EnsureRequiredScopesPresent(TwitchTokenSet tokenSet)
    {
        var granted = tokenSet.Scopes.ToHashSet(StringComparer.Ordinal);
        var missing = RequiredScopes.Where(scope => !granted.Contains(scope)).ToArray();
        if (missing.Length > 0)
        {
            if (missing.Contains("user:read:chat", StringComparer.Ordinal))
            {
                throw new InvalidOperationException("Нет доступа user:read:chat. Выйдите и войдите через Twitch заново.");
            }

            throw new InvalidOperationException(
                "Twitch token is missing required scopes: " + string.Join(", ", missing) +
                ". Use Logout / Disconnect Twitch and sign in again.");
        }
    }

    private sealed class TokenValidationResponse
    {
        [JsonPropertyName("client_id")]
        public string ClientId { get; set; } = string.Empty;

        [JsonPropertyName("login")]
        public string? Login { get; set; }

        [JsonPropertyName("scopes")]
        public List<string>? Scopes { get; set; }

        [JsonPropertyName("user_id")]
        public string? UserId { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }
}
