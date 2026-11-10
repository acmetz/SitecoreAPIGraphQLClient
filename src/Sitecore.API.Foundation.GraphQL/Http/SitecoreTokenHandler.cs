using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sitecore.API.Foundation.Authorization.Abstractions;
using Sitecore.API.Foundation.Authorization.Models;
using Sitecore.API.Foundation.GraphQL.Internal;

namespace Sitecore.API.Foundation.GraphQL.Http;

/// <summary>
/// <see cref="DelegatingHandler"/> that injects OAuth2 bearer tokens per Sitecore GraphQL request
/// and supports coalesced token refresh with <c>HTTP POST</c> payload replay on <c>401 Unauthorized</c> responses.
/// </summary>
public sealed class SitecoreTokenHandler : DelegatingHandler
{
    private readonly ISitecoreTokenCoordinator _coordinator;
    private readonly IOptionsMonitor<SitecoreGraphQLOptions>? _optionsMonitor;
    private readonly IOptions<SitecoreGraphQLOptions>? _staticOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SitecoreTokenHandler"/> class.
    /// </summary>
    /// <param name="tokenService">The Sitecore authentication token service.</param>
    /// <param name="options">Configured Sitecore GraphQL options.</param>
    /// <param name="tokenAccessor">Accessor for extracting bearer token strings.</param>
    /// <param name="logger">Optional logger instance.</param>
    public SitecoreTokenHandler(
        ISitecoreTokenService tokenService,
        IOptions<SitecoreGraphQLOptions> options,
        ITokenValueAccessor tokenAccessor,
        ILogger<SitecoreTokenHandler>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(tokenService);
        _staticOptions = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(tokenAccessor);
        _coordinator = new SitecoreTokenCoordinator(tokenService, tokenAccessor);
        _timeProvider = TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger<SitecoreTokenHandler>.Instance;
    }

    internal SitecoreTokenHandler(
        ISitecoreTokenCoordinator coordinator,
        IOptionsMonitor<SitecoreGraphQLOptions> optionsMonitor,
        TimeProvider? timeProvider = null,
        ILogger<SitecoreTokenHandler>? logger = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger<SitecoreTokenHandler>.Instance;
    }

    private SitecoreGraphQLOptions CurrentOptions
        => _optionsMonitor?.CurrentValue ?? _staticOptions!.Value;

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var opts = CurrentOptions;
        SitecoreAuthClientCredentials credentials;
        string? clientName = null;
        string sanitizedEndpoint;
        bool allowPostRetries;

        if (request.Options.TryGetValue(CredentialContext.OptionsKey, out var credentialContext))
        {
            credentials = credentialContext.Credentials;
            clientName = credentialContext.ClientName;
            sanitizedEndpoint = credentialContext.SanitizedEndpoint;
            allowPostRetries = credentialContext.AllowPostRetries;
        }
        else if (opts.TryBuildCredentials(out var defaultCredentials))
        {
            credentials = defaultCredentials;
            sanitizedEndpoint = EndpointUriValidator.SanitizeForLogging(request.RequestUri?.OriginalString);
            allowPostRetries = opts.AllowPostRetries;
        }
        else
        {
            Log.HandlerMissingCredentials(_logger);
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var (authToken, accessToken) = await _coordinator.GetTokenAsync(credentials, clientName, cancellationToken).ConfigureAwait(false);
        AttachAuthorizationHeader(request, accessToken);

        var maxRetries = opts.EnableUnauthorizedRefresh ? Math.Max(0, opts.MaxUnauthorizedRetries) : 0;
        var canReplayContent = await TryBufferContentIfNeededAsync(
            request,
            shouldBuffer: maxRetries > 0 || allowPostRetries,
            opts.MaxRetryBufferBytes,
            cancellationToken).ConfigureAwait(false);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized || maxRetries == 0)
        {
            return response;
        }

        if (!canReplayContent)
        {
            Log.UnauthorizedRetrySkippedUnbufferedPayload(_logger, sanitizedEndpoint, opts.MaxRetryBufferBytes);
            return response;
        }

