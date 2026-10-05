using System.Net;
using System.Reflection;
using System.Text;
using WitherChat.Core.Models;
using WitherChat.Core.Services;
using Xunit;

namespace WitherChat.Core.ApiTests;

public sealed class EmoteProviderDeadlineAuditTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AProviderDeadlineKeepsTheOtherProviderCatalog(bool stallBody)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var service = new ThirdPartyEmoteCatalogService(new ProviderHandler(stallBody));
        SetTimeout(service, TimeSpan.FromMilliseconds(80));
        var load = service.LoadDetailedAsync(string.Empty, cancellation.Token);
        try
        {
            var result = await load.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.False(result.IsComplete);
            Assert.Contains(ThirdPartyEmoteProviders.SevenTv, result.FailedProviders);
            Assert.DoesNotContain(ThirdPartyEmoteProviders.Bttv, result.FailedProviders);
            Assert.Equal(ThirdPartyEmoteProviders.Bttv, result.Catalog["KeepMe"].Provider);
        }
        finally
        {
            await cancellation.CancelAsync();
            _ = await Record.ExceptionAsync(() => load);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UserCancellationIsNotReportedAsPartialProviderFailure(bool stallBody)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var service = new ThirdPartyEmoteCatalogService(new ProviderHandler(stallBody));
        SetTimeout(service, TimeSpan.FromSeconds(2));
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(60));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.LoadDetailedAsync(string.Empty, cancellation.Token));
    }

    private static void SetTimeout(ThirdPartyEmoteCatalogService service, TimeSpan timeout)
    {
        var http = (HttpClient)typeof(ThirdPartyEmoteCatalogService)
            .GetField("_httpClient", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;
        http.Timeout = timeout;
    }

    private sealed class ProviderHandler(bool stallBody) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host.Contains("betterttv", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[{\"id\":\"kept-id\",\"code\":\"KeepMe\"}]", Encoding.UTF8, "application/json")
                };
            }
            if (!stallBody)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) };
        }
    }

    private sealed class StalledStream : MemoryStream
    {
        public override bool CanSeek => false;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
