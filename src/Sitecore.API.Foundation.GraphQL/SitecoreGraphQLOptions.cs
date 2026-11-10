using Sitecore.API.Foundation.GraphQL.Internal;

namespace Sitecore.API.Foundation.GraphQL;

[Flags]
internal enum GraphQLOptionsAssignedMask : uint
{
    None = 0,
    Endpoint = 1u << 0,
    ClientId = 1u << 1,
    ClientSecret = 1u << 2,
    EnableUnauthorizedRefresh = 1u << 3,
    MaxUnauthorizedRetries = 1u << 4,
    EnableInternalLoggingSetup = 1u << 5,
    AllowInsecureHttp = 1u << 6,
    AllowPostRetries = 1u << 7,
    MaxCachedClients = 1u << 8,
    MaxRetryBufferBytes = 1u << 9,
    Clients = 1u << 10
}

[Flags]
internal enum GraphQLClientOptionsAssignedMask : byte
{
    None = 0,
    Endpoint = 1 << 0,
    ClientId = 1 << 1,
    ClientSecret = 1 << 2,
    AllowInsecureHttp = 1 << 3,
    AllowPostRetries = 1 << 4,
    All = Endpoint | ClientId | ClientSecret | AllowInsecureHttp | AllowPostRetries
}

/// <summary>
/// Options for configuring Sitecore GraphQL endpoints, security controls, client caching, and token refresh behavior.
/// </summary>
public sealed class SitecoreGraphQLOptions
{
    private string? _endpoint;
    private string? _clientId;
    private string? _clientSecret;
    private bool _enableUnauthorizedRefresh = true;
    private int _maxUnauthorizedRetries = 1;
    private bool _enableInternalLoggingSetup = true;
    private bool _allowInsecureHttp;
    private bool _allowPostRetries;
    private int _maxCachedClients = 128;
    private long _maxRetryBufferBytes = 10 * 1024 * 1024;
    private Dictionary<string, SitecoreGraphQLClientOptions> _clients = new();

    internal GraphQLOptionsAssignedMask AssignedMask { get; private set; }

    /// <summary>
    /// Default GraphQL endpoint used when no URL is specified in <see cref="ISitecoreGraphQLFactory.CreateClientAsync(CancellationToken)"/>.
    /// </summary>
    public string? Endpoint
    {
        get => _endpoint;
        set
        {
            _endpoint = value;
            AssignedMask |= GraphQLOptionsAssignedMask.Endpoint;
        }
    }

    /// <summary>
    /// Default OAuth client identifier used to acquire tokens.
    /// </summary>
    public string? ClientId
    {
        get => _clientId;
        set
        {
            _clientId = value;
            AssignedMask |= GraphQLOptionsAssignedMask.ClientId;
        }
    }

    /// <summary>
    /// Default OAuth client secret used to acquire tokens.
    /// </summary>
    public string? ClientSecret
    {
        get => _clientSecret;
        set
        {
            _clientSecret = value;
            AssignedMask |= GraphQLOptionsAssignedMask.ClientSecret;
        }
    }

    /// <summary>
    /// When <c>true</c>, the HTTP pipeline refreshes the bearer token and retries on <c>401 Unauthorized</c> responses.
    /// </summary>
    public bool EnableUnauthorizedRefresh
    {
        get => _enableUnauthorizedRefresh;
        set
        {
            _enableUnauthorizedRefresh = value;
            AssignedMask |= GraphQLOptionsAssignedMask.EnableUnauthorizedRefresh;
        }
    }

    /// <summary>
    /// Maximum number of retry attempts after an initial <c>401 Unauthorized</c> response. Set to <c>0</c> to disable retries.
    /// </summary>
    public int MaxUnauthorizedRetries
    {
        get => _maxUnauthorizedRetries;
        set
        {
            _maxUnauthorizedRetries = value;
            AssignedMask |= GraphQLOptionsAssignedMask.MaxUnauthorizedRetries;
        }
    }

    /// <summary>
    /// When <c>true</c> (default), dependency injection registers standard logging infrastructure if the host
    /// did not call <c>AddLogging</c>, ensuring downstream services can resolve <see cref="Microsoft.Extensions.Logging.ILogger{TCategoryName}"/>.
    /// Set to <c>false</c> to opt out.
    /// </summary>
    public bool EnableInternalLoggingSetup
    {
        get => _enableInternalLoggingSetup;
        set
        {
            _enableInternalLoggingSetup = value;
            AssignedMask |= GraphQLOptionsAssignedMask.EnableInternalLoggingSetup;
        }
    }

    /// <summary>
    /// When <c>true</c>, permits cleartext <c>http://</c> GraphQL endpoints on non-loopback hosts (for example internal container networks).
    /// Defaults to <c>false</c>, enforcing <c>https://</c> (or loopback <c>http://localhost</c> / <c>http://127.0.0.1</c>).
    /// </summary>
    public bool AllowInsecureHttp
    {
        get => _allowInsecureHttp;
        set
        {
            _allowInsecureHttp = value;
            AssignedMask |= GraphQLOptionsAssignedMask.AllowInsecureHttp;
        }
    }

