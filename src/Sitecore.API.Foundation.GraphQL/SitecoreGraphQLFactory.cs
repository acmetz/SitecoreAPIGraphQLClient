using System.Collections.Concurrent;
using GraphQL.Client.Abstractions;
using GraphQL.Client.Http;
using GraphQL.Client.Serializer.Newtonsoft;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sitecore.API.Foundation.Authorization.Models;
using Sitecore.API.Foundation.GraphQL.Internal;

namespace Sitecore.API.Foundation.GraphQL;

/// <summary>
/// Thread-safe factory for creating and caching Sitecore GraphQL clients with per-client OAuth2 credential isolation.
/// </summary>
public class SitecoreGraphQLFactory : ISitecoreGraphQLFactory, IDisposable
{
    private readonly record struct CachedClientEntry(IGraphQLClient Client, Task<IGraphQLClient> CompletedTask);

    private readonly ConcurrentDictionary<ClientCacheKey, Lazy<CachedClientEntry>> _clients = new();
    private readonly Queue<(ClientCacheKey Key, Lazy<CachedClientEntry> Entry)> _dynamicEvictionQueue = new();
    private readonly object _evictionLock = new();

    private readonly ISitecoreTokenCoordinator _coordinator;
    private readonly IOptionsMonitor<SitecoreGraphQLOptions>? _optionsMonitor;
    private readonly IOptions<SitecoreGraphQLOptions>? _staticOptions;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
#pragma warning disable CS0618 // Retained for v0.9.x legacy 5-parameter constructor compatibility
    private readonly ISitecoreTokenCache? _legacyTokenCache;
#pragma warning restore CS0618

    private bool _disposed;

    /// <summary>
    /// Logical <see cref="HttpClient"/> name registered with <see cref="IHttpClientFactory"/> for Sitecore GraphQL calls.
    /// </summary>
    public const string NamedHttpClient = "SitecoreGraphQL";

    /// <summary>
    /// Initializes a new instance of the <see cref="SitecoreGraphQLFactory"/> class.
    /// </summary>
    /// <param name="tokenService">The Sitecore authentication token service.</param>
    /// <param name="options">Configured Sitecore GraphQL options.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="tokenCache">The obsolete token cache adapter.</param>
    /// <param name="logger">Optional logger instance.</param>
#pragma warning disable CS0618 // Preserves exact v0.9.x public constructor signature
    public SitecoreGraphQLFactory(
        Authorization.Abstractions.ISitecoreTokenService tokenService,
        IOptions<SitecoreGraphQLOptions> options,
        IHttpClientFactory httpClientFactory,
        ISitecoreTokenCache tokenCache,
        ILogger<SitecoreGraphQLFactory>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(tokenService);
        _staticOptions = options ?? throw new ArgumentNullException(nameof(options));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _legacyTokenCache = tokenCache ?? throw new ArgumentNullException(nameof(tokenCache));
        _coordinator = new SitecoreTokenCoordinator(tokenService, new DefaultTokenValueAccessor());
        _logger = (ILogger?)logger ?? NullLogger<SitecoreGraphQLFactory>.Instance;
    }
#pragma warning restore CS0618

    internal SitecoreGraphQLFactory(
        ISitecoreTokenCoordinator coordinator,
        IOptionsMonitor<SitecoreGraphQLOptions> optionsMonitor,
        IHttpClientFactory httpClientFactory,
        ILogger<SitecoreGraphQLFactory>? logger = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = (ILogger?)logger ?? NullLogger<SitecoreGraphQLFactory>.Instance;
    }

    private SitecoreGraphQLOptions CurrentOptions
        => (_optionsMonitor?.CurrentValue ?? _staticOptions?.Value)
           ?? throw new InvalidOperationException("SitecoreGraphQLOptions are not configured.");

    /// <inheritdoc />
    public Task<IGraphQLClient> CreateClientAsync(string url, string clientId, string clientSecret, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);

        var opts = CurrentOptions;
        var normalizedUrl = EndpointUriValidator.ValidateAndNormalizeEndpoint(
            url,
            opts.AllowInsecureHttp,
            nameof(url),
            throwInvalidOperation: false);
        var credentials = new SitecoreAuthClientCredentials(clientId, clientSecret);

