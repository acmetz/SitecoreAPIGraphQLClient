# Sitecore.API.Foundation.GraphQL

## Overview
Main NuGet class library (`SitecoreAPIGraphQLClient`) implementing thread-safe GraphQL client creation and caching, per-client OAuth2 bearer authentication, multi-tenant credential isolation, endpoint security validation, and opt-in HTTP resilience for Sitecore XM Cloud, Experience Edge, and Authoring/Management APIs.

## Interface
- `ISitecoreGraphQLFactory` (`ISitecoreGraphQLFactory.cs`, namespace `Sitecore.API.Foundation.GraphQL`):
  - `Task<IGraphQLClient> CreateClientAsync(string url, string clientId, string clientSecret, CancellationToken cancellationToken = default)`
  - `Task<IGraphQLClient> CreateClientAsync(string url, CancellationToken cancellationToken = default)`
  - `Task<IGraphQLClient> CreateClientAsync(CancellationToken cancellationToken = default)`
  - `Task<IGraphQLClient> CreateClientByNameAsync(string clientName, CancellationToken cancellationToken = default)`
  - `Task<bool> RefreshTokenAsync(CancellationToken cancellationToken = default)`
  - `Task<bool> RefreshTokenByNameAsync(string clientName, CancellationToken cancellationToken = default)` (includes default interface implementation delegating to `RefreshTokenAsync(cancellationToken)`)
- `SitecoreGraphQLFactory` (`SitecoreGraphQLFactory.cs`, namespace `Sitecore.API.Foundation.GraphQL`):
  - Implements `ISitecoreGraphQLFactory` and `IDisposable` (`Dispose()`, `protected virtual void Dispose(bool disposing)`).
  - `public const string NamedHttpClient = "SitecoreGraphQL"`
  - Constructors:
    - `public SitecoreGraphQLFactory(ISitecoreTokenService tokenService, IOptions<SitecoreGraphQLOptions> options, IHttpClientFactory httpClientFactory, ISitecoreTokenCache tokenCache, ILogger<SitecoreGraphQLFactory>? logger = null)` (retains v0.9.x 5-parameter public constructor compatibility)
    - `internal SitecoreGraphQLFactory(ISitecoreTokenCoordinator coordinator, IOptionsMonitor<SitecoreGraphQLOptions> optionsMonitor, IHttpClientFactory httpClientFactory, ILogger<SitecoreGraphQLFactory>? logger = null)`
  - Additional convenience overloads without `CancellationToken`: `CreateClientAsync(string url, string clientId, string clientSecret)`, `CreateClientAsync(string url)`, `CreateClientAsync()`.
- `SitecoreGraphQLOptions` (`SitecoreGraphQLOptions.cs`, namespace `Sitecore.API.Foundation.GraphQL`):
  - `string? Endpoint { get; set; }` (default `null`)
  - `string? ClientId { get; set; }` (default `null`)
  - `string? ClientSecret { get; set; }` (default `null`)
  - `bool EnableUnauthorizedRefresh { get; set; }` (default `true`)
  - `int MaxUnauthorizedRetries { get; set; }` (default `1`)
  - `bool EnableInternalLoggingSetup { get; set; }` (default `true`)
  - `bool AllowInsecureHttp { get; set; }` (default `false`)
  - `bool AllowPostRetries { get; set; }` (default `false`)
  - `int MaxCachedClients { get; set; }` (default `128`)
  - `long MaxRetryBufferBytes { get; set; }` (default `10485760` / `10 MB`)
  - `Dictionary<string, SitecoreGraphQLClientOptions> Clients { get; set; }` (default empty dictionary)
  - `public override string ToString()`: Formats options with sanitized `Endpoint` and `ClientSecret = ***`.
  - Internal assignment tracking: `internal GraphQLOptionsAssignedMask AssignedMask { get; }`, `internal void ResetAssignmentTracking()`.
- `SitecoreGraphQLClientOptions` (`SitecoreGraphQLOptions.cs`, namespace `Sitecore.API.Foundation.GraphQL`):
  - `string? Endpoint { get; set; }`, `string? ClientId { get; set; }`, `string? ClientSecret { get; set; }`, `bool? AllowInsecureHttp { get; set; }`, `bool? AllowPostRetries { get; set; }` (unset properties inherit from `SitecoreGraphQLOptions`).
  - `public override string ToString()`: Formats named client options with `ClientSecret = ***`.
  - Internal assignment tracking: `internal GraphQLClientOptionsAssignedMask AssignedMask { get; }`, `internal void ResetAssignmentTracking()`.
