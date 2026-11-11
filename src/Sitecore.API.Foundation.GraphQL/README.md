# Sitecore.API.Foundation.GraphQL

## Overview
High-performance, thread-safe GraphQL client factory and HTTP authentication pipeline for Sitecore XM Cloud, Experience Edge, and Authoring/Management APIs.

## Local Interface & Invariants
- `ISitecoreGraphQLFactory`: Primary public contract for acquiring shared `IGraphQLClient` instances and triggering proactive token refreshes.
- `SitecoreGraphQLFactory`: Thread-safe factory managing `GraphQLHttpClient` caching with FIFO eviction (`MaxCachedClients`), URL validation, and socket handler reuse.
- `SitecoreGraphQLOptions`: Root configuration options specifying endpoint URIs, OAuth credentials, retry budgets, buffer limits, and named client definitions.
- `SitecoreGraphQLClientOptions`: Per-client option overrides inheriting unset values from root options.
- `SitecoreGraphQLDiagnostics`: Diagnostic instrumentation exposing `ActivitySource` and `Meter` constants.

## Subdirectories & Boundaries
- `Abstractions`: [./Abstractions/README.md](./Abstractions/README.md) - Core interfaces for token cache and token accessor contracts.
- `DependencyInjection`: [./DependencyInjection/README.md](./DependencyInjection/README.md) - Service collection extensions for DI container registration and resilience configuration.
- `Http`: [./Http/README.md](./Http/README.md) - Delegating HTTP handler managing token injection and 401 response retry coordination.
- `Internal`: [./Internal/README.md](./Internal/README.md) - Internal token coordinators, credential context carriers, and options validators.
- `Properties`: [./Properties/README.md](./Properties/README.md) - Assembly metadata and internal visibility grants for test suites.

## Global Invariants
- Client caching: `IGraphQLClient` instances are owned and cached by `SitecoreGraphQLFactory`; callers must not dispose shared instances manually.
- Socket rotation: Dispatches via `IHttpClientFactory` named client `SitecoreGraphQL` so socket handlers rotate periodically for DNS updates.
- Protocol security: Enforces `https://` (or loopback `http://`) unless `AllowInsecureHttp = true`. Rejects embedded user info (`Uri.UserInfo`).
- Secret isolation: Redacts query strings and masks `ClientSecret` across logs and `ToString()` representations.

## Global Side Effects
- Issues outbound HTTP network requests to configured Sitecore GraphQL endpoints and OAuth2 authorization servers.
- Emits OpenTelemetry traces and runtime metric counters.

## System Verification
- Run: `dotnet test`
