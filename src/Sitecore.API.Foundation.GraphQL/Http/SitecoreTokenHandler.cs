using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sitecore.API.Foundation.Authorization.Abstractions;
using Sitecore.API.Foundation.Authorization.Models;

namespace Sitecore.API.Foundation.GraphQL.Http;

/// <summary>
/// DelegatingHandler that injects a bearer token for Sitecore GraphQL requests and supports refresh on 401 responses.
/// </summary>
public sealed class SitecoreTokenHandler : DelegatingHandler
{
    private readonly ISitecoreTokenService _tokenService;
    private readonly IOptions<SitecoreGraphQLOptions> _options;
    private readonly ITokenValueAccessor _tokenAccessor;
    private readonly ILogger<SitecoreTokenHandler>? _logger;

    private volatile string? _cachedToken;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="SitecoreTokenHandler"/> class.
    /// </summary>
    public SitecoreTokenHandler(
        ISitecoreTokenService tokenService,
        IOptions<SitecoreGraphQLOptions> options,
        ITokenValueAccessor tokenAccessor,
        ILogger<SitecoreTokenHandler>? logger = null)
    {
        _tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _tokenAccessor = tokenAccessor ?? throw new ArgumentNullException(nameof(tokenAccessor));
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await EnsureTokenAsync(cancellationToken);
        AttachAuthorizationHeaderIfPresent(request);

        var response = await base.SendAsync(request, cancellationToken);
        if (!ShouldAttemptRefresh(response))
        {
            return response;
        }

        return await RefreshAndRetryOnUnauthorizedAsync(request, response, cancellationToken);
    }

    private void AttachAuthorizationHeaderIfPresent(HttpRequestMessage request)
    {
        if (string.IsNullOrEmpty(_cachedToken)) return;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cachedToken);
    }

    private bool ShouldAttemptRefresh(HttpResponseMessage response)
        => response.StatusCode == System.Net.HttpStatusCode.Unauthorized && _options.Value.EnableUnauthorizedRefresh;

    private async Task<HttpResponseMessage> RefreshAndRetryOnUnauthorizedAsync(HttpRequestMessage request, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var maxRetries = Math.Max(0, _options.Value.MaxUnauthorizedRetries);
        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            _logger?.LogDebug("401 received, attempting token refresh (attempt {Attempt}/{MaxRetries})", attempt + 1, maxRetries);

            await ForceRefreshAsync(cancellationToken);
            AttachAuthorizationHeaderIfPresent(request);

            response.Dispose();
            response = await base.SendAsync(request, cancellationToken);
            if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized)
            {
                break;
            }

            // Exponential backoff with jitter before next retry
            if (attempt < maxRetries - 1)
            {
                var delay = ComputeBackoffDelay(attempt);
                _logger?.LogTrace($"Unauthorized persisted. Waiting {delay}ms before next retry.");
                await Task.Delay(delay, cancellationToken);
            }
        }
        return response;
    }

    private static TimeSpan ComputeBackoffDelay(int attempt)
    {
        // Base 200ms, exponential growth capped at 5s, with +/-20% jitter
        var baseMs = 200d * Math.Pow(2, attempt);
        var jitter = 0.8 + Random.Shared.NextDouble() * 0.4; // 0.8 .. 1.2
        var ms = Math.Min(baseMs * jitter, 5000d);
        return TimeSpan.FromMilliseconds(ms);
    }

    private async Task EnsureTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_cachedToken)) return;
        await RefreshTokenInternalAsync(cancellationToken, force: false);
    }

    /// <summary>
    /// Forces token refresh.
    /// </summary>
    public Task ForceRefreshAsync(CancellationToken cancellationToken)
        => RefreshTokenInternalAsync(cancellationToken, force: true);

    private async Task RefreshTokenInternalAsync(CancellationToken cancellationToken, bool force)
    {
        if (!force && !string.IsNullOrEmpty(_cachedToken)) return;

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (!force && !string.IsNullOrEmpty(_cachedToken)) return;

            if (!TryBuildCredentials(out var credentials))
            {
                _logger?.LogWarning("Sitecore GraphQL options missing ClientId/ClientSecret; token cannot be acquired.");
                _cachedToken = null;
                return;
            }

            var token = await _tokenService.GetSitecoreAuthToken(credentials);
            _cachedToken = _tokenAccessor.GetAccessToken(token);
        }
        finally
        {
            _refreshLock.Release();
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
