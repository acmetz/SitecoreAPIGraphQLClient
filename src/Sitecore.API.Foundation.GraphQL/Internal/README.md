# Sitecore.API.Foundation.GraphQL.Internal

## Overview
Internal coordination primitives, request credential contexts, socket-free forwarding handlers, startup and runtime endpoint validators, legacy token cache adapters, and source-generated structured logging definitions in the `Sitecore.API.Foundation.GraphQL.Internal` namespace.

## Interface
- `CredentialContext` (`CredentialContext.cs`):
  - `public static readonly HttpRequestOptionsKey<CredentialContext> OptionsKey = new("Sitecore.API.Foundation.GraphQL.CredentialContext")`
  - Properties: `Credentials` (`SitecoreAuthClientCredentials`), `ClientName` (`string?`), `SanitizedEndpoint` (`string`), `IsExplicitClient` (`bool`), `AllowPostRetries` (`bool`), `ResiliencePartitionKey` (`string`).
  - `public override string ToString()`: Formats context with `ClientSecret = ***`.
- `ClientCacheKey` (`CredentialContext.cs`):
  - `internal readonly record struct ClientCacheKey(string NormalizedUrl, string ClientId, string ClientSecret, string? ClientName, bool AllowPostRetries)`: Ordinal-compared cache key for `SitecoreGraphQLFactory` with `ClientSecret = ***` masking in `ToString()`.
- `CredentialForwardingHandler` (`CredentialForwardingHandler.cs`):
  - `internal sealed class CredentialForwardingHandler : HttpMessageHandler`: Socket-free handler attached to cached `GraphQLHttpClient` instances; creates a shallow `HttpRequestMessage` envelope, stamps `CredentialContext.OptionsKey`, and dispatches via `IHttpClientFactory.CreateClient(httpClientName)` using `HttpCompletionOption.ResponseHeadersRead`.
- `ISitecoreTokenCoordinator` / `ISitecoreTokenCoordinator<TClient>` / `SitecoreTokenCoordinator` / `SitecoreTokenCoordinator<TClient>` (`ISitecoreTokenCoordinator.cs`):
  - `Task<(SitecoreAuthToken AuthToken, string AccessToken)> GetTokenAsync(SitecoreAuthClientCredentials credentials, string? clientName, CancellationToken cancellationToken, bool isExplicitClient = false)`
  - `Task<(SitecoreAuthToken AuthToken, string AccessToken)> RefreshTokenAsync(SitecoreAuthClientCredentials credentials, SitecoreAuthToken? failedAuthToken, string? failedAccessToken, string? clientName, CancellationToken cancellationToken, bool isExplicitClient = false)`
  - Constructors on `SitecoreTokenCoordinator` (unsealed): `public SitecoreTokenCoordinator(IServiceScopeFactory, ITokenValueAccessor, ILogger<SitecoreTokenCoordinator>? = null)`, `public SitecoreTokenCoordinator(ISitecoreTokenService, ITokenValueAccessor, ILogger<SitecoreTokenCoordinator>? = null)`, and `protected SitecoreTokenCoordinator(IServiceScopeFactory, ITokenValueAccessor, ILogger?, string?)` (no default parameter values to avoid `CS0121`), plus virtual `TryResolveTypedTokenService` and `ResolveUntypedTokenService` resolution seam.
  - `SitecoreTokenCoordinator<TClient>` overrides the seam to resolve `ISitecoreTokenService<TClient>` first, fall back to untyped `ISitecoreTokenService` with a one-time `EventId = 1202` `Information` log, or throw an actionable `InvalidOperationException` when neither is registered.
- `SitecoreGraphQLOptionsExtensions` (`SitecoreGraphQLOptionsExtensions.cs`):
  - `public static bool TryBuildCredentials(this SitecoreGraphQLOptions? options, out SitecoreAuthClientCredentials credentials)`
  - `public static bool TryResolveNamedClient(this SitecoreGraphQLOptions? options, string clientName, out string endpoint, out SitecoreAuthClientCredentials credentials, out bool allowInsecureHttp, out bool allowPostRetries)`
- `SitecoreGraphQLOptionsValidator` (`SitecoreGraphQLOptionsValidator.cs`):
  - `internal sealed class SitecoreGraphQLOptionsValidator : IValidateOptions<SitecoreGraphQLOptions>`: Validates `MaxUnauthorizedRetries >= 0`, `MaxCachedClients >= 1`, `MaxRetryBufferBytes > 0`, presence of complete default credentials or at least one named client, and endpoint URI validity; prefixes failure messages with `$"[{name}] "` when validating named options (`!string.IsNullOrEmpty(name)`).
