namespace TwitchChatMvp.Services;

public sealed class OAuthPortUnavailableException : Exception
{
    public OAuthPortUnavailableException(string redirectUri, int port, Exception? innerException = null)
        : base($"Twitch OAuth port {port} is unavailable for {redirectUri}", innerException)
    {
        RedirectUri = redirectUri;
        Port = port;
    }

    public string RedirectUri { get; }
    public int Port { get; }
}