        return GetOrCreateClientTask(
            normalizedUrl,
            url,
            credentials,
            clientName: null,
            isExplicitClient: true,
            isConfiguredClient: false,
            allowPostRetries: opts.AllowPostRetries,
            opts.MaxCachedClients);
    }

    /// <summary>
    /// Creates or reuses a cached GraphQL client for the specified URL using explicit client credentials.
    /// </summary>
    /// <param name="url">The GraphQL endpoint URL.</param>
    /// <param name="clientId">The OAuth client identifier.</param>
    /// <param name="clientSecret">The OAuth client secret.</param>
    /// <returns>An <see cref="IGraphQLClient"/> configured for the URL.</returns>
    public Task<IGraphQLClient> CreateClientAsync(string url, string clientId, string clientSecret)
        => CreateClientAsync(url, clientId, clientSecret, CancellationToken.None);

    /// <inheritdoc />
    public Task<IGraphQLClient> CreateClientAsync(string url, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var opts = CurrentOptions;
        var normalizedUrl = EndpointUriValidator.ValidateAndNormalizeEndpoint(
            url,
            opts.AllowInsecureHttp,
            nameof(url),
            throwInvalidOperation: false);

        if (!opts.TryBuildCredentials(out var credentials))
        {
            Log.MissingDefaultCredentialsForClient(_logger, EndpointUriValidator.SanitizeForLogging(url));
            throw new InvalidOperationException("ClientId and ClientSecret must be configured in options when not provided explicitly.");
        }

        var isDefaultEndpoint = !string.IsNullOrWhiteSpace(opts.Endpoint) &&
            EndpointUriValidator.TryValidateEndpoint(opts.Endpoint!, opts.AllowInsecureHttp, out var normalizedDefault, out _) &&
            string.Equals(normalizedUrl, normalizedDefault, StringComparison.Ordinal);

        return GetOrCreateClientTask(
            normalizedUrl,
            url,
            credentials,
            clientName: null,
            isExplicitClient: false,
            isConfiguredClient: isDefaultEndpoint,
            allowPostRetries: opts.AllowPostRetries,
            opts.MaxCachedClients);
    }

    /// <summary>
    /// Creates or reuses a cached GraphQL client for the specified URL using credentials from options.
    /// </summary>
    /// <param name="url">The trusted GraphQL endpoint URL.</param>
    /// <returns>An <see cref="IGraphQLClient"/> configured for the URL.</returns>
    public Task<IGraphQLClient> CreateClientAsync(string url)
        => CreateClientAsync(url, CancellationToken.None);

    /// <inheritdoc />
    public Task<IGraphQLClient> CreateClientAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        var opts = CurrentOptions;
        if (string.IsNullOrWhiteSpace(opts.Endpoint))
        {
            Log.MissingDefaultEndpoint(_logger);
            throw new InvalidOperationException("Endpoint must be configured in options.");
        }

        var normalizedUrl = EndpointUriValidator.ValidateAndNormalizeEndpoint(
            opts.Endpoint,
            opts.AllowInsecureHttp,
            nameof(opts.Endpoint),
            throwInvalidOperation: true);

        if (!opts.TryBuildCredentials(out var credentials))
        {
            Log.MissingDefaultCredentialsForClient(_logger, EndpointUriValidator.SanitizeForLogging(opts.Endpoint));
            throw new InvalidOperationException("ClientId and ClientSecret must be configured in options when not provided explicitly.");
        }

        return GetOrCreateClientTask(
            normalizedUrl,
            opts.Endpoint,
            credentials,
            clientName: null,
            isExplicitClient: false,
            isConfiguredClient: true,
            allowPostRetries: opts.AllowPostRetries,
            opts.MaxCachedClients);
    }

    /// <summary>
    /// Creates or reuses a cached GraphQL client using the default endpoint and credentials from options.
    /// </summary>
    /// <returns>An <see cref="IGraphQLClient"/> configured for the default endpoint.</returns>
    public Task<IGraphQLClient> CreateClientAsync()
        => CreateClientAsync(CancellationToken.None);

    /// <inheritdoc />
    public Task<IGraphQLClient> CreateClientByNameAsync(string clientName, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);

        var opts = CurrentOptions;
        var (normalizedUrl, rawUrl, credentials, allowPostRetries) = ResolveNamedClientOrThrow(opts, clientName);

        return GetOrCreateClientTask(
            normalizedUrl,
            rawUrl,
            credentials,
            clientName,
            isExplicitClient: false,
            isConfiguredClient: true,
            allowPostRetries,
            opts.MaxCachedClients);
    }

    /// <inheritdoc />
    public async Task<bool> RefreshTokenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        var opts = CurrentOptions;

        if (_legacyTokenCache is not null && (opts.Clients is null || opts.Clients.Count == 0))
        {
#pragma warning disable CS0618
            var legacyToken = await _legacyTokenCache.ForceRefreshAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore CS0618
            var legacyRefreshed = !string.IsNullOrWhiteSpace(legacyToken);
            Log.ManualRefreshCompleted(_logger, legacyRefreshed ? "succeeded" : "failed", legacyRefreshed ? 1 : 0, 1);
            return legacyRefreshed;
        }

        var seenCredentials = new HashSet<(string ClientId, string ClientSecret)>();
        var targets = new List<(SitecoreAuthClientCredentials Credentials, string? ClientName)>();

        if (opts.TryBuildCredentials(out var defaultCredentials) &&
            seenCredentials.Add((defaultCredentials.ClientId, defaultCredentials.ClientSecret)))
        {
            targets.Add((defaultCredentials, null));
        }

        if (opts.Clients is not null)
        {
            foreach (var (clientName, _) in opts.Clients)
            {
                if (opts.TryResolveNamedClient(clientName, out _, out var namedCredentials, out _, out _) &&
                    seenCredentials.Add((namedCredentials.ClientId, namedCredentials.ClientSecret)))
                {
                    targets.Add((namedCredentials, clientName));
                }
            }
        }

        if (targets.Count == 0)
        {
            Log.ManualRefreshMissingCredentials(_logger);
            return false;
        }

        var succeededCount = 0;
        var failedCount = 0;

        foreach (var (credentials, clientName) in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                string? accessToken;
                if (clientName is null && _legacyTokenCache is not null)
                {
#pragma warning disable CS0618
                    accessToken = await _legacyTokenCache.ForceRefreshAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore CS0618
                }
                else
                {
                    (_, accessToken) = await _coordinator.RefreshTokenAsync(
                        credentials,
                        failedAuthToken: null,
                        failedAccessToken: null,
                        clientName,
                        cancellationToken).ConfigureAwait(false);
                }

                if (!string.IsNullOrWhiteSpace(accessToken))
                {
                    succeededCount++;
                }
                else
                {
                    failedCount++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failedCount++;
                Log.ManualRefreshCredentialFailed(_logger, ex, credentials.ClientId);
            }
        }

        var allSucceeded = succeededCount > 0 && failedCount == 0;
        Log.ManualRefreshCompleted(_logger, allSucceeded ? "succeeded" : "failed", succeededCount, targets.Count);
        return allSucceeded;
    }

    /// <inheritdoc />
    public async Task<bool> RefreshTokenByNameAsync(string clientName, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);

        var opts = CurrentOptions;
        var (_, _, credentials, _) = ResolveNamedClientOrThrow(opts, clientName);

        try
        {
            var (_, accessToken) = await _coordinator.RefreshTokenAsync(
                credentials,
                failedAuthToken: null,
                failedAccessToken: null,
                clientName,
                cancellationToken).ConfigureAwait(false);

            var refreshed = !string.IsNullOrWhiteSpace(accessToken);
            Log.ManualRefreshCompleted(_logger, refreshed ? "succeeded" : "failed", refreshed ? 1 : 0, 1);
            return refreshed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.ManualRefreshCredentialFailed(_logger, ex, credentials.ClientId);
            Log.ManualRefreshCompleted(_logger, "failed", 0, 1);
            return false;
        }
    }

    /// <summary>
    /// Disposes cached <see cref="IGraphQLClient"/> wrapper instances on host shutdown.
    /// </summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases managed resources held by the factory.
    /// </summary>
    /// <param name="disposing"><c>true</c> to release managed resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!disposing)
        {
            return;
        }

        lock (_evictionLock)
        {
            _dynamicEvictionQueue.Clear();
        }

        foreach (var (_, lazy) in _clients)
        {
            if (lazy.IsValueCreated && lazy.Value.Client is IDisposable disposableClient)
            {
                disposableClient.Dispose();
            }
        }

        _clients.Clear();
    }

    private Task<IGraphQLClient> GetOrCreateClientTask(
        string normalizedUrl,
        string rawUrlForSanitization,
        SitecoreAuthClientCredentials credentials,
        string? clientName,
        bool isExplicitClient,
        bool isConfiguredClient,
        bool allowPostRetries,
        int maxCachedClients)
    {
        var key = new ClientCacheKey(
            normalizedUrl,
            credentials.ClientId,
            credentials.ClientSecret,
            clientName,
            allowPostRetries);

        if (_clients.TryGetValue(key, out var existingLazy))
        {
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                Log.ReusingCachedGraphQLClient(_logger, EndpointUriValidator.SanitizeForLogging(rawUrlForSanitization), credentials.ClientId);
            }

            return existingLazy.Value.CompletedTask;
        }

        var sanitizedUrl = EndpointUriValidator.SanitizeForLogging(rawUrlForSanitization);
        var createdLazy = new Lazy<CachedClientEntry>(
            () =>
            {
                var client = CreateClientInstance(normalizedUrl, sanitizedUrl, credentials, clientName, isExplicitClient, allowPostRetries);
                return new CachedClientEntry(client, Task.FromResult(client));
            },
            LazyThreadSafetyMode.ExecutionAndPublication);

        var winningLazy = _clients.GetOrAdd(key, createdLazy);
        if (!ReferenceEquals(winningLazy, createdLazy))
        {
            Log.ReusingCachedGraphQLClient(_logger, sanitizedUrl, credentials.ClientId);
            return winningLazy.Value.CompletedTask;
        }

        Log.CreatingGraphQLClient(_logger, sanitizedUrl, credentials.ClientId);

        if (!isConfiguredClient)
        {
            TrackAndEvictDynamicClients(key, winningLazy, Math.Max(1, maxCachedClients));
        }

        return winningLazy.Value.CompletedTask;
    }

    private void TrackAndEvictDynamicClients(
        ClientCacheKey addedKey,
        Lazy<CachedClientEntry> addedEntry,
        int maxCachedClients)
    {
        lock (_evictionLock)
        {
            _dynamicEvictionQueue.Enqueue((addedKey, addedEntry));

            while (_dynamicEvictionQueue.Count > maxCachedClients)
            {
                var (oldestKey, oldestEntry) = _dynamicEvictionQueue.Dequeue();
                if (((ICollection<KeyValuePair<ClientCacheKey, Lazy<CachedClientEntry>>>)_clients)
                    .Remove(new KeyValuePair<ClientCacheKey, Lazy<CachedClientEntry>>(oldestKey, oldestEntry)))
                {
                    // Intentionally do not Dispose() the evicted socket-free wrapper so in-flight callers never hit ObjectDisposedException.
                    SitecoreGraphQLDiagnostics.ClientCacheEvictions.Add(1);
                    Log.EvictedDynamicGraphQLClient(_logger, EndpointUriValidator.SanitizeForLogging(oldestKey.NormalizedUrl), maxCachedClients);
                }
            }
        }
    }

    private IGraphQLClient CreateClientInstance(
        string normalizedUrl,
        string sanitizedUrl,
        SitecoreAuthClientCredentials credentials,
        string? clientName,
        bool isExplicitClient,
        bool allowPostRetries)
    {
        var credentialContext = new CredentialContext(
            credentials,
            clientName,
            sanitizedUrl,
            isExplicitClient,
            allowPostRetries);

#pragma warning disable CA2000 // GraphQLHttpClient(options, serializer) takes ownership of options.HttpMessageHandler and disposes it when the client is disposed.
        var forwardingHandler = new CredentialForwardingHandler(_httpClientFactory, NamedHttpClient, credentialContext);
#pragma warning restore CA2000

        var clientOptions = new GraphQLHttpClientOptions
        {
            EndPoint = new Uri(normalizedUrl),
            HttpMessageHandler = forwardingHandler
        };

        var client = new GraphQLHttpClient(clientOptions, new NewtonsoftJsonSerializer());
        client.HttpClient.Timeout = Timeout.InfiniteTimeSpan;
        return client;
    }

    private static (string NormalizedUrl, string RawUrl, SitecoreAuthClientCredentials Credentials, bool AllowPostRetries) ResolveNamedClientOrThrow(
        SitecoreGraphQLOptions opts,
        string clientName)
    {
        if (opts.Clients is null || !opts.Clients.TryGetValue(clientName, out var clientOpts) || clientOpts is null)
        {
            throw new InvalidOperationException($"No client named '{clientName}' configured.");
        }

        var rawUrl = !string.IsNullOrWhiteSpace(clientOpts.Endpoint) ? clientOpts.Endpoint : opts.Endpoint;
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            throw new InvalidOperationException($"Endpoint must be configured for named client '{clientName}'.");
        }

        var rawId = !string.IsNullOrWhiteSpace(clientOpts.ClientId) ? clientOpts.ClientId : opts.ClientId;
        if (string.IsNullOrWhiteSpace(rawId))
        {
            throw new InvalidOperationException($"ClientId must be configured for named client '{clientName}'.");
        }

        var rawSecret = !string.IsNullOrWhiteSpace(clientOpts.ClientSecret) ? clientOpts.ClientSecret : opts.ClientSecret;
        if (string.IsNullOrWhiteSpace(rawSecret))
        {
            throw new InvalidOperationException($"ClientSecret must be configured for named client '{clientName}'.");
        }

        var allowInsecureHttp = clientOpts.AllowInsecureHttp ?? opts.AllowInsecureHttp;
        var allowPostRetries = clientOpts.AllowPostRetries ?? opts.AllowPostRetries;

        var normalizedUrl = EndpointUriValidator.ValidateAndNormalizeEndpoint(
            rawUrl,
            allowInsecureHttp,
            nameof(clientName),
            throwInvalidOperation: true);

        return (normalizedUrl, rawUrl, new SitecoreAuthClientCredentials(rawId, rawSecret), allowPostRetries);
    }
}
