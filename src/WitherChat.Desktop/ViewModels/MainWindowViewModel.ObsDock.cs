using System.ComponentModel;
using Avalonia;

namespace WitherChat.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    // Only the OBS host consumes these dimensions; standalone defaults stay identical.

    public double ComposerCardMinHeight => IsObsDockMode ? 44 : 76;
    public Thickness ComposerCardPadding => new(IsObsDockMode ? 5 : 8);
    public double ComposerInputMinHeight => IsObsDockMode ? 32 : 56;
    public Thickness ComposerInputPadding => IsObsDockMode ? new(10, 4) : new(16, 10);
    public double ComposerSendSize => IsObsDockMode ? 32 : 46;
    public Thickness ChatEmptyCardPadding => new(IsObsDockMode ? 14 : 24);
    public double ChatEmptyTitleSize => IsObsDockMode ? 14 : 18;
    public Thickness WelcomeCardPadding => new(IsObsDockMode ? 14 : 28);
    public double WelcomeTitleSize => IsObsDockMode ? 18 : 24;
    public Thickness WelcomeActionMargin => IsObsDockMode ? new(0, 8, 0, 0) : new(0);
    public Thickness WelcomeDescriptionMargin => IsObsDockMode ? new(0, 6, 0, 12) : new(0, 10, 0, 22);

    public string ObsDockAccountSummary
    {
        get
        {
            var twitch = IsAccountConnected ? "Twitch: @" + AccountLogin : string.Empty;
            var youtube = IsYouTubeConnected ? Texts.YouTube + ": " + YouTubeAccountName : string.Empty;
            return twitch.Length > 0 && youtube.Length > 0 ? twitch + " · " + youtube
                : twitch.Length > 0 ? twitch : youtube.Length > 0 ? youtube : Texts.Guest;
        }
    }

    public string ObsDockChannelSummary
    {
        get
        {
            var twitch = string.IsNullOrWhiteSpace(Channel) ? string.Empty : Texts.CurrentChannel + ": @" + Channel;
            var youtube = IsYouTubeLiveConnected && !string.IsNullOrWhiteSpace(YouTubeBroadcastTitle)
                ? Texts.YouTube + ": " + YouTubeBroadcastTitle : string.Empty;
            return twitch.Length > 0 && youtube.Length > 0 ? twitch + " · " + youtube
                : twitch.Length > 0 ? twitch : youtube.Length > 0 ? youtube : Texts.Chat;
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(AccountLogin) or nameof(IsAccountConnected) or
            nameof(YouTubeAccountName) or nameof(IsYouTubeConnected) or nameof(Texts) or nameof(Language))
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ObsDockAccountSummary)));
        if (e.PropertyName is nameof(Channel) or nameof(YouTubeBroadcastTitle) or
            nameof(IsYouTubeLiveConnected) or nameof(Texts) or nameof(Language))
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ObsDockChannelSummary)));
        if (e.PropertyName == nameof(IsObsDockMode))
        {
            foreach (var name in new[] { nameof(ComposerCardMinHeight),
                nameof(ComposerCardPadding), nameof(ComposerInputMinHeight), nameof(ComposerInputPadding), nameof(ComposerSendSize),
                nameof(ChatEmptyCardPadding), nameof(ChatEmptyTitleSize), nameof(WelcomeCardPadding),
                nameof(WelcomeTitleSize), nameof(WelcomeDescriptionMargin), nameof(WelcomeActionMargin) })
                base.OnPropertyChanged(new PropertyChangedEventArgs(name));
        }
    }
}
