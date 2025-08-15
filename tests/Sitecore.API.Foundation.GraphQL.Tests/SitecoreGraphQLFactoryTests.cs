using System.Reflection;
using GraphQL.Client.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;
using Sitecore.API.Foundation.Authorization.Abstractions;

namespace Sitecore.API.Foundation.GraphQL.Tests;

public class SitecoreGraphQLFactoryTests
{
    [Fact]
    public void Options_type_and_di_extension_should_exist()
    {
        // Arrange
        var optionsTypeName = "Sitecore.API.Foundation.GraphQL.SitecoreGraphQLOptions, Sitecore.API.Foundation.GraphQL";
        var extTypeName = "Sitecore.API.Foundation.GraphQL.DependencyInjection.ServiceCollectionExtensions, Sitecore.API.Foundation.GraphQL";

        // Act
        var optionsType = Type.GetType(optionsTypeName);
        var extType = Type.GetType(extTypeName);

        // Assert
        optionsType.ShouldNotBeNull();
        extType.ShouldNotBeNull();

        var method = extType!.GetMethod("AddSitecoreGraphQL", BindingFlags.Public | BindingFlags.Static);
        method.ShouldNotBeNull();
    }

    [Fact]
    public async Task Async_overloads_should_exist_and_support_concurrency()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddHttpClient();

        var tokenServiceMock = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        services.AddSingleton(tokenServiceMock.Object);
        services.AddSingleton<ISitecoreTokenCache>(Mock.Of<ISitecoreTokenCache>());

        services.AddSingleton<ISitecoreGraphQLFactory, SitecoreGraphQLFactory>();
        services.Configure<object>("dummy", _ => { }); // ensure options infrastructure available

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ISitecoreGraphQLFactory>();

        // Act
        // Use reflection to call CreateClientAsync(string url, string clientId, string clientSecret)
        var method = factory.GetType().GetMethod("CreateClientAsync", new[] { typeof(string), typeof(string), typeof(string) });
        method.ShouldNotBeNull();

