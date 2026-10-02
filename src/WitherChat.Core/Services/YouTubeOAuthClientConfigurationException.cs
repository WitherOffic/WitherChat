namespace WitherChat.Core.Services;

public sealed class YouTubeOAuthClientConfigurationException : InvalidOperationException
{
    public YouTubeOAuthClientConfigurationException()
        : base("The configured Google OAuth client is not a Desktop app.")
    {
    }

    public string GetLocalizedMessage(bool useEnglish) => useEnglish
        ? "YouTube needs a Google OAuth client of type “Desktop app”. Replace the current web client ID with a Desktop app client ID and reconnect."
        : "Для YouTube нужен Google OAuth-клиент типа «Приложение для ПК». Замените текущий web client ID на Desktop client ID и подключитесь снова.";
}
