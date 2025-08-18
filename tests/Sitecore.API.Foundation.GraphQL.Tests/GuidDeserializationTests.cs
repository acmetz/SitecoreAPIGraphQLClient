using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using GraphQL; // for GraphQLRequest
using GraphQL.Client.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;
using Sitecore.API.Foundation.Authorization.Abstractions;
using Sitecore.API.Foundation.Authorization.Models;
using Sitecore.API.Foundation.GraphQL.DependencyInjection;

namespace Sitecore.API.Foundation.GraphQL.Tests;

public class GuidDeserializationTests
{
    private sealed class StubPrimaryHandler : HttpMessageHandler
    {
        private readonly string _json;
        public int Calls { get; private set; }
        public StubPrimaryHandler(string json) => _json = json;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(resp);
        }
    }

    private static IServiceProvider BuildProviderWithStubHandler(string json)
    {
        var services = new ServiceCollection();

        // Minimal options via delegate
        services.AddSitecoreGraphQL(o =>
        {
            o.Endpoint = "http://unit.test/graphql";
            o.ClientId = "id";
            o.ClientSecret = "secret";
            o.EnableUnauthorizedRefresh = false; // keep handler behavior minimal
        });

        // Provide token service/accessor for handler
        var tokenService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        tokenService.Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>()))
                    .ReturnsAsync(default(SitecoreAuthToken));
        services.AddSingleton(tokenService.Object);

        var accessor = new Mock<ITokenValueAccessor>(MockBehavior.Strict);
        accessor.Setup(a => a.GetAccessToken(It.IsAny<SitecoreAuthToken>())).Returns("tkn");
        services.AddSingleton(accessor.Object);

        // Override the named HttpClient to use our stub primary handler
        services.AddHttpClient(SitecoreGraphQLFactory.NamedHttpClient)
                .ConfigurePrimaryHttpMessageHandler(() => new StubPrimaryHandler(json));

        return services.BuildServiceProvider();
    }

    private sealed class GuidContainer
    {
        public Guid Id { get; set; }
        public Guid FolderId { get; set; }
        public Guid? MaybeId { get; set; }
        public Guid[] Ids { get; set; } = Array.Empty<Guid>();
        public List<Guid> MoreIds { get; set; } = new();
    }

    private sealed class RootResponse
    {
        public GuidContainer Node { get; set; } = new();
    }

    [Fact]
    public async Task GraphQL_client_deserializes_Guids_in_object_arrays_and_nullables()
    {
        // Arrange a GraphQL JSON response
        var g1 = Guid.NewGuid();
        var g2 = Guid.NewGuid();
        var g3 = Guid.NewGuid();
        var json = $"{{\n  \"data\": {{\n    \"node\": {{\n      \"id\": \"{g1}\",\n      \"folderId\": \"{g2}\",\n      \"maybeId\": null,\n      \"ids\": [\"{g1}\", \"{g2}\"],\n      \"moreIds\": [\"{g3}\"]\n    }}\n  }}\n}}";

        var provider = BuildProviderWithStubHandler(json);
        var factory = provider.GetRequiredService<ISitecoreGraphQLFactory>();
        var client = await factory.CreateClientAsync();

        var request = new GraphQLRequest { Query = "query { node { id } }" };

        // Act
        var result = await client.SendQueryAsync<RootResponse>(request, CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        result.Data.ShouldNotBeNull();
        result.Data.Node.Id.ShouldBeOfType<Guid>();
        result.Data.Node.Id.ShouldBe(g1);
        result.Data.Node.FolderId.ShouldBe(g2);
        result.Data.Node.MaybeId.ShouldBeNull();
        result.Data.Node.Ids.Length.ShouldBe(2);
        result.Data.Node.Ids[0].ShouldBe(g1);
        result.Data.Node.Ids[1].ShouldBe(g2);
        result.Data.Node.MoreIds.Count.ShouldBe(1);
        result.Data.Node.MoreIds[0].ShouldBe(g3);
    }

    [Fact]
    public async Task GraphQL_client_deserializes_various_guid_formats()
    {
        // Arrange a response including different valid GUID string formats
        var g = Guid.NewGuid();
        var n = g.ToString("N"); // 32 digits
        var d = g.ToString("D"); // 8-4-4-4-12
        var b = g.ToString("B"); // {D}
        var p = g.ToString("P"); // (D)
        var json = $"{{\n  \"data\": {{\n    \"node\": {{\n      \"id\": \"{d}\",\n      \"folderId\": \"{n}\",\n      \"maybeId\": \"{b}\",\n      \"ids\": [\"{p}\"]\n    }}\n  }}\n}}";

        var provider = BuildProviderWithStubHandler(json);
        var factory = provider.GetRequiredService<ISitecoreGraphQLFactory>();
        var client = await factory.CreateClientAsync();
        var request = new GraphQLRequest { Query = "query { node { id } }" };

        // Act
        var result = await client.SendQueryAsync<RootResponse>(request, CancellationToken.None);

        // Assert: all formats should parse to the same Guid
        result.Data.Node.Id.ShouldBe(g);
        result.Data.Node.FolderId.ShouldBe(g);
        result.Data.Node.MaybeId.ShouldBe(g);
        result.Data.Node.Ids.Single().ShouldBe(g);
    }
}
