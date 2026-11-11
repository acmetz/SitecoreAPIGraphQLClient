# Sitecore.API.Foundation.GraphQL.DependencyInjection

## Overview
Dependency injection registration extensions for configuring the Sitecore GraphQL client factory, HTTP pipelines, options validation, token coordination, and optional HTTP resilience.

## Interface
- `ServiceCollectionExtensions.AddSitecoreGraphQL(IServiceCollection, IConfiguration, Action<HttpStandardResilienceOptions>?, Action<IHttpClientBuilder>?)`: Registers factory, HTTP pipelines, and configuration options bound from `Sitecore:GraphQL`.
- `ServiceCollectionExtensions.AddSitecoreGraphQL(IServiceCollection, Action<SitecoreGraphQLOptions>?, Action<HttpStandardResilienceOptions>?, Action<IHttpClientBuilder>?)`: Registers factory, HTTP pipelines, and options using a configuration delegate.

## Invariants
- `services` and `configuration` must be non-null; throws `ArgumentNullException` on null arguments.
- Options are validated on application startup via `ValidateOnStart()` and `SitecoreGraphQLOptionsValidator`.
- Resolves `ISitecoreTokenService` per token operation using `IServiceScopeFactory.CreateAsyncScope()` to support both `Scoped` and `Singleton` lifetime configurations without captive dependencies.
- HTTP resilience retries on `HTTP POST` requests are disabled by default unless explicitly allowed via `AllowPostRetries = true`.
- Resilience pipelines and circuit breakers are partitioned by `(redacted authority, client identity)` using `SelectPipelineBy`.

## Side Effects
- Modifies the service descriptor collection by registering singletons (`ISitecoreGraphQLFactory`, `ISitecoreTokenCoordinator`, `ITokenValueAccessor`), transient `SitecoreTokenHandler`, named HTTP client `SitecoreGraphQL`, and options validators.

## Verification
- Run: `dotnet test`
