# Sitecore.API.Foundation.GraphQL.Abstractions

## Overview
Defines core abstraction contracts for access token extraction and legacy v1.x token caching within the GraphQL client foundation. All types in this directory are declared in the root `Sitecore.API.Foundation.GraphQL` namespace.

## Interface
- `ISitecoreTokenCache` (`ISitecoreTokenCache.cs`, namespace `Sitecore.API.Foundation.GraphQL`):
  - Marked `[Obsolete("ISitecoreTokenCache in Sitecore.API.Foundation.GraphQL is obsolete and will be removed in v2.0.0. Token caching is now delegated directly to ISitecoreTokenService in Sitecore.API.Foundation.Authorization.")]`.
  - `string? CurrentToken { get; }`: Gets the currently cached raw access token value if available.
  - `Task<string?> GetOrRefreshAsync(CancellationToken cancellationToken)`: Gets a token, refreshing it if none is cached.
  - `Task<string?> ForceRefreshAsync(CancellationToken cancellationToken)`: Forces a refresh of the token regardless of cache state.
- `ITokenValueAccessor` (`ITokenValueAccessor.cs`, namespace `Sitecore.API.Foundation.GraphQL`):
  - `string GetAccessToken(SitecoreAuthToken token)`: Extracts the raw access token string from a `SitecoreAuthToken` instance, or an empty string if none is available.
- `DefaultTokenValueAccessor` (`ITokenValueAccessor.cs`, namespace `Sitecore.API.Foundation.GraphQL`):
  - `internal sealed class DefaultTokenValueAccessor : ITokenValueAccessor` returning `token.AccessToken ?? string.Empty`.

## Invariants
- All types in `Abstractions/` reside in the root namespace `Sitecore.API.Foundation.GraphQL` rather than a `.Abstractions` sub-namespace.
- `ISitecoreTokenCache` is retained for v1.x binary compatibility and is implemented by `Internal.SitecoreTokenCache`, which delegates via `ISitecoreTokenCoordinator` to `ISitecoreTokenService` in `Sitecore.API.Foundation.Authorization`.
- `DefaultTokenValueAccessor.GetAccessToken` never returns `null`; it returns `string.Empty` when `token.AccessToken` is `null`.

## Side Effects
- `ISitecoreTokenCache.GetOrRefreshAsync` and `ForceRefreshAsync` trigger asynchronous token acquisition or refresh via `ISitecoreTokenCoordinator` / `ISitecoreTokenService`.
- `ITokenValueAccessor.GetAccessToken` and `DefaultTokenValueAccessor.GetAccessToken` are pure functions with zero side effects.

## Verification
- Run: `dotnet test`
