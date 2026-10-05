using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using WitherChat.Desktop.Services;
using Xunit;
namespace WitherChat.Avalonia.HeadlessTests;

public sealed class ImageCacheShutdownAuditTests
{
    [Fact]
    public void ImageRequestWaitingForCacheLockCannotRepopulateDisposedCache()
    {
        using var cache=new ChatImageCache(new EmptyHandler());
        var gate=typeof(ChatImageCache).GetField("_entryGate",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(cache)!;
        ChatImageResource? result=null;
        Exception? failure=null;
        using var started=new ManualResetEventSlim();
        var worker=new Thread(() =>
        {
            started.Set();
            try { result=cache.GetResource(new Uri("https://static-cdn.jtvnw.net/audit/r7.png")); }
            catch(Exception e) {failure=e;}
        }){IsBackground=true};
        lock(gate)
        {
            worker.Start();
            Assert.True(started.Wait(TimeSpan.FromSeconds(3),TestContext.Current.CancellationToken));
            Assert.True(SpinWait.SpinUntil(()=> (worker.ThreadState & ThreadState.WaitSleepJoin)!=0,3000),
                "Worker must be waiting inside the cache lock, not merely scheduled.");
            cache.Dispose();
        }
        Assert.True(worker.Join(TimeSpan.FromSeconds(3)));
        Assert.Null(failure);
        Assert.Null(result);
        var entries=(ConcurrentDictionary<string,ChatImageResource>)typeof(ChatImageCache)
            .GetField("_entries",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(cache)!;
        Assert.Empty(entries);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadingObserverMayCloseCacheWithoutPublishingANewResource(bool retry)
    {
        using var cache=new ChatImageCache(new TemporaryFailureHandler());
        var uri=new Uri("https://static-cdn.jtvnw.net/audit/progress-r7.png");
        if(retry) await cache.PreloadAsync([uri],TestContext.Current.CancellationToken);
        cache.LoadingStateChanged+=(_,_)=>{if(cache.IsLoading)cache.Dispose();};
        var resource=cache.GetResource(uri);
        Assert.Null(resource);
        Assert.False(cache.IsLoading);
        var pending=(ConcurrentDictionary<string,Task>)typeof(ChatImageCache)
            .GetField("_loadTasks",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(cache)!;
        Assert.Empty(pending);
    }
    private sealed class TemporaryFailureHandler:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
    private sealed class EmptyHandler:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
