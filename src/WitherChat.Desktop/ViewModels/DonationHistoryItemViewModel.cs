using System.Globalization;
using WitherChat.Core.Models;

namespace WitherChat.Desktop.ViewModels;

public sealed class DonationHistoryItemViewModel
{
    public DonationHistoryItemViewModel(DonationAlert donation, bool useEnglish)
    {
        Donation = donation ?? throw new ArgumentNullException(nameof(donation));
        Username = string.IsNullOrWhiteSpace(donation.Username)
            ? useEnglish ? "Anonymous" : "Аноним"
            : donation.Username;
        Message = string.IsNullOrWhiteSpace(donation.Message)
            ? useEnglish ? "No message" : "Без сообщения"
            : donation.Message;
        AmountText = donation.Amount.ToString("0.##", CultureInfo.CurrentCulture) + " " + donation.Currency;
        TimeText = FormatRelativeTime(donation.ReceivedAtUtc, useEnglish);
    }

    public DonationAlert Donation { get; }
    public string Id => Donation.Id;
    public string Username { get; }
    public string Message { get; }
    public string AmountText { get; }
    public string TimeText { get; }
    public bool IsAudio => string.Equals(Donation.MessageType, "audio", StringComparison.OrdinalIgnoreCase);

    private static string FormatRelativeTime(DateTimeOffset timestampUtc, bool useEnglish)
    {
        var elapsed = DateTimeOffset.UtcNow - timestampUtc.ToUniversalTime();
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return useEnglish ? "just now" : "только что";
        }
        if (elapsed < TimeSpan.FromHours(1))
        {
            var minutes = Math.Max(1, (int)elapsed.TotalMinutes);
            return useEnglish ? $"{minutes} min ago" : $"{minutes} мин. назад";
        }
        if (elapsed < TimeSpan.FromDays(1))
        {
            var hours = Math.Max(1, (int)elapsed.TotalHours);
            return useEnglish ? $"{hours} h ago" : $"{hours} ч. назад";
        }
        if (elapsed < TimeSpan.FromDays(31))
        {
            var days = Math.Max(1, (int)elapsed.TotalDays);
            return useEnglish ? $"{days} d ago" : $"{days} дн. назад";
        }
        return timestampUtc.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);
    }
}