        return await RefreshAndRetryOnUnauthorizedAsync(
            request,
            response,
            credentials,
            authToken,
            accessToken,
            clientName,
            sanitizedEndpoint,
            maxRetries,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Forces a token refresh for the configured default Sitecore GraphQL client credentials.
    /// </summary>
    /// <param name="cancellationToken">A token to observe while waiting for the refresh to complete.</param>
    /// <returns>A task representing the asynchronous refresh operation.</returns>
    public async Task ForceRefreshAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var opts = CurrentOptions;
        if (!opts.TryBuildCredentials(out var credentials))
        {
            Log.HandlerMissingCredentials(_logger);
            return;
        }

        _ = await _coordinator.RefreshTokenAsync(
            credentials,
            failedAuthToken: null,
            failedAccessToken: null,
            clientName: null,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<bool> TryBufferContentIfNeededAsync(
        HttpRequestMessage request,
        bool shouldBuffer,
        long maxBufferBytes,
        CancellationToken cancellationToken)
    {
        if (request.Content is null)
        {
            return true;
        }

        if (!shouldBuffer || maxBufferBytes <= 0)
        {
            return false;
        }

        var contentLength = request.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > maxBufferBytes)
        {
            return false;
        }

        try
        {
#if NET9_0_OR_GREATER
            await request.Content.LoadIntoBufferAsync(maxBufferBytes, cancellationToken).ConfigureAwait(false);
#else
            cancellationToken.ThrowIfCancellationRequested();
            await request.Content.LoadIntoBufferAsync(maxBufferBytes).ConfigureAwait(false);
#endif
            return true;
        }
        catch (HttpRequestException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<HttpResponseMessage> RefreshAndRetryOnUnauthorizedAsync(
        HttpRequestMessage originalRequest,
        HttpResponseMessage response,
        SitecoreAuthClientCredentials credentials,
        SitecoreAuthToken currentAuthToken,
        string currentAccessToken,
        string? clientName,
        string sanitizedEndpoint,
        int maxRetries,
        CancellationToken cancellationToken)
    {
        byte[]? payloadBytes = null;
        if (originalRequest.Content is not null)
        {
            payloadBytes = await originalRequest.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        var clientTag = SitecoreGraphQLDiagnostics.GetClientTag(clientName);

        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            Log.UnauthorizedRefreshAttempt(_logger, sanitizedEndpoint, attempt + 1, maxRetries);
            SitecoreGraphQLDiagnostics.AuthRetries.Add(1, new KeyValuePair<string, object?>("client.name", clientTag));

            (currentAuthToken, currentAccessToken) = await _coordinator.RefreshTokenAsync(
                credentials,
                currentAuthToken,
                currentAccessToken,
                clientName,
                cancellationToken).ConfigureAwait(false);

            response.Dispose();

            using var retryRequest = CloneRequestForRetry(originalRequest, payloadBytes, currentAccessToken);
            response = await base.SendAsync(retryRequest, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                break;
            }

            if (attempt < maxRetries - 1)
            {
                var delay = ComputeBackoffDelay(attempt);
                Log.UnauthorizedRetryBackoff(_logger, sanitizedEndpoint, delay.TotalMilliseconds);
                await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }

        return response;
    }

    private static HttpRequestMessage CloneRequestForRetry(
        HttpRequestMessage originalRequest,
        byte[]? payloadBytes,
        string accessToken)
    {
        var clone = new HttpRequestMessage(originalRequest.Method, originalRequest.RequestUri)
        {
            Version = originalRequest.Version,
            VersionPolicy = originalRequest.VersionPolicy
        };

        foreach (var header in originalRequest.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        AttachAuthorizationHeader(clone, accessToken);

        foreach (var option in originalRequest.Options)
        {
            clone.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        }

        if (payloadBytes is not null)
        {
            var contentClone = new ReadOnlyMemoryContent(payloadBytes);
            if (originalRequest.Content is not null)
            {
                foreach (var contentHeader in originalRequest.Content.Headers)
                {
                    contentClone.Headers.TryAddWithoutValidation(contentHeader.Key, contentHeader.Value);
                }
            }

            clone.Content = contentClone;
        }

        return clone;
    }

    private static void AttachAuthorizationHeader(HttpRequestMessage request, string accessToken)
    {
        if (string.IsNullOrEmpty(accessToken))
        {
            request.Headers.Authorization = null;
            return;
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    private static TimeSpan ComputeBackoffDelay(int attempt)
    {
        var baseMs = 200d * Math.Pow(2, attempt);
        var jitter = 0.8 + (Random.Shared.NextDouble() * 0.4);
        var ms = Math.Min(baseMs * jitter, 5000d);
        return TimeSpan.FromMilliseconds(ms);
    }
}
