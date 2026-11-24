# Sitecore.API.Foundation.GraphQL.DependencyInjection

## Overview
Dependency injection registration extensions (`ServiceCollectionExtensions.cs`) and typed fluent builder abstractions (`ISitecoreGraphQLBuilder.cs`) for configuring untyped and typed (`TClient`) Sitecore GraphQL client factories, named HTTP pipelines, options binding and startup validation, token coordination, fluent `SitecoreAPIAuthorization` wiring, and optional partitioned HTTP resilience.

## Interface
- `ServiceCollectionExtensions` (`ServiceCollectionExtensions.cs`, namespace `Sitecore.API.Foundation.GraphQL.DependencyInjection`):
  - `public static IServiceCollection AddSitecoreGraphQL(this IServiceCollection services, IConfiguration configuration)`: Registers the factory, HTTP pipeline, and options bound from `Sitecore:GraphQL` without HTTP resilience.
  - `public static IServiceCollection AddSitecoreGraphQL(this IServiceCollection services, IConfiguration configuration, Action<HttpStandardResilienceOptions>? configureResilience, Action<IHttpClientBuilder>? configureClient = null)`: Registers the factory, HTTP pipeline, options bound from `Sitecore:GraphQL`, and optional HTTP resilience and `IHttpClientBuilder` customization.
  - `public static IServiceCollection AddSitecoreGraphQL(this IServiceCollection services, Action<SitecoreGraphQLOptions>? configure)`: Registers the factory, HTTP pipeline, and options using an optional configuration delegate without HTTP resilience.
  - `public static IServiceCollection AddSitecoreGraphQL(this IServiceCollection services, Action<SitecoreGraphQLOptions>? configure, Action<HttpStandardResilienceOptions>? configureResilience, Action<IHttpClientBuilder>? configureClient = null)`: Registers the factory, HTTP pipeline, options via a configuration delegate, and optional HTTP resilience and `IHttpClientBuilder` customization.
  - `public static ISitecoreGraphQLBuilder<TClient> AddSitecoreGraphQL<TClient>(this IServiceCollection services, Action<SitecoreGraphQLOptions>? configure = null) where TClient : class`: Registers an isolated typed factory (`ISitecoreGraphQLFactory<TClient>`), coordinator (`ISitecoreTokenCoordinator<TClient>`), named HTTP pipeline, and named `SitecoreGraphQLOptions`.
  - `public static ISitecoreGraphQLBuilder<TClient> AddSitecoreGraphQL<TClient>(this IServiceCollection services, IConfiguration configuration, string sectionName = SectionName) where TClient : class`: Binds typed options from `sectionName` with pre-mutation argument validation and evaluates `EnableInternalLoggingSetup`.
  - `public static ISitecoreGraphQLBuilder<TClient> ConfigureAuthentication<TClient>(this ISitecoreGraphQLBuilder<TClient> builder, Action<ISitecoreAuthenticationBuilder<TClient>>? configureAuth = null) where TClient : class`: Chains `builder.Services.AddSitecoreAuthentication<TClient>()`.
  - `public static ISitecoreGraphQLBuilder<TClient> ConfigureAuthenticationSingleton<TClient>(this ISitecoreGraphQLBuilder<TClient> builder, Action<ISitecoreAuthenticationBuilder<TClient>>? configureAuth = null) where TClient : class`: Chains `builder.Services.AddSitecoreAuthenticationSingleton<TClient>()`.
  - `internal static void TryAddInternalLogging(this IServiceCollection services)`: Calls `services.AddLogging()`.
- `ISitecoreGraphQLBuilder<TClient>` & `SitecoreGraphQLBuilder<TClient>` (`ISitecoreGraphQLBuilder.cs`, namespace `Sitecore.API.Foundation.GraphQL.DependencyInjection`):
  - `IServiceCollection Services { get; }`
  - `ISitecoreGraphQLBuilder<TClient> ConfigureOptions(Action<SitecoreGraphQLOptions> configure)`
  - `ISitecoreGraphQLBuilder<TClient> ConfigureOptions(IConfiguration configuration, string sectionName = ServiceCollectionExtensions.SectionName)`
  - `ISitecoreGraphQLBuilder<TClient> ConfigureResilience(Action<HttpStandardResilienceOptions>? configureResilience = null)`
  - `ISitecoreGraphQLBuilder<TClient> ConfigureHttpClient(Action<IHttpClientBuilder> configureClient)`

