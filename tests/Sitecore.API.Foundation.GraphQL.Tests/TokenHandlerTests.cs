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
        tokenService.Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>(), It.IsAny<CancellationToken>()))
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
        var resp = await client.GetAsync("https://unit.test");

        // Assert
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        inner.LastAuth.ShouldNotBeNull();
        inner.LastAuth!.Scheme.ShouldBe("Bearer");
        inner.LastAuth!.Parameter.ShouldBe("abc");
        tokenService.Verify(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task On_401_refreshes_and_retries_once()
    {
        // Arrange
        var tokenService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        tokenService.Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>(), It.IsAny<CancellationToken>()))
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
        var resp = await client.GetAsync("https://unit.test");

        // Assert
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        inner.Calls.ShouldBe(2);
        tokenService.Verify(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
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
        var resp = await client.GetAsync("https://unit.test");

        // Assert
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        inner.LastAuth.ShouldBeNull();
        tokenService.VerifyNoOtherCalls();
        accessor.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task On_401_POST_clones_request_body_and_invokes_TryRefreshSitecoreAuthToken()
    {
        var initialToken = new SitecoreAuthToken("stale-token", DateTimeOffset.UtcNow.AddMinutes(30));
        var refreshedToken = new SitecoreAuthToken("fresh-token", DateTimeOffset.UtcNow.AddMinutes(60));

        var tokenService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        tokenService
            .Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(initialToken);
        tokenService
            .Setup(s => s.TryRefreshSitecoreAuthToken(initialToken, It.IsAny<CancellationToken>()))
            .ReturnsAsync(refreshedToken);

        var options = Options.Create(new SitecoreGraphQLOptions { ClientId = "id", ClientSecret = "secret" });
        var capturedBodies = new List<string>();
        var capturedContentTypes = new List<string?>();

        using var handler = new SitecoreTokenHandler(tokenService.Object, options, new DefaultTokenValueAccessor())
        {
            InnerHandler = new AsyncBodySpyHandler(async (req, call) =>
            {
                capturedContentTypes.Add(req.Content?.Headers.ContentType?.MediaType);
                capturedBodies.Add(await req.Content!.ReadAsStringAsync());
                return call == 1
                    ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    : new HttpResponseMessage(HttpStatusCode.OK);
            })
        };

        using var client = new HttpClient(handler);
        const string payload = "{\"query\":\"mutation CreateItem($name: String!) { createItem(name: $name) { id } }\",\"variables\":{\"name\":\"Test\"}}";

        using var postRequest = new HttpRequestMessage(HttpMethod.Post, "https://unit.test/graphql")
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
        };

        using var response = await client.SendAsync(postRequest);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        capturedBodies.Count.ShouldBe(2);
        capturedBodies[0].ShouldBe(payload);
        capturedBodies[1].ShouldBe(payload);
        capturedContentTypes.ShouldAllBe(ct => ct == "application/json");
        tokenService.Verify(s => s.TryRefreshSitecoreAuthToken(initialToken, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ForceRefreshAsync_refreshes_when_configured_and_noops_when_unconfigured()
    {
        var stale = new SitecoreAuthToken("stale", DateTimeOffset.UtcNow.AddMinutes(10));
        var fresh = new SitecoreAuthToken("fresh", DateTimeOffset.UtcNow.AddMinutes(60));

        var tokenService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        tokenService.Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>())).ReturnsAsync(stale);
        tokenService.Setup(s => s.TryRefreshSitecoreAuthToken(stale)).ReturnsAsync(fresh);

        using var configuredHandler = new SitecoreTokenHandler(
            tokenService.Object,
            Options.Create(new SitecoreGraphQLOptions { ClientId = "id", ClientSecret = "secret" }),
            new DefaultTokenValueAccessor());

        await configuredHandler.ForceRefreshAsync(CancellationToken.None);
        tokenService.Verify(s => s.TryRefreshSitecoreAuthToken(stale), Times.Once);

        var strictUnconfiguredService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        using var unconfiguredHandler = new SitecoreTokenHandler(
            strictUnconfiguredService.Object,
            Options.Create(new SitecoreGraphQLOptions()),
            new DefaultTokenValueAccessor());

        await unconfiguredHandler.ForceRefreshAsync(CancellationToken.None);
        strictUnconfiguredService.VerifyNoOtherCalls();
    }

    private sealed class AsyncBodySpyHandler : HttpMessageHandler
    {
        private readonly HashSet<HttpRequestMessage> _seenRequests = new(ReferenceEqualityComparer.Instance);
        private readonly Func<HttpRequestMessage, int, Task<HttpResponseMessage>> _onSend;
        private int _calls;

        public AsyncBodySpyHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> onSend)
            => _onSend = onSend;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_seenRequests)
            {
                if (!_seenRequests.Add(request))
                {
                    throw new InvalidOperationException("The request message was already sent.");
                }
            }

            return _onSend(request, Interlocked.Increment(ref _calls));
        }
    }
}
