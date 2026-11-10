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
        tokenService.Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>(), It.IsAny<CancellationToken>()))
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
        var resp = await client.GetAsync("https://unit.test");

        // Assert
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        inner.Calls.ShouldBe(1);
        tokenService.Verify(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Retries_respect_MaxUnauthorizedRetries()
    {
        // Arrange
        var tokenService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        tokenService.Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>(), It.IsAny<CancellationToken>()))
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
        var resp = await client.GetAsync("https://unit.test");

        // Assert
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        inner.Calls.ShouldBe(3); // initial + 2 retries
        tokenService.Verify(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task Subsequent_401_retries_use_TimeProvider_for_exponential_backoff_after_immediate_first_retry()
    {
        var fakeTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var optionsMonitor = new StaticOptionsMonitor(new SitecoreGraphQLOptions
        {
            ClientId = "id",
            ClientSecret = "secret",
            EnableUnauthorizedRefresh = true,
            MaxUnauthorizedRetries = 2
        });

        var tokenService = new Mock<ISitecoreTokenService>();
        tokenService
            .Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SitecoreAuthToken("tok-1", DateTimeOffset.UtcNow.AddHours(1)));
        tokenService
            .Setup(s => s.TryRefreshSitecoreAuthToken(It.IsAny<SitecoreAuthToken>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SitecoreAuthToken("tok-2", DateTimeOffset.UtcNow.AddHours(1)));

        var coordinator = new GraphQL.Internal.SitecoreTokenCoordinator(tokenService.Object, new DefaultTokenValueAccessor());

        var secondAttemptObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new SpyHandler((_, call) =>
        {
            if (call == 2)
            {
                secondAttemptObserved.TrySetResult();
            }

            return call <= 2
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var handler = new SitecoreTokenHandler(coordinator, optionsMonitor, fakeTime)
        {
            InnerHandler = inner
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        // Start request: initial call (1) + immediate first retry (2) happen with 0ms delay, then pauses on FakeTimeProvider backoff (160ms..240ms)
        var responseTask = client.GetAsync("https://unit.test");
        await secondAttemptObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(20);

        inner.Calls.ShouldBe(2);
        responseTask.IsCompleted.ShouldBeFalse();

        // Advance FakeTimeProvider past max jittered attempt-0 backoff (240ms) -> triggers retry 2 immediately without wall-clock sleep!
        fakeTime.Advance(TimeSpan.FromMilliseconds(300));

        using var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(2));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        inner.Calls.ShouldBe(3);
    }

    private sealed class StaticOptionsMonitor : IOptionsMonitor<SitecoreGraphQLOptions>
    {
        public StaticOptionsMonitor(SitecoreGraphQLOptions currentValue) => CurrentValue = currentValue;
        public SitecoreGraphQLOptions CurrentValue { get; }
        public SitecoreGraphQLOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<SitecoreGraphQLOptions, string?> listener) => null;
    }
}
