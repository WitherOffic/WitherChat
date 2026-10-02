using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data;
using Avalonia.Automation;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WitherChat.Desktop.Models;
using WitherChat.Desktop.Services;
using WitherChat.Desktop.ViewModels;

namespace WitherChat.Desktop.Controls;

public sealed class RichChatTextBlock : TextBlock
{
    private const double CompactEmoteHeight = 22;
    private static readonly IBrush LinkBrush = new SolidColorBrush(Color.Parse("#B69CFF"));
    private bool _rebuildScheduled;

    public event EventHandler<ValueEventArgs<Uri>>? LinkClicked;

    public static readonly StyledProperty<IReadOnlyList<ChatMessagePartViewModel>?> PartsProperty =
        AvaloniaProperty.Register<RichChatTextBlock, IReadOnlyList<ChatMessagePartViewModel>?>(nameof(Parts));

    public static readonly StyledProperty<string> PrefixTextProperty =
        AvaloniaProperty.Register<RichChatTextBlock, string>(nameof(PrefixText), string.Empty);

    public static readonly StyledProperty<IBrush?> PrefixBrushProperty =
        AvaloniaProperty.Register<RichChatTextBlock, IBrush?>(nameof(PrefixBrush));

    public static readonly StyledProperty<bool> UseCompactEmotesProperty =
        AvaloniaProperty.Register<RichChatTextBlock, bool>(nameof(UseCompactEmotes));

    public IReadOnlyList<ChatMessagePartViewModel>? Parts
    {
        get => GetValue(PartsProperty);
        set => SetValue(PartsProperty, value);
    }

    public string PrefixText
    {
        get => GetValue(PrefixTextProperty);
        set => SetValue(PrefixTextProperty, value);
    }

    public IBrush? PrefixBrush
    {
        get => GetValue(PrefixBrushProperty);
        set => SetValue(PrefixBrushProperty, value);
    }

