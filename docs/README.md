# SitecoreAPIGraphQLClient — Extended Developer and Architecture Guide

This guide covers multi-tenant configuration, security hardening, HTTP resilience, token refresh coordination, serialization, observability, and operational best practices for `SitecoreAPIGraphQLClient` (`Sitecore.API.Foundation.GraphQL` `1.0.0`).

---

## Table of Contents

1. [Architecture Overview](#architecture-overview)
2. [Two-Package Setup and DI Lifetimes](#two-package-setup-and-di-lifetimes)
3. [Named Clients and Configuration Inheritance](#named-clients-and-configuration-inheritance)
4. [Multi-Tenant Explicit Credentials and Client Caching](#multi-tenant-explicit-credentials-and-client-caching)
5. [Endpoint Security and Internal Container HTTP](#endpoint-security-and-internal-container-http)
6. [HTTP 401 Recovery and Payload Buffering](#http-401-recovery-and-payload-buffering)
7. [Opt-In HTTP Resilience and POST Retry Safety](#opt-in-http-resilience-and-post-retry-safety)
8. [Newtonsoft.Json Serialization and GUID Handling](#newtonsoftjson-serialization-and-guid-handling)
9. [Observability (Logging, Metrics, and Tracing)](#observability-logging-metrics-and-tracing)
10. [Known Scope Boundaries](#known-scope-boundaries)
11. [Migration Notes from v0.9.x to v1.0.0](#migration-notes-from-v09x-to-v100)

---

## Architecture Overview

`SitecoreAPIGraphQLClient` layers per-client GraphQL management over [`SitecoreAPIAuthorization`](https://www.nuget.org/packages/SitecoreAPIAuthorization) `1.0.0` (`ISitecoreTokenService`):

1. **`SitecoreGraphQLFactory`** validates URLs and credentials synchronously and caches `GraphQLHttpClient` instances (`NewtonsoftJsonSerializer`) keyed by canonical endpoint URL, `ClientId`, `ClientSecret`, optional client name, and retry policy.
2. **`CredentialForwardingHandler`** is a lightweight socket-free `HttpMessageHandler` attached to each cached `GraphQLHttpClient`. For every outbound GraphQL query or mutation, it creates a shallow `HttpRequestMessage` envelope, stamps a secret-masked `CredentialContext` into `HttpRequestMessage.Options`, shares `request.Content` by reference without copying bytes, and dispatches through `IHttpClientFactory.CreateClient(SitecoreGraphQLFactory.NamedHttpClient)` (`"SitecoreGraphQL"`). This preserves `ReferenceEquals` on cached `IGraphQLClient` instances while allowing `IHttpClientFactory` to rotate pooled socket handlers every 2 minutes for DNS updates.
3. **`SitecoreTokenHandler`** sits outermost on the `SitecoreGraphQLFactory.NamedHttpClient` (`"SitecoreGraphQL"`) pipeline. It reads per-request credentials from `HttpRequestMessage.Options` (falling back to default `SitecoreGraphQLOptions`), resolves bearer tokens via a stateless internal coordinator, pre-buffers request content up to `MaxRetryBufferBytes` when replay is enabled, and clones fresh `HttpRequestMessage` instances over `ReadOnlyMemoryContent` strictly when an `HTTP 401 Unauthorized` retry occurs.
4. **Stateless Token Coordination** resolves `ISitecoreTokenService` inside async DI scopes (`IServiceScopeFactory.CreateAsyncScope()`) and coalesces concurrent `401` or manual refresh bursts per `(ClientId, ClientSecret)` into a single `ISitecoreTokenService.TryRefreshSitecoreAuthToken` call.
5. **Optional `StandardResilienceHandler`** sits inside `SitecoreTokenHandler` when `configureResilience` is supplied, partitioned by `(authority, client)` with `HTTP POST` retries disabled by default.

See the [EGM Source Architectural Blueprint](../src/README.md) and its linked component contracts under [../src/Sitecore.API.Foundation.GraphQL/README.md](../src/Sitecore.API.Foundation.GraphQL/README.md) for per-directory symbol interfaces, invariants, and side effects.

---

## Two-Package Setup and DI Lifetimes

Install both packages:

```bash
dotnet add package SitecoreAPIAuthorization
dotnet add package SitecoreAPIGraphQLClient
```

Register authentication and GraphQL services during startup:

```csharp
using Sitecore.API.Foundation.Authorization.DependencyInjection;
using Sitecore.API.Foundation.GraphQL.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// Registers ISitecoreTokenService as Scoped by default (backed by a singleton token cache)
builder.Services.AddSitecoreAuthentication(builder.Configuration);

// Registers ISitecoreGraphQLFactory, SitecoreTokenHandler, and options validation
builder.Services.AddSitecoreGraphQL(builder.Configuration);
```

`AddSitecoreGraphQL` resolves `ISitecoreTokenService` via `IServiceScopeFactory.CreateAsyncScope()` per token operation, so it works with:
- `AddSitecoreAuthentication(...)` (`Scoped` `ISitecoreTokenService`)
- `AddSitecoreAuthenticationSingleton(...)` (`Singleton` `ISitecoreTokenService`)
- Custom `ISitecoreTokenService` implementations implementing `IAsyncDisposable`
- Hosts enforcing `ValidateScopes = true` and `ValidateOnBuild = true`

---

## Named Clients and Configuration Inheritance

Named clients configured under `Sitecore:GraphQL:Clients:<name>` inherit any omitted (`null` or whitespace) `Endpoint`, `ClientId`, `ClientSecret`, `AllowInsecureHttp`, or `AllowPostRetries` setting from top-level `Sitecore:GraphQL` defaults.

```json
{
  "SitecoreAuthentication": {
    "AuthTokenUrl": "https://auth.sitecorecloud.io/oauth/token"
  },
  "Sitecore": {
    "GraphQL": {
      "ClientId": "shared-xmcloud-client-id",
      "ClientSecret": "shared-xmcloud-client-secret",
      "Clients": {
        "authoring": {
          "Endpoint": "https://xmcloudcm.localhost/sitecore/api/authoring/graphql/v1"
        },
        "delivery": {
          "Endpoint": "https://edge.sitecorecloud.io/api/graphql/v1?sc_apikey=your-api-key",
          "ClientId": "delivery-client-id",
          "ClientSecret": "delivery-client-secret",
          "AllowPostRetries": true
        }
      }
    }
  }
}
```

Startup validation (`ValidateOnStart()`) verifies that every configured named client resolves non-empty `Endpoint`, `ClientId`, and `ClientSecret` values and a valid endpoint URI.

---

## Multi-Tenant Explicit Credentials and Client Caching

### Explicit Credentials and Query Parameters

```csharp
var tenantClient = await factory.CreateClientAsync(
    url: "https://edge.sitecorecloud.io/api/graphql/v1?sc_apikey=tenant-a",
    clientId: tenantClientId,
    clientSecret: tenantClientSecret,
    cancellationToken: ct);
```

- Query strings on endpoint URLs (such as `?sc_apikey=...`) are preserved for GraphQL HTTP dispatch and cache-key differentiation, while automatically stripped from log messages, exception messages, and `ToString()` output.
- Rotating `clientSecret` on the same `(url, clientId)` creates a distinct cached client entry with isolated credentials.

### Client Lifetime, Disposal, and Secret Rotation

- **Do not dispose shared clients manually**: `IGraphQLClient` instances returned by `ISitecoreGraphQLFactory` are cached and shared across callers. `SitecoreGraphQLFactory` implements `IDisposable` and disposes cached wrapper instances automatically when the DI container shuts down.
- **Dynamic client eviction (`MaxCachedClients`)**: Configured default and named clients are pinned for the lifetime of the factory. Dynamic clients created via `CreateClientAsync(...)` are bounded by `MaxCachedClients` (default `128`); when exceeded, the oldest dynamic entry is evicted (FIFO) without disposing its socket-free wrapper so in-flight requests never fail with `ObjectDisposedException`.
- **Dynamic secret rotation (`IOptionsMonitor`)**: Each cached `IGraphQLClient` binds the credential snapshot active when it was created. When rotating secrets dynamically through `IOptionsMonitor<SitecoreGraphQLOptions>`, resolve clients via `CreateClientAsync` / `CreateClientByNameAsync` per operation rather than storing an `IGraphQLClient` reference in a long-lived field.

---

## Endpoint Security and Internal Container HTTP

### Default HTTPS Enforcement and `AllowInsecureHttp`

By default (`AllowInsecureHttp = false`), both startup validation and `CreateClientAsync` accept:
- `https://` endpoints on any host
- `http://` endpoints on loopback hosts (`localhost`, `127.0.0.1`, `[::1]`) for local development

Non-loopback cleartext `http://` endpoints (for example `http://cm/sitecore/api/graph/edge` inside an isolated Docker Compose network) are rejected unless `AllowInsecureHttp = true` is explicitly configured globally or on that named client:

```csharp
builder.Services.AddSitecoreGraphQL(options =>
{
    options.Clients["local-cm"] = new SitecoreGraphQLClientOptions
    {
        Endpoint = "http://cm/sitecore/api/graph/edge",
        ClientId = "cm-client-id",
        ClientSecret = "cm-client-secret",
        AllowInsecureHttp = true
    };
});
```

### Unconditional `Uri.UserInfo` Rejection and Trusted URLs

- Endpoints containing embedded user info (`https://user:pass@host/graphql`) are rejected unconditionally even when `AllowInsecureHttp = true`.
- **Trusted URL warning**: `CreateClientAsync(string url, CancellationToken)` attaches your configured default `ClientId`/`ClientSecret` bearer token to `url`. Never pass untrusted user-supplied URLs to default-credential overloads, as doing so would send your bearer token to an arbitrary host.

---

## HTTP 401 Recovery and Payload Buffering

When an outbound GraphQL request receives `401 Unauthorized` and `EnableUnauthorizedRefresh = true` with `MaxUnauthorizedRetries > 0`:

1. Request content (`HttpContent`) up to `MaxRetryBufferBytes` (default `10 MB`) is pre-buffered via `LoadIntoBufferAsync` (a zero-copy no-op on `GraphQL.Client`'s `StringContent`). Payloads larger than `MaxRetryBufferBytes` still dispatch unbuffered on the initial request; if a `> 10 MB` request receives `401`, replay is skipped with a warning rather than failing `200 OK` uploads.
2. Concurrent `401` responses for the same `(ClientId, ClientSecret)` coalesce into a single `ISitecoreTokenService.TryRefreshSitecoreAuthToken` call, evicting the revoked token from `SitecoreAPIAuthorization`'s cache.
3. `SitecoreTokenHandler` clones a fresh `HttpRequestMessage` wrapping `ReadOnlyMemoryContent` and retries immediately on the first `401` (`0 ms` delay), applying jittered exponential backoff (`200 ms` base capped at `5000 ms`) on any additional configured retries.

Manual refresh is also supported:
- `await factory.RefreshTokenAsync(ct)` refreshes default credentials and all configured named clients (deduplicating shared credential pairs).
- `await factory.RefreshTokenByNameAsync("delivery", ct)` refreshes a specific configured named client.

---

## Opt-In HTTP Resilience and POST Retry Safety

Standard HTTP resilience (`Microsoft.Extensions.Http.Resilience`) can be enabled on the `"SitecoreGraphQL"` pipeline using the 4-parameter `AddSitecoreGraphQL` overloads:

```csharp
builder.Services.AddSitecoreGraphQL(
    builder.Configuration,
    configureResilience: resilience =>
    {
        resilience.Retry.MaxRetryAttempts = 2;
        resilience.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
    },
    configureClient: httpBuilder =>
    {
        httpBuilder.ConfigureHttpClient(client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MySitecoreApp/1.0");
        });
    });
```

Key behaviors:
- **Opt-in only**: Passing `configureResilience: null` (or calling the 2-parameter `AddSitecoreGraphQL` overloads) adds zero resilience handlers.
- **Handler ordering**: `SitecoreTokenHandler` (outermost) -> `StandardResilienceHandler` (middle) -> `configureClient` handlers -> primary transport handler. Transient `5xx`/`429` retries inside the resilience pipeline reuse the already-attached bearer token without re-querying `ISitecoreTokenService`, while `401` responses bubble out to `SitecoreTokenHandler` for token refresh.
- **Per-client circuit-breaker isolation**: Resilience pipelines are partitioned by `(redactedAuthority, clientPartitionKey)` so one tenant's `429`/`503` responses on a shared host (such as `https://edge.sitecorecloud.io`) do not trip another client's circuit breaker. Dynamic explicit-client partitions are capped at `MaxCachedClients` using non-sensitive SHA-256 `ClientId` prefixes.
- **Safe `HTTP POST` defaults (`AllowPostRetries = false`)**: Because GraphQL sends state-changing Authoring/Management `mutation` operations over `HTTP POST`, automatic `POST` retries are disabled by default (`AllowPostRetries = false`). For read-only query clients (such as Experience Edge delivery), set `AllowPostRetries = true` on that `SitecoreGraphQLClientOptions` entry to enable transient `POST` retries selectively.

---

## Newtonsoft.Json Serialization and GUID Handling

`SitecoreGraphQLFactory` configures `GraphQLHttpClient` with `NewtonsoftJsonSerializer` (`GraphQL.Client.Serializer.Newtonsoft`). Use `GraphQL.GraphQLRequest` with strongly typed DTOs (`Newtonsoft.Json` attributes) or `Newtonsoft.Json.Linq.JObject`—do not pass `System.Text.Json.JsonDocument` or `JsonElement` as the response type.

```csharp
using GraphQL;
using Newtonsoft.Json;

public sealed class ItemQueryResult
{
    [JsonProperty("item")]
    public SitecoreItemDto? Item { get; set; }
}

public sealed class SitecoreItemDto
{
    [JsonProperty("id")]
    public Guid Id { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;
}

var client = await factory.CreateClientByNameAsync("delivery", ct);
var request = new GraphQLRequest
{
    Query = "query GetItem($path: String!, $language: String!) { item(path: $path, language: $language) { id name } }",
    Variables = new { path = "/sitecore/content/Home", language = "en" }
};

var response = await client.SendQueryAsync<ItemQueryResult>(request, ct);
Guid? itemId = response.Data?.Item?.Id;
```

`System.Guid` values serialize as hyphenated strings (`"D"` format) in mutation variables and deserialize symmetrically from `"D"`, `"N"`, `"B"`, and `"P"` GUID formats in responses.

---

## Observability (Logging, Metrics, and Tracing)

### OpenTelemetry / `System.Diagnostics`

Register `SitecoreGraphQLDiagnostics.InstrumentationName` (`"Sitecore.API.Foundation.GraphQL"`) with OpenTelemetry or `MeterListener`/`ActivityListener`:

- **`ActivitySource` (`"Sitecore.API.Foundation.GraphQL"`)**:
  - `SitecoreGraphQL.GetToken`
  - `SitecoreGraphQL.RefreshToken`
- **`Meter` (`"Sitecore.API.Foundation.GraphQL"`)**:
  - `sitecore.graphql.token.requests` (`Counter<long>`) — Bearer token lookups
  - `sitecore.graphql.token.refreshes` (`Counter<long>`) — Coalesced OAuth token refresh executions
  - `sitecore.graphql.auth.retries` (`Counter<long>`) — `HTTP 401` retry attempts
  - `sitecore.graphql.client_cache.evictions` (`Counter<long>`) — Dynamic client cache FIFO evictions
  - `sitecore.graphql.token.refresh.duration` (`Histogram<double>`, `ms`) — Token refresh latency

Token and authentication telemetry tags use bounded low-cardinality `client.name` values (configured named client key, `"default"`, or `"explicit"`), while `sitecore.graphql.client_cache.evictions` is emitted untagged; telemetry never includes raw URLs, query strings, client IDs, or secrets.

---

## Known Scope Boundaries

- **GraphQL WebSocket Subscriptions**: `GraphQLHttpClient` establishes WebSocket subscription streams (`CreateSubscriptionStream`) directly over `ClientWebSocket` rather than `HttpMessageHandler`. `SitecoreTokenHandler` bearer-token injection and `StandardResilienceHandler` policies apply to HTTP GraphQL queries and mutations (`SendQueryAsync` / `SendMutationAsync`).

---

## Migration Notes from v0.9.x to v1.0.0

1. **`SitecoreAPIAuthorization` `1.0.0`**: Ensure your host registers `ISitecoreTokenService` via `SitecoreAPIAuthorization` `1.0.0` (`services.AddSitecoreAuthentication(...)` or `AddSitecoreAuthenticationSingleton(...)`).
2. **Non-loopback `http://` endpoints**: Non-loopback `http://` endpoints now throw `OptionsValidationException` / `ArgumentException` by default. Migrate to `https://` or set `AllowInsecureHttp = true` for trusted internal container endpoints.
3. **`[Obsolete] ISitecoreTokenCache` (`CS0618`)**: `Sitecore.API.Foundation.GraphQL.ISitecoreTokenCache` remains binary-compatible as a pass-through adapter to `ISitecoreTokenService`, marked `[Obsolete]` for removal in `v2.0.0`. Remove direct references to the local cache interface or suppress `CS0618` during transition.
4. **Removed unused `GraphQL` (`8.5.0`) server dependency**: `SitecoreAPIGraphQLClient` `1.0.0` depends only on `GraphQL.Client`, `GraphQL.Client.Abstractions`, and `GraphQL.Client.Serializer.Newtonsoft`. If your application directly referenced server-side `GraphQL` types transitively through `SitecoreAPIGraphQLClient`, add an explicit `<PackageReference Include="GraphQL" />` to your application project.
