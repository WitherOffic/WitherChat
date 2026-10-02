namespace WitherChat.Core.Services;

public sealed class YouTubeChannelUnavailableException : InvalidOperationException
{
    public YouTubeChannelUnavailableException()
        : base("The selected Google account did not return a YouTube channel.")
    {
    }

    public string GetLocalizedMessage(bool useEnglish) => useEnglish
        ? "The selected Google account did not return a YouTube channel. If the stream belongs to a Brand Account, set that channel as the default channel for third-party apps and reconnect."
        : "Для выбранного Google-аккаунта YouTube-канал не найден. Если трансляция идёт с Brand Account, сделайте этот канал каналом по умолчанию для сторонних приложений и подключитесь снова.";
}
