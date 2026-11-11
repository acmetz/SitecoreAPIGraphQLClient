# Sitecore.API.Foundation.GraphQL.Abstractions

## Overview
Defines core abstraction contracts for token extraction and legacy token caching within the GraphQL client foundation.

## Interface
- `ISitecoreTokenCache`: Legacy interface for shared access token retrieval and refreshing (`CurrentToken`, `GetOrRefreshAsync`, `ForceRefreshAsync`). Marked obsolete for removal in v2.0.0.
- `ITokenValueAccessor`: Contract for extracting the raw bearer token string from authorization model objects (`GetAccessToken(SitecoreAuthToken token)`).
- `DefaultTokenValueAccessor`: Internal implementation of `ITokenValueAccessor` returning `token.AccessToken ?? string.Empty`.

## Invariants
- `ISitecoreTokenCache` delegates to `ISitecoreTokenService` in `Sitecore.API.Foundation.Authorization` and is retained solely for backward compatibility.
- `ITokenValueAccessor.GetAccessToken` must never return null; empty string is returned when the token property is null.

## Side Effects
- `ISitecoreTokenCache.GetOrRefreshAsync` and `ForceRefreshAsync` trigger asynchronous token retrieval via underlying token coordinators.
- `ITokenValueAccessor.GetAccessToken` is pure with zero side effects.

## Verification
- Run: `dotnet test`
