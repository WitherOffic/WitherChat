using WitherChat.Core.Models;

namespace WitherChat.Core.Services;

public interface IDonationAlertsObsController
{
    event EventHandler<DonationAlertsPlaybackEventArgs>? PlaybackChanged;
    bool IsConfigured { get; }
    bool IsConnected { get; }
    bool IsAlertPlaying { get; }
    string ActiveAlertId { get; }
    string SourceName { get; }
    bool TryAutoConfigure();
    Task EnsureConnectedAsync(CancellationToken cancellationToken = default);
    Task RepeatDonationAsync(DonationAlert donation, CancellationToken cancellationToken = default);
    Task SkipDonationAsync(DonationAlert donation, CancellationToken cancellationToken = default);
}
