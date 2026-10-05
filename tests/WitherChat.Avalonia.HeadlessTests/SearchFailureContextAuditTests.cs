using System.Net;
using System.Reflection;
using System.Text;
using Avalonia.Headless.XUnit;
using WitherChat.Desktop.ViewModels;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingConnectPanelThroughSettingsRejectsLateSearchFailure(bool invalidJson)
    {
        var handler = new LateSearchFailureHandler(invalidJson);
        await using var fixture = new WindowFixture(apiHandler: handler);
        var vm = fixture.ViewModel;
        vm.ConnectPanelChannel = "audit";
        vm.IsConnectPanelOpen = true;
        var search = (Task)typeof(MainWindowViewModel).GetMethod("SearchChannelsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, ["audit", true])!;
        try
        {
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await vm.ToggleSettingsCommand.ExecuteAsync(null);
            Assert.False(vm.IsConnectPanelOpen);
            vm.ChannelSearchStatus = "closed search context";
            handler.Release.TrySetResult();
            await search.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("closed search context", vm.ChannelSearchStatus);
            Assert.False(vm.IsChannelSearchBusy);
        }
        finally { handler.Release.TrySetResult(); await search; }
    }

    private sealed class LateSearchFailureHandler(bool invalidJson) : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(invalidJson ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(invalidJson ? "{" : "Unavailable", Encoding.UTF8, "application/json")
            };
        }
    }
}
