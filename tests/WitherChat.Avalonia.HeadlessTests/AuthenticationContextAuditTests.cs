using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using WitherChat.Core;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using WitherChat.Desktop.ViewModels;
using Xunit;

namespace WitherChat.Avalonia.HeadlessTests;

public sealed partial class MainWindowLayoutTests
{
    [AvaloniaFact]
    public async Task SigningOutDuringTokenRefreshCannotRestoreTheAccount()
    {
        var handler = new DelayedAuthAuditHandler();
        await using var fixture = new WindowFixture(authHandler: handler);
        var vm = fixture.ViewModel;
        ApplyAuditSession(vm);
        var field = typeof(MainWindowViewModel).GetField("_authSession", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var session = ((TwitchAuthSession)field.GetValue(vm)!) with
        {
            RefreshToken = "test-refresh-token",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        field.SetValue(vm, session);
        var method = typeof(MainWindowViewModel).GetMethod("EnsureValidSessionAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pending = (Task<TwitchAuthSession>)method.Invoke(vm, [session, CancellationToken.None])!;
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        vm.SignOutCommand.Execute(null);
        handler.Release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await pending; });
        Assert.False(vm.IsAccountConnected);
        Assert.Empty(vm.AccountLogin);
        var store = (TwitchAuthSessionStore)typeof(MainWindowViewModel)
            .GetField("_authSessionStore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        Assert.Null(store.Load());
    }

    [AvaloniaFact]
    public async Task SigningOutDuringModerationRefreshFinishesTheCommandWithoutSendingAnAction()
    {
        var auth = new DelayedAuthAuditHandler();
        var api = new DelayedModerationAuditHandler("none");
        await using var fixture = new WindowFixture(authHandler: auth, apiHandler: api);
        var vm = fixture.ViewModel;
        ApplyAuditSession(vm);
        var field = typeof(MainWindowViewModel).GetField("_authSession", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var session = ((TwitchAuthSession)field.GetValue(vm)!) with
        {
            RefreshToken = "test-refresh-token",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        field.SetValue(vm, session);
        vm.CanModerate = true;
        vm.ModerationUserLogin = "viewer";
        var pending = vm.UnbanByLoginCommand.ExecuteAsync(null);
        await auth.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        vm.SignOutCommand.Execute(null);
        auth.Release.TrySetResult();
        await pending;
        Assert.False(vm.IsAccountConnected);
        Assert.False(vm.CanModerate);
        Assert.False(vm.IsModerationPanelBusy);
        Assert.DoesNotContain(api.Requests, request => !request.StartsWith("GET", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task ASessionCapturedBeforeSignOutCannotBeRestoredByALateCaller()
    {
        await using var fixture = new WindowFixture();
        var vm = fixture.ViewModel;
        ApplyAuditSession(vm);
        var field = typeof(MainWindowViewModel).GetField("_authSession", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var oldSession = (TwitchAuthSession)field.GetValue(vm)!;
        vm.SignOutCommand.Execute(null);
        var method = typeof(MainWindowViewModel).GetMethod("EnsureValidSessionAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pending = (Task<TwitchAuthSession>)method.Invoke(vm, [oldSession, CancellationToken.None])!;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await pending; });
        Assert.False(vm.IsAccountConnected);
        var store = (TwitchAuthSessionStore)typeof(MainWindowViewModel)
            .GetField("_authSessionStore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        Assert.Null(store.Load());
    }

    [AvaloniaFact]
    public async Task SigningOutCancelsFollowedChannelsTokenRefreshWithoutFaulting()
    {
        var auth = new DelayedAuthAuditHandler();
        await using var fixture = new WindowFixture(authHandler: auth);
        var vm = fixture.ViewModel;
        ApplyAuditSession(vm);
        var field = typeof(MainWindowViewModel).GetField("_authSession", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(vm, ((TwitchAuthSession)field.GetValue(vm)!) with
        {
            RefreshToken = "test-refresh-token",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            Scopes = TwitchApplication.RequiredScopes.Concat([TwitchApplication.FollowedChannelsScope]).ToArray()
        });
        var method = typeof(MainWindowViewModel).GetMethod("RefreshFollowedChannelsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pending = (Task)method.Invoke(vm, null)!;
        await auth.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        vm.SignOutCommand.Execute(null);
        auth.Release.TrySetResult();
        await pending;
        Assert.False(vm.IsAccountConnected);
        Assert.Empty(vm.FollowedChannels);
        Assert.False(vm.IsFollowedChannelsLoading);
    }

    [AvaloniaFact]
    public async Task LateValidationFailureDoesNotDisconnectOrEraseANewAccount()
    {
        var auth = new DelayedValidationFailureAuditHandler();
        await using var fixture = new WindowFixture(authHandler: auth);
        var vm = fixture.ViewModel;
        ApplyAuditSession(vm);
        var field = typeof(MainWindowViewModel).GetField("_authSession", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var original = (TwitchAuthSession)field.GetValue(vm)!;
        field.SetValue(vm, original with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) });
        var tick = typeof(MainWindowViewModel).GetMethod("OnSessionValidationTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        tick.Invoke(vm, [null, EventArgs.Empty]);
        await auth.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var replacement = original with
        {
            AccessToken = "test-new-account-token", UserId = "new-owner-id", Login = "new_owner",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
        };
        typeof(MainWindowViewModel).GetMethod("ApplyAuthenticatedSession",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [replacement]);
        var store = (TwitchAuthSessionStore)typeof(MainWindowViewModel)
            .GetField("_authSessionStore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        store.Save(replacement);
        vm.AuthStatus = "new account ready";
        auth.Release.TrySetResult();
        var busy = typeof(MainWindowViewModel).GetField("_sessionValidationInProgress",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        await WaitForAsync(() => !(bool)busy.GetValue(vm)!, TimeSpan.FromSeconds(3));
        Assert.True(vm.IsAccountConnected);
        Assert.Equal("new_owner", vm.AccountLogin);
        Assert.Equal("new account ready", vm.AuthStatus);
        Assert.Equal("new-owner-id", store.Load()!.UserId);
    }

    private sealed class DelayedValidationFailureAuditHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(token);
            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"message\":\"Old token expired\"}")
            };
        }
    }

    private sealed class DelayedAuthAuditHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(token);
                return Response(new
                {
                    access_token = "test-refreshed-token", refresh_token = "test-refresh-token",
                    expires_in = 3600, token_type = "bearer", scope = TwitchApplication.RequiredScopes
                });
            }
            return Response(new
            {
                client_id = TwitchApplication.ClientId, login = "audit_owner", user_id = "owner-id",
                scopes = TwitchApplication.RequiredScopes, expires_in = 3600
            });
        }

        private static HttpResponseMessage Response(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };
    }
}
