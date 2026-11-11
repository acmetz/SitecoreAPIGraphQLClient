# Sitecore.API.Foundation.GraphQL.Internal

## Overview
Internal coordination primitives, credential propagation contexts, options validators, and telemetry definitions for the Sitecore GraphQL library.

## Interface
- `CredentialContext`: Carrier for client credentials, client name, sanitized endpoint URI, explicit client status, and resilience partition key stored in `HttpRequestMessage.Options`.
- `ClientCacheKey`: Value record struct identifying cached `IGraphQLClient` instances by normalized endpoint URL, client ID, client secret, client name, and retry policy.
- `CredentialForwardingHandler`: Lightweight socket-free `HttpMessageHandler` creating shallow request envelopes and forwarding requests through `IHttpClientFactory.CreateClient("SitecoreGraphQL")`.
- `ISitecoreTokenCoordinator` / `SitecoreTokenCoordinator`: Single-flight token coordinator coalescing concurrent token refresh requests per `(ClientId, ClientSecret)`.
- `SitecoreGraphQLOptionsExtensions`: Options validation helpers and assignment tracking methods.
- `SitecoreGraphQLOptionsValidator`: Startup validator (`IValidateOptions<SitecoreGraphQLOptions>`) enforcing HTTPS schemes, valid URIs, and non-empty credentials.
- `EndpointUriValidator`: Endpoint URL sanitization utility redacting query parameters (such as `sc_apikey`) and user information.
- `SitecoreGraphQLTelemetry`: Metrics (`Meter`) and tracing (`ActivitySource`) instruments.
- `SitecoreTokenCache`: Internal implementation of legacy `ISitecoreTokenCache` backed by `ISitecoreTokenCoordinator`.

## Invariants
- Class and symbol visibility is internal to the assembly and granted to test assemblies via `[InternalsVisibleTo]`.
- `CredentialContext.ToString()` and `ClientCacheKey.ToString()` always mask `ClientSecret` values.
- Endpoint query parameters (`sc_apikey`) and `Uri.UserInfo` are stripped from all logged URIs.
- Single-flight refresh guarantees at most one active token acquisition in flight per credential pair.

## Side Effects
- Creates asynchronous DI scopes via `IServiceScopeFactory.CreateAsyncScope()`.
- Records telemetry metrics and opens OpenTelemetry activity traces.

## Verification
- Run: `dotnet test`
