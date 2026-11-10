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

        var optionsBuilder = services.AddOptions<SitecoreGraphQLOptions>();
        if (configure is not null)
        {
            var gate = new object();
            SitecoreGraphQLOptions? cachedBefore = null;
            SitecoreGraphQLOptions? cachedAfter = null;
            var cachedAssignedMask = GraphQLOptionsAssignedMask.None;
            Dictionary<string, GraphQLClientOptionsAssignedMask>? cachedClientMasks = null;

            optionsBuilder.Configure(target =>
            {
                lock (gate)
                {
                    if (cachedAfter is null)
                    {
                        var before = CloneOptions(target);
                        var initialClients = CaptureClientReferences(target.Clients);
                        target.ResetAssignmentTracking();
                        configure(target);
                        cachedBefore = before;
                        cachedAssignedMask = target.AssignedMask;
                        cachedClientMasks = CaptureClientMasks(target.Clients, initialClients);
                        cachedAfter = CloneOptions(target);
                        return;
                    }

                    ApplyConfiguredChanges(cachedBefore!, cachedAfter, cachedAssignedMask, cachedClientMasks, target);
                }
            });
        }

        optionsBuilder.ValidateOnStart();

        return AddCore(services, enableLogging: true, configureResilience, configureClient);
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
            var partitionGate = new object();

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
                    lock (partitionGate)
                    {
                        if (!knownDynamicPartitions.ContainsKey(partitionKey))
                        {
                            if (knownDynamicPartitions.Count >= maxDynamicPartitions)
                            {
                                return $"{authority}|explicit:shared";
                            }

                            knownDynamicPartitions.TryAdd(partitionKey, 0);
                        }
                    }
                }

                return $"{authority}|{partitionKey}";
            };
        });
