using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sitecore.API.Foundation.Authorization.Abstractions;
using Sitecore.API.Foundation.Authorization.Models;

namespace Sitecore.API.Foundation.GraphQL.Internal;

internal interface ISitecoreTokenCoordinator
{
    Task<(SitecoreAuthToken AuthToken, string AccessToken)> GetTokenAsync(
        SitecoreAuthClientCredentials credentials,
        string? clientName,
        CancellationToken cancellationToken,
        bool isExplicitClient = false);

    Task<(SitecoreAuthToken AuthToken, string AccessToken)> RefreshTokenAsync(
        SitecoreAuthClientCredentials credentials,
        SitecoreAuthToken? failedAuthToken,
        string? failedAccessToken,
        string? clientName,
        CancellationToken cancellationToken,
        bool isExplicitClient = false);
}

internal sealed class SitecoreTokenCoordinator : ISitecoreTokenCoordinator
{
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly ISitecoreTokenService? _directTokenService;
    private readonly ITokenValueAccessor _tokenAccessor;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<(string ClientId, string ClientSecret), InFlightRefresh> _inFlightRefreshes = new();

    public SitecoreTokenCoordinator(
        IServiceScopeFactory scopeFactory,
        ITokenValueAccessor tokenAccessor,
        ILogger<SitecoreTokenCoordinator>? logger = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _tokenAccessor = tokenAccessor ?? throw new ArgumentNullException(nameof(tokenAccessor));
        _logger = (ILogger?)logger ?? NullLogger<SitecoreTokenCoordinator>.Instance;
    }

    public SitecoreTokenCoordinator(
        ISitecoreTokenService tokenService,
        ITokenValueAccessor tokenAccessor,
        ILogger<SitecoreTokenCoordinator>? logger = null)
    {
        _directTokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
        _tokenAccessor = tokenAccessor ?? throw new ArgumentNullException(nameof(tokenAccessor));
        _logger = (ILogger?)logger ?? NullLogger<SitecoreTokenCoordinator>.Instance;
    }

    public async Task<(SitecoreAuthToken AuthToken, string AccessToken)> GetTokenAsync(
        SitecoreAuthClientCredentials credentials,
        string? clientName,
        CancellationToken cancellationToken,
        bool isExplicitClient = false)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var clientTag = SitecoreGraphQLDiagnostics.GetClientTag(clientName, isExplicitClient);
        SitecoreGraphQLDiagnostics.TokenRequests.Add(1, new KeyValuePair<string, object?>("client.name", clientTag));

        using var activity = SitecoreGraphQLDiagnostics.ActivitySource.StartActivity("SitecoreGraphQL.GetToken");
        activity?.SetTag("client.name", clientTag);

        if (_directTokenService is not null)
        {
            var token = await InvokeGetTokenAsync(_directTokenService, credentials, cancellationToken).ConfigureAwait(false);
            return (token, _tokenAccessor.GetAccessToken(token));
        }

