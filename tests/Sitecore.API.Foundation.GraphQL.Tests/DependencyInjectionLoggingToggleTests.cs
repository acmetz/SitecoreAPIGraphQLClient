using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Sitecore.API.Foundation.GraphQL.DependencyInjection;

namespace Sitecore.API.Foundation.GraphQL.Tests;

public class DependencyInjectionLoggingToggleTests
{
    [Fact]
    public void AddSitecoreGraphQL_registers_loggerfactory_when_toggle_enabled_by_default()
    {
        // Arrange: minimal valid configuration
        var inMemory = new Dictionary<string, string?>
        {
            ["Sitecore:GraphQL:Endpoint"] = "https://configured/graphql",
            ["Sitecore:GraphQL:ClientId"] = "cfg-id",
            ["Sitecore:GraphQL:ClientSecret"] = "cfg-secret"
            // EnableInternalLoggingSetup defaults to true
        };
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(inMemory!).Build();
        var services = new ServiceCollection();

        // Act
        services.AddSitecoreGraphQL(cfg);
        var provider = services.BuildServiceProvider();

        // Assert
        var loggerFactory = provider.GetService<ILoggerFactory>();
        loggerFactory.ShouldNotBeNull();
    }

    [Fact]
    public void AddSitecoreGraphQL_does_not_register_loggerfactory_when_toggle_disabled()
    {
        // Arrange
        var inMemory = new Dictionary<string, string?>
        {
            ["Sitecore:GraphQL:Endpoint"] = "https://configured/graphql",
            ["Sitecore:GraphQL:ClientId"] = "cfg-id",
            ["Sitecore:GraphQL:ClientSecret"] = "cfg-secret",
            ["Sitecore:GraphQL:EnableInternalLoggingSetup"] = "false"
        };
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(inMemory!).Build();
        var services = new ServiceCollection();

        // Act
        services.AddSitecoreGraphQL(cfg);
        var provider = services.BuildServiceProvider();

        // Assert
        var loggerFactory = provider.GetService<ILoggerFactory>();
        loggerFactory.ShouldBeNull();
    }
}
