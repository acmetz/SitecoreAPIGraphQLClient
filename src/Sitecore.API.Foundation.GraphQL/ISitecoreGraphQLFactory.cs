using GraphQL.Client.Abstractions;

namespace Sitecore.API.Foundation.GraphQL;

/// <summary>
/// Factory for creating configured GraphQL clients for Sitecore endpoints.
/// </summary>
/// <remarks>
/// Returned <see cref="IGraphQLClient"/> instances are cached and shared by the factory; callers should not dispose
/// shared instances manually. When dynamic secret rotation via <c>IOptionsMonitor</c> is used, resolve clients from
/// the factory per operation rather than storing a returned <see cref="IGraphQLClient"/> reference indefinitely.
/// </remarks>
public interface ISitecoreGraphQLFactory
{
    /// <summary>
    /// Creates or reuses a cached GraphQL client for the specified URL using explicit client credentials.
    /// </summary>
    /// <param name="url">The GraphQL endpoint URL.</param>
    /// <param name="clientId">The OAuth client identifier.</param>
    /// <param name="clientSecret">The OAuth client secret.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>An <see cref="IGraphQLClient"/> configured for the URL and credentials.</returns>
    Task<IGraphQLClient> CreateClientAsync(string url, string clientId, string clientSecret, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or reuses a cached GraphQL client for the specified URL using default credentials from options.
    /// </summary>
    /// <remarks>
    /// Because this overload attaches configured default <c>ClientId</c>/<c>ClientSecret</c> bearer tokens to <paramref name="url"/>,
    /// pass only trusted Sitecore endpoint URLs—never untrusted user-supplied URLs.
    /// </remarks>
    /// <param name="url">The trusted Sitecore GraphQL endpoint URL.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>An <see cref="IGraphQLClient"/> configured for the URL.</returns>
    Task<IGraphQLClient> CreateClientAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or reuses a cached GraphQL client using the default endpoint and credentials from options.
    /// </summary>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>An <see cref="IGraphQLClient"/> configured for the default endpoint.</returns>
    Task<IGraphQLClient> CreateClientAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or reuses a cached GraphQL client for a named configuration from options (<c>Sitecore:GraphQL:Clients:&lt;name&gt;</c>).
    /// </summary>
    /// <param name="clientName">The configured client name.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>An <see cref="IGraphQLClient"/> based on the named client options.</returns>
    Task<IGraphQLClient> CreateClientByNameAsync(string clientName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forces a refresh of cached Sitecore access tokens across all configured default and named client credentials.
    /// </summary>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns><c>true</c> if at least one configured credential set refreshed a non-empty token and none failed; otherwise <c>false</c>.</returns>
    Task<bool> RefreshTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Forces a refresh of the cached Sitecore access token for a specific configured named client.
    /// </summary>
    /// <param name="clientName">The configured client name.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns><c>true</c> if a non-empty token was acquired for the named client; otherwise <c>false</c>.</returns>
    Task<bool> RefreshTokenByNameAsync(string clientName, CancellationToken cancellationToken = default)
        => RefreshTokenAsync(cancellationToken);
}