        var scope = _scopeFactory!.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var tokenService = scope.ServiceProvider.GetRequiredService<ISitecoreTokenService>();
            var token = await InvokeGetTokenAsync(tokenService, credentials, cancellationToken).ConfigureAwait(false);
            return (token, _tokenAccessor.GetAccessToken(token));
        }
    }

    public async Task<(SitecoreAuthToken AuthToken, string AccessToken)> RefreshTokenAsync(
        SitecoreAuthClientCredentials credentials,
        SitecoreAuthToken? failedAuthToken,
        string? failedAccessToken,
        string? clientName,
        CancellationToken cancellationToken,
        bool isExplicitClient = false)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var key = (credentials.ClientId, credentials.ClientSecret);
        while (true)
        {
            var created = false;
            var candidate = new InFlightRefresh(key, _inFlightRefreshes, cancellationToken.CanBeCanceled);
            var entry = _inFlightRefreshes.GetOrAdd(key, candidate);
            if (ReferenceEquals(entry, candidate))
            {
                created = true;
            }
            else
            {
                candidate.Dispose();
            }

            if (!entry.TryAcquireWaiter())
            {
                // Ensure the closed entry is evicted from _inFlightRefreshes so the next iteration never spins.
                entry.RemoveFromDictionary();
                continue;
            }

            if (created)
            {
                _ = ExecuteAndPublishAsync(
                    entry,
                    credentials,
                    failedAuthToken,
                    failedAccessToken,
                    clientName,
                    isExplicitClient);
            }
            else
            {
                Log.CoalescedTokenRefresh(_logger, credentials.ClientId);
            }

            try
            {
                return await entry.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                entry.ReleaseWaiterOnCancel();
                throw;
            }
        }
    }

    private async Task ExecuteAndPublishAsync(
        InFlightRefresh entry,
        SitecoreAuthClientCredentials credentials,
        SitecoreAuthToken? failedAuthToken,
        string? failedAccessToken,
        string? clientName,
        bool isExplicitClient)
    {
        try
        {
            var result = await ExecuteRefreshCoreAsync(
                credentials,
                failedAuthToken,
                failedAccessToken,
                clientName,
                isExplicitClient,
                entry.Token).ConfigureAwait(false);
            entry.SetResult(result);
        }
        catch (OperationCanceledException oce) when (entry.IsSelfCanceled)
        {
            entry.SetCanceled(oce.CancellationToken);
        }
        catch (Exception ex)
        {
            entry.SetException(ex);
        }
        finally
        {
            entry.RemoveFromDictionary();
            entry.MarkCompleted();
            entry.Dispose();
        }
    }

    private async Task<(SitecoreAuthToken AuthToken, string AccessToken)> ExecuteRefreshCoreAsync(
        SitecoreAuthClientCredentials credentials,
        SitecoreAuthToken? failedAuthToken,
        string? failedAccessToken,
        string? clientName,
        bool isExplicitClient,
        CancellationToken cancellationToken)
    {
        var clientTag = SitecoreGraphQLDiagnostics.GetClientTag(clientName, isExplicitClient);

        if (_directTokenService is not null)
        {
            return await MeasureRefreshWithServiceAsync(
                _directTokenService,
                authCache: null,
                credentials,
                failedAuthToken,
                clientTag,
                cancellationToken).ConfigureAwait(false);
        }

        var scope = _scopeFactory!.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var authCache = scope.ServiceProvider.GetService<Sitecore.API.Foundation.Authorization.Abstractions.ISitecoreTokenCache>();
            if (failedAuthToken.HasValue && !string.IsNullOrEmpty(failedAccessToken) && authCache is not null)
            {
                // Staggered 401 check: if an earlier request already refreshed the cached token past failedAccessToken, reuse it directly.
                var cached = authCache.GetToken(credentials);
                if (cached.HasValue)
                {
                    var cachedAccess = _tokenAccessor.GetAccessToken(cached.Value);
                    if (!string.IsNullOrEmpty(cachedAccess) &&
                        !string.Equals(cachedAccess, failedAccessToken, StringComparison.Ordinal))
                    {
                        Log.CoalescedTokenRefresh(_logger, credentials.ClientId);
                        return (cached.Value, cachedAccess);
                    }
                }
            }

            var tokenService = scope.ServiceProvider.GetRequiredService<ISitecoreTokenService>();
            return await MeasureRefreshWithServiceAsync(
                tokenService,
                authCache,
                credentials,
                failedAuthToken,
                clientTag,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<(SitecoreAuthToken AuthToken, string AccessToken)> MeasureRefreshWithServiceAsync(
        ISitecoreTokenService tokenService,
        Sitecore.API.Foundation.Authorization.Abstractions.ISitecoreTokenCache? authCache,
        SitecoreAuthClientCredentials credentials,
        SitecoreAuthToken? failedAuthToken,
        string clientTag,
        CancellationToken cancellationToken)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        SitecoreGraphQLDiagnostics.TokenRefreshes.Add(1, new KeyValuePair<string, object?>("client.name", clientTag));

        using var activity = SitecoreGraphQLDiagnostics.ActivitySource.StartActivity("SitecoreGraphQL.RefreshToken");
        activity?.SetTag("client.name", clientTag);

        try
        {
            return await RefreshWithServiceAsync(
                tokenService,
                authCache,
                credentials,
                failedAuthToken,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            SitecoreGraphQLDiagnostics.TokenRefreshDurationMs.Record(
                elapsedMs,
                new KeyValuePair<string, object?>("client.name", clientTag));
        }
    }

    private async Task<(SitecoreAuthToken AuthToken, string AccessToken)> RefreshWithServiceAsync(
        ISitecoreTokenService tokenService,
        Sitecore.API.Foundation.Authorization.Abstractions.ISitecoreTokenCache? authCache,
        SitecoreAuthClientCredentials credentials,
        SitecoreAuthToken? failedAuthToken,
        CancellationToken cancellationToken)
    {
        SitecoreAuthToken? tokenToInvalidate = failedAuthToken;
        SitecoreAuthToken? preFetchedToken = null;

        if (tokenToInvalidate is null)
        {
            if (authCache is not null)
            {
                // Fast synchronous check: if cache is cold, fetch directly with 1 OAuth request.
                var cached = authCache.GetToken(credentials);
                if (!cached.HasValue)
                {
                    var freshColdToken = await InvokeGetTokenAsync(tokenService, credentials, cancellationToken).ConfigureAwait(false);
                    return (freshColdToken, _tokenAccessor.GetAccessToken(freshColdToken));
                }

                tokenToInvalidate = cached.Value;
            }
            else
            {
                var current = await InvokeGetTokenAsync(tokenService, credentials, cancellationToken).ConfigureAwait(false);
                preFetchedToken = current;
                tokenToInvalidate = current;
            }
        }

        if (tokenToInvalidate is { } candidate && !string.IsNullOrEmpty(candidate.AccessToken))
        {
            try
            {
                var refreshed = await InvokeTryRefreshAsync(tokenService, candidate, cancellationToken).ConfigureAwait(false);
                var refreshedAccess = _tokenAccessor.GetAccessToken(refreshed);
                if (!string.IsNullOrEmpty(refreshed.AccessToken) || !string.IsNullOrEmpty(refreshedAccess))
                {
                    return (refreshed, refreshedAccess);
                }
            }
            catch (ArgumentException)
            {
                // Token was already evicted by a prior refresh or is unmanaged; fall back to GetSitecoreAuthToken.
            }

            Log.TokenRefreshFallbackToGet(_logger, credentials.ClientId);
        }
        else if (preFetchedToken.HasValue)
        {
            // Manual refresh against a test double returning default(SitecoreAuthToken): avoid a redundant second GetSitecoreAuthToken call.
            return (preFetchedToken.Value, _tokenAccessor.GetAccessToken(preFetchedToken.Value));
        }

        var fallbackToken = await InvokeGetTokenAsync(tokenService, credentials, cancellationToken).ConfigureAwait(false);
        return (fallbackToken, _tokenAccessor.GetAccessToken(fallbackToken));
    }

    private static Task<SitecoreAuthToken> InvokeGetTokenAsync(
        ISitecoreTokenService tokenService,
        SitecoreAuthClientCredentials credentials,
        CancellationToken cancellationToken)
        => cancellationToken.CanBeCanceled
            ? tokenService.GetSitecoreAuthToken(credentials, cancellationToken)
            : tokenService.GetSitecoreAuthToken(credentials);

    private static Task<SitecoreAuthToken> InvokeTryRefreshAsync(
        ISitecoreTokenService tokenService,
        SitecoreAuthToken tokenToInvalidate,
        CancellationToken cancellationToken)
        => cancellationToken.CanBeCanceled
            ? tokenService.TryRefreshSitecoreAuthToken(tokenToInvalidate, cancellationToken)
            : tokenService.TryRefreshSitecoreAuthToken(tokenToInvalidate);

    private sealed class InFlightRefresh : IDisposable
    {
        private readonly object _gate = new();
        private readonly (string ClientId, string ClientSecret) _key;
        private readonly ConcurrentDictionary<(string ClientId, string ClientSecret), InFlightRefresh> _dictionary;
        private readonly CancellationTokenSource? _cts;
        private readonly TaskCompletionSource<(SitecoreAuthToken AuthToken, string AccessToken)> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeWaiters;
        private bool _completed;
        private bool _selfCanceled;
        private bool _disposed;

        public InFlightRefresh(
            (string ClientId, string ClientSecret) key,
            ConcurrentDictionary<(string ClientId, string ClientSecret), InFlightRefresh> dictionary,
            bool canBeCanceled)
        {
            _key = key;
            _dictionary = dictionary;
            if (canBeCanceled)
            {
                _cts = new CancellationTokenSource();
            }
        }

        public Task<(SitecoreAuthToken AuthToken, string AccessToken)> Task => _tcs.Task;

        public CancellationToken Token => _cts?.Token ?? CancellationToken.None;

        public bool IsSelfCanceled
        {
            get
            {
                lock (_gate)
                {
                    return _selfCanceled;
                }
            }
        }

        public bool TryAcquireWaiter()
        {
            lock (_gate)
            {
                if (_completed || _disposed)
                {
                    return false;
                }

                _activeWaiters++;
                return true;
            }
        }

        public void RemoveFromDictionary()
            => ((ICollection<KeyValuePair<(string ClientId, string ClientSecret), InFlightRefresh>>)_dictionary)
                .Remove(new KeyValuePair<(string ClientId, string ClientSecret), InFlightRefresh>(_key, this));

        public void SetResult((SitecoreAuthToken AuthToken, string AccessToken) result)
            => _tcs.TrySetResult(result);

        public void SetException(Exception exception)
            => _tcs.TrySetException(exception);

        public void SetCanceled(CancellationToken cancellationToken)
            => _tcs.TrySetCanceled(cancellationToken);

        public void ReleaseWaiterOnCancel()
        {
            if (_cts is null)
            {
                return;
            }

            var shouldCancel = false;
            lock (_gate)
            {
                if (_completed || _disposed || _activeWaiters <= 0)
                {
                    return;
                }

                _activeWaiters--;
                if (_activeWaiters == 0)
                {
                    _completed = true;
                    _selfCanceled = true;
                    shouldCancel = true;
                }
            }

            if (shouldCancel)
            {
                RemoveFromDictionary();
                try
                {
                    _cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Completed concurrently.
                }
            }
        }

        public void MarkCompleted()
        {
            lock (_gate)
            {
                _completed = true;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _completed = true;
            }

            _cts?.Dispose();
        }
    }
}
