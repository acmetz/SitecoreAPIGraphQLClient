using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shouldly;
using Sitecore.API.Foundation.Authorization.Abstractions;
using Sitecore.API.Foundation.Authorization.Models;
using Sitecore.API.Foundation.GraphQL.Http;

namespace Sitecore.API.Foundation.GraphQL.Tests;

public class NamedClientsTests
{
    [Fact]
    public async Task Factory_can_create_clients_from_named_options_and_cache_by_url_and_id()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddHttpClient();
        services.AddSingleton(Mock.Of<ISitecoreTokenService>());
        services.AddSingleton<ISitecoreTokenCache>(Mock.Of<ISitecoreTokenCache>());
        services.Configure<SitecoreGraphQLOptions>(o =>
        {
            o.ClientId = "default-id";
            o.ClientSecret = "default-secret";
            o.Clients["content"] = new SitecoreGraphQLClientOptions
            {
                Endpoint = "https://content/graphql",
                ClientId = "content-id",
                ClientSecret = "content-secret"
            };
            o.Clients["search"] = new SitecoreGraphQLClientOptions
            {
                Endpoint = "https://search/graphql",
                ClientId = "search-id",
                ClientSecret = "search-secret"
            };
        });
        services.AddSingleton<ISitecoreGraphQLFactory, SitecoreGraphQLFactory>();

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ISitecoreGraphQLFactory>();

        // Act
        var c1a = await factory.CreateClientByNameAsync("content");
        var c1b = await factory.CreateClientByNameAsync("content");
        var c2 = await factory.CreateClientByNameAsync("search");

        // Assert
        ReferenceEquals(c1a, c1b).ShouldBeTrue();
        ReferenceEquals(c1a, c2).ShouldBeFalse();
    }

    [Fact]
    public async Task Factory_can_create_multiple_clients_concurrently_per_name_and_deduplicate()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddHttpClient();
        services.AddSingleton(Mock.Of<ISitecoreTokenService>());
        services.AddSingleton<ISitecoreTokenCache>(Mock.Of<ISitecoreTokenCache>());
        services.Configure<SitecoreGraphQLOptions>(o =>
        {
            o.Clients["content"] = new SitecoreGraphQLClientOptions
            {
                Endpoint = "https://content/graphql",
                ClientId = "content-id",
                ClientSecret = "content-secret"
            };
        });
        services.AddSingleton<ISitecoreGraphQLFactory, SitecoreGraphQLFactory>();

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ISitecoreGraphQLFactory>();

        // Act
        var tasks = Enumerable.Range(0, 16)
            .Select(_ => factory.CreateClientByNameAsync("content"))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        // Assert
        var first = results[0];
        foreach (var c in results)
        {
            ReferenceEquals(first, c).ShouldBeTrue();
        }
    }
}
