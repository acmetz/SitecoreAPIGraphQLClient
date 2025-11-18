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

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
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
        var tokenService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        tokenService.Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>()))
                    .ReturnsAsync(default(SitecoreAuthToken));

        var accessor = new Mock<ITokenValueAccessor>(MockBehavior.Strict);
        accessor.Setup(a => a.GetAccessToken(It.IsAny<SitecoreAuthToken>())).Returns("t");

        var options = Options.Create(new SitecoreGraphQLOptions { ClientId = "id", ClientSecret = "secret", EnableUnauthorizedRefresh = true, MaxUnauthorizedRetries = 3 });
        var logger = Mock.Of<ILogger<SitecoreTokenHandler>>();

        var inner = new CancellableSpyHandler();
        var handler = new SitecoreTokenHandler(tokenService.Object, options, accessor.Object, logger)
        {
            InnerHandler = inner
        };

        var client = new HttpClient(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        // Act/Assert
        await Should.ThrowAsync<TaskCanceledException>(async () => await client.GetAsync("http://unit.test", cts.Token));
        inner.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task CancellationToken_cancels_retry_backoff_and_stops_further_retries()
    {
        // Arrange
        var tokenService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        tokenService.Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>()))
                    .ReturnsAsync(default(SitecoreAuthToken));

        var accessor = new Mock<ITokenValueAccessor>(MockBehavior.Strict);
        accessor.SetupSequence(a => a.GetAccessToken(It.IsAny<SitecoreAuthToken>()))
                .Returns("t1")
                .Returns("t2")
                .Returns("t3");

        var options = Options.Create(new SitecoreGraphQLOptions { ClientId = "id", ClientSecret = "secret", EnableUnauthorizedRefresh = true, MaxUnauthorizedRetries = 5 });
        var logger = Mock.Of<ILogger<SitecoreTokenHandler>>();

        var inner = new AlwaysUnauthorizedHandler();
        var handler = new SitecoreTokenHandler(tokenService.Object, options, accessor.Object, logger)
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
