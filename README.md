# SitecoreAPIGraphQLClient

[![NuGet](https://img.shields.io/nuget/v/SitecoreGraphQLClient.svg)](https://www.nuget.org/packages/SitecoreGraphQLClient)
<!--[![NuGet Downloads](https://img.shields.io/nuget/dt/SitecoreGraphQLClient.svg)](https://www.nuget.org/packages/SitecoreGraphQLClient)-->
[![PR Check](https://github.com/acmetz/SitecoreAPIGraphQLClient/actions/workflows/pr-check.yml/badge.svg?branch=main)](https://github.com/acmetz/SitecoreAPIGraphQLClient/actions/workflows/pr-check.yml)
[![NuGet Release Pipeline](https://github.com/acmetz/SitecoreAPIGraphQLClient/actions/workflows/nuget-release.yml/badge.svg?branch=main)](https://github.com/acmetz/SitecoreAPIGraphQLClient/actions/workflows/nuget-release.yml)
[![License](https://img.shields.io/github/license/acmetz/SitecoreAPIGraphQLClient.svg)](LICENSE)
![.NET](https://img.shields.io/badge/.NET-8%20%7C%209-512BD4?logo=.net&logoColor=white)

A .NET 8 class library that provides a DI-friendly, thread-safe factory for GraphQL clients targeting Sitecore endpoints. It integrates with an authorization service to inject bearer tokens and supports configurable token refresh behavior.

## Features
- Thread-safe GraphQL client factory with per-URL and credential caching (key: url::clientId)
- Token injection via HttpClient DelegatingHandler using ISitecoreTokenService
- Configurable refresh on 401 Unauthorized (EnableUnauthorizedRefresh, MaxUnauthorizedRetries)
- Manual token refresh API: ISitecoreGraphQLFactory.RefreshTokenAsync()
- Options binding, validation, and DI extension for easy setup
- Named clients support via configuration (multiple endpoints/credentials)
- Optional internal logging setup toggle to assist authorization service logging without requiring host AddLogging
- Unit tests using xUnit, Shouldly, and Moq

## Requirements
- .NET 8 SDK/runtime

## Installation
- NuGet package ID: SitecoreAPIGraphQLClient
- Using CLI
  ```bash
  dotnet add package SitecoreAPIGraphQLClient
  ```
- Using PackageReference
  ```xml
  <ItemGroup>
    <PackageReference Include="SitecoreAPIGraphQLClient" Version="x.y.z" />
  </ItemGroup>
  ```

## Configuration (appsettings.json)
```json
{
  "Sitecore": {
    "GraphQL": {
      "Endpoint": "https://your.sitecore/graphql",
      "ClientId": "your-client-id",
      "ClientSecret": "your-client-secret",
      "EnableUnauthorizedRefresh": true,
      "MaxUnauthorizedRetries": 1,
      "EnableInternalLoggingSetup": true,
      "Clients": {
        "content": {
          "Endpoint": "https://content/graphql",
          "ClientId": "content-id",
          "ClientSecret": "content-secret"
        },
        "search": {
          "Endpoint": "https://search/graphql",
          "ClientId": "search-id",
          "ClientSecret": "search-secret"
        }
      }
    }
  }
}
```

## Registration (Program.cs)
```csharp
var builder = WebApplication.CreateBuilder(args);
// Bind options and register factory + HTTP pipeline
builder.Services.AddSitecoreGraphQL(builder.Configuration);
// Also register an ISitecoreTokenService implementation provided by Sitecore API Authorization
```

### Optional internal logging setup
By default, AddSitecoreGraphQL will try to register minimal logging services (LoggerFactory and ILogger<T>) if the host did not already add logging, so that downstream services like the Sitecore Token Service can receive ILogger<T>. To opt out, set EnableInternalLoggingSetup to false in configuration.

## Basic usage
```csharp
public class Consumer
{
    private readonly ISitecoreGraphQLFactory _factory;
    public Consumer(ISitecoreGraphQLFactory factory) => _factory = factory;

    public async Task QueryAsync(CancellationToken ct = default)
    {
        // Uses Endpoint, ClientId, ClientSecret from configuration
        var client = await _factory.CreateClientAsync(ct);

        var request = new GraphQL.Client.Abstractions.GraphQLRequest
        {
            Query = "query Example { ping }"
        };
        var response = await client.SendQueryAsync<System.Text.Json.JsonDocument>(request, ct);
        var data = response.Data;
    }
}
```

## Named clients usage
```csharp
// appsettings.json contains Sitecore:GraphQL:Clients:content and :search

var contentClient = await factory.CreateClientByNameAsync("content", ct);
var searchClient  = await factory.CreateClientByNameAsync("search", ct);

// contentClient and searchClient are cached independently by url::clientId
```

## Advanced: named client from sub-section configuration
```csharp
// You can bind a sub-section rather than full configuration, e.g. from an options object
var graphQlSection = builder.Configuration.GetSection("Sitecore:GraphQL");
builder.Services.AddSitecoreGraphQL(new ConfigurationBuilder().AddConfiguration(graphQlSection).Build());

// Or compose programmatic options (e.g., in tests)
services.Configure<SitecoreGraphQLOptions>(o =>
{
    o.Clients["analytics"] = new SitecoreGraphQLClientOptions
    {
        Endpoint = "https://analytics/graphql",
        ClientId = env["ANALYTICS_ID"],
        ClientSecret = env["ANALYTICS_SECRET"]
    };
});
var analytics = await factory.CreateClientByNameAsync("analytics", ct);
```

## Overloads
```csharp
// 1) Explicit endpoint (uses configured credentials)
var c1 = await factory.CreateClientAsync("https://your.sitecore/graphql", ct);

// 2) Explicit endpoint and explicit credentials (bypasses configured credentials)
var c2 = await factory.CreateClientAsync(
    url: "https://another/graphql",
    clientId: "client-id",
    clientSecret: "client-secret",
    cancellationToken: ct);

// 3) Default endpoint and credentials from configuration
var c3 = await factory.CreateClientAsync(ct);

// 4) Named client from configuration
var c4 = await factory.CreateClientByNameAsync("content", ct);
```

## Manual token refresh
```csharp
var refreshed = await factory.RefreshTokenAsync(ct);
if (!refreshed)
{
    // handle refresh failure (log, alert, etc.)
}
```

## Options reference
- Endpoint: default GraphQL endpoint used when not passing a URL into CreateClientAsync (must be valid http/https if provided)
- ClientId: OAuth client id used to acquire tokens
- ClientSecret: OAuth client secret used to acquire tokens
- EnableUnauthorizedRefresh: when true, refresh and retry on 401 responses
- MaxUnauthorizedRetries: number of additional retries after the first 401 (0 disables retries)
- EnableInternalLoggingSetup: when true (default), DI will TryAdd minimal logging so downstream services can receive ILogger<T>; set to false to opt out
- Clients: named client map; each entry requires Endpoint (valid http/https), ClientId, ClientSecret

## API surface
- Interfaces
  - ISitecoreGraphQLFactory
    - Task<IGraphQLClient> CreateClientAsync(string url, string clientId, string clientSecret, CancellationToken ct = default)
    - Task<IGraphQLClient> CreateClientAsync(string url, CancellationToken ct = default)
    - Task<IGraphQLClient> CreateClientAsync(CancellationToken ct = default)
    - Task<IGraphQLClient> CreateClientByNameAsync(string clientName, CancellationToken ct = default)
    - Task<bool> RefreshTokenAsync(CancellationToken ct = default)
  - ITokenValueAccessor (advanced)
    - string GetAccessToken(SitecoreAuthToken token)
  - ISitecoreTokenCache (advanced)
    - string? CurrentToken { get; }
    - Task<string?> GetOrRefreshAsync(CancellationToken ct)
    - Task<string?> ForceRefreshAsync(CancellationToken ct)
- Classes
  - DependencyInjection.ServiceCollectionExtensions
    - IServiceCollection AddSitecoreGraphQL(IConfiguration configuration)
  - SitecoreGraphQLOptions
    - string? Endpoint, string? ClientId, string? ClientSecret
    - bool EnableUnauthorizedRefresh (default true), int MaxUnauthorizedRetries (default 1)
    - bool EnableInternalLoggingSetup (default true)
    - Dictionary<string, SitecoreGraphQLClientOptions> Clients
  - Http.SitecoreTokenHandler
    - DelegatingHandler that injects Authorization: Bearer and retries on 401 with exponential backoff
  - SitecoreGraphQLFactory
    - public const string NamedHttpClient = "SitecoreGraphQL"

## Testing
- Run tests locally
  ```bash
  dotnet test -v minimal
  ```
- Generate coverage locally (example)
  ```bash
  dotnet test --configuration Release --collect:"XPlat Code Coverage"
  ```
- Stack: xUnit, Shouldly, Moq
- Pattern: Arrange–Act–Assert

## CI
- GitHub Actions workflows build and test on .NET SDK 8 and 9
- Coverage artifacts are uploaded from CI
- NuGet publishing triggered on version tags (v*)

## Versioning and changelog
- See CHANGELOG.md for release notes

## License
- See LICENSE for terms