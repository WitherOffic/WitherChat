using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TwitchChatMvp.Services;

public enum AnimationRole
{
    None,
    SettingsGear,
    ReconnectSpin
}

public static class AnimationService
{
    public static readonly DependencyProperty EnableButtonAnimationsProperty =
        DependencyProperty.RegisterAttached(
            "EnableButtonAnimations",
            typeof(bool),
            typeof(AnimationService),
            new PropertyMetadata(false, OnEnableButtonAnimationsChanged));

    public static readonly DependencyProperty AnimationRoleProperty =
        DependencyProperty.RegisterAttached(
            "AnimationRole",
            typeof(AnimationRole),
            typeof(AnimationService),
            new PropertyMetadata(AnimationRole.None));

    private static bool _reduceMotion;

    public static bool ReduceMotion => _reduceMotion;

    public static void SetReduceMotion(bool reduceMotion)
    {
        _reduceMotion = reduceMotion;
    }

    public static bool GetEnableButtonAnimations(DependencyObject element) =>
        (bool)element.GetValue(EnableButtonAnimationsProperty);

    public static void SetEnableButtonAnimations(DependencyObject element, bool value) =>
        element.SetValue(EnableButtonAnimationsProperty, value);

    public static AnimationRole GetAnimationRole(DependencyObject element) =>
        (AnimationRole)element.GetValue(AnimationRoleProperty);

    public static void SetAnimationRole(DependencyObject element, AnimationRole value) =>
        element.SetValue(AnimationRoleProperty, value);

    public static void AnimateWindowIn(Window window, double offsetX = 0, double offsetY = 20)
    {
        if (_reduceMotion)
        {
            window.Opacity = 0;
            Animate(window, UIElement.OpacityProperty, 1, 70);
            return;
        }

        window.Opacity = 0;
        var translate = EnsureTranslateTransform(window.Content as FrameworkElement);
        if (translate is not null)
        {
            translate.X = offsetX;
            translate.Y = offsetY;
            Animate(translate, TranslateTransform.XProperty, 0, 210);
            Animate(translate, TranslateTransform.YProperty, 0, 210);
        }

        Animate(window, UIElement.OpacityProperty, 1, 210);
    }

    public static void AnimateDialogIn(Window window)
    {
        if (_reduceMotion)
        {
            window.Opacity = 0;
            Animate(window, UIElement.OpacityProperty, 1, 70);
            return;
        }

        window.Opacity = 0;
        var scale = EnsureScaleTransform(window.Content as FrameworkElement);
        if (scale is not null)
        {
            scale.ScaleX = 0.96;
            scale.ScaleY = 0.96;
            Animate(scale, ScaleTransform.ScaleXProperty, 1, 180);
            Animate(scale, ScaleTransform.ScaleYProperty, 1, 180);
        }

        Animate(window, UIElement.OpacityProperty, 1, 180);
    }

    public static Task AnimateWindowCloseAsync(Window window, double offsetX = 0, double offsetY = 18)
    {
        if (_reduceMotion)
        {
            return AnimateAsync(window, UIElement.OpacityProperty, 0, 70);
        }

        var translate = EnsureTranslateTransform(window.Content as FrameworkElement);
        if (translate is not null)
        {
            Animate(translate, TranslateTransform.XProperty, offsetX, 140);
            Animate(translate, TranslateTransform.YProperty, offsetY, 140);
        }

        return AnimateAsync(window, UIElement.OpacityProperty, 0, 140);
    }

    public static void AnimateListBoxItem(ListBoxItem item)
    {
        if (_reduceMotion)
        {
            item.Opacity = 1;
            return;
        }

        item.Opacity = 0;
        var translate = EnsureTranslateTransform(item);
        if (translate is not null)
        {
            translate.Y = 6;
            Animate(translate, TranslateTransform.YProperty, 0, 140);
        }

        Animate(item, UIElement.OpacityProperty, 1, 140);
    }

