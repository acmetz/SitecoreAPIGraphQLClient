using GraphQL.Client.Abstractions;
using GraphQL.Client.Http;
using GraphQL.Client.Serializer.Newtonsoft;
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace Sitecore.API.Foundation.GraphQL;

/// <summary>
/// Thread-safe factory for creating and caching Sitecore GraphQL clients.
/// </summary>
public class SitecoreGraphQLFactory : ISitecoreGraphQLFactory
{
    private readonly ConcurrentDictionary<string, Lazy<Task<IGraphQLClient>>> _clients = new();
    private readonly IOptions<SitecoreGraphQLOptions> _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SitecoreGraphQLFactory>? _logger;
    private readonly ISitecoreTokenCache _tokenCache;

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
    /// <param name="tokenCache">The token cache adapter.</param>
    /// <param name="logger">Optional logger instance.</param>
    public SitecoreGraphQLFactory(
        Authorization.Abstractions.ISitecoreTokenService tokenService,
        IOptions<SitecoreGraphQLOptions> options,
        IHttpClientFactory httpClientFactory,
        ISitecoreTokenCache tokenCache,
        ILogger<SitecoreGraphQLFactory>? logger = null)
    {
        _ = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _tokenCache = tokenCache ?? throw new ArgumentNullException(nameof(tokenCache));
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IGraphQLClient> CreateClientAsync(string url, string clientId, string clientSecret, CancellationToken cancellationToken = default)
    {
        ValidateRequired(url, nameof(url));
        ValidateRequired(clientId, nameof(clientId));
        ValidateRequired(clientSecret, nameof(clientSecret));

        var key = GetCacheKey(url, clientId);
        var lazy = _clients.GetOrAdd(key, _ =>
        {
            _logger?.LogDebug("Creating new GraphQL client for {Url} and clientId {ClientId}", url, clientId);
            return new Lazy<Task<IGraphQLClient>>(() => CreateClientInternalAsync(url));
        });

        if (_clients.ContainsKey(key))
        {
            _logger?.LogTrace("Reusing cached GraphQL client for {Url} and clientId {ClientId}", url, clientId);
        }

        var client = await lazy.Value.ConfigureAwait(false);
        return client;
    }

    /// <summary>
    /// Creates or reuses a cached GraphQL client for the specified URL using explicit client credentials.
    /// </summary>
    /// <param name="url">The GraphQL endpoint URL.</param>
    /// <param name="clientId">The OAuth client identifier.</param>
    /// <param name="clientSecret">The OAuth client secret.</param>
    /// <returns>An <see cref="IGraphQLClient"/> configured for the URL.</returns>
    public async Task<IGraphQLClient> CreateClientAsync(string url, string clientId, string clientSecret)
        => await CreateClientAsync(url, clientId, clientSecret, CancellationToken.None).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IGraphQLClient> CreateClientAsync(string url, CancellationToken cancellationToken = default)
    {
        var (id, secret) = GetDefaultCredentialsOrThrow(url);
        return await CreateClientAsync(url, id, secret, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates or reuses a cached GraphQL client for the specified URL using credentials from options.
    /// </summary>
    /// <param name="url">The GraphQL endpoint URL.</param>
    /// <returns>An <see cref="IGraphQLClient"/> configured for the URL.</returns>
    public async Task<IGraphQLClient> CreateClientAsync(string url)
        => await CreateClientAsync(url, CancellationToken.None).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IGraphQLClient> CreateClientAsync(CancellationToken cancellationToken = default)
    {
        var endpoint = GetDefaultEndpointOrThrow();
        return await CreateClientAsync(endpoint, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates or reuses a cached GraphQL client using the default endpoint and credentials from options.
    /// </summary>
    /// <returns>An <see cref="IGraphQLClient"/> configured for the default endpoint.</returns>
    public async Task<IGraphQLClient> CreateClientAsync()
        => await CreateClientAsync(CancellationToken.None).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IGraphQLClient> CreateClientByNameAsync(string clientName, CancellationToken cancellationToken = default)
    {
        ValidateRequired(clientName, nameof(clientName));
        var (url, id, secret) = GetNamedOrDefaultClientOrThrow(clientName);
        return await CreateClientAsync(url, id, secret, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> RefreshTokenAsync(CancellationToken cancellationToken = default)
    {
        var token = await _tokenCache.ForceRefreshAsync(cancellationToken).ConfigureAwait(false);
        var refreshed = !string.IsNullOrWhiteSpace(token);
        _logger?.LogInformation("Manual token refresh {Result}", refreshed ? "succeeded" : "failed");
        return refreshed;
    }

    private Task<IGraphQLClient> CreateClientInternalAsync(string url)
    {
        var httpClient = _httpClientFactory.CreateClient(NamedHttpClient);
        var options = new GraphQLHttpClientOptions { EndPoint = new Uri(url) };

        // Use Newtonsoft.Json serializer
        var serializer = new NewtonsoftJsonSerializer();

        IGraphQLClient client = new GraphQLHttpClient(options, serializer, httpClient);
        return Task.FromResult(client);
    }

    private static string GetCacheKey(string url, string clientId) => $"{url}::{clientId}";

    private static void ValidateRequired(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Value cannot be null or whitespace.", paramName);
    }

    private (string id, string secret) GetDefaultCredentialsOrThrow(string urlForLog)
    {
        var opts = _options.Value ?? throw new InvalidOperationException("Options not configured.");
        if (string.IsNullOrWhiteSpace(opts.ClientId) || string.IsNullOrWhiteSpace(opts.ClientSecret))
        {
            _logger?.LogError("ClientId/ClientSecret not configured in options when attempting to create client for {Url}", urlForLog);
            throw new InvalidOperationException("ClientId and ClientSecret must be configured in options when not provided explicitly.");
        }
        return (opts.ClientId!, opts.ClientSecret!);
    }

    private string GetDefaultEndpointOrThrow()
    {
        var opts = _options.Value ?? throw new InvalidOperationException("Options not configured.");
        if (string.IsNullOrWhiteSpace(opts.Endpoint))
        {
            _logger?.LogError("Endpoint not configured in options when attempting to create default GraphQL client");
            throw new InvalidOperationException("Endpoint must be configured in options.");
        }
        return opts.Endpoint!;
    }

    private (string url, string id, string secret) GetNamedOrDefaultClientOrThrow(string clientName)
    {
        var opts = _options.Value;
        if (!opts.Clients.TryGetValue(clientName, out var clientOpts))
        {
            throw new InvalidOperationException($"No client named '{clientName}' configured.");
        }
        var url = clientOpts.Endpoint ?? opts.Endpoint ?? throw new InvalidOperationException("Endpoint must be configured.");
        var id = clientOpts.ClientId ?? opts.ClientId ?? throw new InvalidOperationException("ClientId must be configured.");
        var secret = clientOpts.ClientSecret ?? opts.ClientSecret ?? throw new InvalidOperationException("ClientSecret must be configured.");
        return (url, id, secret);
    }
}
