using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using WitherChat.Models;
using WitherChat.Services;

namespace WitherChat.Views;

public partial class ModerationDialog : Window
{
    private readonly bool _isTimeout;

    public ModerationDialog(string title, bool isTimeout, int? initialSeconds)
    {
        InitializeComponent();
        _isTimeout = isTimeout;
        TitleText.Text = title;
        DurationPanel.Visibility = isTimeout ? Visibility.Visible : Visibility.Collapsed;
        DurationMinutesText.Text = Math.Max(1, (initialSeconds ?? 600) / 60).ToString(CultureInfo.CurrentCulture);
    }

    public ModerationRequest? Request { get; private set; }

    private void ReasonText_TextChanged(object sender, TextChangedEventArgs e)
    {
        ReasonPlaceholder.Visibility = string.IsNullOrEmpty(ReasonText.Text) ? Visibility.Visible : Visibility.Collapsed;
        ReasonCountText.Text = $"{ReasonText.Text.Length} / 500";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        int? duration = null;
        if (_isTimeout)
        {
            if (!int.TryParse(DurationMinutesText.Text.Trim(), out var minutes) || minutes <= 0)
            {
                ErrorText.Text = LocalizationService.Get(
                    LocalizationService.CurrentLanguage,
                    "InvalidTimeoutDuration");
                ErrorText.Visibility = Visibility.Visible;
                return;
            }

            duration = Math.Clamp(minutes * 60, 1, 1_209_600);
        }

        Request = new ModerationRequest
        {
            Reason = ReasonText.Text.Trim(),
            DurationSeconds = duration
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
