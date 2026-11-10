using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Sitecore.API.Foundation.GraphQL.Http;
using Sitecore.API.Foundation.GraphQL.Internal;

namespace Sitecore.API.Foundation.GraphQL.DependencyInjection;

/// <summary>
/// Dependency injection extensions for configuring the Sitecore GraphQL client factory, token handler, and optional HTTP resilience pipeline.
/// </summary>
public static class ServiceCollectionExtensions
{
    private const string SectionName = "Sitecore:GraphQL";

    internal static void TryAddInternalLogging(this IServiceCollection services)
    {
        services.AddLogging();
    }

    /// <summary>
    /// Registers the Sitecore GraphQL client factory, HTTP pipeline, and options bound from <c>Sitecore:GraphQL</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration instance.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddSitecoreGraphQL(
        this IServiceCollection services,
        IConfiguration configuration)
        => AddSitecoreGraphQL(services, configuration, configureResilience: null, configureClient: null);

    /// <summary>
    /// Registers the Sitecore GraphQL client factory, HTTP pipeline, options bound from <c>Sitecore:GraphQL</c>,
    /// and optional library-scoped HTTP resilience and client customization.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration instance.</param>
    /// <param name="configureResilience">
    /// Optional delegate to enable and configure standard HTTP resilience on the <c>SitecoreGraphQL</c> pipeline.
    /// When <c>null</c>, no resilience handler is added. When non-<c>null</c>, <c>HTTP POST</c> retries are disabled by default
    /// unless <see cref="SitecoreGraphQLOptions.AllowPostRetries"/> or <see cref="SitecoreGraphQLClientOptions.AllowPostRetries"/> is <c>true</c>.
    /// </param>
    /// <param name="configureClient">Optional delegate to customize the underlying <see cref="IHttpClientBuilder"/> for <c>SitecoreGraphQL</c>.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddSitecoreGraphQL(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<HttpStandardResilienceOptions>? configureResilience,
        Action<IHttpClientBuilder>? configureClient = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        services.AddOptions<SitecoreGraphQLOptions>()
            .Bind(section)
            .ValidateOnStart();

        var snapshot = new SitecoreGraphQLOptions();
        section.Bind(snapshot);

        return AddCore(services, snapshot.EnableInternalLoggingSetup, configureResilience, configureClient);
    }

    /// <summary>
    /// Registers the Sitecore GraphQL client factory, HTTP pipeline, and options using an optional configuration delegate.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration delegate for <see cref="SitecoreGraphQLOptions"/>.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddSitecoreGraphQL(
        this IServiceCollection services,
        Action<SitecoreGraphQLOptions>? configure)
        => AddSitecoreGraphQL(services, configure, configureResilience: null, configureClient: null);

    /// <summary>
    /// Registers the Sitecore GraphQL client factory, HTTP pipeline, options using an optional configuration delegate,
    /// and optional library-scoped HTTP resilience and client customization.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration delegate for <see cref="SitecoreGraphQLOptions"/>.</param>
    /// <param name="configureResilience">
    /// Optional delegate to enable and configure standard HTTP resilience on the <c>SitecoreGraphQL</c> pipeline.
    /// When <c>null</c>, no resilience handler is added. When non-<c>null</c>, <c>HTTP POST</c> retries are disabled by default
    /// unless <see cref="SitecoreGraphQLOptions.AllowPostRetries"/> or <see cref="SitecoreGraphQLClientOptions.AllowPostRetries"/> is <c>true</c>.
    /// </param>
    /// <param name="configureClient">Optional delegate to customize the underlying <see cref="IHttpClientBuilder"/> for <c>SitecoreGraphQL</c>.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddSitecoreGraphQL(
        this IServiceCollection services,
        Action<SitecoreGraphQLOptions>? configure,
        Action<HttpStandardResilienceOptions>? configureResilience,
        Action<IHttpClientBuilder>? configureClient = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Evaluate configure at most once and copy the captured snapshot into OptionsBuilder.
        var snapshot = new SitecoreGraphQLOptions();
        configure?.Invoke(snapshot);

        var optionsBuilder = services.AddOptions<SitecoreGraphQLOptions>();
        if (configure is not null)
        {
            optionsBuilder.Configure(target => CopyOptions(snapshot, target));
        }

        optionsBuilder.ValidateOnStart();

        return AddCore(services, snapshot.EnableInternalLoggingSetup, configureResilience, configureClient);
    }

    private static IServiceCollection AddCore(
        IServiceCollection services,
        bool enableLogging,
        Action<HttpStandardResilienceOptions>? configureResilience,
        Action<IHttpClientBuilder>? configureClient)
    {
        if (enableLogging)
        {
            services.TryAddInternalLogging();
        }

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<SitecoreGraphQLOptions>, SitecoreGraphQLOptionsValidator>());

        services.TryAddSingleton<ITokenValueAccessor, DefaultTokenValueAccessor>();
        services.TryAddSingleton<ISitecoreTokenCoordinator>(sp => new SitecoreTokenCoordinator(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ITokenValueAccessor>(),
            sp.GetService<ILogger<SitecoreTokenCoordinator>>()));

#pragma warning disable CS0618 // Registered for v1.x backward compatibility
        services.TryAddSingleton<ISitecoreTokenCache>(sp => new SitecoreTokenCache(
            sp.GetRequiredService<ISitecoreTokenCoordinator>(),
            sp.GetRequiredService<IOptionsMonitor<SitecoreGraphQLOptions>>(),
            sp.GetService<ILogger<SitecoreTokenCache>>()));
