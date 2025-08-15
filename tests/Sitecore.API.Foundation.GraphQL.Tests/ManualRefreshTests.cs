using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shouldly;
using Sitecore.API.Foundation.GraphQL.DependencyInjection;
using Sitecore.API.Foundation.Authorization.Models;

namespace Sitecore.API.Foundation.GraphQL.Tests;

public class ManualRefreshTests
{
    [Fact]
    public async Task Factory_manual_refresh_uses_cache_and_returns_true_on_success()
    {
        // Arrange
        var inMemory = new Dictionary<string, string?>
        {
            ["Sitecore:GraphQL:Endpoint"] = "https://unit/graphql",
            ["Sitecore:GraphQL:ClientId"] = "id",
            ["Sitecore:GraphQL:ClientSecret"] = "secret"
        };
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(inMemory!).Build();

        var services = new ServiceCollection();
        services.AddSitecoreGraphQL(cfg);

        var tokenService = new Mock<Sitecore.API.Foundation.Authorization.Abstractions.ISitecoreTokenService>();
        tokenService.Setup(s => s.GetSitecoreAuthToken(Moq.It.IsAny<Sitecore.API.Foundation.Authorization.Models.SitecoreAuthClientCredentials>()))
                    .ReturnsAsync(default(SitecoreAuthToken));
        services.AddSingleton(tokenService.Object);

        var accessor = new Mock<ITokenValueAccessor>();
        accessor.Setup(a => a.GetAccessToken(Moq.It.IsAny<Sitecore.API.Foundation.Authorization.Models.SitecoreAuthToken>()))
                .Returns("abc");
        services.AddSingleton(accessor.Object);

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ISitecoreGraphQLFactory>();

        // Act
        var ok = await factory.RefreshTokenAsync();

        // Assert
        ok.ShouldBeTrue();
    }
}
