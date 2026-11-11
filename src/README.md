# src Blueprint

## System Overview
Source root for the `SitecoreAPIGraphQLClient` (`Sitecore.API.Foundation.GraphQL`) repository, containing the multi-targeted production NuGet class library.

## Subdirectories & System Boundaries
- `Sitecore.API.Foundation.GraphQL`: [./Sitecore.API.Foundation.GraphQL/README.md](./Sitecore.API.Foundation.GraphQL/README.md) - High-performance GraphQL client factory, OAuth2 token refresh coordinator, and HTTP authentication/resilience pipeline for Sitecore APIs.

## Global Invariants
- Compiles with `LangVersion = 14.0`, `Nullable = enable`, `ImplicitUsings = enable`, `Features = strict`, `GenerateDocumentationFile = true`, `AnalysisLevel = latest`, `EnforceCodeStyleInBuild = true`, and `TreatWarningsAsErrors = true` across all target frameworks (`net8.0`, `net9.0`, and `net10.0`).
- Central package version management is enforced via `Directory.Packages.props` (`ManagePackageVersionsCentrally = true`), with runtime dependencies strictly bounded to `GraphQL.Client`, `GraphQL.Client.Abstractions`, `GraphQL.Client.Serializer.Newtonsoft`, `Microsoft.Extensions.Http`, `Microsoft.Extensions.Http.Resilience`, `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.Options.ConfigurationExtensions`, and `SitecoreAPIAuthorization`.
- No raw `ClientSecret` values, bearer tokens, `Uri.UserInfo`, or endpoint query strings (`?sc_apikey=...`) are ever written to logs, telemetry tags, or exception messages.

## Global Side Effects
- Produces the `SitecoreAPIGraphQLClient` NuGet package (`.nupkg`) and portable symbol package (`.snupkg`) with SourceLink metadata.

## System Verification
- Run: `dotnet test`
