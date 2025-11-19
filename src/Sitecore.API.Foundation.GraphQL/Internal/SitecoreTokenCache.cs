using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sitecore.API.Foundation.Authorization.Abstractions;
using Sitecore.API.Foundation.Authorization.Models;

namespace Sitecore.API.Foundation.GraphQL.Internal;

internal sealed class SitecoreTokenCache : ISitecoreTokenCache
{
    private readonly ISitecoreTokenService _tokenService;
    private readonly IOptions<SitecoreGraphQLOptions> _options;
    private readonly ITokenValueAccessor _accessor;
    private readonly ILogger<SitecoreTokenCache>? _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private string? _token;

    public SitecoreTokenCache(
        ISitecoreTokenService tokenService,
        IOptions<SitecoreGraphQLOptions> options,
        ITokenValueAccessor accessor,
        ILogger<SitecoreTokenCache>? logger = null)
    {
        _tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
        _logger = logger;
    }

    public string? CurrentToken => _token;

    public async Task<string?> GetOrRefreshAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_token)) return _token;
        return await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<string?> ForceRefreshAsync(CancellationToken cancellationToken)
        => RefreshCoreAsync(cancellationToken);

    private async Task<string?> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrEmpty(_token)) return _token;

            if (!TryBuildCredentials(out var credentials))
            {
                _logger?.LogWarning("Cannot refresh Sitecore token due to missing ClientId/ClientSecret in options.");
                _token = null;
                return _token;
            }

            var token = await _tokenService.GetSitecoreAuthToken(credentials).ConfigureAwait(false);
            _token = _accessor.GetAccessToken(token);
            return _token;
        }
        finally
        {
            _lock.Release();
        }
    }

    private bool TryBuildCredentials(out SitecoreAuthClientCredentials credentials)
    {
        var opts = _options.Value;
        if (!string.IsNullOrWhiteSpace(opts.ClientId) && !string.IsNullOrWhiteSpace(opts.ClientSecret))
        {
            credentials = new SitecoreAuthClientCredentials(opts.ClientId, opts.ClientSecret);
            return true;
        }
        credentials = default;
        return false;
    }
}