#pragma warning restore EXTEXP0001
    }

    private static SitecoreGraphQLOptions CloneOptions(SitecoreGraphQLOptions source)
    {
        var clone = new SitecoreGraphQLOptions
        {
            Endpoint = source.Endpoint,
            ClientId = source.ClientId,
            ClientSecret = source.ClientSecret,
            EnableUnauthorizedRefresh = source.EnableUnauthorizedRefresh,
            MaxUnauthorizedRetries = source.MaxUnauthorizedRetries,
            EnableInternalLoggingSetup = source.EnableInternalLoggingSetup,
            AllowInsecureHttp = source.AllowInsecureHttp,
            AllowPostRetries = source.AllowPostRetries,
            MaxCachedClients = source.MaxCachedClients,
            MaxRetryBufferBytes = source.MaxRetryBufferBytes,
            Clients = source.Clients is null
                ? null!
                : new Dictionary<string, SitecoreGraphQLClientOptions>(source.Clients.Comparer)
        };

        if (source.Clients is not null)
        {
            foreach (var (name, clientOpts) in source.Clients)
            {
                clone.Clients[name] = clientOpts is null ? null! : CloneClientOptions(clientOpts);
            }
        }

        return clone;
    }

    private static SitecoreGraphQLClientOptions CloneClientOptions(SitecoreGraphQLClientOptions source)
        => new()
        {
            Endpoint = source.Endpoint,
            ClientId = source.ClientId,
            ClientSecret = source.ClientSecret,
            AllowInsecureHttp = source.AllowInsecureHttp,
            AllowPostRetries = source.AllowPostRetries
        };

    private static Dictionary<string, SitecoreGraphQLClientOptions?>? CaptureClientReferences(
        Dictionary<string, SitecoreGraphQLClientOptions>? clients)
    {
        if (clients is null)
        {
            return null;
        }

        var snapshot = new Dictionary<string, SitecoreGraphQLClientOptions?>(clients.Count, clients.Comparer);
        foreach (var (name, clientOpts) in clients)
        {
            snapshot[name] = clientOpts;
        }

        return snapshot;
    }

    private static Dictionary<string, GraphQLClientOptionsAssignedMask>? CaptureClientMasks(
        Dictionary<string, SitecoreGraphQLClientOptions>? liveClients,
        Dictionary<string, SitecoreGraphQLClientOptions?>? initialClients)
    {
        if (liveClients is null)
        {
            return null;
        }

        var masks = new Dictionary<string, GraphQLClientOptionsAssignedMask>(liveClients.Count, liveClients.Comparer);
        foreach (var (name, clientOpts) in liveClients)
        {
            if (clientOpts is null)
            {
                continue;
            }

            if (initialClients is not null &&
                initialClients.TryGetValue(name, out var initialRef) &&
                ReferenceEquals(initialRef, clientOpts))
            {
                masks[name] = clientOpts.AssignedMask;
            }
            else
            {
                masks[name] = GraphQLClientOptionsAssignedMask.All;
            }
        }

        return masks;
    }

    private static void ApplyConfiguredChanges(
        SitecoreGraphQLOptions before,
        SitecoreGraphQLOptions after,
        GraphQLOptionsAssignedMask assignedMask,
        Dictionary<string, GraphQLClientOptionsAssignedMask>? clientMasks,
        SitecoreGraphQLOptions target)
    {
        if (assignedMask.HasFlag(GraphQLOptionsAssignedMask.Endpoint) ||
            !string.Equals(before.Endpoint, after.Endpoint, StringComparison.Ordinal))
        {
            target.Endpoint = after.Endpoint;
        }

        if (assignedMask.HasFlag(GraphQLOptionsAssignedMask.ClientId) ||
            !string.Equals(before.ClientId, after.ClientId, StringComparison.Ordinal))
        {
            target.ClientId = after.ClientId;
        }

        if (assignedMask.HasFlag(GraphQLOptionsAssignedMask.ClientSecret) ||
            !string.Equals(before.ClientSecret, after.ClientSecret, StringComparison.Ordinal))
        {
            target.ClientSecret = after.ClientSecret;
        }

        if (assignedMask.HasFlag(GraphQLOptionsAssignedMask.EnableUnauthorizedRefresh) ||
            before.EnableUnauthorizedRefresh != after.EnableUnauthorizedRefresh)
        {
            target.EnableUnauthorizedRefresh = after.EnableUnauthorizedRefresh;
        }

        if (assignedMask.HasFlag(GraphQLOptionsAssignedMask.MaxUnauthorizedRetries) ||
            before.MaxUnauthorizedRetries != after.MaxUnauthorizedRetries)
        {
            target.MaxUnauthorizedRetries = after.MaxUnauthorizedRetries;
        }

        if (assignedMask.HasFlag(GraphQLOptionsAssignedMask.EnableInternalLoggingSetup) ||
            before.EnableInternalLoggingSetup != after.EnableInternalLoggingSetup)
        {
            target.EnableInternalLoggingSetup = after.EnableInternalLoggingSetup;
        }

        if (assignedMask.HasFlag(GraphQLOptionsAssignedMask.AllowInsecureHttp) ||
            before.AllowInsecureHttp != after.AllowInsecureHttp)
        {
            target.AllowInsecureHttp = after.AllowInsecureHttp;
        }

        if (assignedMask.HasFlag(GraphQLOptionsAssignedMask.AllowPostRetries) ||
            before.AllowPostRetries != after.AllowPostRetries)
        {
            target.AllowPostRetries = after.AllowPostRetries;
        }

        if (assignedMask.HasFlag(GraphQLOptionsAssignedMask.MaxCachedClients) ||
            before.MaxCachedClients != after.MaxCachedClients)
        {
            target.MaxCachedClients = after.MaxCachedClients;
        }

        if (assignedMask.HasFlag(GraphQLOptionsAssignedMask.MaxRetryBufferBytes) ||
            before.MaxRetryBufferBytes != after.MaxRetryBufferBytes)
        {
            target.MaxRetryBufferBytes = after.MaxRetryBufferBytes;
        }

        if (after.Clients is null)
        {
            target.Clients = null!;
            return;
        }

        if (assignedMask.HasFlag(GraphQLOptionsAssignedMask.Clients))
        {
            var replaced = new Dictionary<string, SitecoreGraphQLClientOptions>(after.Clients.Count, after.Clients.Comparer);
            foreach (var (name, afterClient) in after.Clients)
            {
                replaced[name] = afterClient is null ? null! : CloneClientOptions(afterClient);
            }

            target.Clients = replaced;
            return;
        }

        if (target.Clients is null)
        {
            target.Clients = new Dictionary<string, SitecoreGraphQLClientOptions>(after.Clients.Comparer);
        }
        else if (!Equals(target.Clients.Comparer, after.Clients.Comparer))
        {
            var rebuilt = new Dictionary<string, SitecoreGraphQLClientOptions>(target.Clients.Count, after.Clients.Comparer);
            foreach (var (name, existingClient) in target.Clients)
            {
                rebuilt[name] = existingClient;
            }

            target.Clients = rebuilt;
        }

        if (before.Clients is not null)
        {
            foreach (var (name, _) in before.Clients)
            {
                if (!after.Clients.ContainsKey(name))
                {
                    target.Clients.Remove(name);
                }
            }
        }

        foreach (var (name, afterClient) in after.Clients)
        {
            if (afterClient is null)
            {
                target.Clients[name] = null!;
                continue;
            }

            var clientMask = clientMasks is not null && clientMasks.TryGetValue(name, out var recordedMask)
                ? recordedMask
                : GraphQLClientOptionsAssignedMask.None;

            if (clientMask != GraphQLClientOptionsAssignedMask.All &&
                before.Clients is not null &&
                before.Clients.TryGetValue(name, out var beforeClient) &&
                beforeClient is not null &&
                target.Clients.TryGetValue(name, out var targetClient) &&
                targetClient is not null)
            {
                ApplyClientChanges(beforeClient, afterClient, clientMask, targetClient);
            }
            else
            {
                target.Clients[name] = CloneClientOptions(afterClient);
            }
        }
    }

    private static void ApplyClientChanges(
        SitecoreGraphQLClientOptions before,
        SitecoreGraphQLClientOptions after,
        GraphQLClientOptionsAssignedMask assignedMask,
        SitecoreGraphQLClientOptions target)
    {
        if (assignedMask.HasFlag(GraphQLClientOptionsAssignedMask.Endpoint) ||
            !string.Equals(before.Endpoint, after.Endpoint, StringComparison.Ordinal))
        {
            target.Endpoint = after.Endpoint;
        }

        if (assignedMask.HasFlag(GraphQLClientOptionsAssignedMask.ClientId) ||
            !string.Equals(before.ClientId, after.ClientId, StringComparison.Ordinal))
        {
            target.ClientId = after.ClientId;
        }

        if (assignedMask.HasFlag(GraphQLClientOptionsAssignedMask.ClientSecret) ||
            !string.Equals(before.ClientSecret, after.ClientSecret, StringComparison.Ordinal))
        {
            target.ClientSecret = after.ClientSecret;
        }

        if (assignedMask.HasFlag(GraphQLClientOptionsAssignedMask.AllowInsecureHttp) ||
            before.AllowInsecureHttp != after.AllowInsecureHttp)
        {
            target.AllowInsecureHttp = after.AllowInsecureHttp;
        }

        if (assignedMask.HasFlag(GraphQLClientOptionsAssignedMask.AllowPostRetries) ||
            before.AllowPostRetries != after.AllowPostRetries)
        {
            target.AllowPostRetries = after.AllowPostRetries;
        }
    }
}
