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
        var tokenService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        tokenService.Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>()))
                    .ReturnsAsync(default(SitecoreAuthToken));

        var accessor = new Mock<ITokenValueAccessor>(MockBehavior.Strict);
        accessor.Setup(a => a.GetAccessToken(It.IsAny<SitecoreAuthToken>())).Returns("abc");

        var options = Options.Create(new SitecoreGraphQLOptions { ClientId = "id", ClientSecret = "secret" });
        var logger = Mock.Of<ILogger<SitecoreTokenHandler>>();

        var inner = new SpyHandler((_, __) => new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new SitecoreTokenHandler(tokenService.Object, options, accessor.Object, logger)
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
        tokenService.Verify(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>()), Times.Once);
    }

    [Fact]
    public async Task On_401_refreshes_and_retries_once()
    {
        // Arrange
        var tokenService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        tokenService.Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>()))
                    .ReturnsAsync(default(SitecoreAuthToken));

        var accessor = new Mock<ITokenValueAccessor>(MockBehavior.Strict);
        accessor.SetupSequence(a => a.GetAccessToken(It.IsAny<SitecoreAuthToken>()))
                .Returns("t1")
                .Returns("t2");

        var options = Options.Create(new SitecoreGraphQLOptions { ClientId = "id", ClientSecret = "secret" });
        var logger = Mock.Of<ILogger<SitecoreTokenHandler>>();

        var inner = new SpyHandler((_, call) =>
        {
            return call == 1
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
        inner.Calls.ShouldBe(2);
        tokenService.Verify(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Missing_options_does_not_set_header_and_does_not_call_token_service()
    {
        // Arrange
        var tokenService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        var accessor = new Mock<ITokenValueAccessor>(MockBehavior.Strict);
        var options = Options.Create(new SitecoreGraphQLOptions());
        var logger = Mock.Of<ILogger<SitecoreTokenHandler>>();

        var inner = new SpyHandler((_, __) => new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new SitecoreTokenHandler(tokenService.Object, options, accessor.Object, logger)
        {
            InnerHandler = inner
        };

        var client = new HttpClient(handler);

        // Act
        var resp = await client.GetAsync("http://unit.test");

        // Assert
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        inner.LastAuth.ShouldBeNull();
        tokenService.VerifyNoOtherCalls();
        accessor.VerifyNoOtherCalls();
    }
}
