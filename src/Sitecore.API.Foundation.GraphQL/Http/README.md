# Sitecore.API.Foundation.GraphQL.Http

## Overview
HTTP delegating handler pipeline providing per-request OAuth2 bearer token injection, automated 401 recovery, request buffering, and payload replay for GraphQL operations.

## Interface
- `SitecoreTokenHandler`: `DelegatingHandler` that injects OAuth2 bearer tokens, manages 401 token refresh coalescing, and retries failed requests.

## Invariants
- Attached to the `SitecoreGraphQL` named HTTP client pipeline.
- Resolves request credentials from `HttpRequestMessage.Options` via `CredentialContext.OptionsKey`, falling back to default options.
- Buffers request payload bytes up to `MaxRetryBufferBytes` (default 2 MB) when retry/replay is enabled.
- Re-creates fresh `HttpRequestMessage` instances using `ReadOnlyMemoryContent` strictly upon encountering `HTTP 401 Unauthorized`.
- Mutates outbound request authorization headers with the active bearer token.

## Side Effects
- Dispatches HTTP requests through inner message handlers (`base.SendAsync`).
- Triggers asynchronous token refresh via `ISitecoreTokenCoordinator.RefreshTokenAsync` when 401 status codes are received.
- Emits structured telemetry logs and diagnostics.

## Verification
- Run: `dotnet test`