- `EndpointUriValidator` (`SitecoreGraphQLOptionsValidator.cs`):
  - `public static bool TryValidateEndpoint(string rawEndpoint, bool allowInsecureHttp, out string normalizedEndpoint, out string errorMessage)`
  - `public static string ValidateAndNormalizeEndpoint(string rawEndpoint, bool allowInsecureHttp, string paramName, bool throwInvalidOperation = false)`
  - `public static string SanitizeForLogging(string? rawEndpoint)`
  - `public static string GetRedactedAuthority(Uri? uri)`
- `Log` (`SitecoreGraphQLTelemetry.cs`):
  - `internal static partial class Log` (`[ExcludeFromCodeCoverage]`): Source-generated `[LoggerMessage]` structured logging definitions (`EventId = 1000`–`1007`, `1100`–`1103`, `1200`–`1203`, `1300`), including `TypedTokenServiceFallbackToUntyped` (`EventId = 1202`, `Information`) and `TypedTokenPreCheckReturnedDifferentToken` (`EventId = 1203`, `Debug`).
- `SitecoreTokenCache` (`SitecoreTokenCache.cs`):
  - `internal sealed class SitecoreTokenCache : ISitecoreTokenCache`: Obsolete v1.x compatibility adapter delegating `GetOrRefreshAsync` and `ForceRefreshAsync` to `ISitecoreTokenCoordinator`.

## Invariants
- All types are `internal` and exposed only to authorized test, benchmark, and proxy assemblies via `Properties/InternalsVisibleTo.cs`.
- `CredentialContext.ResiliencePartitionKey` resolves deterministically to `"named:{clientName}"` for named clients, `"default"` for default-credential clients, or `"explicit:{16-char uppercase SHA-256 hex prefix of ClientId}"` for dynamic explicit-credential clients.
- `CredentialForwardingHandler` shares `request.Content` by reference on the forwarded envelope and detaches `forwardedRequest.Content = null` before disposing the envelope if `client.SendAsync` faults, preserving caller ownership of the original `HttpContent`.
- `SitecoreTokenCoordinator` coalesces concurrent refresh operations per `(ClientId, ClientSecret)` within each coordinator instance (each `TClient` coordinator maintains its own `_inFlightRefreshes` dictionary) using a reference-counted `InFlightRefresh` entry so individual waiter cancellation does not abort the underlying shared refresh for other concurrent callers.
- On the untyped and fallback paths before executing a network refresh on a staggered `401`, `SitecoreTokenCoordinator` inspects `Sitecore.API.Foundation.Authorization.Abstractions.ISitecoreTokenCache` (if registered) via the 1-argument `GetToken(credentials)` lookup to reuse an already-refreshed unexpired token, and skips `TryRefreshSitecoreAuthToken` when cold.
- On the typed `ISitecoreTokenService<TClient>` path:
  - When `typedTokenService is SitecoreTokenService` (built-in `SitecoreAPIAuthorization` service): if DI's `ISitecoreTokenCache` reports `CacheSize == 0`, routes directly into `MeasureRefreshWithServiceAsync(..., cacheKnownEmpty: true)` (calling `GetSitecoreAuthToken` once without `TryRefreshSitecoreAuthToken`); otherwise, on a staggered `401` (`failedAuthToken.HasValue`), runs a stateless `InvokeGetTokenAsync` pre-check and short-circuits with `EventId = 1203` (`Debug`) when a different access token is returned, without ever calling the 1-argument `ISitecoreTokenCache.GetToken(credentials)`.
  - Custom `ISitecoreTokenService<TClient>` implementations not deriving from `SitecoreTokenService` bypass both global-cache short-circuits and always proceed through `MeasureRefreshWithServiceAsync` (`TryRefreshSitecoreAuthToken` -> `GetSitecoreAuthToken` fallback on `ArgumentException` or empty token).
- `EndpointUriValidator` enforces `https://` (or loopback `http://`) unless `allowInsecureHttp = true`, rejects embedded `Uri.UserInfo` (`@` in authority), rejects invalid DNS labels and out-of-range numeric IPv4 literals (such as `999.999.999.999`), and strips query strings (`?sc_apikey=...`) and fragments in `SanitizeForLogging`.

## Side Effects
- `CredentialForwardingHandler.SendAsync` dispatches HTTP requests through `IHttpClientFactory.CreateClient(httpClientName)`.
- `SitecoreTokenCoordinator` creates async DI scopes (`IServiceScopeFactory.CreateAsyncScope()`) per token operation, calls `ISitecoreTokenService.GetSitecoreAuthToken` / `TryRefreshSitecoreAuthToken`, starts `SitecoreGraphQLDiagnostics.ActivitySource` activities (`SitecoreGraphQL.GetToken`, `SitecoreGraphQL.RefreshToken`), and records `SitecoreGraphQLDiagnostics` metrics (`TokenRequests`, `TokenRefreshes`, `TokenRefreshDurationMs`) tagged with `client.name` and (on typed coordinators) `client.type` (`typeof(TClient).ToString()`).
- `Log` methods emit structured log entries to `ILogger`.

## Verification
- Run: `dotnet test`
