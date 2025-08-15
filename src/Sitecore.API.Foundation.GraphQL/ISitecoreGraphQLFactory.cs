using GraphQL.Client.Abstractions;

namespace Sitecore.API.Foundation.GraphQL;

/// <summary>
/// Factory for creating configured GraphQL clients for Sitecore endpoints.
/// </summary>
public interface ISitecoreGraphQLFactory
{
    /// <summary>
    /// Creates or reuses a cached GraphQL client for the specified URL using explicit client credentials.
    /// </summary>
    /// <param name="url">The GraphQL endpoint URL.</param>
    /// <param name="clientId">The OAuth client identifier.</param>
    /// <param name="clientSecret">The OAuth client secret.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>An <see cref="IGraphQLClient"/> configured for the URL.</returns>
    Task<IGraphQLClient> CreateClientAsync(string url, string clientId, string clientSecret, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or reuses a cached GraphQL client for the specified URL using credentials from options.
    /// </summary>
    /// <param name="url">The GraphQL endpoint URL.</param>
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
    /// Creates or reuses a cached GraphQL client for a named configuration from options (Sitecore:GraphQL:Clients:name).
    /// </summary>
    /// <param name="clientName">The configured client name.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>An <see cref="IGraphQLClient"/> based on the named client options.</returns>
    Task<IGraphQLClient> CreateClientByNameAsync(string clientName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forces a refresh of the cached Sitecore access token used by the HttpClient pipeline.
    /// </summary>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns><c>true</c> if a non-empty token was acquired; otherwise <c>false</c>.</returns>
    Task<bool> RefreshTokenAsync(CancellationToken cancellationToken = default);
}
