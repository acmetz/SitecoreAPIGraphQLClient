using System.Buffers;
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
        var isExplicitClient = false;
        string sanitizedEndpoint;
        bool allowPostRetries;

        if (request.Options.TryGetValue(CredentialContext.OptionsKey, out var credentialContext))
        {
            credentials = credentialContext.Credentials;
            clientName = credentialContext.ClientName;
            isExplicitClient = credentialContext.IsExplicitClient;
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

        var (authToken, accessToken) = await _coordinator.GetTokenAsync(
            credentials,
            clientName,
            cancellationToken,
            isExplicitClient).ConfigureAwait(false);
        AttachAuthorizationHeader(request, accessToken);

        var maxRetries = opts.EnableUnauthorizedRefresh ? Math.Max(0, opts.MaxUnauthorizedRetries) : 0;
        var preserveCallerTraceContext = maxRetries > 0 && request.Headers.NonValidated.Contains("traceparent");
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
            isExplicitClient,
            sanitizedEndpoint,
            maxRetries,
            preserveCallerTraceContext,
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
        if (contentLength.HasValue)
        {
            if (contentLength.Value > maxBufferBytes)
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

        // ContentLength is null (for example a non-seekable StreamContent). Probe up to maxBufferBytes + 1 bytes
        // without discarding prefix bytes if the stream exceeds MaxRetryBufferBytes.
        return await TryBufferUnknownLengthStreamAsync(request, maxBufferBytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> TryBufferUnknownLengthStreamAsync(
        HttpRequestMessage request,
        long maxBufferBytes,
        CancellationToken cancellationToken)
    {
        var originalContent = request.Content!;
        var maxInspectableBytes = (int)Math.Min(maxBufferBytes, Array.MaxLength - 2);
        var stream = await originalContent.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        using var memoryBuffer = new MemoryStream();
        var scratch = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (memoryBuffer.Length <= maxInspectableBytes)
            {
                var remainingToProbe = (maxInspectableBytes + 1) - (int)memoryBuffer.Length;
                var toRead = Math.Min(scratch.Length, remainingToProbe);
                var read = await stream.ReadAsync(scratch.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    // Entire stream fit within MaxRetryBufferBytes; swap in replayable ReadOnlyMemoryContent.
                    var completeBytes = memoryBuffer.ToArray();
#pragma warning disable CA2000 // Ownership transfers to request.Content
                    var bufferedContent = new ReadOnlyMemoryContent(completeBytes);
#pragma warning restore CA2000
                    CopyContentHeaders(originalContent, bufferedContent);
                    request.Content = bufferedContent;
                    originalContent.Dispose();
                    return true;
                }

                memoryBuffer.Write(scratch, 0, read);
            }

            // Stream exceeded MaxRetryBufferBytes: stitch the already-read prefix ahead of the unread tail stream
            // so the initial SendAsync still transmits 100% of the payload without buffering the rest in memory.
            var prefixBytes = memoryBuffer.ToArray();
#pragma warning disable CA2000 // Ownership transfers to request.Content
            var stitchedStream = new PrefixedRemainingStream(prefixBytes, stream, originalContent);
            var stitchedContent = new StreamContent(stitchedStream);
#pragma warning restore CA2000
            CopyContentHeaders(originalContent, stitchedContent);
            request.Content = stitchedContent;
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    private static void CopyContentHeaders(HttpContent source, HttpContent destination)
    {
        foreach (var contentHeader in source.Headers)
        {
            if (!string.Equals(contentHeader.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                destination.Headers.TryAddWithoutValidation(contentHeader.Key, contentHeader.Value);
            }
        }
    }

    private async Task<HttpResponseMessage> RefreshAndRetryOnUnauthorizedAsync(
        HttpRequestMessage originalRequest,
        HttpResponseMessage response,
        SitecoreAuthClientCredentials credentials,
        SitecoreAuthToken currentAuthToken,
        string currentAccessToken,
        string? clientName,
        bool isExplicitClient,
        string sanitizedEndpoint,
        int maxRetries,
        bool preserveCallerTraceContext,
        CancellationToken cancellationToken)
    {
        byte[]? payloadBytes = null;
        HttpRequestMessage? lastRetryRequest = null;

        try
        {
            if (originalRequest.Content is not null)
            {
                payloadBytes = await originalRequest.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            }

            var clientTag = SitecoreGraphQLDiagnostics.GetClientTag(clientName, isExplicitClient);

            for (var attempt = 0; attempt < maxRetries; attempt++)
            {
                // Dispose the superseded 401 response and previous retry request immediately before awaiting refresh/network I/O.
                response?.Dispose();
                response = null!;
                lastRetryRequest?.Dispose();
                lastRetryRequest = null;

                Log.UnauthorizedRefreshAttempt(_logger, sanitizedEndpoint, attempt + 1, maxRetries);
                SitecoreGraphQLDiagnostics.AuthRetries.Add(1, new KeyValuePair<string, object?>("client.name", clientTag));

                (currentAuthToken, currentAccessToken) = await _coordinator.RefreshTokenAsync(
                    credentials,
                    currentAuthToken,
                    currentAccessToken,
                    clientName,
                    cancellationToken,
                    isExplicitClient).ConfigureAwait(false);

#pragma warning disable CA2000 // Winning retryRequest remains attached to the returned HttpResponseMessage; superseded/faulted requests are disposed here or in catch.
                var retryRequest = CloneRequestForRetry(originalRequest, payloadBytes, currentAccessToken, preserveCallerTraceContext);
#pragma warning restore CA2000
                lastRetryRequest = retryRequest;

                response = await base.SendAsync(retryRequest, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.Unauthorized)
                {
                    break;
                }

                if (attempt < maxRetries - 1)
                {
                    response.Dispose();
                    response = null!;
                    lastRetryRequest.Dispose();
                    lastRetryRequest = null;

                    var delay = ComputeBackoffDelay(attempt);
                    Log.UnauthorizedRetryBackoff(_logger, sanitizedEndpoint, delay.TotalMilliseconds);
                    await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                }
            }

            return response;
        }
        catch
        {
            response?.Dispose();
            lastRetryRequest?.Dispose();
            throw;
        }
    }

    private static HttpRequestMessage CloneRequestForRetry(
        HttpRequestMessage originalRequest,
        byte[]? payloadBytes,
        string accessToken,
        bool preserveCallerTraceContext)
    {
        var clone = new HttpRequestMessage(originalRequest.Method, originalRequest.RequestUri)
        {
            Version = originalRequest.Version,
            VersionPolicy = originalRequest.VersionPolicy
        };

        foreach (var header in originalRequest.Headers)
        {
            if (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!preserveCallerTraceContext &&
                (string.Equals(header.Key, "traceparent", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(header.Key, "tracestate", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(header.Key, "Request-Id", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

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

    private sealed class PrefixedRemainingStream : Stream
    {
        private readonly byte[] _prefix;
        private readonly Stream _tail;
        private readonly HttpContent _originalContent;
        private int _prefixOffset;
        private bool _disposed;

        public PrefixedRemainingStream(byte[] prefix, Stream tail, HttpContent originalContent)
        {
            _prefix = prefix;
            _tail = tail;
            _originalContent = originalContent;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_prefixOffset < _prefix.Length)
            {
                var toCopy = Math.Min(buffer.Length, _prefix.Length - _prefixOffset);
                _prefix.AsSpan(_prefixOffset, toCopy).CopyTo(buffer);
                _prefixOffset += toCopy;
                return toCopy;
            }

            return _tail.Read(buffer);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_prefixOffset < _prefix.Length)
            {
                var toCopy = Math.Min(buffer.Length, _prefix.Length - _prefixOffset);
                _prefix.AsMemory(_prefixOffset, toCopy).CopyTo(buffer);
                _prefixOffset += toCopy;
                return ValueTask.FromResult(toCopy);
            }

            return _tail.ReadAsync(buffer, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _tail.Dispose();
                _originalContent.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
