using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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

        // Assert on registrations (avoid environment-provided defaults in provider)
        var hasLoggerFactory = services.Any(d => d.ServiceType == typeof(ILoggerFactory) && d.ImplementationType == typeof(LoggerFactory));
        var hasGenericLogger = services.Any(d => d.ServiceType.IsGenericType && d.ServiceType.GetGenericTypeDefinition() == typeof(ILogger<>) && d.ImplementationType == typeof(Logger<>));
        hasLoggerFactory.ShouldBeTrue();
        hasGenericLogger.ShouldBeTrue();
    }

    [Fact]
    public void TryAddInternalLogging_adds_expected_registrations()
    {
        var services = new ServiceCollection();
        ServiceCollectionExtensions.TryAddInternalLogging(services);
        var hasLoggerFactory = services.Any(d => d.ServiceType == typeof(ILoggerFactory) && d.ImplementationType == typeof(LoggerFactory));
        var hasGenericLogger = services.Any(d => d.ServiceType.IsGenericType && d.ServiceType.GetGenericTypeDefinition() == typeof(ILogger<>) && d.ImplementationType == typeof(Logger<>));
        hasLoggerFactory.ShouldBeTrue();
        hasGenericLogger.ShouldBeTrue();
    }

    [Fact]
    public void AddSitecoreGraphQL_binds_EnableInternalLoggingSetup_false()
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
        var options = provider.GetRequiredService<IOptions<SitecoreGraphQLOptions>>().Value;

        // Assert
        options.EnableInternalLoggingSetup.ShouldBeFalse();
    }

    [Fact]
    public void TryAddInternalLogging_registers_LoggerFilterOptions_and_preserves_host_filter_rules()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sitecore:GraphQL:Endpoint"] = "https://configured/graphql",
                ["Sitecore:GraphQL:ClientId"] = "cfg-id",
                ["Sitecore:GraphQL:ClientSecret"] = "cfg-secret"
            })
            .Build();

        services.AddSitecoreGraphQL(cfg);

        using var provider = services.BuildServiceProvider();
        var filterOptions = provider.GetRequiredService<IOptions<LoggerFilterOptions>>().Value;
        filterOptions.MinLevel.ShouldBe(LogLevel.Warning);
    }
}
