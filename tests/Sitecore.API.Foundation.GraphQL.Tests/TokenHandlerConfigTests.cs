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
        var tokenService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        tokenService.Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>()))
                    .ReturnsAsync(default(SitecoreAuthToken));

        var accessor = new Mock<ITokenValueAccessor>(MockBehavior.Strict);
        accessor.Setup(a => a.GetAccessToken(It.IsAny<SitecoreAuthToken>())).Returns("t");

        var options = Options.Create(new SitecoreGraphQLOptions { ClientId = "id", ClientSecret = "secret", EnableUnauthorizedRefresh = false, MaxUnauthorizedRetries = 3 });
        var logger = Mock.Of<ILogger<SitecoreTokenHandler>>();

        var inner = new SpyHandler((_, __) => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var handler = new SitecoreTokenHandler(tokenService.Object, options, accessor.Object, logger)
        {
            InnerHandler = inner
        };

        var client = new HttpClient(handler);

        // Act
        var resp = await client.GetAsync("http://unit.test");

        // Assert
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        inner.Calls.ShouldBe(1);
        tokenService.Verify(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>()), Times.Once);
    }

    [Fact]
    public async Task Retries_respect_MaxUnauthorizedRetries()
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

        var options = Options.Create(new SitecoreGraphQLOptions { ClientId = "id", ClientSecret = "secret", EnableUnauthorizedRefresh = true, MaxUnauthorizedRetries = 2 });
        var logger = Mock.Of<ILogger<SitecoreTokenHandler>>();

        var inner = new SpyHandler((_, call) =>
        {
            return call <= 2
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(HttpStatusCode.OK);
        });

        var handler = new SitecoreTokenHandler(tokenService.Object, options, accessor.Object, logger)
        {
            InnerHandler = inner
        };

        var client = new HttpClient(handler);

        // Act
        var resp = await client.GetAsync("http://unit.test");

        // Assert
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        inner.Calls.ShouldBe(3); // initial + 2 retries
        tokenService.Verify(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>()), Times.Exactly(3));
    }
}
