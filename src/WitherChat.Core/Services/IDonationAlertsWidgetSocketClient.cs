namespace WitherChat.Core.Services;

internal interface IDonationAlertsWidgetSocketClient
{
    event EventHandler<DonationAlertsPlaybackEventArgs>? PlaybackChanged;

    bool IsAlertPlaying { get; }
    bool IsConnected { get; }
    long ActiveAlertId { get; }

    Task PrepareAsync(string widgetToken, CancellationToken cancellationToken = default);

    Task StartAlertAsync(
        string widgetToken,
        long alertId,
        CancellationToken cancellationToken = default);

    Task SkipAlertAsync(
        string widgetToken,
        long alertId,
        CancellationToken cancellationToken = default);
}
