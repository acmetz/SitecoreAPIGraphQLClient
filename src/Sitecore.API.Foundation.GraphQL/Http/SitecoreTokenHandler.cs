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
    private readonly ISitecoreTokenCache _tokenCache;
    private readonly IOptions<SitecoreGraphQLOptions> _options;
    private readonly ILogger<SitecoreTokenHandler>? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SitecoreTokenHandler"/> class.
    /// </summary>
    public SitecoreTokenHandler(
        ISitecoreTokenCache tokenCache,
        IOptions<SitecoreGraphQLOptions> options,
        ILogger<SitecoreTokenHandler>? logger = null)
    {
        _tokenCache = tokenCache ?? throw new ArgumentNullException(nameof(tokenCache));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await EnsureTokenAsync(cancellationToken).ConfigureAwait(false);
        AttachAuthorizationHeaderIfPresent(request);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!ShouldAttemptRefresh(response))
        {
            return response;
        }

        return await RefreshAndRetryOnUnauthorizedAsync(request, response, cancellationToken).ConfigureAwait(false);
    }

    private void AttachAuthorizationHeaderIfPresent(HttpRequestMessage request)
    {
        var token = _tokenCache.CurrentToken;
        if (string.IsNullOrEmpty(token)) return;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private bool ShouldAttemptRefresh(HttpResponseMessage response)
        => response.StatusCode == System.Net.HttpStatusCode.Unauthorized && _options.Value.EnableUnauthorizedRefresh;

    private async Task<HttpResponseMessage> RefreshAndRetryOnUnauthorizedAsync(HttpRequestMessage request, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var maxRetries = Math.Max(0, _options.Value.MaxUnauthorizedRetries);
        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            _logger?.LogDebug("401 received, attempting token refresh (attempt {Attempt}/{MaxRetries})", attempt + 1, maxRetries);

            await ForceRefreshAsync(cancellationToken).ConfigureAwait(false);
            AttachAuthorizationHeaderIfPresent(request);

            response.Dispose();
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized)
            {
                break;
            }

            // Exponential backoff with jitter before next retry
            if (attempt < maxRetries - 1)
            {
                var delay = ComputeBackoffDelay(attempt);
                _logger?.LogTrace($"Unauthorized persisted. Waiting {delay}ms before next retry.");
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
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

    private Task EnsureTokenAsync(CancellationToken cancellationToken)
        => _tokenCache.GetOrRefreshAsync(cancellationToken);

    /// <summary>
    /// Forces token refresh.
    /// </summary>
    public Task ForceRefreshAsync(CancellationToken cancellationToken)
        => _tokenCache.ForceRefreshAsync(cancellationToken);
}
