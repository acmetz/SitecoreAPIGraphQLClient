using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sitecore.API.Foundation.Authorization.Abstractions;

namespace Sitecore.API.Foundation.GraphQL.Internal;

#pragma warning disable CS0618 // Implements obsolete ISitecoreTokenCache adapter for v1.x backward compatibility
internal sealed class SitecoreTokenCache : ISitecoreTokenCache
{
    private readonly ISitecoreTokenCoordinator _coordinator;
    private readonly IOptionsMonitor<SitecoreGraphQLOptions>? _optionsMonitor;
    private readonly IOptions<SitecoreGraphQLOptions>? _staticOptions;
    private readonly ILogger _logger;

    private volatile string? _lastToken;

    public SitecoreTokenCache(
        ISitecoreTokenService tokenService,
        IOptions<SitecoreGraphQLOptions> options,
        ITokenValueAccessor accessor,
        ILogger<SitecoreTokenCache>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(tokenService);
        _staticOptions = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(accessor);
        _coordinator = new SitecoreTokenCoordinator(tokenService, accessor);
        _logger = (ILogger?)logger ?? NullLogger<SitecoreTokenCache>.Instance;
    }

    internal SitecoreTokenCache(
        ISitecoreTokenCoordinator coordinator,
        IOptionsMonitor<SitecoreGraphQLOptions> optionsMonitor,
        ILogger<SitecoreTokenCache>? logger = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        _logger = (ILogger?)logger ?? NullLogger<SitecoreTokenCache>.Instance;
    }

    public string? CurrentToken => _lastToken;

    public async Task<string?> GetOrRefreshAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var opts = CurrentOptions;
        if (!opts.TryBuildCredentials(out var credentials))
        {
            Log.LegacyCacheMissingCredentials(_logger);
            _lastToken = null;
            return null;
        }

        var (_, accessToken) = await _coordinator.GetTokenAsync(credentials, clientName: null, cancellationToken).ConfigureAwait(false);
        _lastToken = accessToken;
        return accessToken;
    }

    public async Task<string?> ForceRefreshAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var opts = CurrentOptions;
        if (!opts.TryBuildCredentials(out var credentials))
        {
            Log.LegacyCacheMissingCredentials(_logger);
            _lastToken = null;
            return null;
        }

        var (_, accessToken) = await _coordinator.RefreshTokenAsync(
            credentials,
            failedAuthToken: null,
            failedAccessToken: _lastToken,
            clientName: null,
            cancellationToken).ConfigureAwait(false);
        _lastToken = accessToken;
        return accessToken;
    }

    private SitecoreGraphQLOptions CurrentOptions
        => _optionsMonitor?.CurrentValue ?? _staticOptions!.Value;
}
#pragma warning restore CS0618
