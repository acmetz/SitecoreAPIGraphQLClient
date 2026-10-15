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

public class TokenHandlerTests
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
    public async Task Adds_bearer_header_from_access_token()
    {
        // Arrange
        var tokenCache = new Mock<ISitecoreTokenCache>(MockBehavior.Strict);
        tokenCache.Setup(c => c.GetOrRefreshAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync("abc");
        tokenCache.SetupGet(c => c.CurrentToken)
                  .Returns("abc");

        var options = Options.Create(new SitecoreGraphQLOptions { ClientId = "id", ClientSecret = "secret" });
        var logger = Mock.Of<ILogger<SitecoreTokenHandler>>();

        var inner = new SpyHandler((_, __) => new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new SitecoreTokenHandler(tokenCache.Object, options, logger)
        {
            InnerHandler = inner
        };

        var client = new HttpClient(handler);

        // Act
        var resp = await client.GetAsync("http://unit.test");

        // Assert
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        inner.LastAuth.ShouldNotBeNull();
        inner.LastAuth!.Scheme.ShouldBe("Bearer");
        inner.LastAuth!.Parameter.ShouldBe("abc");
        tokenCache.Verify(c => c.GetOrRefreshAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task On_401_refreshes_and_retries_once()
    {
        // Arrange
        var tokenCache = new Mock<ISitecoreTokenCache>(MockBehavior.Strict);
        tokenCache.Setup(c => c.GetOrRefreshAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync("t1");

        tokenCache.SetupSequence(c => c.CurrentToken)
                  .Returns("t1") // Attempt 1 attach
                  .Returns("t2"); // Attempt 2 attach

        tokenCache.Setup(c => c.ForceRefreshAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync("t2");

        var options = Options.Create(new SitecoreGraphQLOptions { ClientId = "id", ClientSecret = "secret" });
        var logger = Mock.Of<ILogger<SitecoreTokenHandler>>();

        var inner = new SpyHandler((_, call) =>
        {
            return call == 1
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
        inner.Calls.ShouldBe(2);
        tokenCache.Verify(c => c.ForceRefreshAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Missing_token_does_not_set_header()
    {
        // Arrange
        var tokenCache = new Mock<ISitecoreTokenCache>(MockBehavior.Strict);
        tokenCache.Setup(c => c.GetOrRefreshAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync((string?)null);
        tokenCache.SetupGet(c => c.CurrentToken)
                  .Returns((string?)null);

        var options = Options.Create(new SitecoreGraphQLOptions());
        var logger = Mock.Of<ILogger<SitecoreTokenHandler>>();

        var inner = new SpyHandler((_, __) => new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new SitecoreTokenHandler(tokenCache.Object, options, logger)
        {
            InnerHandler = inner
        };

        var client = new HttpClient(handler);

        // Act
        var resp = await client.GetAsync("http://unit.test");

        // Assert
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        inner.LastAuth.ShouldBeNull();
        tokenCache.Verify(c => c.GetOrRefreshAsync(It.IsAny<CancellationToken>()), Times.Once);
        tokenCache.VerifyGet(c => c.CurrentToken, Times.Once);
        tokenCache.VerifyNoOtherCalls();
    }
}