    public static Task AnimatePanelInAsync(UIElement scrim, FrameworkElement panel, double scrimOpacity = 0.45, double offsetX = 40)
    {
        scrim.Opacity = 0;
        panel.Opacity = 0;
        var translate = EnsureTranslateTransform(panel);
        if (translate is not null)
        {
            translate.X = _reduceMotion ? 0 : offsetX;
            translate.Y = 0;
        }

        var duration = _reduceMotion ? 70 : 210;
        var tasks = new List<Task>
        {
            AnimateAsync(scrim, UIElement.OpacityProperty, scrimOpacity, duration),
            AnimateAsync(panel, UIElement.OpacityProperty, 1, duration)
        };
        if (!_reduceMotion && translate is not null)
        {
            tasks.Add(AnimateAsync(translate, TranslateTransform.XProperty, 0, duration));
        }

        return Task.WhenAll(tasks);
    }

    public static Task AnimatePanelOutAsync(UIElement scrim, FrameworkElement panel, double offsetX = 40)
    {
        var translate = EnsureTranslateTransform(panel);
        var duration = _reduceMotion ? 70 : 150;
        var tasks = new List<Task>
        {
            AnimateAsync(scrim, UIElement.OpacityProperty, 0, duration),
            AnimateAsync(panel, UIElement.OpacityProperty, 0, duration)
        };
        if (!_reduceMotion && translate is not null)
        {
            tasks.Add(AnimateAsync(translate, TranslateTransform.XProperty, offsetX, duration));
        }

        return Task.WhenAll(tasks);
    }

    private static void OnEnableButtonAnimationsChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not ButtonBase button)
        {
            return;
        }

        if (e.NewValue is true)
        {
            button.Loaded += Button_Loaded;
            button.Unloaded += Button_Unloaded;
            button.MouseEnter += Button_MouseEnter;
            button.MouseLeave += Button_MouseLeave;
            button.PreviewMouseDown += Button_PreviewMouseDown;
            button.PreviewMouseUp += Button_PreviewMouseUp;
            button.LostMouseCapture += Button_LostMouseCapture;
            button.Click += Button_Click;
        }
        else
        {
            DetachButton(button);
        }
    }

    private static void Button_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ButtonBase button)
        {
            EnsureButtonTransforms(button);
        }
    }

    private static void Button_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ButtonBase button && !_reduceMotion)
        {
            var transforms = EnsureButtonTransforms(button);
            transforms.Scale.ScaleX = 1;
            transforms.Scale.ScaleY = 1;
        }
    }

    private static void Button_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not ButtonBase button || _reduceMotion)
        {
            return;
        }

        var transforms = EnsureButtonTransforms(button);
        Animate(transforms.Scale, ScaleTransform.ScaleXProperty, 1.03, 120);
        Animate(transforms.Scale, ScaleTransform.ScaleYProperty, 1.03, 120);

        if (GetAnimationRole(button) == AnimationRole.SettingsGear)
        {
            Animate(transforms.Rotate, RotateTransform.AngleProperty, 18, 160);
        }
    }

    private static void Button_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not ButtonBase button || _reduceMotion)
        {
            return;
        }

        var transforms = EnsureButtonTransforms(button);
        Animate(transforms.Scale, ScaleTransform.ScaleXProperty, 1, 100);
        Animate(transforms.Scale, ScaleTransform.ScaleYProperty, 1, 100);
        if (GetAnimationRole(button) == AnimationRole.SettingsGear)
        {
            Animate(transforms.Rotate, RotateTransform.AngleProperty, 0, 120);
        }
    }

    private static void Button_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not ButtonBase button || _reduceMotion)
        {
            return;
        }

        var transforms = EnsureButtonTransforms(button);
        Animate(transforms.Scale, ScaleTransform.ScaleXProperty, 0.97, 80);
        Animate(transforms.Scale, ScaleTransform.ScaleYProperty, 0.97, 80);
    }

    private static void Button_PreviewMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        RestoreButtonScale(sender as ButtonBase);
    }

    private static void Button_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        RestoreButtonScale(sender as ButtonBase);
    }

    private static void Button_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ButtonBase button || _reduceMotion ||
            GetAnimationRole(button) != AnimationRole.ReconnectSpin)
        {
            return;
        }

        var transforms = EnsureButtonTransforms(button);
        var target = transforms.Rotate.Angle + 360;
        Animate(transforms.Rotate, RotateTransform.AngleProperty, target, 240);
    }

    private static void RestoreButtonScale(ButtonBase? button)
    {
        if (button is null || _reduceMotion)
        {
            return;
        }

        var target = button.IsMouseOver ? 1.03 : 1;
        var transforms = EnsureButtonTransforms(button);
        Animate(transforms.Scale, ScaleTransform.ScaleXProperty, target, 100);
        Animate(transforms.Scale, ScaleTransform.ScaleYProperty, target, 100);
    }

    private static void DetachButton(ButtonBase button)
    {
        button.Loaded -= Button_Loaded;
        button.Unloaded -= Button_Unloaded;
        button.MouseEnter -= Button_MouseEnter;
        button.MouseLeave -= Button_MouseLeave;
        button.PreviewMouseDown -= Button_PreviewMouseDown;
        button.PreviewMouseUp -= Button_PreviewMouseUp;
        button.LostMouseCapture -= Button_LostMouseCapture;
        button.Click -= Button_Click;
    }

    private static ButtonTransforms EnsureButtonTransforms(ButtonBase button)
    {
        button.RenderTransformOrigin = new Point(0.5, 0.5);
        if (button.RenderTransform is TransformGroup group)
        {
            var scale = group.Children.OfType<ScaleTransform>().FirstOrDefault();
            var rotate = group.Children.OfType<RotateTransform>().FirstOrDefault();
            if (scale is not null && rotate is not null)
            {
                return new ButtonTransforms(scale, rotate);
            }
        }

        var newScale = new ScaleTransform(1, 1);
        var newRotate = new RotateTransform(0);
        button.RenderTransform = new TransformGroup
        {
            Children =
            {
                newScale,
                newRotate
            }
        };
        return new ButtonTransforms(newScale, newRotate);
    }

    private static TranslateTransform? EnsureTranslateTransform(FrameworkElement? element)
    {
        if (element is null)
        {
            return null;
        }

        element.RenderTransformOrigin = new Point(0.5, 0.5);
        if (element.RenderTransform is TranslateTransform existingTranslate)
        {
            return existingTranslate;
        }

        if (element.RenderTransform is TransformGroup group)
        {
            var translate = group.Children.OfType<TranslateTransform>().FirstOrDefault();
            if (translate is not null)
            {
                return translate;
            }

            translate = new TranslateTransform();
            group.Children.Add(translate);
            return translate;
        }

        var newTranslate = new TranslateTransform();
        element.RenderTransform = newTranslate;
        return newTranslate;
    }

    private static ScaleTransform? EnsureScaleTransform(FrameworkElement? element)
    {
        if (element is null)
        {
            return null;
        }

        element.RenderTransformOrigin = new Point(0.5, 0.5);
        if (element.RenderTransform is ScaleTransform existingScale)
        {
            return existingScale;
        }

        if (element.RenderTransform is TransformGroup group)
        {
            var scale = group.Children.OfType<ScaleTransform>().FirstOrDefault();
            if (scale is not null)
            {
                return scale;
            }

            scale = new ScaleTransform(1, 1);
            group.Children.Add(scale);
            return scale;
        }

        var newScale = new ScaleTransform(1, 1);
        element.RenderTransform = newScale;
        return newScale;
    }

    private static void Animate(DependencyObject target, DependencyProperty property, double to, int milliseconds)
    {
        var animation = CreateAnimation(to, milliseconds);
        BeginAnimation(target, property, animation);
    }

    private static Task AnimateAsync(DependencyObject target, DependencyProperty property, double to, int milliseconds)
    {
        var tcs = new TaskCompletionSource<object?>();
        var animation = CreateAnimation(to, milliseconds);
        animation.Completed += (_, _) => tcs.TrySetResult(null);
        BeginAnimation(target, property, animation);
        return tcs.Task;
    }

    private static void BeginAnimation(DependencyObject target, DependencyProperty property, AnimationTimeline animation)
    {
        switch (target)
        {
            case UIElement element:
                element.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
                break;
            case Animatable animatable:
                animatable.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
                break;
        }
    }

    private static DoubleAnimation CreateAnimation(double to, int milliseconds)
    {
        return new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(Math.Max(1, milliseconds)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
    }

    private sealed record ButtonTransforms(ScaleTransform Scale, RotateTransform Rotate);
}
