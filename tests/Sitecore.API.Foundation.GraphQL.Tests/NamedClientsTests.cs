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

#pragma warning disable CS0618 // Intentionally exercising legacy ISitecoreTokenCache constructor path
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

    [Fact]
    public async Task NamedClients_inherit_unset_defaults_and_send_isolated_Authorization_headers()
    {
        var capturedHeaders = new System.Collections.Concurrent.ConcurrentDictionary<string, string?>();

        var services = new ServiceCollection();
        GraphQL.DependencyInjection.ServiceCollectionExtensions.AddSitecoreGraphQL(services, o =>
        {
            o.Endpoint = "https://shared.sitecore/graphql";
            o.ClientId = "shared-id";
            o.ClientSecret = "shared-secret";
            o.Clients["inherits-creds"] = new SitecoreGraphQLClientOptions
            {
                Endpoint = "https://inherited-creds.sitecore/graphql"
                // Inherits ClientId and ClientSecret from top-level options
            };
            o.Clients["inherits-endpoint"] = new SitecoreGraphQLClientOptions
            {
                // Inherits Endpoint from top-level options
                ClientId = "override-id",
                ClientSecret = "override-secret"
            };
        });

        var tokenService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        tokenService
            .Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SitecoreAuthClientCredentials creds, CancellationToken _) =>
                new SitecoreAuthToken($"bearer-{creds.ClientId}-{creds.ClientSecret}", DateTimeOffset.UtcNow.AddHours(1)));
        services.AddSingleton(tokenService.Object);

        services.AddHttpClient(SitecoreGraphQLFactory.NamedHttpClient)
            .ConfigurePrimaryHttpMessageHandler(() => new HeaderCaptureHandler(capturedHeaders));

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ISitecoreGraphQLFactory>();

        var client1 = await factory.CreateClientByNameAsync("inherits-creds");
        var client2 = await factory.CreateClientByNameAsync("inherits-endpoint");

        var gqlRequest = new global::GraphQL.GraphQLRequest { Query = "query { ok }" };
        await Task.WhenAll(
            client1.SendQueryAsync<Newtonsoft.Json.Linq.JObject>(gqlRequest),
            client2.SendQueryAsync<Newtonsoft.Json.Linq.JObject>(gqlRequest));

        capturedHeaders["inherited-creds.sitecore"].ShouldBe("bearer-shared-id-shared-secret");
        capturedHeaders["shared.sitecore"].ShouldBe("bearer-override-id-override-secret");
    }

    private sealed class HeaderCaptureHandler : HttpMessageHandler
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> _headers;

        public HeaderCaptureHandler(System.Collections.Concurrent.ConcurrentDictionary<string, string?> headers)
            => _headers = headers;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _headers[request.RequestUri!.Host] = request.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":{\"ok\":true}}", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
#pragma warning restore CS0618