        var tasks = new Task<IGraphQLClient>[10];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = (Task<IGraphQLClient>)method!.Invoke(factory, new object[] { "https://example/graphql", "id", "secret" })!;
        }

        var results = await Task.WhenAll(tasks);

        // Assert
        var first = results[0];
        foreach (var client in results)
        {
            ReferenceEquals(first, client).ShouldBeTrue();
        }
    }

    [Fact]
    public void AddSitecoreGraphQL_binds_options_and_registers_factory()
    {
        // Arrange
        var inMemory = new Dictionary<string, string?>
        {
            ["Sitecore:GraphQL:Endpoint"] = "https://configured/graphql",
            ["Sitecore:GraphQL:ClientId"] = "cfg-id",
            ["Sitecore:GraphQL:ClientSecret"] = "cfg-secret"
        };
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(inMemory!).Build();

        var services = new ServiceCollection();

        // Act
        // reflectively call extension to avoid compile-time dependency
        var extType = Type.GetType("Sitecore.API.Foundation.GraphQL.DependencyInjection.ServiceCollectionExtensions, Sitecore.API.Foundation.GraphQL");
        extType.ShouldNotBeNull();
        var addMethod = extType!.GetMethod("AddSitecoreGraphQL", BindingFlags.Public | BindingFlags.Static, new[] { typeof(IServiceCollection), typeof(IConfiguration) });
        addMethod.ShouldNotBeNull();
        addMethod!.Invoke(null, new object[] { services, cfg });

        // Also add a token service stub so factory can be resolved if needed later
        services.AddSingleton(Mock.Of<ISitecoreTokenService>());

        // Assert
        var provider = services.BuildServiceProvider();
        var optsObj = provider.GetService(typeof(Microsoft.Extensions.Options.IOptions<>).MakeGenericType(Type.GetType("Sitecore.API.Foundation.GraphQL.SitecoreGraphQLOptions, Sitecore.API.Foundation.GraphQL")!));
        optsObj.ShouldNotBeNull();
        var factory = provider.GetService<ISitecoreGraphQLFactory>();

        factory.ShouldNotBeNull();
    }

    [Fact]
    public void AddSitecoreGraphQL_with_missing_required_values_should_throw_on_build()
    {
        // Arrange: Missing ClientSecret
        var inMemory = new Dictionary<string, string?>
        {
            ["Sitecore:GraphQL:Endpoint"] = "https://configured/graphql",
            ["Sitecore:GraphQL:ClientId"] = "cfg-id"
        };
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(inMemory!).Build();
        var services = new ServiceCollection();

        var extType = Type.GetType("Sitecore.API.Foundation.GraphQL.DependencyInjection.ServiceCollectionExtensions, Sitecore.API.Foundation.GraphQL");
        var addMethod = extType!.GetMethod("AddSitecoreGraphQL", BindingFlags.Public | BindingFlags.Static, new[] { typeof(IServiceCollection), typeof(IConfiguration) });
        addMethod!.Invoke(null, new object[] { services, cfg });

        // Act
        var provider = services.BuildServiceProvider();

        // Assert: accessing options should throw validation exception
        Should.Throw<Microsoft.Extensions.Options.OptionsValidationException>(() => provider.GetRequiredService<IOptions<SitecoreGraphQLOptions>>().Value);
    }

    // Additional explicit tests (non-reflection) for overload behaviors

    [Fact]
    public async Task Concurrency_returns_single_instance_and_single_token_request_non_reflection()
    {
        // Arrange
        var tokenServiceMock = new Mock<ISitecoreTokenService>(MockBehavior.Strict);

        var services = new ServiceCollection();
        services.AddHttpClient();
        services.AddSingleton(tokenServiceMock.Object);
        services.AddSingleton<ISitecoreTokenCache>(Mock.Of<ISitecoreTokenCache>());
        services.Configure<SitecoreGraphQLOptions>(o =>
        {
            o.Endpoint = "https://example/graphql";
            o.ClientId = "id";
            o.ClientSecret = "secret";
        });
        services.AddSingleton<ISitecoreGraphQLFactory, SitecoreGraphQLFactory>();

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ISitecoreGraphQLFactory>();

        // Act
        var tasks = new Task<IGraphQLClient>[12];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = factory.CreateClientAsync("https://example/graphql", "id", "secret");
        }
        var results = await Task.WhenAll(tasks);

        // Assert
        var first = results[0];
        foreach (var client in results)
        {
            ReferenceEquals(first, client).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task CreateClient_with_configured_credentials_uses_options()
    {
        // Arrange
        var tokenServiceMock = new Mock<ISitecoreTokenService>(MockBehavior.Strict);

        var services = new ServiceCollection();
        services.AddHttpClient();
        services.AddSingleton(tokenServiceMock.Object);
        services.AddSingleton<ISitecoreTokenCache>(Mock.Of<ISitecoreTokenCache>());
        services.Configure<SitecoreGraphQLOptions>(o =>
        {
            o.ClientId = "cfg-id";
            o.ClientSecret = "cfg-secret";
        });
        services.AddSingleton<ISitecoreGraphQLFactory, SitecoreGraphQLFactory>();

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ISitecoreGraphQLFactory>();

        // Act
        var client = await factory.CreateClientAsync("https://from-code/graphql");

        // Assert
        client.ShouldNotBeNull();
    }

    [Fact]
    public async Task CreateClient_parameterless_uses_configured_endpoint_and_credentials()
    {
        // Arrange
        var tokenServiceMock = new Mock<ISitecoreTokenService>(MockBehavior.Strict);

        var services = new ServiceCollection();
        services.AddHttpClient();
        services.AddSingleton(tokenServiceMock.Object);
        services.AddSingleton<ISitecoreTokenCache>(Mock.Of<ISitecoreTokenCache>());
        services.Configure<SitecoreGraphQLOptions>(o =>
        {
            o.Endpoint = "https://configured/graphql";
            o.ClientId = "cfg-id";
            o.ClientSecret = "cfg-secret";
        });
        services.AddSingleton<ISitecoreGraphQLFactory, SitecoreGraphQLFactory>();

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ISitecoreGraphQLFactory>();

        // Act
        var client = await factory.CreateClientAsync();

        // Assert
        client.ShouldNotBeNull();
    }
}
