namespace WitherChat.Core.Services;

public enum DonationAlertsPlaybackAction
{
    Started,
    Ended,
    Skipped,
    ConnectionRestored,
    ConnectionLost
}

public sealed class DonationAlertsPlaybackEventArgs(
    DonationAlertsPlaybackAction action,
    long alertId) : EventArgs
{
    public DonationAlertsPlaybackAction Action { get; } = action;
    public long AlertId { get; } = alertId;
}
