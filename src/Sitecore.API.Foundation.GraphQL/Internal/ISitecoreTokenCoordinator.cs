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
        CancellationToken cancellationToken);

    Task<(SitecoreAuthToken AuthToken, string AccessToken)> RefreshTokenAsync(
        SitecoreAuthClientCredentials credentials,
        SitecoreAuthToken? failedAuthToken,
        string? failedAccessToken,
        string? clientName,
        CancellationToken cancellationToken);
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
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var clientTag = SitecoreGraphQLDiagnostics.GetClientTag(clientName);
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
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var key = (credentials.ClientId, credentials.ClientSecret);
        while (true)
        {
            var created = false;
            var candidate = new InFlightRefresh(cancellationToken.CanBeCanceled);
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
                // Entry completed right as we observed it; retry to create or join the next entry.
                continue;
            }

            if (created)
            {
                _ = ExecuteAndPublishAsync(
                    key,
                    entry,
                    credentials,
                    failedAuthToken,
                    clientName);
            }
            else
            {
                Log.CoalescedTokenRefresh(_logger, credentials.ClientId);
            }

            CancellationTokenRegistration registration = default;
            if (cancellationToken.CanBeCanceled)
            {
                registration = cancellationToken.Register(static state => ((InFlightRefresh)state!).ReleaseWaiterOnCancel(), entry);
            }

            try
            {
                return await entry.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await registration.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task ExecuteAndPublishAsync(
        (string ClientId, string ClientSecret) key,
        InFlightRefresh entry,
        SitecoreAuthClientCredentials credentials,
        SitecoreAuthToken? failedAuthToken,
        string? clientName)
    {
        try
        {
            var result = await ExecuteRefreshCoreAsync(
                credentials,
                failedAuthToken,
                clientName,
                entry.Token).ConfigureAwait(false);
            entry.SetResult(result);
        }
        catch (OperationCanceledException oce)
        {
            entry.SetCanceled(oce.CancellationToken);
        }
        catch (Exception ex)
        {
            entry.SetException(ex);
        }
        finally
        {
            entry.MarkCompleted();
            ((ICollection<KeyValuePair<(string ClientId, string ClientSecret), InFlightRefresh>>)_inFlightRefreshes)
                .Remove(new KeyValuePair<(string ClientId, string ClientSecret), InFlightRefresh>(key, entry));
            entry.Dispose();
        }
    }

    private async Task<(SitecoreAuthToken AuthToken, string AccessToken)> ExecuteRefreshCoreAsync(
        SitecoreAuthClientCredentials credentials,
        SitecoreAuthToken? failedAuthToken,
        string? clientName,
        CancellationToken cancellationToken)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var clientTag = SitecoreGraphQLDiagnostics.GetClientTag(clientName);
        SitecoreGraphQLDiagnostics.TokenRefreshes.Add(1, new KeyValuePair<string, object?>("client.name", clientTag));

        using var activity = SitecoreGraphQLDiagnostics.ActivitySource.StartActivity("SitecoreGraphQL.RefreshToken");
        activity?.SetTag("client.name", clientTag);

        try
        {
            if (_directTokenService is not null)
            {
                return await RefreshWithServiceAsync(
                    _directTokenService,
                    authCache: null,
                    credentials,
                    failedAuthToken,
                    cancellationToken).ConfigureAwait(false);
            }

            var scope = _scopeFactory!.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var tokenService = scope.ServiceProvider.GetRequiredService<ISitecoreTokenService>();
                var authCache = scope.ServiceProvider.GetService<Sitecore.API.Foundation.Authorization.Abstractions.ISitecoreTokenCache>();
                return await RefreshWithServiceAsync(
                    tokenService,
                    authCache,
                    credentials,
                    failedAuthToken,
                    cancellationToken).ConfigureAwait(false);
            }
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
        private readonly CancellationTokenSource? _cts;
        private readonly TaskCompletionSource<(SitecoreAuthToken AuthToken, string AccessToken)> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeWaiters;
        private bool _completed;
        private bool _disposed;

        public InFlightRefresh(bool canBeCanceled)
        {
            if (canBeCanceled)
            {
                _cts = new CancellationTokenSource();
            }
        }

        public Task<(SitecoreAuthToken AuthToken, string AccessToken)> Task => _tcs.Task;

        public CancellationToken Token => _cts?.Token ?? CancellationToken.None;

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
                    shouldCancel = true;
                }
            }

            if (shouldCancel)
            {
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
