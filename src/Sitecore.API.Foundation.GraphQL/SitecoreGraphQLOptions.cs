namespace Sitecore.API.Foundation.GraphQL;

/// <summary>
/// Options for configuring Sitecore GraphQL endpoints and token refresh behavior.
/// </summary>
public sealed class SitecoreGraphQLOptions
{
    /// <summary>
    /// Default GraphQL endpoint used when no URL is specified in CreateClientAsync.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Default OAuth client identifier used to acquire tokens.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Default OAuth client secret used to acquire tokens.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// When true, the handler will refresh the token and retry on 401 Unauthorized responses.
    /// </summary>
    public bool EnableUnauthorizedRefresh { get; set; } = true;

    /// <summary>
    /// The maximum number of retries after the initial 401 Unauthorized response. 0 disables retries.
    /// </summary>
    public int MaxUnauthorizedRetries { get; set; } = 1;

    /// <summary>
    /// Optional named clients that can be created from the factory by name.
    /// </summary>
    public Dictionary<string, SitecoreGraphQLClientOptions> Clients { get; set; } = new();
}

/// <summary>
/// Options for a single named GraphQL client.
/// </summary>
public sealed class SitecoreGraphQLClientOptions
{
    public string? Endpoint { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}
