using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using WitherChat.Core.Models;
using WitherChat.Desktop.ViewModels;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaFact]
    public async Task StreamEventDeduplicationRemainsBoundedAndKeepsVisibleEventsProtected()
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        for (var index = 0; index < 6000; index++)
        {
            vm.EnqueueStreamEventForTesting(new StreamEvent
            {
                Id = "bounded-event-" + index, Platform = ChatPlatforms.Twitch, Channel = "audit",
                Kind = StreamEventKinds.Subscription, DisplayName = "Viewer",
                Timestamp = DateTimeOffset.UtcNow
            });
        }
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Equal(1000, vm.StreamEvents.Count);
        var ids = (HashSet<string>)typeof(MainWindowViewModel).GetField("_streamEventIds",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        Assert.InRange(ids.Count, 1, 4096);
        var latest = vm.StreamEvents[^1];
        vm.EnqueueStreamEventForTesting(latest.Value);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Same(latest, vm.StreamEvents[^1]);
        Assert.Equal(1000, vm.StreamEvents.Count);
        Assert.All(vm.StreamEvents.Concat(vm.VisibleStreamEvents),
            item => Assert.Contains(item.Value.Platform + ":" + item.Value.Id, ids));
    }
}
