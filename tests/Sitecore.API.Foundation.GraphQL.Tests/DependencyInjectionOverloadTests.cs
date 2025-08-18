using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;
using Sitecore.API.Foundation.Authorization.Abstractions;
using Sitecore.API.Foundation.Authorization.Models;
using Sitecore.API.Foundation.GraphQL.DependencyInjection;

namespace Sitecore.API.Foundation.GraphQL.Tests;

public class DependencyInjectionOverloadTests
{
    [Fact]
    public async Task AddSitecoreGraphQL_delegate_registers_and_factory_creates_client()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSitecoreGraphQL(o =>
        {
            o.Endpoint = "https://unit/graphql";
            o.ClientId = "id";
            o.ClientSecret = "secret";
        });

        var tokenService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        tokenService.Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>()))
                    .ReturnsAsync(default(SitecoreAuthToken));
        services.AddSingleton(tokenService.Object);

        var accessor = new Mock<ITokenValueAccessor>(MockBehavior.Strict);
        accessor.Setup(a => a.GetAccessToken(It.IsAny<SitecoreAuthToken>())).Returns("abc");
        services.AddSingleton(accessor.Object);

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ISitecoreGraphQLFactory>();

        // Act
        var client = await factory.CreateClientAsync();

        // Assert
        client.ShouldNotBeNull();
    }

    [Fact]
    public async Task AddSitecoreGraphQL_delegate_missing_defaults_throws_on_use()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSitecoreGraphQL(o => { /* no defaults */ });

        var tokenService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        services.AddSingleton(tokenService.Object);
        var accessor = new Mock<ITokenValueAccessor>(MockBehavior.Strict);
        services.AddSingleton(accessor.Object);

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ISitecoreGraphQLFactory>();

        // Act/Assert - Options validation will throw when used
        await Should.ThrowAsync<OptionsValidationException>(async () => await factory.CreateClientAsync());
    }

    [Fact]
    public void AddSitecoreGraphQL_delegate_respects_logging_toggle()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddSitecoreGraphQL(o =>
        {
            o.EnableInternalLoggingSetup = false;
            o.Endpoint = "https://unit/graphql";
            o.ClientId = "id";
            o.ClientSecret = "secret";
        });
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<SitecoreGraphQLOptions>>().Value;

        // Assert: the toggle is bound to false
        options.EnableInternalLoggingSetup.ShouldBeFalse();
    }
}
