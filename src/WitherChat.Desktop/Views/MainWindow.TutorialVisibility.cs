using Avalonia.Controls;
using Avalonia.Threading;
using WitherChat.Desktop.Models;
using WitherChat.Desktop.ViewModels;

namespace WitherChat.Desktop.Views;

public partial class MainWindow
{
    private void QueueTutorialTargetVisibility(int step, int version)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (version != _onboardingStepTransitionVersion ||
                DataContext is not MainWindowViewModel { IsOnboardingOpen: true } vm) return;
            OnboardingContentScrollViewer.Offset = default;
            Control? target = vm.IsContextTutorial
                ? vm.ActiveTutorialTopic is TutorialTopic.Settings or TutorialTopic.ObsPlugin
                    ? GetContextTutorialTargetControl(vm.ActiveTutorialTopic, step) : null
                : step switch { 7 => CopyOverlayUrlButton, 10 => ObsPluginStatusText, 11 => ObsPluginHelpButton, _ => null };
            target?.BringIntoView();
        }, DispatcherPriority.Loaded);
    }
}
