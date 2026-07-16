using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using WitherChat.Models;
using WitherChat.Services;

namespace WitherChat.Views;

public partial class UnbanRequestResolutionDialog : Window
{
    public UnbanRequestResolutionDialog(UnbanRequestEntry request, bool approve)
    {
        InitializeComponent();
        Title = LocalizationService.Get(LocalizationService.CurrentLanguage, approve ? "Approve" : "Deny");
        TitleText.Text = LocalizationService.Get(LocalizationService.CurrentLanguage, approve ? "ApproveUnbanConfirm" : "DenyUnbanConfirm");
        SubmitButton.Content = LocalizationService.Get(LocalizationService.CurrentLanguage, approve ? "Approve" : "Deny");
        UserText.Text = request.UserLabel + " " + request.LoginLabel;
        RequestText.Text = request.RequestText;
    }

    public UnbanRequestResolution? Resolution { get; private set; }

    private void ResponseText_TextChanged(object sender, TextChangedEventArgs e)
    {
        ResponsePlaceholder.Visibility = string.IsNullOrEmpty(ResponseText.Text) ? Visibility.Visible : Visibility.Collapsed;
        ResponseCount.Text = ResponseText.Text.Length.ToString(CultureInfo.CurrentCulture) + " / 500";
    }

    private void Submit_Click(object sender, RoutedEventArgs e)
    {
        Resolution = new UnbanRequestResolution(ResponseText.Text.Trim());
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