#pragma warning restore CS0618

        services.AddTransient<SitecoreTokenHandler>(sp => new SitecoreTokenHandler(
            sp.GetRequiredService<ISitecoreTokenCoordinator>(),
            sp.GetRequiredService<IOptionsMonitor<SitecoreGraphQLOptions>>(),
            sp.GetService<TimeProvider>(),
            sp.GetService<ILogger<SitecoreTokenHandler>>()));

        var httpBuilder = services.AddHttpClient(SitecoreGraphQLFactory.NamedHttpClient)
            .AddHttpMessageHandler<SitecoreTokenHandler>();

        if (configureResilience is not null)
        {
            ConfigureResiliencePipeline(httpBuilder, configureResilience);
        }

        configureClient?.Invoke(httpBuilder);

        services.TryAddSingleton<ISitecoreGraphQLFactory>(sp => new SitecoreGraphQLFactory(
            sp.GetRequiredService<ISitecoreTokenCoordinator>(),
            sp.GetRequiredService<IOptionsMonitor<SitecoreGraphQLOptions>>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetService<ILogger<SitecoreGraphQLFactory>>()));

        return services;
    }

    private static void ConfigureResiliencePipeline(
        IHttpClientBuilder httpBuilder,
        Action<HttpStandardResilienceOptions> configureResilience)
    {
        var resilienceBuilder = httpBuilder.AddStandardResilienceHandler();

        resilienceBuilder.Services
            .AddOptions<HttpStandardResilienceOptions>(resilienceBuilder.PipelineName)
            .Configure<IOptionsMonitor<SitecoreGraphQLOptions>>((options, graphQlOptionsMonitor) =>
            {
                var defaultShouldHandle = options.Retry.ShouldHandle;
                options.Retry.ShouldHandle = async args =>
                {
                    var request = args.Context.GetRequestMessage();
                    if (request is not null)
                    {
                        var method = request.Method;
                        if (method == HttpMethod.Put ||
                            method == HttpMethod.Patch ||
                            method == HttpMethod.Delete ||
                            method == HttpMethod.Connect)
                        {
                            return false;
                        }

                        if (method == HttpMethod.Post)
                        {
                            var allowPost = request.Options.TryGetValue(CredentialContext.OptionsKey, out var ctx)
                                ? ctx.AllowPostRetries
                                : graphQlOptionsMonitor.CurrentValue.AllowPostRetries;
                            if (!allowPost)
                            {
                                return false;
                            }
                        }
                    }

                    return await defaultShouldHandle(args).ConfigureAwait(false);
                };

                configureResilience(options);
            });

#pragma warning disable EXTEXP0001 // SelectPipelineBy is used to partition circuit breakers per (authority, client)
        resilienceBuilder.SelectPipelineBy(sp =>
        {
            var optionsMonitor = sp.GetRequiredService<IOptionsMonitor<SitecoreGraphQLOptions>>();
            var knownDynamicPartitions = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

            return request =>
            {
                var authority = EndpointUriValidator.GetRedactedAuthority(request.RequestUri);
                if (!request.Options.TryGetValue(CredentialContext.OptionsKey, out var ctx))
                {
                    return $"{authority}|default";
                }

                var partitionKey = ctx.ResiliencePartitionKey;
                if (ctx.IsExplicitClient && !knownDynamicPartitions.ContainsKey(partitionKey))
                {
                    var maxDynamicPartitions = Math.Max(1, optionsMonitor.CurrentValue.MaxCachedClients);
                    if (knownDynamicPartitions.Count >= maxDynamicPartitions)
                    {
                        return $"{authority}|explicit:shared";
                    }

                    knownDynamicPartitions.TryAdd(partitionKey, 0);
                }

                return $"{authority}|{partitionKey}";
            };
        });
#pragma warning restore EXTEXP0001
    }

    private static void CopyOptions(SitecoreGraphQLOptions source, SitecoreGraphQLOptions target)
    {
        target.Endpoint = source.Endpoint;
        target.ClientId = source.ClientId;
        target.ClientSecret = source.ClientSecret;
        target.EnableUnauthorizedRefresh = source.EnableUnauthorizedRefresh;
        target.MaxUnauthorizedRetries = source.MaxUnauthorizedRetries;
        target.EnableInternalLoggingSetup = source.EnableInternalLoggingSetup;
        target.AllowInsecureHttp = source.AllowInsecureHttp;
        target.AllowPostRetries = source.AllowPostRetries;
        target.MaxCachedClients = source.MaxCachedClients;
        target.MaxRetryBufferBytes = source.MaxRetryBufferBytes;

        target.Clients.Clear();
        if (source.Clients is null)
        {
            return;
        }

        foreach (var (name, clientOpts) in source.Clients)
        {
            if (clientOpts is null)
            {
                continue;
            }

            target.Clients[name] = new SitecoreGraphQLClientOptions
            {
                Endpoint = clientOpts.Endpoint,
                ClientId = clientOpts.ClientId,
                ClientSecret = clientOpts.ClientSecret,
                AllowInsecureHttp = clientOpts.AllowInsecureHttp,
                AllowPostRetries = clientOpts.AllowPostRetries
            };
        }
    }
}
