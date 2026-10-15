using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;
using Sitecore.API.Foundation.Authorization.Abstractions;
using Sitecore.API.Foundation.Authorization.Models;
using Sitecore.API.Foundation.GraphQL.Internal;

namespace Sitecore.API.Foundation.GraphQL.Tests;

public class SitecoreTokenCacheTests
{
    [Fact]
    public async Task ForceRefreshAsync_returns_null_and_logs_warning_when_credentials_missing()
    {
        // Arrange
        var tokenService = new Mock<ISitecoreTokenService>();
        var options = new Mock<IOptions<SitecoreGraphQLOptions>>();
        options.Setup(o => o.Value).Returns(new SitecoreGraphQLOptions
        {
            ClientId = null,
            ClientSecret = null
        });
        var accessor = new Mock<ITokenValueAccessor>();
        var logger = new Mock<ILogger<SitecoreTokenCache>>();

        var cache = new SitecoreTokenCache(tokenService.Object, options.Object, accessor.Object, logger.Object);

        // Act
        var result = await cache.ForceRefreshAsync(CancellationToken.None);

        // Assert
        result.ShouldBeNull();

        tokenService.Verify(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>()), Times.Never);
        logger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("Cannot refresh Sitecore token due to missing ClientId/ClientSecret in options.")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
