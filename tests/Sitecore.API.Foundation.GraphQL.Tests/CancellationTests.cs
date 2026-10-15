using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;
using Sitecore.API.Foundation.Authorization.Abstractions;
using Sitecore.API.Foundation.Authorization.Models;
using Sitecore.API.Foundation.GraphQL.Http;

namespace Sitecore.API.Foundation.GraphQL.Tests;

public class CancellationTests
{
    private sealed class CancellableSpyHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        private readonly TaskCompletionSource _startedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Started => _startedTcs.Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            _startedTcs.TrySetResult();
            // Simulate long running request that respects cancellation
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class AlwaysUnauthorizedHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }
    }

    [Fact]
    public async Task CancellationToken_cancels_inflight_request()
    {
        // Arrange
        var tokenCache = new Mock<ISitecoreTokenCache>(MockBehavior.Strict);
        tokenCache.Setup(c => c.GetOrRefreshAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync("t");
        tokenCache.SetupGet(c => c.CurrentToken).Returns("t");

        var options = Options.Create(new SitecoreGraphQLOptions { ClientId = "id", ClientSecret = "secret", EnableUnauthorizedRefresh = true, MaxUnauthorizedRetries = 3 });
        var logger = Mock.Of<ILogger<SitecoreTokenHandler>>();

        var inner = new CancellableSpyHandler();
        var handler = new SitecoreTokenHandler(tokenCache.Object, options, logger)
        {
            InnerHandler = inner
        };

        var client = new HttpClient(handler);
        using var cts = new CancellationTokenSource();

        // Start request and wait until inner handler actually begins processing, then cancel
        var sendTask = client.GetAsync("http://unit.test", cts.Token);
        await inner.Started.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();

        // Act/Assert
        await Should.ThrowAsync<TaskCanceledException>(async () => await sendTask);
        inner.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task CancellationToken_cancels_retry_backoff_and_stops_further_retries()
    {
        // Arrange
        var tokenCache = new Mock<ISitecoreTokenCache>(MockBehavior.Strict);
        tokenCache.Setup(c => c.GetOrRefreshAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync("t1");

        tokenCache.SetupSequence(c => c.CurrentToken)
                  .Returns("t1") // Attempt 1 attach
                  .Returns("t2") // Attempt 2 attach
                  .Returns("t3"); // Attempt 3 attach (won't be called due to cancel)

        tokenCache.Setup(c => c.ForceRefreshAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync("t2");

        var options = Options.Create(new SitecoreGraphQLOptions { ClientId = "id", ClientSecret = "secret", EnableUnauthorizedRefresh = true, MaxUnauthorizedRetries = 5 });
        var logger = Mock.Of<ILogger<SitecoreTokenHandler>>();

        var inner = new AlwaysUnauthorizedHandler();
        var handler = new SitecoreTokenHandler(tokenCache.Object, options, logger)
        {
            InnerHandler = inner
        };

        var client = new HttpClient(handler);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(50); // less than initial backoff (200ms)

        // Act/Assert
        await Should.ThrowAsync<TaskCanceledException>(async () => await client.GetAsync("http://unit.test", cts.Token));

        // Expect only two sends: initial + immediate retry before backoff; cancellation prevents further retries
        inner.Calls.ShouldBe(2);
    }
}
