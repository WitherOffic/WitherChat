using CommunityToolkit.Mvvm.ComponentModel;

namespace WitherChat.Desktop.ViewModels;

public sealed partial class SelectionOptionViewModel(string value, string label) : ObservableObject
{
    public string Value { get; } = value;

    [ObservableProperty]
    private string _label = label;

    public override string ToString() => Label;
}
