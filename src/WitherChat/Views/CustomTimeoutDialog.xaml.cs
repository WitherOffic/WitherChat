using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using WitherChat.Models;
using WitherChat.Services;

namespace WitherChat.Views;

public partial class CustomTimeoutDialog : Window
{
    private const int MaximumTimeoutSeconds = 1_209_600;

    public CustomTimeoutDialog(ChatMessageModel message, string channelName)
    {
        InitializeComponent();

        DisplayNameText.Text = message.UserLabel;
        LoginAndChannelText.Text = $"@{message.Login} · #{channelName}";
        AvatarInitialText.Text = message.AvatarInitial;
        if (Uri.TryCreate(message.ProfileImageUrl, UriKind.Absolute, out var avatarUri) && avatarUri.Scheme == Uri.UriSchemeHttps)
        {
            AvatarImage.Source = new BitmapImage(avatarUri);
            AvatarInitialText.Visibility = Visibility.Collapsed;
        }

        QuickDurationCombo.ItemsSource = CreateDurationOptions();
        QuickDurationCombo.DisplayMemberPath = nameof(DurationOption.Label);
        QuickDurationCombo.SelectedValuePath = nameof(DurationOption.Seconds);
        QuickDurationCombo.SelectedIndex = 3;

        DurationUnitCombo.ItemsSource = CreateUnitOptions();
        DurationUnitCombo.DisplayMemberPath = nameof(UnitOption.Label);
        DurationUnitCombo.SelectedIndex = 1;
    }

    public ModerationRequest? Request { get; private set; }

    private static IReadOnlyList<DurationOption> CreateDurationOptions()
    {
        return
        [
            new(LocalizationService.Get(LocalizationService.CurrentLanguage, "Duration30Seconds"), 30),
            new(LocalizationService.Get(LocalizationService.CurrentLanguage, "Duration1Minute"), 60),
            new(LocalizationService.Get(LocalizationService.CurrentLanguage, "Duration5Minutes"), 300),
            new(LocalizationService.Get(LocalizationService.CurrentLanguage, "Duration10Minutes"), 600),
            new(LocalizationService.Get(LocalizationService.CurrentLanguage, "Duration30Minutes"), 1800),
            new(LocalizationService.Get(LocalizationService.CurrentLanguage, "Duration1Hour"), 3600),
            new(LocalizationService.Get(LocalizationService.CurrentLanguage, "Duration1Day"), 86400),
            new(LocalizationService.Get(LocalizationService.CurrentLanguage, "CustomDuration"), null)
        ];
    }

    private static IReadOnlyList<UnitOption> CreateUnitOptions() =>
    [
        new(LocalizationService.Get(LocalizationService.CurrentLanguage, "Seconds"), 1),
        new(LocalizationService.Get(LocalizationService.CurrentLanguage, "Minutes"), 60),
        new(LocalizationService.Get(LocalizationService.CurrentLanguage, "Hours"), 3600),
        new(LocalizationService.Get(LocalizationService.CurrentLanguage, "Days"), 86400)
    ];

    private void QuickDurationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CustomDurationPanel.Visibility = QuickDurationCombo.SelectedItem is DurationOption { Seconds: null }
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ReasonText_TextChanged(object sender, TextChangedEventArgs e)
    {
        ReasonPlaceholder.Visibility = string.IsNullOrEmpty(ReasonText.Text) ? Visibility.Visible : Visibility.Collapsed;
        ReasonCountText.Text = $"{ReasonText.Text.Length.ToString(CultureInfo.CurrentCulture)} / 500";
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        long seconds;
        if (QuickDurationCombo.SelectedItem is DurationOption { Seconds: { } presetSeconds })
        {
            seconds = presetSeconds;
        }
        else if (!long.TryParse(CustomDurationText.Text.Trim(), NumberStyles.None, CultureInfo.CurrentCulture, out var value) ||
                 value <= 0 || DurationUnitCombo.SelectedItem is not UnitOption unit ||
                 value > MaximumTimeoutSeconds / unit.Multiplier)
        {
            ShowValidationError();
            return;
        }
        else
        {
            seconds = value * unit.Multiplier;
        }

        if (seconds is < 1 or > MaximumTimeoutSeconds)
        {
            ShowValidationError();
            return;
        }

        Request = new ModerationRequest
        {
            DurationSeconds = (int)seconds,
            Reason = ReasonText.Text.Trim()
        };
        DialogResult = true;
    }

    private void ShowValidationError()
    {
        ErrorText.Text = LocalizationService.Get(LocalizationService.CurrentLanguage, "InvalidTimeoutDuration");
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private sealed record DurationOption(string Label, int? Seconds);
    private sealed record UnitOption(string Label, int Multiplier);
}
