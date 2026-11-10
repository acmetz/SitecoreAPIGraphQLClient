# SitecoreAPIGraphQLClient

[![NuGet](https://img.shields.io/nuget/v/SitecoreAPIGraphQLClient.svg)](https://www.nuget.org/packages/SitecoreAPIGraphQLClient)
[![PR Check](https://github.com/acmetz/SitecoreAPIGraphQLClient/actions/workflows/pr-check.yml/badge.svg?branch=main)](https://github.com/acmetz/SitecoreAPIGraphQLClient/actions/workflows/pr-check.yml)
[![NuGet Release Pipeline](https://github.com/acmetz/SitecoreAPIGraphQLClient/actions/workflows/nuget-release.yml/badge.svg?branch=main)](https://github.com/acmetz/SitecoreAPIGraphQLClient/actions/workflows/nuget-release.yml)
[![License](https://img.shields.io/github/license/acmetz/SitecoreAPIGraphQLClient.svg)](LICENSE)
![.NET](https://img.shields.io/badge/.NET-8%20%7C%209%20%7C%2010-512BD4?logo=.net&logoColor=white)

A high-performance, thread-safe `.NET 8`, `.NET 9`, and `.NET 10` GraphQL client factory and HTTP authentication pipeline for Sitecore XM Cloud, Experience Edge, and Authoring/Management APIs. Built on [`SitecoreAPIAuthorization`](https://www.nuget.org/packages/SitecoreAPIAuthorization) `1.0.0`.

## Key Features

- Multi-targeted for `.NET 8.0`, `.NET 9.0`, and `.NET 10.0` with portable `.snupkg` symbols and SourceLink
- Strict per-client `(url, clientId, clientSecret)` OAuth2 bearer-token isolation across named and dynamic multi-tenant clients
- Preserves 2-minute `IHttpClientFactory` socket handler rotation for DNS updates while caching `IGraphQLClient` instances (`MaxCachedClients` FIFO eviction for dynamic tenants)
- Automatic `HTTP 401 Unauthorized` recovery with single-flight token refresh coalescing (`TryRefreshSitecoreAuthToken`) and `HTTP POST` GraphQL mutation payload replay
- Opt-in HTTP resilience (`Microsoft.Extensions.Http.Resilience`) partitioned per `(authority, client)` with `HTTP POST` retries disabled by default (`AllowPostRetries = false`) and configurable per read-only client
- Secure by default: enforces `https://` (or loopback `http://`) unless `AllowInsecureHttp = true`, rejects embedded `Uri.UserInfo`, redacts query strings (`?sc_apikey=...`) from logs/exceptions, and masks secrets in `ToString()`
- Compatible with both `Scoped` and `Singleton` `ISitecoreTokenService` lifetimes (`ValidateScopes = true`, `ValidateOnBuild = true`)
- Built-in `System.Diagnostics` (`ActivitySource` and `Meter`) telemetry and source-generated `[LoggerMessage]` logging

## Installation

```bash
dotnet add package SitecoreAPIAuthorization
dotnet add package SitecoreAPIGraphQLClient
```

## Configuration (`appsettings.json`)

```json
{
  "SitecoreAuthentication": {
    "AuthTokenUrl": "https://auth.sitecorecloud.io/oauth/token"
  },
  "Sitecore": {
    "GraphQL": {
      "Endpoint": "https://edge.sitecorecloud.io/api/graphql/v1?sc_apikey=your-api-key",
      "ClientId": "your-client-id",
      "ClientSecret": "your-client-secret",
      "EnableUnauthorizedRefresh": true,
      "MaxUnauthorizedRetries": 1,
      "AllowInsecureHttp": false,
      "AllowPostRetries": false,
      "MaxCachedClients": 128,
      "Clients": {
        "authoring": {
          "Endpoint": "https://xmcloud.example/sitecore/api/authoring/graphql/v1",
          "ClientId": "authoring-client-id",
          "ClientSecret": "authoring-client-secret"
        },
        "delivery": {
          "Endpoint": "https://edge.sitecorecloud.io/api/graphql/v1?sc_apikey=delivery-key",
          "ClientId": "delivery-client-id",
          "ClientSecret": "delivery-client-secret",
          "AllowPostRetries": true
        }
      }
    }
  }
}
```

## Registration

```csharp
using Sitecore.API.Foundation.Authorization.DependencyInjection;
using Sitecore.API.Foundation.GraphQL.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// 1. Register Sitecore OAuth2 token service (Scoped by default)
builder.Services.AddSitecoreAuthentication(builder.Configuration);

// 2. Register Sitecore GraphQL client factory (with optional HTTP resilience)
builder.Services.AddSitecoreGraphQL(
    builder.Configuration,
    configureResilience: resilience =>
    {
        resilience.Retry.MaxRetryAttempts = 2;
        resilience.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
    });
```

Delegate-based registration without `IConfiguration` is also supported:

```csharp
builder.Services.AddSitecoreGraphQL(options =>
{
    options.Endpoint = "https://edge.sitecorecloud.io/api/graphql/v1";
    options.ClientId = "your-client-id";
    options.ClientSecret = "your-client-secret";
});
```

## Basic Usage

`GraphQLHttpClient` uses `NewtonsoftJsonSerializer` (`Newtonsoft.Json`). Deserialize responses into strongly typed DTOs or `Newtonsoft.Json.Linq.JObject`:

```csharp
using GraphQL;
using Newtonsoft.Json;
using Sitecore.API.Foundation.GraphQL;

public sealed class PageQueryResponse
{
    [JsonProperty("item")]
    public PageItem? Item { get; set; }
}

public sealed class PageItem
{
    [JsonProperty("id")]
    public Guid Id { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;
}

public sealed class ContentConsumer
{
    private readonly ISitecoreGraphQLFactory _factory;

    public ContentConsumer(ISitecoreGraphQLFactory factory) => _factory = factory;

    public async Task<PageItem?> GetHomeItemAsync(CancellationToken ct = default)
    {
        // Do not manually dispose shared IGraphQLClient instances returned by the factory
        var client = await _factory.CreateClientAsync(ct);
        var request = new GraphQLRequest
        {
            Query = "query GetItem($path: String!, $language: String!) { item(path: $path, language: $language) { id name } }",
            Variables = new { path = "/sitecore/content/Home", language = "en" }
        };

        var response = await client.SendQueryAsync<PageQueryResponse>(request, ct);
        return response.Data?.Item;
    }
}
```

## Named, Explicit, and Manual Refresh APIs

```csharp
// Named clients (any unset property inherits from top-level Sitecore:GraphQL options)
var authoringClient = await factory.CreateClientByNameAsync("authoring", ct);
var deliveryClient  = await factory.CreateClientByNameAsync("delivery", ct);

// Explicit endpoint + explicit credentials (isolated per URL + ClientId + ClientSecret)
var tenantClient = await factory.CreateClientAsync(
    url: "https://edge.sitecorecloud.io/api/graphql/v1?sc_apikey=tenant-key",
    clientId: "tenant-client-id",
    clientSecret: "tenant-client-secret",
    cancellationToken: ct);

// Trusted custom endpoint with configured default credentials (never pass untrusted user URLs)
var customEndpointClient = await factory.CreateClientAsync("https://trusted.sitecore/graphql", ct);

// Manual token refresh across all configured credentials or for a single named client
bool allRefreshed = await factory.RefreshTokenAsync(ct);
bool deliveryRefreshed = await factory.RefreshTokenByNameAsync("delivery", ct);
```

## Benchmarks

Measured with [BenchmarkDotNet](https://benchmarkdotnet.org/) `v0.15.8` against a 64-client populated `SitecoreGraphQLFactory` and in-memory HTTP transport handlers:

```text
BenchmarkDotNet v0.15.8, macOS 27.0.1 (26A434) [Darwin 27.0.0]
Apple M1 Max, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.401
  .NET 8.0  : .NET 8.0.25 (8.0.2526.11203), Arm64 RyuJIT armv8.0-a
  .NET 9.0  : .NET 9.0.14 (9.0.1426.11910), Arm64 RyuJIT armv8.0-a
  .NET 10.0 : .NET 10.0.12 (10.0.1226.42308), Arm64 RyuJIT armv8.0-a
```

| Method | Runtime | Mean | Error | StdDev | Gen0 | Gen1 | Allocated |
| :--- | :--- | ---: | ---: | ---: | ---: | ---: | ---: |
| `CreateClientAsync` (Explicit Cache Hit) | .NET 8.0 | 184.9 ns | 0.53 ns | 0.49 ns | 0.0114 | - | 72 B |
| `CreateClientAsync` (Explicit Cache Hit) | .NET 9.0 | 184.1 ns | 0.33 ns | 0.31 ns | 0.0114 | - | 72 B |
| `CreateClientAsync` (Explicit Cache Hit) | .NET 10.0 | 173.5 ns | 0.55 ns | 0.49 ns | 0.0114 | - | 72 B |
| `CreateClientByNameAsync` (Named Cache Hit) | .NET 8.0 | 201.1 ns | 0.62 ns | 0.55 ns | 0.0114 | - | 72 B |
| `CreateClientByNameAsync` (Named Cache Hit) | .NET 9.0 | 231.3 ns | 0.62 ns | 0.58 ns | 0.0114 | - | 72 B |
| `CreateClientByNameAsync` (Named Cache Hit) | .NET 10.0 | 196.7 ns | 0.46 ns | 0.43 ns | 0.0114 | - | 72 B |
| `SitecoreTokenHandler.SendAsync` (200 OK POST) | .NET 8.0 | 666.7 ns | 2.30 ns | 2.16 ns | 0.3824 | 0.0019 | 2,400 B |
| `SitecoreTokenHandler.SendAsync` (200 OK POST) | .NET 9.0 | 617.7 ns | 2.27 ns | 2.13 ns | 0.3614 | 0.0010 | 2,272 B |
| `SitecoreTokenHandler.SendAsync` (200 OK POST) | .NET 10.0 | 533.4 ns | 1.71 ns | 1.34 ns | 0.3595 | 0.0010 | 2,256 B |
| `SitecoreTokenHandler.SendAsync` (401 Refresh + POST Replay) | .NET 8.0 | 1,724.6 ns | 4.20 ns | 3.93 ns | 0.8793 | 0.0057 | 5,520 B |
| `SitecoreTokenHandler.SendAsync` (401 Refresh + POST Replay) | .NET 9.0 | 1,771.4 ns | 5.64 ns | 5.00 ns | 0.8888 | 0.0076 | 5,584 B |
| `SitecoreTokenHandler.SendAsync` (401 Refresh + POST Replay) | .NET 10.0 | 1,562.0 ns | 6.04 ns | 5.04 ns | 0.8774 | 0.0076 | 5,504 B |

Run the benchmark suite locally:

```bash
dotnet run --configuration Release --framework net10.0 \
  --project tests/Sitecore.API.Foundation.GraphQL.Benchmarks/Sitecore.API.Foundation.GraphQL.Benchmarks.csproj
```

## Options Summary

| Option | Default | Description |
| :--- | :--- | :--- |
| `Endpoint` | `null` | Default GraphQL endpoint URL (`https://` or loopback `http://` by default). |
| `ClientId` | `null` | Default OAuth2 client identifier. |
| `ClientSecret` | `null` | Default OAuth2 client secret (masked as `***` in `ToString()`). |
| `EnableUnauthorizedRefresh` | `true` | Refresh token and replay request on `401 Unauthorized`. |
| `MaxUnauthorizedRetries` | `1` | Maximum retry attempts after an initial `401` (`0` disables retries). |
| `EnableInternalLoggingSetup` | `true` | Calls `AddLogging()` if the host has not configured logging. |
| `AllowInsecureHttp` | `false` | Permits non-loopback `http://` endpoints (for internal container networks). |
| `AllowPostRetries` | `false` | Permits transient resilience retries on `HTTP POST` requests (overridable per named client). |
| `MaxCachedClients` | `128` | Maximum dynamic clients cached before FIFO eviction (configured clients are exempt). |
| `MaxRetryBufferBytes` | `10485760` (`10 MB`) | Maximum request content size pre-buffered for `401` / resilience `POST` replay. |
| `Clients` | `{}` | Dictionary of named `SitecoreGraphQLClientOptions` entries inheriting unset defaults. |

## Documentation

- [Extended Developer and Architecture Guide](docs/README.md) — Multi-tenant patterns, `AllowInsecureHttp`, HTTP resilience & `AllowPostRetries`, `IGraphQLClient` rotation/disposal, WebSocket scope, `System.Diagnostics` telemetry, and `v0.9.x` migration notes.
- [Contributing Guide](CONTRIBUTING.md) — Contributor prerequisites, `.slnx` layout, coverage/pack gates, and OIDC release workflow.
- [Changelog](CHANGELOG.md) — Release history.

## License

MIT License — see [LICENSE](LICENSE) for details.