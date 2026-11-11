# Sitecore.API.Foundation.GraphQL.DependencyInjection

## Overview
Dependency injection registration extensions (`ServiceCollectionExtensions.cs`) for configuring the Sitecore GraphQL client factory, named HTTP pipeline (`SitecoreGraphQLFactory.NamedHttpClient`), options binding and startup validation, token coordination, and optional partitioned HTTP resilience.

## Interface
- `ServiceCollectionExtensions` (`ServiceCollectionExtensions.cs`, namespace `Sitecore.API.Foundation.GraphQL.DependencyInjection`):
  - `public static IServiceCollection AddSitecoreGraphQL(this IServiceCollection services, IConfiguration configuration)`: Registers the factory, HTTP pipeline, and options bound from `Sitecore:GraphQL` without HTTP resilience.
  - `public static IServiceCollection AddSitecoreGraphQL(this IServiceCollection services, IConfiguration configuration, Action<HttpStandardResilienceOptions>? configureResilience, Action<IHttpClientBuilder>? configureClient = null)`: Registers the factory, HTTP pipeline, options bound from `Sitecore:GraphQL`, and optional HTTP resilience and `IHttpClientBuilder` customization.
  - `public static IServiceCollection AddSitecoreGraphQL(this IServiceCollection services, Action<SitecoreGraphQLOptions>? configure)`: Registers the factory, HTTP pipeline, and options using an optional configuration delegate without HTTP resilience.
  - `public static IServiceCollection AddSitecoreGraphQL(this IServiceCollection services, Action<SitecoreGraphQLOptions>? configure, Action<HttpStandardResilienceOptions>? configureResilience, Action<IHttpClientBuilder>? configureClient = null)`: Registers the factory, HTTP pipeline, options via a configuration delegate, and optional HTTP resilience and `IHttpClientBuilder` customization.
  - `internal static void TryAddInternalLogging(this IServiceCollection services)`: Calls `services.AddLogging()`.

## Invariants
- `services` (and `configuration` on `IConfiguration` overloads) must be non-null; throws `ArgumentNullException` when null.
- Enforces startup options validation via `OptionsBuilder<SitecoreGraphQLOptions>.ValidateOnStart()` backed by `SitecoreGraphQLOptionsValidator`.
- In `AddSitecoreGraphQL(IServiceCollection, IConfiguration, ...)`, a startup snapshot of `Sitecore:GraphQL` is bound to inspect `snapshot.EnableInternalLoggingSetup`; `TryAddInternalLogging()` is called only when `EnableInternalLoggingSetup` is `true` (default). The delegate overloads always pass `enableLogging: true`.
- In the delegate overloads (`Action<SitecoreGraphQLOptions>? configure`), the configure delegate is executed once under a lock to capture an assignment bitmask diff (`GraphQLOptionsAssignedMask` and per-client `GraphQLClientOptionsAssignedMask`); subsequent `IOptionsMonitor<SitecoreGraphQLOptions>` reloads replay only the explicitly assigned properties without re-invoking the user delegate.
- Resolves `ISitecoreTokenService` per token operation via `IServiceScopeFactory.CreateAsyncScope()` inside `SitecoreTokenCoordinator`, supporting both `Scoped` and `Singleton` `ISitecoreTokenService` lifetimes under `ValidateScopes = true` and `ValidateOnBuild = true`.
- When `configureResilience` is non-null, `AddStandardResilienceHandler()` is added to the `SitecoreGraphQL` HTTP pipeline:
  - `Retry.ShouldHandle` unconditionally disables retries for `HttpMethod.Put`, `HttpMethod.Patch`, `HttpMethod.Delete`, and `HttpMethod.Connect`.
  - `Retry.ShouldHandle` disables retries for `HttpMethod.Post` unless `CredentialContext.AllowPostRetries` (or `SitecoreGraphQLOptions.AllowPostRetries` when no `CredentialContext` is present) is `true`.
  - `SelectPipelineBy` partitions resilience pipelines and circuit breakers by `$"{authority}|{partitionKey}"` (where `authority` is `EndpointUriValidator.GetRedactedAuthority(request.RequestUri)` and `partitionKey` is `"default"`, `"named:{clientName}"`, or `"explicit:{sha256Prefix}"`). Tracked dynamic explicit-client partitions are capped at `Math.Max(1, MaxCachedClients)`; overflow dynamic explicit clients route to `$"{authority}|explicit:shared"`.

## Side Effects
- Modifies `IServiceCollection` by registering:
  - `IValidateOptions<SitecoreGraphQLOptions>` -> `SitecoreGraphQLOptionsValidator` (Singleton, `TryAddEnumerable`)
  - `ITokenValueAccessor` -> `DefaultTokenValueAccessor` (Singleton, `TryAddSingleton`)
  - `ISitecoreTokenCoordinator` -> `SitecoreTokenCoordinator` (Singleton, `TryAddSingleton`)
  - `ISitecoreTokenCache` -> `SitecoreTokenCache` (Singleton, `TryAddSingleton`, obsolete v1.x compatibility adapter)
  - `SitecoreTokenHandler` (Transient, `AddTransient`)
  - Named `HttpClient` (`SitecoreGraphQLFactory.NamedHttpClient` = `"SitecoreGraphQL"`) with `SitecoreTokenHandler`, optional `StandardResilienceHandler`, and optional `configureClient` callback
  - `ISitecoreGraphQLFactory` -> `SitecoreGraphQLFactory` (Singleton, `TryAddSingleton`)

## Verification
- Run: `dotnet test`
