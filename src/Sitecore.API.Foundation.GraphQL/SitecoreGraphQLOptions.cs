using Sitecore.API.Foundation.GraphQL.Internal;

namespace Sitecore.API.Foundation.GraphQL;

/// <summary>
/// Options for configuring Sitecore GraphQL endpoints, security controls, client caching, and token refresh behavior.
/// </summary>
public sealed class SitecoreGraphQLOptions
{
    /// <summary>
    /// Default GraphQL endpoint used when no URL is specified in <see cref="ISitecoreGraphQLFactory.CreateClientAsync(CancellationToken)"/>.
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
    /// When <c>true</c>, the HTTP pipeline refreshes the bearer token and retries on <c>401 Unauthorized</c> responses.
    /// </summary>
    public bool EnableUnauthorizedRefresh { get; set; } = true;

    /// <summary>
    /// Maximum number of retry attempts after an initial <c>401 Unauthorized</c> response. Set to <c>0</c> to disable retries.
    /// </summary>
    public int MaxUnauthorizedRetries { get; set; } = 1;

    /// <summary>
    /// When <c>true</c> (default), dependency injection registers standard logging infrastructure if the host
    /// did not call <c>AddLogging</c>, ensuring downstream services can resolve <see cref="Microsoft.Extensions.Logging.ILogger{TCategoryName}"/>.
    /// Set to <c>false</c> to opt out.
    /// </summary>
    public bool EnableInternalLoggingSetup { get; set; } = true;

    /// <summary>
    /// When <c>true</c>, permits cleartext <c>http://</c> GraphQL endpoints on non-loopback hosts (for example internal container networks).
    /// Defaults to <c>false</c>, enforcing <c>https://</c> (or loopback <c>http://localhost</c> / <c>http://127.0.0.1</c>).
    /// </summary>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>
    /// When <c>true</c> and HTTP resilience is enabled, permits transient HTTP retries on <c>HTTP POST</c> requests by default.
    /// Defaults to <c>false</c> because GraphQL mutations use <c>HTTP POST</c> and may not be idempotent.
    /// Individual read-only named clients can override this via <see cref="SitecoreGraphQLClientOptions.AllowPostRetries"/>.
    /// </summary>
    public bool AllowPostRetries { get; set; }

    /// <summary>
    /// Maximum number of dynamic GraphQL client instances retained in the factory cache before oldest dynamic entries are evicted (FIFO).
    /// Configured default and named clients are exempt from eviction. Defaults to <c>128</c>.
    /// </summary>
    public int MaxCachedClients { get; set; } = 128;

    /// <summary>
    /// Maximum request content size in bytes pre-buffered for <c>401 Unauthorized</c> or resilience <c>POST</c> replay.
    /// Requests larger than this limit are still sent unbuffered, skipping replay on <c>401</c>. Defaults to <c>10 MB</c> (<c>10485760</c> bytes).
    /// </summary>
    public long MaxRetryBufferBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// Optional named GraphQL clients resolvable via <see cref="ISitecoreGraphQLFactory.CreateClientByNameAsync"/>.
    /// </summary>
    public Dictionary<string, SitecoreGraphQLClientOptions> Clients { get; set; } = new();

    /// <summary>
    /// Returns a string representation of the options with <see cref="ClientSecret"/> masked.
    /// </summary>
    /// <returns>A sanitized string representation of the options.</returns>
    public override string ToString()
        => $"SitecoreGraphQLOptions {{ Endpoint = {EndpointUriValidator.SanitizeForLogging(Endpoint)}, ClientId = {ClientId ?? "(unset)"}, ClientSecret = ***, EnableUnauthorizedRefresh = {EnableUnauthorizedRefresh}, MaxUnauthorizedRetries = {MaxUnauthorizedRetries}, AllowInsecureHttp = {AllowInsecureHttp}, AllowPostRetries = {AllowPostRetries}, MaxCachedClients = {MaxCachedClients}, MaxRetryBufferBytes = {MaxRetryBufferBytes}, ClientsCount = {Clients?.Count ?? 0} }}";
}

/// <summary>
/// Options for a single named GraphQL client. Any unset (<c>null</c> or whitespace) property inherits from <see cref="SitecoreGraphQLOptions"/>.
/// </summary>
public sealed class SitecoreGraphQLClientOptions
{
    /// <summary>
    /// GraphQL endpoint URL for this named client. Inherits <see cref="SitecoreGraphQLOptions.Endpoint"/> when <c>null</c> or whitespace.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// OAuth client identifier for this named client. Inherits <see cref="SitecoreGraphQLOptions.ClientId"/> when <c>null</c> or whitespace.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// OAuth client secret for this named client. Inherits <see cref="SitecoreGraphQLOptions.ClientSecret"/> when <c>null</c> or whitespace.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Optional override permitting cleartext <c>http://</c> on non-loopback hosts for this named client.
    /// Inherits <see cref="SitecoreGraphQLOptions.AllowInsecureHttp"/> when <c>null</c>.
    /// </summary>
    public bool? AllowInsecureHttp { get; set; }

    /// <summary>
    /// Optional override permitting transient HTTP resilience retries on <c>HTTP POST</c> requests for this named client
    /// (for example read-only Experience Edge delivery endpoints). Inherits <see cref="SitecoreGraphQLOptions.AllowPostRetries"/> when <c>null</c>.
    /// </summary>
    public bool? AllowPostRetries { get; set; }

    /// <summary>
    /// Returns a string representation of the named client options with <see cref="ClientSecret"/> masked.
    /// </summary>
    /// <returns>A sanitized string representation of the named client options.</returns>
    public override string ToString()
        => $"SitecoreGraphQLClientOptions {{ Endpoint = {EndpointUriValidator.SanitizeForLogging(Endpoint)}, ClientId = {ClientId ?? "(inherit)"}, ClientSecret = ***, AllowInsecureHttp = {AllowInsecureHttp?.ToString() ?? "(inherit)"}, AllowPostRetries = {AllowPostRetries?.ToString() ?? "(inherit)"} }}";
}