- `GraphQLOptionsAssignedMask` / `GraphQLClientOptionsAssignedMask` (`SitecoreGraphQLOptions.cs`):
  - `internal enum GraphQLOptionsAssignedMask : uint` (`[Flags]`) and `internal enum GraphQLClientOptionsAssignedMask : byte` (`[Flags]`) tracking explicit property assignments.
- `SitecoreGraphQLDiagnostics` (`SitecoreGraphQLDiagnostics.cs`, namespace `Sitecore.API.Foundation.GraphQL`):
  - `public const string InstrumentationName = "Sitecore.API.Foundation.GraphQL"`
  - `internal static readonly ActivitySource ActivitySource = new(InstrumentationName, "1.0.0")`
  - `internal static readonly Meter Meter = new(InstrumentationName, "1.0.0")`
  - `internal static readonly Counter<long> TokenRequests` (`"sitecore.graphql.token.requests"`)
  - `internal static readonly Counter<long> TokenRefreshes` (`"sitecore.graphql.token.refreshes"`)
  - `internal static readonly Counter<long> AuthRetries` (`"sitecore.graphql.auth.retries"`)
  - `internal static readonly Counter<long> ClientCacheEvictions` (`"sitecore.graphql.client_cache.evictions"`)
  - `internal static readonly Histogram<double> TokenRefreshDurationMs` (`"sitecore.graphql.token.refresh.duration"`, unit `"ms"`)
  - `internal static string GetClientTag(string? clientName, bool isExplicit = false)`
- Subdirectories & Contracts:
  - `Abstractions`: [./Abstractions/README.md](./Abstractions/README.md) - Core interfaces for token extraction (`ITokenValueAccessor`) and legacy caching (`ISitecoreTokenCache`).
  - `DependencyInjection`: [./DependencyInjection/README.md](./DependencyInjection/README.md) - `ServiceCollectionExtensions.AddSitecoreGraphQL` overloads, startup options validation, and resilience pipeline partitioning.
  - `Http`: [./Http/README.md](./Http/README.md) - `SitecoreTokenHandler` delegating handler for bearer token injection, payload buffering, and `401` retry replay.
  - `Internal`: [./Internal/README.md](./Internal/README.md) - Internal token coordinator, credential forwarding handler, cache keys, URI/options validators, and `[LoggerMessage]` definitions.
  - `Properties`: [./Properties/README.md](./Properties/README.md) - Assembly `InternalsVisibleTo` declarations for test and benchmark suites.

## Invariants
- Shared client instances: `IGraphQLClient` instances (`GraphQLHttpClient` configured with `NewtonsoftJsonSerializer`) are cached in `_clients` (`ConcurrentDictionary<ClientCacheKey, Lazy<CachedClientEntry>>`) and shared across callers; callers must not dispose returned instances manually.
- Bounded dynamic client cache: Dynamic clients (`isConfiguredClient = false`) are tracked in `_dynamicEvictionQueue` and evicted in FIFO order when dynamic entries exceed `Math.Max(1, MaxCachedClients)`. Configured default and named clients (`isConfiguredClient = true`) are exempt from eviction.
- Socket rotation preservation: Each cached `GraphQLHttpClient` is constructed over a lightweight `CredentialForwardingHandler` with `HttpClient.Timeout = Timeout.InfiniteTimeSpan`, delegating transport execution and timeouts to `IHttpClientFactory.CreateClient("SitecoreGraphQL")` so pooled socket handlers continue rotating every 2 minutes for DNS updates.
- Endpoint security: Enforces `https://` (or loopback `http://localhost` / `http://127.0.0.1`) unless `AllowInsecureHttp = true`, and unconditionally rejects embedded `Uri.UserInfo`.
- Secret and query-string redaction: Query strings (such as `?sc_apikey=...`) are stripped from logged/exception URIs and `ClientSecret` is always masked as `***` in `ToString()`.

## Side Effects
- Caches and evicts `GraphQLHttpClient` instances in memory; increments `SitecoreGraphQLDiagnostics.ClientCacheEvictions` on FIFO eviction without disposing evicted instances immediately so in-flight requests complete safely.
- `RefreshTokenAsync` and `RefreshTokenByNameAsync` trigger outbound OAuth2 token refresh calls via `ISitecoreTokenCoordinator` (or `_legacyTokenCache` when constructed via the legacy 5-parameter constructor).
- `Dispose()` disposes all cached `IGraphQLClient` instances and marks the factory disposed (`ObjectDisposedException` on subsequent calls).

## Verification
- Run: `dotnet test`
