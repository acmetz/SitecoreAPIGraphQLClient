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
- `ISitecoreTokenCoordinator` / `SitecoreTokenCoordinator` (`ISitecoreTokenCoordinator.cs`):
  - `Task<(SitecoreAuthToken AuthToken, string AccessToken)> GetTokenAsync(SitecoreAuthClientCredentials credentials, string? clientName, CancellationToken cancellationToken, bool isExplicitClient = false)`
  - `Task<(SitecoreAuthToken AuthToken, string AccessToken)> RefreshTokenAsync(SitecoreAuthClientCredentials credentials, SitecoreAuthToken? failedAuthToken, string? failedAccessToken, string? clientName, CancellationToken cancellationToken, bool isExplicitClient = false)`
  - Constructors: `SitecoreTokenCoordinator(IServiceScopeFactory scopeFactory, ITokenValueAccessor tokenAccessor, ILogger<SitecoreTokenCoordinator>? logger = null)` and `SitecoreTokenCoordinator(ISitecoreTokenService tokenService, ITokenValueAccessor tokenAccessor, ILogger<SitecoreTokenCoordinator>? logger = null)`.
- `SitecoreGraphQLOptionsExtensions` (`SitecoreGraphQLOptionsExtensions.cs`):
  - `public static bool TryBuildCredentials(this SitecoreGraphQLOptions? options, out SitecoreAuthClientCredentials credentials)`
  - `public static bool TryResolveNamedClient(this SitecoreGraphQLOptions? options, string clientName, out string endpoint, out SitecoreAuthClientCredentials credentials, out bool allowInsecureHttp, out bool allowPostRetries)`
- `SitecoreGraphQLOptionsValidator` (`SitecoreGraphQLOptionsValidator.cs`):
  - `internal sealed class SitecoreGraphQLOptionsValidator : IValidateOptions<SitecoreGraphQLOptions>`: Validates `MaxUnauthorizedRetries >= 0`, `MaxCachedClients >= 1`, `MaxRetryBufferBytes > 0`, presence of complete default credentials or at least one named client, and endpoint URI validity.
- `EndpointUriValidator` (`SitecoreGraphQLOptionsValidator.cs`):
  - `public static bool TryValidateEndpoint(string rawEndpoint, bool allowInsecureHttp, out string normalizedEndpoint, out string errorMessage)`
  - `public static string ValidateAndNormalizeEndpoint(string rawEndpoint, bool allowInsecureHttp, string paramName, bool throwInvalidOperation = false)`
  - `public static string SanitizeForLogging(string? rawEndpoint)`
  - `public static string GetRedactedAuthority(Uri? uri)`
- `Log` (`SitecoreGraphQLTelemetry.cs`):
  - `internal static partial class Log` (`[ExcludeFromCodeCoverage]`): Source-generated `[LoggerMessage]` structured logging definitions (`EventId = 1000`–`1007`, `1100`–`1103`, `1200`–`1201`, `1300`).
- `SitecoreTokenCache` (`SitecoreTokenCache.cs`):
  - `internal sealed class SitecoreTokenCache : ISitecoreTokenCache`: Obsolete v1.x compatibility adapter delegating `GetOrRefreshAsync` and `ForceRefreshAsync` to `ISitecoreTokenCoordinator`.

## Invariants
- All types are `internal` and exposed only to authorized test, benchmark, and proxy assemblies via `Properties/InternalsVisibleTo.cs`.
- `CredentialContext.ResiliencePartitionKey` resolves deterministically to `"named:{clientName}"` for named clients, `"default"` for default-credential clients, or `"explicit:{16-char uppercase SHA-256 hex prefix of ClientId}"` for dynamic explicit-credential clients.
- `CredentialForwardingHandler` shares `request.Content` by reference on the forwarded envelope and detaches `forwardedRequest.Content = null` before disposing the envelope if `client.SendAsync` faults, preserving caller ownership of the original `HttpContent`.
- `SitecoreTokenCoordinator` coalesces concurrent refresh operations per `(ClientId, ClientSecret)` using a reference-counted `InFlightRefresh` entry so individual waiter cancellation does not abort the underlying shared refresh for other concurrent callers.
- Before executing a network refresh on a staggered `401`, `SitecoreTokenCoordinator` inspects `Sitecore.API.Foundation.Authorization.Abstractions.ISitecoreTokenCache` (if registered) to reuse an already-refreshed unexpired token, and skips `TryRefreshSitecoreAuthToken` in favor of a single `GetSitecoreAuthToken` call when the cache is cold. If `TryRefreshSitecoreAuthToken` throws `ArgumentException` or returns an empty token, it falls back to `GetSitecoreAuthToken`.
- `EndpointUriValidator` enforces `https://` (or loopback `http://`) unless `allowInsecureHttp = true`, rejects embedded `Uri.UserInfo` (`@` in authority), rejects invalid DNS labels and out-of-range numeric IPv4 literals (such as `999.999.999.999`), and strips query strings (`?sc_apikey=...`) and fragments in `SanitizeForLogging`.

## Side Effects
- `CredentialForwardingHandler.SendAsync` dispatches HTTP requests through `IHttpClientFactory.CreateClient(httpClientName)`.
- `SitecoreTokenCoordinator` creates async DI scopes (`IServiceScopeFactory.CreateAsyncScope()`) per token operation, calls `ISitecoreTokenService.GetSitecoreAuthToken` / `TryRefreshSitecoreAuthToken`, starts `SitecoreGraphQLDiagnostics.ActivitySource` activities (`SitecoreGraphQL.GetToken`, `SitecoreGraphQL.RefreshToken`), and records `SitecoreGraphQLDiagnostics` metrics (`TokenRequests`, `TokenRefreshes`, `TokenRefreshDurationMs`).
- `Log` methods emit structured log entries to `ILogger`.

## Verification
- Run: `dotnet test`