## Invariants
- `services`, `configuration`, `sectionName`, `configure`, and `configureClient` inputs are validated before mutating `IServiceCollection` or builder state, throwing `ArgumentNullException` or `ArgumentException`.
- Repeated `AddSitecoreGraphQL<TClient>(...)` calls on the same `IServiceCollection` are idempotent via `ConditionalWeakTable<IServiceCollection, ConcurrentDictionary<Type, object>>`, returning the winning `SitecoreGraphQLBuilder<TClient>` and attaching the per-type `SitecoreTokenHandler` only once via the factory delegate overload (`httpBuilder.AddHttpMessageHandler(sp => new SitecoreTokenHandler(...))` — never `services.AddTransient<SitecoreTokenHandler>`).
- Repeated `.ConfigureResilience(...)` calls on a `SitecoreGraphQLBuilder<TClient>` reuse the lazily initialized `IHttpStandardResiliencePipelineBuilder` without stacking duplicate resilience handlers. Call `ConfigureResilience(...)` before adding inner delegating handlers via `ConfigureHttpClient(...)`.
- Typed options and `HttpClient` registrations are deterministically named using `typeName = typeof(TClient).AssemblyQualifiedName ?? typeof(TClient).FullName ?? typeof(TClient).Name` (`$"Sitecore.API.Foundation.GraphQL.Options.{typeName}"` and `$"SitecoreGraphQL.{typeName}"`), isolating them from `AddHttpClient<TClient>()` and sibling typed clients, while telemetry `client.type` uses `typeof(TClient).ToString()`.
- Enforces startup options validation via `OptionsBuilder<SitecoreGraphQLOptions>.ValidateOnStart()` backed by `SitecoreGraphQLOptionsValidator`.
- In `AddSitecoreGraphQL(IServiceCollection, IConfiguration, ...)` and `AddSitecoreGraphQL<TClient>(IServiceCollection, IConfiguration, string)`, a startup snapshot of the configuration section is bound to inspect `snapshot.EnableInternalLoggingSetup`; `TryAddInternalLogging()` is called only when `EnableInternalLoggingSetup` is `true` (default). Delegate overloads and chained `ISitecoreGraphQLBuilder<TClient>.ConfigureOptions(...)` calls do not suppress or re-evaluate initial internal logging setup.
- In delegate configuration paths (`ConfigureOptionsWithDeltaTracking`), the configure delegate is executed once under a lock to capture an assignment bitmask diff (`GraphQLOptionsAssignedMask` and per-client `GraphQLClientOptionsAssignedMask`); subsequent `IOptionsMonitor<SitecoreGraphQLOptions>` reloads replay only the explicitly assigned properties without re-invoking the user delegate.
- Resolves typed `ISitecoreTokenService<TClient>` or fallback `ISitecoreTokenService` per token operation via `IServiceScopeFactory.CreateAsyncScope()` inside `SitecoreTokenCoordinator`, supporting both `Scoped` and `Singleton` lifetimes under `ValidateScopes = true` and `ValidateOnBuild = true`.
- When HTTP resilience is enabled (`AddStandardResiliencePipeline`), `Retry.ShouldHandle` unconditionally disables retries for `HttpMethod.Put`, `HttpMethod.Patch`, `HttpMethod.Delete`, and `HttpMethod.Connect`, and disables retries for `HttpMethod.Post` unless `CredentialContext.AllowPostRetries` (or the resolved named/current `SitecoreGraphQLOptions.AllowPostRetries`) is `true`.
- `SelectPipelineBy` partitions resilience pipelines and circuit breakers by `$"{authority}|{partitionKey}"` (where `authority` is `EndpointUriValidator.GetRedactedAuthority(request.RequestUri)` and `partitionKey` is `"default"`, `"named:{clientName}"`, or `"explicit:{sha256Prefix}"`). Tracked dynamic explicit-client partitions are capped at `Math.Max(1, MaxCachedClients)`; overflow dynamic explicit clients route to `$"{authority}|explicit:shared"`.

## Side Effects
- Modifies `IServiceCollection` by registering:
  - `IValidateOptions<SitecoreGraphQLOptions>` -> `SitecoreGraphQLOptionsValidator` (Singleton, `TryAddEnumerable`)
  - `ITokenValueAccessor` -> `DefaultTokenValueAccessor` (Singleton, `TryAddSingleton`)
  - Untyped path: `ISitecoreTokenCoordinator`, `[Obsolete] ISitecoreTokenCache`, `SitecoreTokenHandler` (`AddTransient`), named `HttpClient` (`"SitecoreGraphQL"`), and `ISitecoreGraphQLFactory`
  - Typed `<TClient>` path: named `OptionsBuilder<SitecoreGraphQLOptions>`, named `HttpClient` (`$"SitecoreGraphQL.{typeName}"`) with per-instance `SitecoreTokenHandler` factory delegate, `ISitecoreTokenCoordinator<TClient>` (Singleton, `TryAddSingleton`), and `ISitecoreGraphQLFactory<TClient>` (Singleton, `TryAddSingleton`)

## Verification
- Run: `dotnet test`
