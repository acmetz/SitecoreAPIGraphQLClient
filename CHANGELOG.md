# Changelog

All notable changes to this project will be documented in this file.

## [1.0.0] - 2026-11-09

### Added
- Upgraded to `SitecoreAPIAuthorization` `1.0.0` (`ISitecoreTokenService.TryRefreshSitecoreAuthToken` and `GetSitecoreAuthToken` with cooperative `CancellationToken` propagation).
- Added opt-in HTTP resilience (`Microsoft.Extensions.Http.Resilience`) and `IHttpClientBuilder` customization overloads on `ServiceCollectionExtensions.AddSitecoreGraphQL`, partitioned per `(authority, client)` with `HTTP POST` retries disabled by default (`AllowPostRetries = false`) and configurable per named client (`SitecoreGraphQLClientOptions.AllowPostRetries`).
- Added `ISitecoreGraphQLFactory.RefreshTokenByNameAsync(string clientName, CancellationToken)` and multi-client deduplicated `RefreshTokenAsync(CancellationToken)`.
- Added `SitecoreGraphQLOptions.AllowInsecureHttp`, `AllowPostRetries`, `MaxCachedClients` (FIFO eviction for dynamic clients), and `MaxRetryBufferBytes` (`10 MB` default), plus per-client `AllowInsecureHttp` and `AllowPostRetries` overrides on `SitecoreGraphQLClientOptions`.
- Added `SitecoreGraphQLDiagnostics` (`ActivitySource` and `Meter` under `"Sitecore.API.Foundation.GraphQL"`) and source-generated `[LoggerMessage]` logging.
- Added multi-runtime `BenchmarkDotNet` performance and allocation benchmarks across `.NET 8.0`, `.NET 9.0`, and `.NET 10.0`, `docs/README.md`, `CONTRIBUTING.md`, portable `.snupkg` symbol packaging, SourceLink, and workflow/packaging contract tests.

### Changed
- Migrated solution format to `Sitecore.API.Foundation.GraphQL.slnx` with `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props` (Central Package Management), `global.json`, and C# 14 / latest .NET analyzers (`TreatWarningsAsErrors=true`).
- Named client configuration (`Sitecore:GraphQL:Clients:<name>`) now inherits unset (`null` or whitespace) `Endpoint`, `ClientId`, `ClientSecret`, `AllowInsecureHttp`, and `AllowPostRetries` values from top-level `SitecoreGraphQLOptions`.
- Resolved `ISitecoreTokenService` via `IServiceScopeFactory.CreateAsyncScope()` so both `Scoped` (`AddSitecoreAuthentication`) and `Singleton` (`AddSitecoreAuthenticationSingleton`) registrations pass `ValidateScopes = true` and `ValidateOnBuild = true`.

### Deprecated
- Marked `Sitecore.API.Foundation.GraphQL.ISitecoreTokenCache` `[Obsolete]` (`CS0618`) for removal in `v2.0.0`; it now acts as a stateless pass-through compatibility adapter delegating to `ISitecoreTokenService`.

### Removed
- Removed unused server-side `GraphQL` (`8.5.0`) and redundant `Microsoft.Extensions.Configuration.Binder` direct NuGet dependencies from `SitecoreAPIGraphQLClient`.

### Fixed
- Fixed cross-tenant OAuth2 token contamination where explicit and named clients shared a single global `_cachedToken` or ignored `clientSecret` differences.
- Fixed `HTTP POST` `401 Unauthorized` retry failures caused by re-sending an already-dispatched `HttpRequestMessage`; retries now clone fresh `HttpRequestMessage` instances over buffered `ReadOnlyMemoryContent` with single-flight refresh coalescing.
- Fixed `IHttpClientFactory` 2-minute DNS handler rotation by dispatching cached `GraphQLHttpClient` requests through a socket-free `CredentialForwardingHandler`.
- Fixed `README.md` `Basic usage` snippet (`GraphQL.GraphQLRequest` with `Newtonsoft.Json` DTOs).

### Security
- Enforced `https://` (and loopback `http://localhost` / `127.0.0.1`) by default across startup validation and `CreateClientAsync`; non-loopback cleartext `http://` now requires explicit `AllowInsecureHttp = true`.
- Rejected embedded `Uri.UserInfo` credentials unconditionally across startup and runtime validation.
- Sanitized logged and exception-reported URIs (`Scheme://Host:Port/Path`, stripping query parameters such as `?sc_apikey=...` and fragments) and masked `ClientSecret` as `***` in `ToString()`.

## [0.9.4] - 2026-11-06
### Added
- Multi-target support for .NET 10.0 (`net8.0;net9.0;net10.0`).

### Changed
- Upgraded GitHub Actions NuGet release pipeline (`nuget-release.yml`) to use NuGet OIDC Trusted Publishing (`NuGet/login@v1`) with the `nuget` GitHub Environment and `vars.NUGET_USER`.
- Hardened CI/CD workflows with job-scoped permissions, `persist-credentials: false` on checkout, tag-to-package version validation, and `--skip-duplicate` on NuGet push.

### Fixed
- Sanitized `README.md` encoding to valid UTF-8 for clean NuGet package rendering.

## [0.9.3] - 2025-08-18
### Changed
- Switched GraphQL client serializer from System.Text.Json to Newtonsoft.Json for broader compatibility.
- Removed custom System.Text.Json converters and related serializer configuration.

### Added
- Tests to verify mutation variable serialization and anonymous object variable shapes using Newtonsoft (JObject parsing).

### Fixed
- Ensured GUID serialization/deserialization symmetry under the new serializer.

## [0.9.2] - 2025-08-18
### Added
- Tests to ensure Guid, Guid?, arrays and lists of Guid from GraphQL responses deserialize to System.Guid across formats (D, N, B, P).

### Changed
- GraphQL factory now configures System.Text.Json serializer with flexible Guid converters for robust Guid parsing.
- Refactored FlexibleGuidConverters to reduce cyclomatic complexity and improve readability with early returns.

### Fixed
- Resolved CI pack/restore issues across SDK 8/9 and ensured README is included in packages.

## [0.9.1] - 2025-08-18
### Added
- New DI registration overload: `IServiceCollection AddSitecoreGraphQL(Action<SitecoreGraphQLOptions> configure)` allowing usage without `IConfiguration`.

### Changed
- CI workflows: sequential .NET 8/9 build, test, and pack to avoid cross-TFM inference issues.
- Package description clarified for NuGet/GitHub.

### Fixed
- Ensure README is included in NuGet package to satisfy `PackageReadmeFile` (fixes NU5039).
- Logging DI tests stabilized; no provider build inside registration.
- Use message templating for logging (no string interpolation) in GraphQL factory.

## [0.9.0] - 2025-08-14
### Added
- library created with initial functionality
