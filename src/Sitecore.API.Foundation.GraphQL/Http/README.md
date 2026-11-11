# Sitecore.API.Foundation.GraphQL.Http

## Overview
HTTP delegating handler pipeline (`SitecoreTokenHandler.cs`) providing per-request OAuth2 bearer token injection, single-flight `401 Unauthorized` token refresh coordination, bounded request payload buffering, and `HTTP POST` payload replay for Sitecore GraphQL queries and mutations.

## Interface
- `SitecoreTokenHandler` (`SitecoreTokenHandler.cs`, namespace `Sitecore.API.Foundation.GraphQL.Http`):
  - `public SitecoreTokenHandler(ISitecoreTokenService tokenService, IOptions<SitecoreGraphQLOptions> options, ITokenValueAccessor tokenAccessor, ILogger<SitecoreTokenHandler>? logger = null)`: Public constructor wrapping `tokenService` and `tokenAccessor` in a `SitecoreTokenCoordinator` using `TimeProvider.System`.
  - `internal SitecoreTokenHandler(ISitecoreTokenCoordinator coordinator, IOptionsMonitor<SitecoreGraphQLOptions> optionsMonitor, TimeProvider? timeProvider = null, ILogger<SitecoreTokenHandler>? logger = null)`: Internal DI constructor supporting live `IOptionsMonitor<SitecoreGraphQLOptions>` updates and injectable `TimeProvider`.
  - `protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)`: Injects bearer tokens, buffers replayable payloads up to `MaxRetryBufferBytes`, and executes `401 Unauthorized` refresh and retry loops.
  - `public Task ForceRefreshAsync(CancellationToken cancellationToken)`: Proactively refreshes the default client token via `ISitecoreTokenCoordinator.RefreshTokenAsync` when default credentials are configured in options.

## Invariants
- Registered as the outermost `DelegatingHandler` on the `SitecoreGraphQLFactory.NamedHttpClient` (`"SitecoreGraphQL"`) pipeline.
- Resolves per-request credentials, `ClientName`, `IsExplicitClient`, `SanitizedEndpoint`, and `AllowPostRetries` from `HttpRequestMessage.Options` (`CredentialContext.OptionsKey`), falling back to `SitecoreGraphQLOptions` defaults. If neither provides valid credentials, logs `EventId = 1100` (`HandlerMissingCredentials`) and forwards the request unmodified.
- Pre-buffers outbound `request.Content` when `(maxRetries > 0 || allowPostRetries)` and `MaxRetryBufferBytes > 0`, up to `MaxRetryBufferBytes` (default `10 MB` / `10,485,760` bytes):
  - When `Headers.ContentLength` is known and exceeds `MaxRetryBufferBytes`, buffering is skipped and the original unbuffered content is sent once.
  - When `Headers.ContentLength` is unknown (`null`), `TryBufferUnknownLengthStreamAsync` reads up to `min(MaxRetryBufferBytes, Array.MaxLength - 2) + 1` bytes using an `8192`-byte `ArrayPool<byte>.Shared` rental. If the stream exceeds `MaxRetryBufferBytes`, the already-read prefix and unread stream remainder are stitched together via `PrefixedRemainingStream` (`StreamContent`) so the initial request still transmits the complete payload without truncating data or buffering the entire stream in memory.
- Clones fresh `HttpRequestMessage` instances over `ReadOnlyMemoryContent` (`CloneRequestForRetry`) strictly when an `HTTP 401 Unauthorized` retry occurs, disposing the prior `401` `HttpResponseMessage` before retrying.
- `CloneRequestForRetry` strips auto-injected distributed tracing headers (`traceparent`, `tracestate`, `Request-Id`) from retry clones unless `traceparent` was explicitly present on the initial request prior to downstream handler execution (`preserveCallerTraceContext`).
- First `401` retry executes immediately (`0 ms` delay); subsequent retries (`attempt < maxRetries - 1`) wait via `TimeProvider` using jittered exponential backoff `min(200 * 2^attempt * (0.8..1.2), 5000)` ms.

## Side Effects
- Mutates outbound `request.Headers.Authorization` (`Bearer <accessToken>`) when a non-empty token is resolved.
- Replaces `request.Content` with a reusable `ReadOnlyMemoryContent` (or `PrefixedRemainingStream` when an unknown-length stream exceeds `MaxRetryBufferBytes`) and disposes the replaced `HttpContent`.
- Dispatches outbound HTTP requests through `base.SendAsync(request, cancellationToken)`.
- Triggers asynchronous single-flight token acquisition and refresh via `ISitecoreTokenCoordinator` and increments `SitecoreGraphQLDiagnostics.AuthRetries` (`sitecore.graphql.auth.retries`) on `401` retry attempts.
- Emits structured log events (`EventId = 1100`–`1103`) via `Internal.Log`.

## Verification
- Run: `dotnet test`
