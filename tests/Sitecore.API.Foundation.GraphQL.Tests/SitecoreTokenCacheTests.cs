using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;
using Sitecore.API.Foundation.Authorization.Abstractions;
using Sitecore.API.Foundation.Authorization.Models;
using Sitecore.API.Foundation.GraphQL.Internal;
using Xunit;

namespace Sitecore.API.Foundation.GraphQL.Tests;

public class SitecoreTokenCacheTests
{
    private readonly Mock<ISitecoreTokenService> _tokenServiceMock;
    private readonly Mock<IOptions<SitecoreGraphQLOptions>> _optionsMock;
    private readonly Mock<ITokenValueAccessor> _accessorMock;
    private readonly SitecoreGraphQLOptions _options;

    public SitecoreTokenCacheTests()
    {
        _tokenServiceMock = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        _optionsMock = new Mock<IOptions<SitecoreGraphQLOptions>>();
        _accessorMock = new Mock<ITokenValueAccessor>(MockBehavior.Strict);

        _options = new SitecoreGraphQLOptions
        {
            ClientId = "test-client-id",
            ClientSecret = "test-client-secret"
        };
        _optionsMock.Setup(o => o.Value).Returns(_options);
    }

    private SitecoreTokenCache CreateCache()
    {
        return new SitecoreTokenCache(
            _tokenServiceMock.Object,
            _optionsMock.Object,
            _accessorMock.Object,
            NullLogger<SitecoreTokenCache>.Instance);
    }

    [Fact]
    public async Task GetOrRefreshAsync_with_valid_credentials_retrieves_and_caches_token()
    {
        var cache = CreateCache();
        var fakeTokenResponse = new SitecoreAuthToken();
        _tokenServiceMock.Setup(s => s.GetSitecoreAuthToken(It.Is<SitecoreAuthClientCredentials>(c => c.ClientId == "test-client-id" && c.ClientSecret == "test-client-secret")))
            .ReturnsAsync(fakeTokenResponse);
        _accessorMock.Setup(a => a.GetAccessToken(fakeTokenResponse))
            .Returns("new-token");

        var token = await cache.GetOrRefreshAsync(CancellationToken.None);

        token.ShouldBe("new-token");
        cache.CurrentToken.ShouldBe("new-token");
        _tokenServiceMock.Verify(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>()), Times.Once);
    }

    [Fact]
    public async Task GetOrRefreshAsync_WhenTokenIsAlreadyCached_DoesNotCallTokenService()
    {
        var cache = CreateCache();
        var fakeTokenResponse = new SitecoreAuthToken();
        _tokenServiceMock.Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>()))
            .ReturnsAsync(fakeTokenResponse);
        _accessorMock.Setup(a => a.GetAccessToken(fakeTokenResponse))
            .Returns("cached-token");

        await cache.GetOrRefreshAsync(CancellationToken.None);
        var secondToken = await cache.GetOrRefreshAsync(CancellationToken.None);

        secondToken.ShouldBe("cached-token");
        cache.CurrentToken.ShouldBe("cached-token");
        _tokenServiceMock.Verify(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>()), Times.Once);
    }

    [Fact]
    public async Task GetOrRefreshAsync_WithoutValidCredentials_ReturnsNull()
    {
        _options.ClientId = null;
        var cache = CreateCache();

        var token = await cache.GetOrRefreshAsync(CancellationToken.None);

        token.ShouldBeNull();
        cache.CurrentToken.ShouldBeNull();
        _tokenServiceMock.Verify(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>()), Times.Never);
    }

    [Fact]
    public async Task ForceRefreshAsync_BypassesCacheAndForcesRefresh()
    {
        var cache = CreateCache();
        var fakeTokenResponse1 = new SitecoreAuthToken();
        var fakeTokenResponse2 = new SitecoreAuthToken();

        _tokenServiceMock.SetupSequence(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>()))
            .ReturnsAsync(fakeTokenResponse1)
            .ReturnsAsync(fakeTokenResponse2);

        _accessorMock.SetupSequence(a => a.GetAccessToken(It.IsAny<SitecoreAuthToken>()))
            .Returns("token-1")
            .Returns("token-2");

        // Act: initial load
        await cache.GetOrRefreshAsync(CancellationToken.None);
        cache.CurrentToken.ShouldBe("token-1");

        // Act: ForceRefresh
        var newToken = await cache.ForceRefreshAsync(CancellationToken.None);

        // Assert
        newToken.ShouldBe("token-2");
        cache.CurrentToken.ShouldBe("token-2");
        _tokenServiceMock.Verify(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>()), Times.Exactly(2));
    }

    [Fact]
    public async Task GetOrRefreshAsync_Concurrency_CallsTokenServiceOnce()
    {
        var cache = CreateCache();
        var fakeTokenResponse = new SitecoreAuthToken();
        var tcs = new TaskCompletionSource<SitecoreAuthToken>();

        _tokenServiceMock.Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>()))
            .Returns(tcs.Task);

        _accessorMock.Setup(a => a.GetAccessToken(fakeTokenResponse))
            .Returns("concurrent-token");

        var task1 = cache.GetOrRefreshAsync(CancellationToken.None);
        var task2 = cache.GetOrRefreshAsync(CancellationToken.None);
        var task3 = cache.GetOrRefreshAsync(CancellationToken.None);

        tcs.SetResult(fakeTokenResponse);

        var results = await Task.WhenAll(task1, task2, task3);

        results[0].ShouldBe("concurrent-token");
        results[1].ShouldBe("concurrent-token");
        results[2].ShouldBe("concurrent-token");

        _tokenServiceMock.Verify(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>()), Times.Once);
    }
}
