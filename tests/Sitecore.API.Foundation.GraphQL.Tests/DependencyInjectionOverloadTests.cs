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

    [Fact]
    public async Task AddSitecoreGraphQL_four_parameter_overloads_invoke_configure_once_and_customize_client_builder()
    {
        var configureInvocationCount = 0;
        var builderCustomized = false;

        var services = new ServiceCollection();
        services.AddSitecoreGraphQL(
            o =>
            {
                configureInvocationCount++;
                o.Endpoint = "https://unit/graphql";
                o.ClientId = "id";
                o.ClientSecret = "secret";
            },
            configureResilience: null,
            configureClient: builder =>
            {
                builderCustomized = true;
                builder.ConfigureHttpClient(c => c.DefaultRequestHeaders.Add("X-Custom-Client", "enabled"));
            });

        services.AddSingleton(Mock.Of<ISitecoreTokenService>());

        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IOptions<SitecoreGraphQLOptions>>().Value;
        var factory = provider.GetRequiredService<ISitecoreGraphQLFactory>();
        var gqlClient = await factory.CreateClientAsync();

        gqlClient.ShouldNotBeNull();
        configureInvocationCount.ShouldBe(1);
        builderCustomized.ShouldBeTrue();

        var namedHttpClient = provider.GetRequiredService<IHttpClientFactory>().CreateClient(SitecoreGraphQLFactory.NamedHttpClient);
        namedHttpClient.DefaultRequestHeaders.GetValues("X-Custom-Client").ShouldContain("enabled");

        // Also verify IConfiguration 4-parameter overload with resilience enabled
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sitecore:GraphQL:Endpoint"] = "https://cfg/graphql",
                ["Sitecore:GraphQL:ClientId"] = "cfg-id",
                ["Sitecore:GraphQL:ClientSecret"] = "cfg-secret"
            })
            .Build();

        var cfgServices = new ServiceCollection();
        var resilienceConfigured = false;
        cfgServices.AddSitecoreGraphQL(
            cfg,
            configureResilience: r =>
            {
                resilienceConfigured = true;
                r.Retry.MaxRetryAttempts = 1;
            },
            configureClient: b =>
            {
                b.ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(45));
                b.ConfigurePrimaryHttpMessageHandler(() => new StubOkHandler());
            });
        cfgServices.AddSingleton(Mock.Of<ISitecoreTokenService>());

        using var cfgProvider = cfgServices.BuildServiceProvider();
        var cfgHttpClient = cfgProvider.GetRequiredService<IHttpClientFactory>().CreateClient(SitecoreGraphQLFactory.NamedHttpClient);
        cfgHttpClient.Timeout.ShouldBe(TimeSpan.FromSeconds(45));
        using var resp = await cfgHttpClient.GetAsync("https://cfg/graphql");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        resilienceConfigured.ShouldBeTrue();
    }

    [Fact]
    public async Task AddSitecoreGraphQL_delegate_preserves_prior_ConfigureOptions_and_invokes_delegate_once()
    {
        var configureCalls = 0;
        var services = new ServiceCollection();

        services.Configure<SitecoreGraphQLOptions>(prior =>
        {
            prior.Endpoint = "https://prior.sitecore/graphql";
            prior.ClientId = "prior-client-id";
            prior.ClientSecret = "prior-client-secret";
            prior.AllowInsecureHttp = true;
            prior.AllowPostRetries = true;
            prior.EnableUnauthorizedRefresh = true;
            prior.MaxUnauthorizedRetries = 4;
            prior.MaxCachedClients = 256;
            prior.MaxRetryBufferBytes = 4096;
            prior.Clients["authoring"] = new SitecoreGraphQLClientOptions
            {
                Endpoint = "https://authoring.sitecore/graphql",
                ClientId = "authoring-id",
                ClientSecret = "authoring-old-secret",
                AllowInsecureHttp = true,
                AllowPostRetries = false
            };
            prior.Clients["obsolete"] = new SitecoreGraphQLClientOptions
            {
                Endpoint = "https://obsolete.sitecore/graphql",
                ClientId = "obsolete-id",
                ClientSecret = "obsolete-secret"
            };
        });

        services.AddSitecoreGraphQL(o =>
        {
            configureCalls++;

            // Inspect and selectively override prior values (including resetting booleans back to false)
            o.Endpoint.ShouldBe("https://prior.sitecore/graphql");
            o.ClientSecret = "overridden-default-secret";
            o.AllowInsecureHttp = false;
            o.AllowPostRetries = false;
            o.EnableUnauthorizedRefresh = false;
            o.EnableInternalLoggingSetup = false;
            o.MaxUnauthorizedRetries = 2;
            o.MaxCachedClients = 64;
            o.MaxRetryBufferBytes = 8192;

            o.Clients["authoring"].ClientSecret = "authoring-rotated-secret";
            o.Clients["authoring"].AllowInsecureHttp = false;
            o.Clients["authoring"].AllowPostRetries = true;
            o.Clients.Remove("obsolete");
            o.Clients["delivery"] = new SitecoreGraphQLClientOptions
            {
                Endpoint = "https://delivery.sitecore/graphql",
                ClientSecret = "delivery-secret"
            };
        });

        services.AddSingleton(Mock.Of<ISitecoreTokenService>());

        // Delegate should not be evaluated eagerly before options resolution
        configureCalls.ShouldBe(0);

        using var provider = services.BuildServiceProvider();

        // Resolve via both IOptions<T> and IOptionsMonitor<T> (and ISitecoreGraphQLFactory)
        var staticOpts = provider.GetRequiredService<IOptions<SitecoreGraphQLOptions>>().Value;
        var monitorOpts = provider.GetRequiredService<IOptionsMonitor<SitecoreGraphQLOptions>>().CurrentValue;
        var factory = provider.GetRequiredService<ISitecoreGraphQLFactory>();

        var defaultClient = await factory.CreateClientAsync();
        var authoringClient = await factory.CreateClientByNameAsync("authoring");
        var deliveryClient = await factory.CreateClientByNameAsync("delivery");

        defaultClient.ShouldNotBeNull();
        authoringClient.ShouldNotBeNull();
        deliveryClient.ShouldNotBeNull();

        configureCalls.ShouldBe(1);

        foreach (var resolved in new[] { staticOpts, monitorOpts })
        {
            resolved.Endpoint.ShouldBe("https://prior.sitecore/graphql");
            resolved.ClientId.ShouldBe("prior-client-id");
            resolved.ClientSecret.ShouldBe("overridden-default-secret");
            resolved.AllowInsecureHttp.ShouldBeFalse();
            resolved.AllowPostRetries.ShouldBeFalse();
            resolved.EnableUnauthorizedRefresh.ShouldBeFalse();
            resolved.EnableInternalLoggingSetup.ShouldBeFalse();
            resolved.MaxUnauthorizedRetries.ShouldBe(2);
            resolved.MaxCachedClients.ShouldBe(64);
            resolved.MaxRetryBufferBytes.ShouldBe(8192);

            resolved.Clients.ContainsKey("obsolete").ShouldBeFalse();
            resolved.Clients["authoring"].Endpoint.ShouldBe("https://authoring.sitecore/graphql");
            resolved.Clients["authoring"].ClientId.ShouldBe("authoring-id");
            resolved.Clients["authoring"].ClientSecret.ShouldBe("authoring-rotated-secret");
            resolved.Clients["authoring"].AllowInsecureHttp.ShouldBe(false);
            resolved.Clients["authoring"].AllowPostRetries.ShouldBe(true);

            resolved.Clients["delivery"].Endpoint.ShouldBe("https://delivery.sitecore/graphql");
            resolved.Clients["delivery"].ClientId.ShouldBeNull();
            resolved.Clients["delivery"].ClientSecret.ShouldBe("delivery-secret");
        }

        // Also verify AddSitecoreGraphQL(configure: null) preserves prior services.Configure<SitecoreGraphQLOptions>
        var nullDelegateServices = new ServiceCollection();
        nullDelegateServices.Configure<SitecoreGraphQLOptions>(prior =>
        {
            prior.Endpoint = "https://null-delegate.sitecore/graphql";
            prior.ClientId = "null-delegate-id";
            prior.ClientSecret = "null-delegate-secret";
        });
        nullDelegateServices.AddSitecoreGraphQL(configure: null);
        nullDelegateServices.AddSingleton(Mock.Of<ISitecoreTokenService>());

        using var nullDelegateProvider = nullDelegateServices.BuildServiceProvider();
        var preservedOpts = nullDelegateProvider.GetRequiredService<IOptions<SitecoreGraphQLOptions>>().Value;
        preservedOpts.Endpoint.ShouldBe("https://null-delegate.sitecore/graphql");
        preservedOpts.ClientId.ShouldBe("null-delegate-id");
        preservedOpts.ClientSecret.ShouldBe("null-delegate-secret");
    }

    [Fact]
    public async Task AddSitecoreGraphQL_delegate_preserves_custom_Clients_comparer_reload_overrides_and_PostConfigure()
    {
        // 1) Custom StringComparer.OrdinalIgnoreCase dictionary assigned in configure must hold across both IOptions<T> and IOptionsMonitor<T> / ISitecoreGraphQLFactory
        var comparerCalls = 0;
        var comparerServices = new ServiceCollection();
        comparerServices.Configure<SitecoreGraphQLOptions>(prior =>
        {
            prior.Clients["stale"] = new SitecoreGraphQLClientOptions
            {
                Endpoint = "https://stale.sitecore/graphql",
                ClientId = "stale-id",
                ClientSecret = "stale-secret"
            };
        });
        comparerServices.AddSitecoreGraphQL(o =>
        {
            comparerCalls++;
            o.Clients = new Dictionary<string, SitecoreGraphQLClientOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["Delivery"] = new SitecoreGraphQLClientOptions
                {
                    Endpoint = "https://edge.sitecorecloud.io/api/graphql/v1",
                    ClientId = "delivery-id",
                    ClientSecret = "delivery-secret"
                }
            };
        });
        comparerServices.AddSingleton(Mock.Of<ISitecoreTokenService>());

        using (var comparerProvider = comparerServices.BuildServiceProvider())
        {
            var staticOpts = comparerProvider.GetRequiredService<IOptions<SitecoreGraphQLOptions>>().Value;
            staticOpts.Clients.ContainsKey("delivery").ShouldBeTrue();
            staticOpts.Clients.ContainsKey("stale").ShouldBeFalse();

            var factory = comparerProvider.GetRequiredService<ISitecoreGraphQLFactory>();
            var lowerCaseClient = await factory.CreateClientByNameAsync("delivery");
            lowerCaseClient.ShouldNotBeNull();
            comparerCalls.ShouldBe(1);
        }

        // 2) Reloadable IConfiguration before AddSitecoreGraphQL + explicit code hardening matching initial defaults + PostConfigure after AddSitecoreGraphQL
        var reloadSource = new ReloadableConfigSource(new Dictionary<string, string?>
        {
            ["Sitecore:GraphQL:Endpoint"] = "https://v1.sitecore/graphql",
            ["Sitecore:GraphQL:ClientId"] = "cfg-id",
            ["Sitecore:GraphQL:ClientSecret"] = "cfg-secret-v1",
            ["Sitecore:GraphQL:AllowInsecureHttp"] = "false",
            ["Sitecore:GraphQL:Clients:authoring:Endpoint"] = "https://authoring-v1.sitecore/graphql",
            ["Sitecore:GraphQL:Clients:authoring:AllowPostRetries"] = "false"
        });
        var configuration = new ConfigurationBuilder().Add(reloadSource).Build();

        var reloadDelegateCalls = 0;
        var reloadServices = new ServiceCollection();
        reloadServices.Configure<SitecoreGraphQLOptions>(configuration.GetSection("Sitecore:GraphQL"));
        reloadServices.AddSitecoreGraphQL(o =>
        {
            reloadDelegateCalls++;

            // Explicit security assignments equal to initial config values must still override later hot-reloads
            o.AllowInsecureHttp = false;
            o.ClientSecret = "code-pinned-secret";
            o.Clients["authoring"].AllowPostRetries = false;
        });
        reloadServices.PostConfigure<SitecoreGraphQLOptions>(post =>
        {
            post.MaxCachedClients = 32;
        });
        reloadServices.AddSingleton(Mock.Of<ISitecoreTokenService>());

        using (var reloadProvider = reloadServices.BuildServiceProvider())
        {
            var monitor = reloadProvider.GetRequiredService<IOptionsMonitor<SitecoreGraphQLOptions>>();
            var initial = monitor.CurrentValue;
            initial.Endpoint.ShouldBe("https://v1.sitecore/graphql");
            initial.ClientSecret.ShouldBe("code-pinned-secret");
            initial.AllowInsecureHttp.ShouldBeFalse();
            initial.MaxCachedClients.ShouldBe(32);
            initial.Clients["authoring"].Endpoint.ShouldBe("https://authoring-v1.sitecore/graphql");
            initial.Clients["authoring"].AllowPostRetries.ShouldBe(false);
            reloadDelegateCalls.ShouldBe(1);

            reloadSource.Reload(new Dictionary<string, string?>
            {
                ["Sitecore:GraphQL:Endpoint"] = "https://v2.sitecore/graphql",
                ["Sitecore:GraphQL:ClientId"] = "cfg-id-v2",
                ["Sitecore:GraphQL:ClientSecret"] = "cfg-secret-v2",
                ["Sitecore:GraphQL:AllowInsecureHttp"] = "true",
                ["Sitecore:GraphQL:Clients:authoring:Endpoint"] = "https://authoring-v2.sitecore/graphql",
                ["Sitecore:GraphQL:Clients:authoring:AllowPostRetries"] = "true"
            });

            var reloaded = monitor.CurrentValue;
            reloadDelegateCalls.ShouldBe(1);
            reloaded.Endpoint.ShouldBe("https://v2.sitecore/graphql");
            reloaded.ClientId.ShouldBe("cfg-id-v2");
            reloaded.ClientSecret.ShouldBe("code-pinned-secret");
            reloaded.AllowInsecureHttp.ShouldBeFalse();
            reloaded.MaxCachedClients.ShouldBe(32);
            reloaded.Clients["authoring"].Endpoint.ShouldBe("https://authoring-v2.sitecore/graphql");
            reloaded.Clients["authoring"].AllowPostRetries.ShouldBe(false);
        }

        // 3) Setting o.Clients = null! in configure is preserved across resolutions
        var nullClientsServices = new ServiceCollection();
        nullClientsServices.AddSitecoreGraphQL(o =>
        {
            o.Endpoint = "https://unit.sitecore/graphql";
            o.ClientId = "id";
            o.ClientSecret = "secret";
            o.Clients = null!;
        });
        nullClientsServices.AddSingleton(Mock.Of<ISitecoreTokenService>());

        using var nullClientsProvider = nullClientsServices.BuildServiceProvider();
        nullClientsProvider.GetRequiredService<IOptions<SitecoreGraphQLOptions>>().Value.Clients.ShouldBeNull();
        nullClientsProvider.GetRequiredService<IOptionsMonitor<SitecoreGraphQLOptions>>().CurrentValue.Clients.ShouldBeNull();
    }

    private sealed class ReloadableConfigSource : IConfigurationSource
    {
        private readonly ReloadableConfigProvider _provider;

        public ReloadableConfigSource(IDictionary<string, string?> initialData)
            => _provider = new ReloadableConfigProvider(initialData);

        public IConfigurationProvider Build(IConfigurationBuilder builder) => _provider;

        public void Reload(IDictionary<string, string?> newData) => _provider.Update(newData);
    }

    private sealed class ReloadableConfigProvider : ConfigurationProvider
    {
        public ReloadableConfigProvider(IDictionary<string, string?> initialData)
            => Data = new Dictionary<string, string?>(initialData, StringComparer.OrdinalIgnoreCase);

        public void Update(IDictionary<string, string?> newData)
        {
            Data = new Dictionary<string, string?>(newData, StringComparer.OrdinalIgnoreCase);
            OnReload();
        }
    }

    private sealed class StubOkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
