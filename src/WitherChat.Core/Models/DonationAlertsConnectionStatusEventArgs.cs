namespace WitherChat.Core.Models;

public sealed class DonationAlertsConnectionStatusEventArgs(
    bool isConnected,
    bool isReconnecting,
    string detail) : EventArgs
{
    public bool IsConnected { get; } = isConnected;
    public bool IsReconnecting { get; } = isReconnecting;
    public string Detail { get; } = detail;
}