    /// <summary>
    /// When <c>true</c> and HTTP resilience is enabled, permits transient HTTP retries on <c>HTTP POST</c> requests by default.
    /// Defaults to <c>false</c> because GraphQL mutations use <c>HTTP POST</c> and may not be idempotent.
    /// Individual read-only named clients can override this via <see cref="SitecoreGraphQLClientOptions.AllowPostRetries"/>.
    /// </summary>
    public bool AllowPostRetries
    {
        get => _allowPostRetries;
        set
        {
            _allowPostRetries = value;
            AssignedMask |= GraphQLOptionsAssignedMask.AllowPostRetries;
        }
    }

    /// <summary>
    /// Maximum number of dynamic GraphQL client instances retained in the factory cache before oldest dynamic entries are evicted (FIFO).
    /// Configured default and named clients are exempt from eviction. Defaults to <c>128</c>.
    /// </summary>
    public int MaxCachedClients
    {
        get => _maxCachedClients;
        set
        {
            _maxCachedClients = value;
            AssignedMask |= GraphQLOptionsAssignedMask.MaxCachedClients;
        }
    }

    /// <summary>
    /// Maximum request content size in bytes pre-buffered for <c>401 Unauthorized</c> or resilience <c>POST</c> replay.
    /// Requests larger than this limit are still sent unbuffered, skipping replay on <c>401</c>. Defaults to <c>10 MB</c> (<c>10485760</c> bytes).
    /// </summary>
    public long MaxRetryBufferBytes
    {
        get => _maxRetryBufferBytes;
        set
        {
            _maxRetryBufferBytes = value;
            AssignedMask |= GraphQLOptionsAssignedMask.MaxRetryBufferBytes;
        }
    }

    /// <summary>
    /// Optional named GraphQL clients resolvable via <see cref="ISitecoreGraphQLFactory.CreateClientByNameAsync"/>.
    /// </summary>
    public Dictionary<string, SitecoreGraphQLClientOptions> Clients
    {
        get => _clients;
        set
        {
            _clients = value;
            AssignedMask |= GraphQLOptionsAssignedMask.Clients;
        }
    }

    internal void ResetAssignmentTracking()
    {
        AssignedMask = GraphQLOptionsAssignedMask.None;
        if (_clients is null)
        {
            return;
        }

        foreach (var (_, clientOptions) in _clients)
        {
            clientOptions?.ResetAssignmentTracking();
        }
    }

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
    private string? _endpoint;
    private string? _clientId;
    private string? _clientSecret;
    private bool? _allowInsecureHttp;
    private bool? _allowPostRetries;

    internal GraphQLClientOptionsAssignedMask AssignedMask { get; private set; }

    /// <summary>
    /// GraphQL endpoint URL for this named client. Inherits <see cref="SitecoreGraphQLOptions.Endpoint"/> when <c>null</c> or whitespace.
    /// </summary>
    public string? Endpoint
    {
        get => _endpoint;
        set
        {
            _endpoint = value;
            AssignedMask |= GraphQLClientOptionsAssignedMask.Endpoint;
        }
    }

    /// <summary>
    /// OAuth client identifier for this named client. Inherits <see cref="SitecoreGraphQLOptions.ClientId"/> when <c>null</c> or whitespace.
    /// </summary>
    public string? ClientId
    {
        get => _clientId;
        set
        {
            _clientId = value;
            AssignedMask |= GraphQLClientOptionsAssignedMask.ClientId;
        }
    }

    /// <summary>
    /// OAuth client secret for this named client. Inherits <see cref="SitecoreGraphQLOptions.ClientSecret"/> when <c>null</c> or whitespace.
    /// </summary>
    public string? ClientSecret
    {
        get => _clientSecret;
        set
        {
            _clientSecret = value;
            AssignedMask |= GraphQLClientOptionsAssignedMask.ClientSecret;
        }
    }

    /// <summary>
    /// Optional override permitting cleartext <c>http://</c> on non-loopback hosts for this named client.
    /// Inherits <see cref="SitecoreGraphQLOptions.AllowInsecureHttp"/> when <c>null</c>.
    /// </summary>
    public bool? AllowInsecureHttp
    {
        get => _allowInsecureHttp;
        set
        {
            _allowInsecureHttp = value;
            AssignedMask |= GraphQLClientOptionsAssignedMask.AllowInsecureHttp;
        }
    }

    /// <summary>
    /// Optional override permitting transient HTTP resilience retries on <c>HTTP POST</c> requests for this named client
    /// (for example read-only Experience Edge delivery endpoints). Inherits <see cref="SitecoreGraphQLOptions.AllowPostRetries"/> when <c>null</c>.
    /// </summary>
    public bool? AllowPostRetries
    {
        get => _allowPostRetries;
        set
        {
            _allowPostRetries = value;
            AssignedMask |= GraphQLClientOptionsAssignedMask.AllowPostRetries;
        }
    }

    internal void ResetAssignmentTracking()
        => AssignedMask = GraphQLClientOptionsAssignedMask.None;

    /// <summary>
    /// Returns a string representation of the named client options with <see cref="ClientSecret"/> masked.
    /// </summary>
    /// <returns>A sanitized string representation of the named client options.</returns>
    public override string ToString()
        => $"SitecoreGraphQLClientOptions {{ Endpoint = {EndpointUriValidator.SanitizeForLogging(Endpoint)}, ClientId = {ClientId ?? "(inherit)"}, ClientSecret = ***, AllowInsecureHttp = {AllowInsecureHttp?.ToString() ?? "(inherit)"}, AllowPostRetries = {AllowPostRetries?.ToString() ?? "(inherit)"} }}";
}
