using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using WitherChat.Models;

namespace WitherChat.Services;

public sealed class AuthService : IDisposable
{
    public const string ModeratedChannelsScope = "user:read:moderated_channels";
    public const string ChannelPointsScope = "channel:read:redemptions";
    public const string ChatModerationScope = "moderator:manage:chat_messages";
    public const string AutoModScope = "moderator:manage:automod";
    public const string BannedUsersScope = "moderator:manage:banned_users";
    public const string ChannelModerateScope = "channel:moderate";
    public const string UnbanRequestsScope = "moderator:manage:unban_requests";
    private const int MaxOAuthCallbackBytes = 16 * 1024;
    public static readonly string[] RequiredScopes =
    [
        "user:read:chat",
        "user:write:chat",
        "chat:read",
        BannedUsersScope
    ];

    private static readonly TimeSpan TokenSafetyWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ValidationInterval = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Func<string> _clientIdProvider;
    private readonly SecureTokenStore _tokenStore;
    private readonly FileLogger _logger;
    private readonly object _sessionGate = new();
    private readonly SemaphoreSlim _validationGate = new(1, 1);
    private readonly HttpClient _http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        CheckCertificateRevocationList = true
    })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };
    private TwitchTokenSet? _currentToken;
    private long _sessionGeneration;

    public event EventHandler? SessionInvalidated;

    public AuthService(Func<string> clientIdProvider, SecureTokenStore tokenStore, FileLogger logger)
    {
        _clientIdProvider = clientIdProvider;
        _tokenStore = tokenStore;
        _logger = logger;
    }

    public void Dispose()
    {
        _http.Dispose();
        _validationGate.Dispose();
    }

    public string ScopesText => string.Join(' ', RequiredScopes.Append(ModeratedChannelsScope).Append(ChannelPointsScope).Append(ChatModerationScope).Append(AutoModScope).Append(ChannelModerateScope).Append(UnbanRequestsScope));
    public TwitchTokenSet? CurrentToken => GetTokenSnapshot().Token;

    public bool HasScope(string scope)
    {
        return GetTokenSnapshot().Token?.Scopes.Contains(scope, StringComparer.Ordinal) == true;
    }

    public async Task<TwitchTokenSet?> TryRestoreSessionAsync(CancellationToken cancellationToken = default)
    {
        var clientId = GetClientIdOrThrow();
        var snapshot = GetTokenSnapshot();
        var token = snapshot.Token;
        if (token is null)
        {
            return null;
        }

        if (!token.IsForClient(clientId))
        {
            _logger.Warn("Stored token belongs to another client id. Clearing local token.");
            ClearStoredSession();
            return null;
        }

        try
        {
            await EnsureValidAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            return GetTokenSnapshot().Token;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ForgetTokenSnapshot(token, snapshot.Generation);
            return null;
        }
        catch (Exception ex)
        {
            _logger.Error("Stored Twitch session could not be restored", ex);
            ForgetTokenSnapshot(token, snapshot.Generation);
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
        var sessionGeneration = GetSessionGeneration();
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
            statusChanged?.Invoke(L("OpeningTwitchLogin"));
            _logger.Info("OAuth browser launch requested.");
            openBrowser(authorizeUrl);

            var fragment = await WaitForOAuthFragmentAsync(listener, state, timeoutCts.Token).ConfigureAwait(false);
            _logger.Info("OAuth callback received.");
            var tokenSet = await BuildAndValidateTokenFromFragmentAsync(
                fragment,
                clientId,
                state,
                sessionGeneration,
                timeoutCts.Token).ConfigureAwait(false);

            timeoutCts.Token.ThrowIfCancellationRequested();
            lock (_sessionGate)
            {
                if (_sessionGeneration != sessionGeneration)
                {
                    throw new InvalidOperationException(L("TwitchLoginCanceled"));
                }

                _tokenStore.Save(tokenSet);
                _currentToken = tokenSet;
                _sessionGeneration++;
            }
            _logger.Info("Twitch account connected through browser OAuth.");
            return tokenSet;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(L("TwitchLoginTimedOut"));
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
        var snapshot = GetTokenSnapshot();
        var token = snapshot.Token;
        if (token is null)
        {
            throw new InvalidOperationException(L("TwitchNotConnectedError"));
        }

        if (!token.IsForClient(clientId))
        {
            ClearStoredSession();
            throw new InvalidOperationException(L("ClientIdChangedSignInAgain"));
        }

        if (token.IsAccessTokenNearExpiry(TokenSafetyWindow))
        {
            ClearExpiredSession();
            throw new InvalidOperationException(L("TwitchSessionExpired"));
        }

        if (DateTimeOffset.UtcNow - token.LastValidatedAtUtc > ValidationInterval)
        {
            await _validationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                snapshot = GetTokenSnapshot();
                token = snapshot.Token;
                if (token is null)
                {
                    throw new InvalidOperationException(L("TwitchNotConnectedError"));
                }

                if (DateTimeOffset.UtcNow - token.LastValidatedAtUtc > ValidationInterval)
                {
                    var valid = await ValidateAndUpdateAsync(token, snapshot.Generation, cancellationToken).ConfigureAwait(false);
                    if (!valid)
                    {
                        if (IsCurrentSnapshot(token, snapshot.Generation))
                        {
                            ClearExpiredSession();
                        }
                        throw new InvalidOperationException(L("TwitchSessionInvalidError"));
                    }
                }
            }
            finally
            {
                _validationGate.Release();
            }
        }

        lock (_sessionGate)
        {
            if (_currentToken is null ||
                _sessionGeneration != snapshot.Generation ||
                !ReferenceEquals(_currentToken, token))
            {
                throw new InvalidOperationException(L("TwitchNotConnectedError"));
            }

            EnsureRequiredScopesPresent(_currentToken);
            return _currentToken.AccessToken;
        }
    }

    public bool HasAccessToken
    {
        get
        {
            return !string.IsNullOrWhiteSpace(GetTokenSnapshot().Token?.AccessToken);
        }
    }

    public async Task<bool> ValidateCurrentAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = GetTokenSnapshot();
        if (snapshot.Token is null || !snapshot.Token.IsForClient(GetClientIdOrThrow()))
        {
            return false;
        }

        return await ValidateAndUpdateAsync(snapshot.Token, snapshot.Generation, cancellationToken).ConfigureAwait(false);
    }

    public Task RefreshTokenAsync(CancellationToken cancellationToken = default)
    {
        ClearExpiredSession();
        return Task.CompletedTask;
    }

    public void SaveProfile(TwitchUser user)
    {
        lock (_sessionGate)
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
    }

    public void Logout()
    {
        ClearStoredSession();
        _logger.Info("Twitch account disconnected locally.");
    }

    private async Task<string> WaitForOAuthFragmentAsync(
        HttpListener listener,
        string expectedState,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var context = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            var request = context.Request;

            if (request.RemoteEndPoint is not { Address: { } remoteAddress } || !IPAddress.IsLoopback(remoteAddress))
            {
                context.Response.StatusCode = 403;
                await WriteTextResponseAsync(context.Response, "Forbidden", "text/plain", cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath.Equals("/oauth-complete", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (request.ContentLength64 < 0 || request.ContentLength64 > MaxOAuthCallbackBytes ||
                    !string.Equals(request.ContentType?.Split(';', 2)[0].Trim(),
                        "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = 400;
                    await WriteTextResponseAsync(context.Response, "Invalid callback", "text/plain", cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var fragment = await ReadOAuthCompleteBodyAsync(request, cancellationToken).ConfigureAwait(false);
                var values = ParseUrlEncoded(fragment.TrimStart('#'));
                if (!values.TryGetValue("state", out var state) ||
                    !string.Equals(state, expectedState, StringComparison.Ordinal))
                {
                    context.Response.StatusCode = 400;
                    await WriteTextResponseAsync(context.Response, "Invalid callback", "text/plain", cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await WriteTextResponseAsync(context.Response, "OK", "text/plain", cancellationToken).ConfigureAwait(false);
                return fragment;
            }

            if (request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                request.Url?.AbsolutePath.Equals("/", StringComparison.Ordinal) == true)
            {
                await WriteTextResponseAsync(context.Response, BuildCallbackPage(), "text/html; charset=utf-8", cancellationToken).ConfigureAwait(false);
                continue;
            }

            context.Response.StatusCode = 404;
            await WriteTextResponseAsync(context.Response, "Not found", "text/plain", cancellationToken).ConfigureAwait(false);
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private async Task<TwitchTokenSet> BuildAndValidateTokenFromFragmentAsync(
        string fragment,
        string expectedClientId,
        string expectedState,
        long expectedGeneration,
        CancellationToken cancellationToken)
    {
        var values = ParseUrlEncoded(fragment.TrimStart('#'));
        if (values.TryGetValue("error", out var error))
        {
            var description = values.TryGetValue("error_description", out var errorDescription) ? errorDescription : error;
            throw new InvalidOperationException(string.Format(
                CultureInfo.CurrentCulture,
                L("TwitchLoginIncompleteFormat"),
                description));
        }

        if (!values.TryGetValue("state", out var state) ||
            !string.Equals(state, expectedState, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(L("OAuthStateFailed"));
        }

        if (!values.TryGetValue("access_token", out var accessToken) ||
            string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException(L("OAuthAccessTokenMissing"));
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

        var valid = await ValidateAndUpdateAsync(
            tokenSet,
            expectedGeneration,
            cancellationToken,
            persistCurrentSession: false).ConfigureAwait(false);
        if (!valid)
        {
            throw new InvalidOperationException(L("TwitchTokenValidationFailed"));
        }

        return tokenSet;
    }

    private async Task<bool> ValidateAndUpdateAsync(
        TwitchTokenSet tokenSet,
        long expectedGeneration,
        CancellationToken cancellationToken,
        bool persistCurrentSession = true)
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
            ?? throw new InvalidOperationException(L("TwitchValidateEmpty"));

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
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sessionGate)
        {
            if (_sessionGeneration != expectedGeneration ||
                persistCurrentSession && !ReferenceEquals(_currentToken, tokenSet))
            {
                return false;
            }

            if (persistCurrentSession)
            {
                _tokenStore.Save(tokenSet);
            }
        }
        _logger.Info($"OAuth validate success: user id={validation.UserId ?? "unknown"} login={validation.Login ?? "unknown"}");
        return true;
    }

    private string GetClientIdOrThrow()
    {
        var clientId = _clientIdProvider().Trim();
        if (string.IsNullOrWhiteSpace(clientId) ||
            string.Equals(clientId, "PASTE_YOUR_TWITCH_CLIENT_ID_HERE", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(L("ReleaseClientIdDetail"));
        }

        return clientId;
    }

    private void ClearExpiredSession()
    {
        ClearStoredSession();
        SessionInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private void ClearStoredSession()
    {
        lock (_sessionGate)
        {
            _tokenStore.Clear();
            _currentToken = null;
            _sessionGeneration++;
        }
    }

    private (TwitchTokenSet? Token, long Generation) GetTokenSnapshot()
    {
        lock (_sessionGate)
        {
            _currentToken ??= _tokenStore.Load();
            return (_currentToken, _sessionGeneration);
        }
    }

    private long GetSessionGeneration()
    {
        lock (_sessionGate)
        {
            return _sessionGeneration;
        }
    }

    private bool IsCurrentSnapshot(TwitchTokenSet token, long generation)
    {
        lock (_sessionGate)
        {
            return _sessionGeneration == generation && ReferenceEquals(_currentToken, token);
        }
    }

    private void ForgetTokenSnapshot(TwitchTokenSet token, long generation)
    {
        lock (_sessionGate)
        {
            if (_sessionGeneration == generation && ReferenceEquals(_currentToken, token))
            {
                _currentToken = null;
            }
        }
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
        if (TryNormalizeRedirectUri(redirectUri, out var normalized))
        {
            return normalized;
        }

        throw new InvalidOperationException(string.Format(
            CultureInfo.CurrentCulture,
            L("RedirectLocalOnlyFormat"),
            AppTwitchDefaults.RedirectUri));
    }

    public static bool TryNormalizeRedirectUri(string? redirectUri, out string normalized)
    {
        var value = string.IsNullOrWhiteSpace(redirectUri) ? AppTwitchDefaults.RedirectUri : redirectUri.Trim();
        if (!value.EndsWith('/'))
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
            normalized = string.Empty;
            return false;
        }

        normalized = uri.AbsoluteUri;
        return true;
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

    private static async Task<string> ReadOAuthCompleteBodyAsync(
        HttpListenerRequest request,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
        var buffer = new char[MaxOAuthCallbackBytes + 1];
        var length = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        if (length > MaxOAuthCallbackBytes)
        {
            throw new InvalidOperationException(L("OAuthCallbackTooLarge"));
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
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        response.Close();
    }

    private static string BuildCallbackPage()
    {
        var page = """
<!doctype html>
<html lang="__LANG__">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1">
  <title>__PAGE_TITLE__</title>
  <style>
    body{margin:0;min-height:100vh;display:grid;place-items:center;background:#090a10;color:#f7f8ff;font-family:system-ui,"Segoe UI",sans-serif}
    main{max-width:560px;margin:24px;padding:32px;border:1px solid rgba(255,255,255,.18);border-radius:28px;background:rgba(28,32,48,.72);box-shadow:0 28px 80px rgba(0,0,0,.45)}
    h1{margin:0 0 10px;font-size:28px} p{color:#aeb4c4;line-height:1.5} .ok{color:#6fe7b4}.bad{color:#ff6b7a}
  </style>
</head>
<body>
<main>
  <h1 id="title">__CONNECTING_HTML__</h1>
  <p id="message">__WAIT_HTML__</p>
</main>
<script>
(async function () {
  const title = document.getElementById('title');
  const message = document.getElementById('message');
  try {
    const hash = (window.location.hash || '').replace(/^#/, '');
    history.replaceState(null, document.title, window.location.pathname);
    if (!hash) {
      throw new Error(__NO_TOKEN__);
    }
    const response = await fetch('/oauth-complete', {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: 'hash=' + encodeURIComponent(hash)
    });
    if (!response.ok) {
      throw new Error(__REJECTED__);
    }
    title.textContent = __CONNECTED_TITLE__;
    title.className = 'ok';
    message.textContent = __CONNECTED_MESSAGE__;
  } catch (error) {
    title.textContent = __FAILED__;
    title.className = 'bad';
    message.textContent = error.message || String(error);
  }
})();
</script>
</body>
</html>
""";
        var language = LocalizationService.CurrentLanguage == LocalizationService.English ? "en" : "ru";
        return page
            .Replace("__LANG__", language, StringComparison.Ordinal)
            .Replace("__PAGE_TITLE__", WebUtility.HtmlEncode(L("OAuthBrowserTitle")), StringComparison.Ordinal)
            .Replace("__CONNECTING_HTML__", WebUtility.HtmlEncode(L("OAuthBrowserConnecting")), StringComparison.Ordinal)
            .Replace("__WAIT_HTML__", WebUtility.HtmlEncode(L("OAuthBrowserWait")), StringComparison.Ordinal)
            .Replace("__NO_TOKEN__", JsonSerializer.Serialize(L("OAuthBrowserNoToken")), StringComparison.Ordinal)
            .Replace("__REJECTED__", JsonSerializer.Serialize(L("OAuthBrowserRejected")), StringComparison.Ordinal)
            .Replace("__CONNECTED_TITLE__", JsonSerializer.Serialize(L("OAuthBrowserConnectedTitle")), StringComparison.Ordinal)
            .Replace("__CONNECTED_MESSAGE__", JsonSerializer.Serialize(L("OAuthBrowserConnectedMessage")), StringComparison.Ordinal)
            .Replace("__FAILED__", JsonSerializer.Serialize(L("OAuthBrowserFailed")), StringComparison.Ordinal);
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
                throw new InvalidOperationException(L("ChatReadScopeMissing"));
            }

            throw new InvalidOperationException(string.Format(
                CultureInfo.CurrentCulture,
                L("TwitchScopesMissingFormat"),
                string.Join(", ", missing)));
        }
    }

    private static string L(string key) =>
        LocalizationService.Get(LocalizationService.CurrentLanguage, key);

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
