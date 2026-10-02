namespace WitherChat.Core.Models;

public sealed class DonationAlertEventArgs(DonationAlert donation) : EventArgs
{
    public DonationAlert Donation { get; } = donation ?? throw new ArgumentNullException(nameof(donation));
}