    public bool UseCompactEmotes
    {
        get => GetValue(UseCompactEmotesProperty);
        set => SetValue(UseCompactEmotesProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == PartsProperty ||
            change.Property == PrefixTextProperty ||
            change.Property == PrefixBrushProperty ||
            change.Property == UseCompactEmotesProperty ||
            change.Property == FontSizeProperty)
        {
            ScheduleRebuild();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs eventArgs)
    {
        base.OnAttachedToVisualTree(eventArgs);
        // Virtualized chat rows are frequently detached and reused while the
        // viewport follows new messages. Build their content synchronously on
        // attach so a newly realized row never renders a blank frame containing
        // only its timestamp and badges. A queued rebuild may still run later to
        // coalesce property changes made by the binding engine.
        RebuildInlines(Parts);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Avalonia 12.1 can render a hidden row before its next measure and
        // cache an empty layout while the inline runs are invalid. Reset both
        // caches before the base measure rebuilds the runs from the message.
        if (_textRuns is null && Inlines is { Count: > 0 })
        {
            InvalidateTextLayout();
            base.OnMeasureInvalidated();
        }

        return base.MeasureOverride(availableSize);
    }

    private void ScheduleRebuild()
    {
        if (_rebuildScheduled)
        {
            return;
        }

        _rebuildScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _rebuildScheduled = false;
            RebuildInlines(Parts);
        }, DispatcherPriority.Render);
    }

    private void RebuildInlines(IReadOnlyList<ChatMessagePartViewModel>? parts)
    {
        var inlines = new InlineCollection();
        if (!string.IsNullOrWhiteSpace(PrefixText))
        {
            inlines.Add(new Run(PrefixText)
            {
                Foreground = PrefixBrush,
                FontWeight = FontWeight.SemiBold
            });
            inlines.Add(" ");
        }
        if (parts is null)
        {
            Inlines = inlines;
            return;
        }

        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
            if (part.IsLink && part.LinkUri is { } linkUri)
            {
                inlines.Add(new InlineUIContainer(CreateLinkButton(part, linkUri))
                {
                    BaselineAlignment = BaselineAlignment.Center
                });
                continue;
            }
            if (part.IsText)
            {
                inlines.Add(part.Text);
                continue;
            }

            var resource = part.ImageResource;
            if (resource is null)
            {
                inlines.Add(part.Text);
                continue;
            }

            var displayHeight = UseCompactEmotes ? CompactEmoteHeight : part.DisplayHeight;
            var widthProperty = UseCompactEmotes
                ? nameof(ChatImageResource.CompactDisplayWidth)
                : nameof(ChatImageResource.DisplayWidth);
            var panel = CreateEmotePanel(part, resource, displayHeight, widthProperty);
            AddEmoteVisual(panel, part, resource, displayHeight, widthProperty);
            inlines.Add(new InlineUIContainer(panel)
            {
                BaselineAlignment = BaselineAlignment.Center
            });

            if (part.IsZeroWidth)
            {
                continue;
            }

            // 7TV zero-width emotes are typed after a base emote, usually with a
            // separating space. They must share the base inline instead of taking
            // their own layout slot, otherwise virtualization makes them appear to
            // jump into neighbouring message text while scrolling.
            var overlayIndex = index + 1;
            while (overlayIndex < parts.Count)
            {
                var overlay = parts[overlayIndex];
                if (overlay.IsText &&
                    !overlay.Text.Contains('\n', StringComparison.Ordinal) &&
                    string.IsNullOrWhiteSpace(overlay.Text) &&
                    overlayIndex + 1 < parts.Count &&
                    !parts[overlayIndex + 1].IsText &&
                    parts[overlayIndex + 1].IsZeroWidth &&
                    parts[overlayIndex + 1].ImageResource is not null)
                {
                    overlayIndex++;
                    overlay = parts[overlayIndex];
                }

                if (overlay.IsText || !overlay.IsZeroWidth || overlay.ImageResource is not { } overlayResource)
                {
                    break;
                }

                AddEmoteVisual(panel, overlay, overlayResource, displayHeight, widthProperty);
                overlayIndex++;
            }

            index = overlayIndex - 1;
        }

        Inlines = inlines;
    }

    private Button CreateLinkButton(ChatMessagePartViewModel part, Uri linkUri)
    {
        var label = new TextBlock
        {
            Text = part.Text,
            FontSize = FontSize,
            FontWeight = FontWeight.SemiBold,
            Foreground = LinkBrush,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 260,
            VerticalAlignment = VerticalAlignment.Center
        };
        var button = new Button
        {
            Content = label,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            Margin = new Thickness(1, 0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        AutomationProperties.SetName(button, part.FullLink);
        ToolTip.SetTip(button, new TextBlock
        {
            Text = part.FullLink,
            MaxWidth = 420,
            TextWrapping = TextWrapping.Wrap
        });
        button.PointerEntered += (_, _) =>
            label.TextDecorations = global::Avalonia.Media.TextDecorations.Underline;
        button.PointerExited += (_, _) => label.TextDecorations = null;
        button.Click += (_, _) => LinkClicked?.Invoke(this, new ValueEventArgs<Uri>(linkUri));
        return button;
    }

    private static Grid CreateEmotePanel(
        ChatMessagePartViewModel part,
        ChatImageResource resource,
        double displayHeight,
        string widthProperty)
    {
        var panel = new Grid
        {
            Height = displayHeight,
            Margin = new Thickness(1, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        // Inline images replace the textual emote code visually. Keep the code
        // and provider available to screen readers instead of exposing an
        // unnamed image in the middle of the message.
        AutomationProperties.SetName(panel, part.ToolTip);
        panel.Bind(WidthProperty, new Binding(widthProperty) { Source = resource });
        return panel;
    }

    private static void AddEmoteVisual(
        Grid panel,
        ChatMessagePartViewModel part,
        ChatImageResource resource,
        double displayHeight,
        string widthProperty)
    {
        var fallback = new TextBlock
        {
            Text = part.Text,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextAlignment = TextAlignment.Center
        };
        fallback.Bind(IsVisibleProperty, new Binding(nameof(ChatImageResource.ShowFallback)) { Source = resource });
        panel.Children.Add(fallback);

        var image = new AnimatedEmoteImage
        {
            Resource = resource,
            Stretch = Stretch.Uniform,
            Height = displayHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        image.Bind(WidthProperty, new Binding(widthProperty) { Source = resource });
        image.Bind(IsVisibleProperty, new Binding(nameof(ChatImageResource.HasImage)) { Source = resource });
        ToolTip.SetTip(image, part.ToolTip);
        panel.Children.Add(image);
    }
}
