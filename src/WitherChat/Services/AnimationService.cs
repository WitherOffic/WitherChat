using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WitherChat.Services;

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

    public static readonly DependencyProperty EnableHoverLiftProperty =
        DependencyProperty.RegisterAttached(
            "EnableHoverLift",
            typeof(bool),
            typeof(AnimationService),
            new PropertyMetadata(false));

    private static bool _reduceMotion;

    public static bool ReduceMotion => _reduceMotion;

    public static event EventHandler? ReduceMotionChanged;

    public static void SetReduceMotion(bool reduceMotion)
    {
        if (_reduceMotion == reduceMotion)
        {
            return;
        }

        _reduceMotion = reduceMotion;
        ReduceMotionChanged?.Invoke(null, EventArgs.Empty);
    }

    public static bool GetEnableButtonAnimations(DependencyObject element) =>
        (bool)element.GetValue(EnableButtonAnimationsProperty);

    public static void SetEnableButtonAnimations(DependencyObject element, bool value) =>
        element.SetValue(EnableButtonAnimationsProperty, value);

    public static AnimationRole GetAnimationRole(DependencyObject element) =>
        (AnimationRole)element.GetValue(AnimationRoleProperty);

    public static void SetAnimationRole(DependencyObject element, AnimationRole value) =>
        element.SetValue(AnimationRoleProperty, value);

    public static bool GetEnableHoverLift(DependencyObject element) =>
        (bool)element.GetValue(EnableHoverLiftProperty);

    public static void SetEnableHoverLift(DependencyObject element, bool value) =>
        element.SetValue(EnableHoverLiftProperty, value);

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
            Animate(translate, TranslateTransform.XProperty, 0, 240);
            Animate(translate, TranslateTransform.YProperty, 0, 240);
        }

        Animate(window, UIElement.OpacityProperty, 1, 240);
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
            Animate(scale, ScaleTransform.ScaleXProperty, 1, 210);
            Animate(scale, ScaleTransform.ScaleYProperty, 1, 210);
        }

        Animate(window, UIElement.OpacityProperty, 1, 210);
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
            Animate(translate, TranslateTransform.XProperty, offsetX, 175);
            Animate(translate, TranslateTransform.YProperty, offsetY, 175);
        }

        return AnimateAsync(window, UIElement.OpacityProperty, 0, 175);
    }

    public static async Task AnimateWindowStateChangeAsync(Window window, Action changeState)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(changeState);
        if (_reduceMotion)
        {
            changeState();
            return;
        }

        var scale = EnsureScaleTransform(window.Content as FrameworkElement);
        var fadeTasks = new List<Task>
        {
            AnimateAsync(window, UIElement.OpacityProperty, 0.82, 90)
        };
        if (scale is not null)
        {
            fadeTasks.Add(AnimateAsync(scale, ScaleTransform.ScaleXProperty, 0.992, 90));
            fadeTasks.Add(AnimateAsync(scale, ScaleTransform.ScaleYProperty, 0.992, 90));
        }

        await Task.WhenAll(fadeTasks).ConfigureAwait(true);
        changeState();
        window.UpdateLayout();

        var restoreTasks = new List<Task>
        {
            AnimateAsync(window, UIElement.OpacityProperty, 1, 190)
        };
        if (scale is not null)
        {
            restoreTasks.Add(AnimateAsync(scale, ScaleTransform.ScaleXProperty, 1, 190));
            restoreTasks.Add(AnimateAsync(scale, ScaleTransform.ScaleYProperty, 1, 190));
        }

        await Task.WhenAll(restoreTasks).ConfigureAwait(true);
        ResetWindowVisuals(window);
    }

    public static void ResetWindowVisuals(Window window)
    {
        window.BeginAnimation(UIElement.OpacityProperty, null);
        window.Opacity = 1;
        if (window.Content is not FrameworkElement content)
        {
            return;
        }

        switch (content.RenderTransform)
        {
            case TranslateTransform translate:
                translate.BeginAnimation(TranslateTransform.XProperty, null);
                translate.BeginAnimation(TranslateTransform.YProperty, null);
                translate.X = 0;
                translate.Y = 0;
                break;
            case ScaleTransform scale:
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleX = 1;
                scale.ScaleY = 1;
                break;
            case TransformGroup group:
                foreach (var child in group.Children)
                {
                    if (child is TranslateTransform groupTranslate)
                    {
                        groupTranslate.BeginAnimation(TranslateTransform.XProperty, null);
                        groupTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                        groupTranslate.X = 0;
                        groupTranslate.Y = 0;
                    }
                    else if (child is ScaleTransform groupScale)
                    {
                        groupScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                        groupScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                        groupScale.ScaleX = 1;
                        groupScale.ScaleY = 1;
                    }
                }
                break;
        }
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
            var translateAnimation = CreateAnimation(0, 180);
            translateAnimation.Completed += (_, _) =>
            {
                translate.BeginAnimation(TranslateTransform.YProperty, null);
                translate.Y = 0;
            };
            BeginAnimation(translate, TranslateTransform.YProperty, translateAnimation);
        }

        var opacityAnimation = CreateAnimation(1, 180);
        opacityAnimation.Completed += (_, _) =>
        {
            item.BeginAnimation(UIElement.OpacityProperty, null);
            item.Opacity = 1;
        };
        BeginAnimation(item, UIElement.OpacityProperty, opacityAnimation);
    }

    public static async Task AnimatePanelInAsync(UIElement scrim, FrameworkElement panel, double scrimOpacity = 0.45, double offsetX = 40)
    {
        scrim.Opacity = 0;
        panel.Opacity = 0;
        var translate = EnsureTranslateTransform(panel);
        if (translate is not null)
        {
            translate.X = _reduceMotion ? 0 : offsetX;
            translate.Y = 0;
        }

        var duration = _reduceMotion ? 70 : 240;
        var tasks = new List<Task>
        {
            AnimateAsync(scrim, UIElement.OpacityProperty, scrimOpacity, duration),
            AnimateAsync(panel, UIElement.OpacityProperty, 1, duration)
        };
        if (!_reduceMotion && translate is not null)
        {
            tasks.Add(AnimateAsync(translate, TranslateTransform.XProperty, 0, duration));
        }

        await Task.WhenAll(tasks).ConfigureAwait(true);

        // Remove the completed composition clocks so text inside the panel is
        // rendered directly again instead of remaining on a softened bitmap layer.
        scrim.BeginAnimation(UIElement.OpacityProperty, null);
        scrim.Opacity = scrimOpacity;
        panel.BeginAnimation(UIElement.OpacityProperty, null);
        panel.Opacity = 1;
        if (translate is not null)
        {
            translate.BeginAnimation(TranslateTransform.XProperty, null);
            translate.X = 0;
        }
    }

    public static Task AnimatePanelOutAsync(UIElement scrim, FrameworkElement panel, double offsetX = 40)
    {
        var translate = EnsureTranslateTransform(panel);
        var duration = _reduceMotion ? 70 : 180;
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
            transforms.Translate.Y = 0;
        }
    }

    private static void Button_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not ButtonBase button || _reduceMotion)
        {
            return;
        }

        var transforms = EnsureButtonTransforms(button);
        Animate(transforms.Scale, ScaleTransform.ScaleXProperty, 1, 120);
        Animate(transforms.Scale, ScaleTransform.ScaleYProperty, 1, 120);
        if (GetEnableHoverLift(button))
        {
            Animate(transforms.Translate, TranslateTransform.YProperty, -2, 140);
        }

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
        if (GetEnableHoverLift(button))
        {
            Animate(transforms.Translate, TranslateTransform.YProperty, 0, 110);
        }
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
        var pressedScale = GetEnableHoverLift(button) ? 0.96 : 1;
        Animate(transforms.Scale, ScaleTransform.ScaleXProperty, pressedScale, 80);
        Animate(transforms.Scale, ScaleTransform.ScaleYProperty, pressedScale, 80);
        if (GetEnableHoverLift(button))
        {
            Animate(transforms.Translate, TranslateTransform.YProperty, 0, 80);
        }
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

        const double target = 1;
        var transforms = EnsureButtonTransforms(button);
        Animate(transforms.Scale, ScaleTransform.ScaleXProperty, target, 100);
        Animate(transforms.Scale, ScaleTransform.ScaleYProperty, target, 100);
        if (GetEnableHoverLift(button))
        {
            Animate(
                transforms.Translate,
                TranslateTransform.YProperty,
                button.IsMouseOver ? -2 : 0,
                110);
        }
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
            var translate = group.Children.OfType<TranslateTransform>().FirstOrDefault();
            if (scale is not null && rotate is not null)
            {
                translate ??= new TranslateTransform();
                if (!group.Children.Contains(translate))
                {
                    group.Children.Add(translate);
                }

                return new ButtonTransforms(scale, rotate, translate);
            }
        }

        var newScale = new ScaleTransform(1, 1);
        var newRotate = new RotateTransform(0);
        var newTranslate = new TranslateTransform(0, 0);
        button.RenderTransform = new TransformGroup
        {
            Children =
            {
                newScale,
                newRotate,
                newTranslate
            }
        };
        return new ButtonTransforms(newScale, newRotate, newTranslate);
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
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
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
            EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }
        };
    }

    private sealed record ButtonTransforms(
        ScaleTransform Scale,
        RotateTransform Rotate,
        TranslateTransform Translate);
}
