namespace Sitecore.API.Foundation.GraphQL;

/// <summary>
/// Abstraction for a shared token cache used to get or refresh the current Sitecore access token.
/// </summary>
[Obsolete("ISitecoreTokenCache in Sitecore.API.Foundation.GraphQL is obsolete and will be removed in v2.0.0. Token caching is now delegated directly to ISitecoreTokenService in Sitecore.API.Foundation.Authorization.")]
public interface ISitecoreTokenCache
{
    /// <summary>
    /// Gets the currently cached raw access token value if available.
    /// </summary>
    string? CurrentToken { get; }

    /// <summary>
    /// Gets a token, refreshing it if none is cached.
    /// </summary>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>The current or refreshed token value; may be null or empty if not available.</returns>
    Task<string?> GetOrRefreshAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Forces a refresh of the token regardless of cache state.
    /// </summary>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>The refreshed token value; may be null or empty if not available.</returns>
    Task<string?> ForceRefreshAsync(CancellationToken cancellationToken);
}
