using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;
using Sitecore.API.Foundation.Authorization.Abstractions;
using Sitecore.API.Foundation.Authorization.Models;
using Sitecore.API.Foundation.GraphQL.DependencyInjection;
using Sitecore.API.Foundation.GraphQL.Internal;

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

        var tokenService = new Mock<ISitecoreTokenService>();
        tokenService.Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>()))
                    .ReturnsAsync(default(SitecoreAuthToken));
        services.AddSingleton(tokenService.Object);

        var accessor = new Mock<ITokenValueAccessor>();
        accessor.Setup(a => a.GetAccessToken(It.IsAny<SitecoreAuthToken>()))
                .Returns("abc");
        services.AddSingleton(accessor.Object);

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ISitecoreGraphQLFactory>();

        // Act
        var ok = await factory.RefreshTokenAsync();

        // Assert
        ok.ShouldBeTrue();
    }

    [Fact]
    public async Task RefreshTokenAsync_evicts_warm_token_via_TryRefreshSitecoreAuthToken_and_updates_subsequent_requests()
    {
        var warmToken = new SitecoreAuthToken("warm-v1", DateTimeOffset.UtcNow.AddMinutes(30));
        var refreshedToken = new SitecoreAuthToken("fresh-v2", DateTimeOffset.UtcNow.AddMinutes(60));
        var activeToken = warmToken;

        var services = new ServiceCollection();
        services.AddSitecoreGraphQL(o =>
        {
            o.Endpoint = "https://unit/graphql";
            o.ClientId = "id";
            o.ClientSecret = "secret";
        });

        var tokenService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        tokenService
            .Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>()))
            .ReturnsAsync(() => activeToken);
        tokenService
            .Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => activeToken);
        tokenService
            .Setup(s => s.TryRefreshSitecoreAuthToken(warmToken))
            .Callback(() => activeToken = refreshedToken)
            .ReturnsAsync(refreshedToken);

        services.AddSingleton(tokenService.Object);

        string? lastBearer = null;
        services.AddHttpClient(SitecoreGraphQLFactory.NamedHttpClient)
            .ConfigurePrimaryHttpMessageHandler(() => new CaptureBearerHandler(b => lastBearer = b));

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ISitecoreGraphQLFactory>();
        var client = await factory.CreateClientAsync();

        await client.SendQueryAsync<Newtonsoft.Json.Linq.JObject>(new global::GraphQL.GraphQLRequest { Query = "query { ok }" });
        lastBearer.ShouldBe("warm-v1");

        var refreshed = await factory.RefreshTokenAsync();
        refreshed.ShouldBeTrue();
        tokenService.Verify(s => s.TryRefreshSitecoreAuthToken(warmToken), Times.Once);

        await client.SendQueryAsync<Newtonsoft.Json.Linq.JObject>(new global::GraphQL.GraphQLRequest { Query = "query { ok }" });
        lastBearer.ShouldBe("fresh-v2");
    }

    [Fact]
    public async Task RefreshTokenAsync_when_no_credentials_configured_returns_false_without_calling_token_service()
    {
        var tokenService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        var coordinator = new SitecoreTokenCoordinator(tokenService.Object, new DefaultTokenValueAccessor());
        var optionsMonitor = new StaticOptionsMonitor(new SitecoreGraphQLOptions());
        using var factory = new SitecoreGraphQLFactory(coordinator, optionsMonitor, Mock.Of<IHttpClientFactory>());

        var result = await factory.RefreshTokenAsync();

        result.ShouldBeFalse();
        tokenService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RefreshTokenAsync_refreshes_named_clients_and_deduplicates_shared_credentials_and_supports_RefreshTokenByNameAsync()
    {
        var refreshedClientIds = new List<string>();

        var services = new ServiceCollection();
        services.AddSitecoreGraphQL(o =>
        {
            o.Clients["content"] = new SitecoreGraphQLClientOptions
            {
                Endpoint = "https://content/graphql",
                ClientId = "shared-id",
                ClientSecret = "shared-secret"
            };
            o.Clients["preview"] = new SitecoreGraphQLClientOptions
            {
                Endpoint = "https://preview/graphql",
                ClientId = "shared-id",
                ClientSecret = "shared-secret"
            };
            o.Clients["search"] = new SitecoreGraphQLClientOptions
            {
                Endpoint = "https://search/graphql",
                ClientId = "search-id",
                ClientSecret = "search-secret"
            };
        });

        var tokenService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        tokenService
            .Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>()))
            .ReturnsAsync((SitecoreAuthClientCredentials c) => new SitecoreAuthToken($"stale-{c.ClientId}", DateTimeOffset.UtcNow.AddMinutes(10)));
        tokenService
            .Setup(s => s.TryRefreshSitecoreAuthToken(It.IsAny<SitecoreAuthToken>()))
            .Callback((SitecoreAuthToken t) => refreshedClientIds.Add(t.AccessToken))
            .ReturnsAsync((SitecoreAuthToken t) => new SitecoreAuthToken($"fresh-{t.AccessToken}", DateTimeOffset.UtcNow.AddHours(1)));

        services.AddSingleton(tokenService.Object);

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ISitecoreGraphQLFactory>();

        var allOk = await factory.RefreshTokenAsync();
        allOk.ShouldBeTrue();
        // 'content' and 'preview' share credentials, so only 2 distinct credential sets are refreshed
        refreshedClientIds.Count.ShouldBe(2);

        refreshedClientIds.Clear();
        var singleOk = await factory.RefreshTokenByNameAsync("search");
        singleOk.ShouldBeTrue();
        refreshedClientIds.ShouldBe(["stale-search-id"]);

        await Should.ThrowAsync<ArgumentNullException>(async () => await factory.RefreshTokenByNameAsync(null!));
        await Should.ThrowAsync<ArgumentException>(async () => await factory.RefreshTokenByNameAsync("   "));
        await Should.ThrowAsync<InvalidOperationException>(async () => await factory.RefreshTokenByNameAsync("unknown-client"));
    }

    [Fact]
    public async Task Obsolete_SitecoreTokenCache_adapter_supports_GetOrRefresh_ForceRefresh_and_missing_credentials()
    {
#pragma warning disable CS0618
        var stale = new SitecoreAuthToken("tok-v1", DateTimeOffset.UtcNow.AddMinutes(30));
        var fresh = new SitecoreAuthToken("tok-v2", DateTimeOffset.UtcNow.AddMinutes(60));

        var tokenService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        tokenService.Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>())).ReturnsAsync(stale);
        tokenService.Setup(s => s.TryRefreshSitecoreAuthToken(stale)).ReturnsAsync(fresh);

        var cache = new SitecoreTokenCache(
            tokenService.Object,
            Options.Create(new SitecoreGraphQLOptions { ClientId = "id", ClientSecret = "secret" }),
            new DefaultTokenValueAccessor());

        cache.CurrentToken.ShouldBeNull();

        var first = await cache.GetOrRefreshAsync(CancellationToken.None);
        first.ShouldBe("tok-v1");
        cache.CurrentToken.ShouldBe("tok-v1");

        // ForceRefreshAsync now forces a real refresh even when CurrentToken is already populated!
        var second = await cache.ForceRefreshAsync(CancellationToken.None);
        second.ShouldBe("tok-v2");
        cache.CurrentToken.ShouldBe("tok-v2");

        // Missing credentials returns null without calling token service
        var strictUnusedService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        var unconfiguredCache = new SitecoreTokenCache(
            strictUnusedService.Object,
            Options.Create(new SitecoreGraphQLOptions()),
            new DefaultTokenValueAccessor());

        (await unconfiguredCache.GetOrRefreshAsync(CancellationToken.None)).ShouldBeNull();
        (await unconfiguredCache.ForceRefreshAsync(CancellationToken.None)).ShouldBeNull();
        unconfiguredCache.CurrentToken.ShouldBeNull();
        strictUnusedService.VerifyNoOtherCalls();

        // Also verify DI-registered ISitecoreTokenCache constructor (resolved via AddSitecoreGraphQL)
        var diServices = new ServiceCollection();
        diServices.AddSitecoreGraphQL(o =>
        {
            o.Endpoint = "https://unit/graphql";
            o.ClientId = "id";
            o.ClientSecret = "secret";
        });
        diServices.AddSingleton(tokenService.Object);
        using var diProvider = diServices.BuildServiceProvider();
        var diCache = diProvider.GetRequiredService<ISitecoreTokenCache>();
        (await diCache.GetOrRefreshAsync(CancellationToken.None)).ShouldBe("tok-v1");
        diCache.CurrentToken.ShouldBe("tok-v1");
#pragma warning restore CS0618
    }

    private sealed class CaptureBearerHandler : HttpMessageHandler
    {
        private readonly Action<string?> _onBearer;

        public CaptureBearerHandler(Action<string?> onBearer) => _onBearer = onBearer;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _onBearer(request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":{\"ok\":true}}", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class StaticOptionsMonitor : IOptionsMonitor<SitecoreGraphQLOptions>
    {
        public StaticOptionsMonitor(SitecoreGraphQLOptions currentValue) => CurrentValue = currentValue;
        public SitecoreGraphQLOptions CurrentValue { get; }
        public SitecoreGraphQLOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<SitecoreGraphQLOptions, string?> listener) => null;
    }
}
