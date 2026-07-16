using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace WitherChat.Models;

public sealed class BadgeModel : INotifyPropertyChanged
{
    private ImageSource? _imageSource;
    private string _title = string.Empty;

    public string SetId { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Info { get; init; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public string Title
    {
        get => _title;
        set
        {
            if (!string.Equals(_title, value, StringComparison.Ordinal))
            {
                _title = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ToolTip));
            }
        }
    }

    public ImageSource? ImageSource
    {
        get => _imageSource;
        set
        {
            if (!ReferenceEquals(_imageSource, value))
            {
                _imageSource = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasImage));
            }
        }
    }

    public bool HasImage => ImageSource is not null;

    public string DisplayText => string.IsNullOrWhiteSpace(Info)
        ? SetId
        : $"{SetId}:{Info}";

    public string ToolTip => string.IsNullOrWhiteSpace(Title) ? DisplayText : Title;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
