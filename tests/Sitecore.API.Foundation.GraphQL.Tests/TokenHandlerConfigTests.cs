using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;
using Sitecore.API.Foundation.Authorization.Abstractions;
using Sitecore.API.Foundation.Authorization.Models;
using Sitecore.API.Foundation.GraphQL.Http;

namespace Sitecore.API.Foundation.GraphQL.Tests;

public class TokenHandlerConfigTests
{
    private sealed class SpyHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public AuthenticationHeaderValue? LastAuth { get; private set; }
        private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _onSend;

        public SpyHandler(Func<HttpRequestMessage, int, HttpResponseMessage> onSend)
        {
            _onSend = onSend;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastAuth = request.Headers.Authorization;
            var resp = _onSend(request, Calls);
            return Task.FromResult(resp);
        }
    }

    [Fact]
    public async Task When_refresh_disabled_401_is_not_retried()
    {
        // Arrange
        var tokenCache = new Mock<ISitecoreTokenCache>(MockBehavior.Strict);
        tokenCache.Setup(c => c.GetOrRefreshAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync("t");
        tokenCache.SetupGet(c => c.CurrentToken).Returns("t");

        var options = Options.Create(new SitecoreGraphQLOptions { ClientId = "id", ClientSecret = "secret", EnableUnauthorizedRefresh = false, MaxUnauthorizedRetries = 3 });
        var logger = Mock.Of<ILogger<SitecoreTokenHandler>>();

        var inner = new SpyHandler((_, __) => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var handler = new SitecoreTokenHandler(tokenCache.Object, options, logger)
        {
            InnerHandler = inner
        };

        var client = new HttpClient(handler);

        // Act
        var resp = await client.GetAsync("http://unit.test");

        // Assert
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        inner.Calls.ShouldBe(1);
        tokenCache.Verify(c => c.GetOrRefreshAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Retries_respect_MaxUnauthorizedRetries()
    {
        // Arrange
        var tokenCache = new Mock<ISitecoreTokenCache>(MockBehavior.Strict);
        tokenCache.Setup(c => c.GetOrRefreshAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync("t1");

        tokenCache.SetupSequence(c => c.CurrentToken)
                  .Returns("t1") // Attempt 1 attach
                  .Returns("t2") // Attempt 2 attach
                  .Returns("t3"); // Attempt 3 attach

        tokenCache.Setup(c => c.ForceRefreshAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync("t2"); // Simplified return for ForceRefreshAsync

        var options = Options.Create(new SitecoreGraphQLOptions { ClientId = "id", ClientSecret = "secret", EnableUnauthorizedRefresh = true, MaxUnauthorizedRetries = 2 });
        var logger = Mock.Of<ILogger<SitecoreTokenHandler>>();

        var inner = new SpyHandler((_, call) =>
        {
            return call <= 2
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(HttpStatusCode.OK);
        });

        var handler = new SitecoreTokenHandler(tokenCache.Object, options, logger)
        {
            InnerHandler = inner
        };

        var client = new HttpClient(handler);

        // Act
        var resp = await client.GetAsync("http://unit.test");

        // Assert
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        inner.Calls.ShouldBe(3); // initial + 2 retries
        tokenCache.Verify(c => c.ForceRefreshAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}
