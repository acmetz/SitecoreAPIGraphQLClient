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

    private sealed class StubOkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
