using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using WitherChat.Core.Models;
using WitherChat.Desktop.ViewModels;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaFact]
    public async Task UserSendAuditEditingNextDraftDoesNotLoseItWhenPreviousSendCompletes()
    {
        var api = new DelayedSendUserAuditHandler("success");
        await using var fixture = new WindowFixture(apiHandler: api);
        var vm = fixture.ViewModel;
        ApplyAuditSession(vm);
        vm.ConnectionState = ChatConnectionState.Connected;
        vm.Channel = "audit";
        vm.ComposerText = "first message";
        var pending = vm.SendMessageCommand.ExecuteAsync(null);
        try
        {
            await api.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            vm.ComposerText = "second draft";
            api.Release.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("second draft", vm.ComposerText);
            Assert.Single(api.Posts);
            Assert.False(vm.IsSending);
        }
        finally { api.Release.TrySetResult(); await pending; }
    }

    [AvaloniaFact]
    public async Task UserSendAuditChannelChangeDuringTokenRefreshDoesNotSendToNewChannel()
    {
        var auth = new DelayedAuthAuditHandler();
        var api = new DelayedSendUserAuditHandler("success");
        await using var fixture = new WindowFixture(apiHandler: api, authHandler: auth);
        var vm = fixture.ViewModel;
        ApplyAuditSession(vm);
        var field = typeof(MainWindowViewModel).GetField("_authSession",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(vm, ((TwitchAuthSession)field.GetValue(vm)!) with
        {
            RefreshToken = "test-refresh-token",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        vm.ConnectionState = ChatConnectionState.Connected;
        vm.Channel = "audit";
        vm.ComposerText = "old channel message";
        var pending = vm.SendMessageCommand.ExecuteAsync(null);
        try
        {
            await auth.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            vm.Channel = "other_channel";
            vm.ComposerText = "new channel draft";
            api.Release.TrySetResult();
            auth.Release.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Empty(api.Posts);
            Assert.Equal("new channel draft", vm.ComposerText);
            Assert.False(vm.IsSending);
        }
        finally { auth.Release.TrySetResult(); api.Release.TrySetResult(); await pending; }
    }

    [AvaloniaTheory]
    [InlineData("success", false)]
    [InlineData("dropped", false)]
    [InlineData("failure", false)]
    [InlineData("timeout", false)]
    [InlineData("success", true)]
    [InlineData("dropped", true)]
    [InlineData("failure", true)]
    [InlineData("timeout", true)]
    public async Task UserSendAuditLateReplyDoesNotChangeAnotherChannelOrSignedOutUi(string outcome, bool signOut)
    {
        var api = new DelayedSendUserAuditHandler(outcome);
        await using var fixture = new WindowFixture(apiHandler: api);
        var vm = fixture.ViewModel;
        ApplyAuditSession(vm);
        vm.ConnectionState = ChatConnectionState.Connected;
        vm.Channel = "audit";
        vm.ComposerText = "first";
        var pending = vm.SendMessageCommand.ExecuteAsync(null);
        try
        {
            await api.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (signOut) vm.SignOutCommand.Execute(null);
            else vm.Channel = "other_channel";
            vm.ComposerText = "keep this draft";
            vm.StatusDetail = "new context ready";
            api.Release.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("keep this draft", vm.ComposerText);
            Assert.Equal("new context ready", vm.StatusDetail);
            Assert.False(vm.IsSending);
        }
        finally { api.Release.TrySetResult(); await pending; }
    }

    [AvaloniaTheory]
    [InlineData("success")]
    [InlineData("dropped")]
    [InlineData("failure")]
    [InlineData("timeout")]
    public async Task UserSendAuditCurrentContextReportsOutcomeAndKeepsUnsentDraft(string outcome)
    {
        var api = new DelayedSendUserAuditHandler(outcome);
        api.Release.TrySetResult();
        await using var fixture = new WindowFixture(apiHandler: api);
        var vm = fixture.ViewModel;
        ApplyAuditSession(vm);
        vm.ConnectionState = ChatConnectionState.Connected;
        vm.Channel = "audit";
        vm.ComposerText = "send this";
        await vm.SendMessageCommand.ExecuteAsync(null);
        Assert.Single(api.Posts);
        Assert.Equal(outcome == "success" ? "" : "send this", vm.ComposerText);
        Assert.NotEmpty(vm.StatusDetail);
        Assert.False(vm.IsSending);
    }

    [AvaloniaTheory]
    [InlineData("channel")]
    [InlineData("return")]
    [InlineData("signout")]
    public async Task UserSendAuditContextChangeDuringRecipientLookupCancelsBeforePost(string change)
    {
        var api = new DelayedSendUserAuditHandler("success") { BlockRecipientLookup = true };
        await using var fixture = new WindowFixture(apiHandler: api);
        var vm = fixture.ViewModel;
        ApplyAuditSession(vm);
        vm.ConnectionState = ChatConnectionState.Connected;
        vm.Channel = "audit";
        vm.ComposerText = "first";
        var pending = vm.SendMessageCommand.ExecuteAsync(null);
        try
        {
            await api.RecipientLookupStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (change == "signout") vm.SignOutCommand.Execute(null);
            else
            {
                vm.Channel = "other_channel";
                if (change == "return") vm.Channel = "audit";
            }
            vm.ComposerText = "keep new draft";
            vm.StatusDetail = "current context";
            api.Release.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(api.RecipientLookupToken.IsCancellationRequested);
            Assert.Empty(api.Posts);
            Assert.Equal("keep new draft", vm.ComposerText);
            Assert.Equal("current context", vm.StatusDetail);
            Assert.False(vm.IsSending);
        }
        finally { api.Release.TrySetResult(); await pending; }
    }

    [AvaloniaFact]
    public async Task UserSendAuditTokenRefreshWithoutContextChangeStillSendsNormally()
    {
        var auth = new DelayedAuthAuditHandler();
        var api = new DelayedSendUserAuditHandler("success");
        api.Release.TrySetResult();
        await using var fixture = new WindowFixture(apiHandler: api, authHandler: auth);
        var vm = fixture.ViewModel;
        ApplyAuditSession(vm);
        var field = typeof(MainWindowViewModel).GetField("_authSession",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(vm, ((TwitchAuthSession)field.GetValue(vm)!) with
        { RefreshToken = "test-refresh-token", ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) });
        vm.ConnectionState = ChatConnectionState.Connected;
        vm.Channel = "audit";
        vm.ComposerText = "send after refresh";
        var pending = vm.SendMessageCommand.ExecuteAsync(null);
        try
        {
            await auth.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            auth.Release.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Single(api.Posts);
            Assert.Empty(vm.ComposerText);
            Assert.False(vm.IsSending);
        }
        finally { auth.Release.TrySetResult(); await pending; }
    }

    private sealed class DelayedSendUserAuditHandler(string outcome) : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Posts { get; } = [];
        public bool BlockRecipientLookup { get; init; }
        public TaskCompletionSource RecipientLookupStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken RecipientLookupToken { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/chat/messages", StringComparison.Ordinal))
            {
                Posts.Add(await request.Content!.ReadAsStringAsync(token));
                Started.TrySetResult();
                await Release.Task.WaitAsync(token);
                if (outcome == "timeout") throw new TaskCanceledException("Simulated HTTP timeout.");
                if (outcome == "failure") throw new HttpRequestException("Simulated send failure.");
                return Response(new { data = new[] { new
                {
                    message_id = "sent-id", is_sent = outcome == "success",
                    drop_reason = new { code = "blocked", message = "blocked by channel settings" }
                } } });
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("/users", StringComparison.Ordinal))
            {
                if (BlockRecipientLookup)
                {
                    RecipientLookupToken = token;
                    RecipientLookupStarted.TrySetResult();
                    await Release.Task.WaitAsync(token);
                }
                return Response(new { data = new[] { new { id = "channel-id", login = "audit",
                    display_name = "Audit", profile_image_url = "" } } });
            }
            return Response(new { data = Array.Empty<object>() });
        }

        private static HttpResponseMessage Response(object data) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") };
    }
}
