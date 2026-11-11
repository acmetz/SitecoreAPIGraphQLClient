# src Blueprint

## System Overview
Source root for the `SitecoreAPIGraphClient` repository, containing the production library project `Sitecore.API.Foundation.GraphQL`.

## Subdirectories & System Boundaries
- `Sitecore.API.Foundation.GraphQL`: [./Sitecore.API.Foundation.GraphQL/README.md](./Sitecore.API.Foundation.GraphQL/README.md) - High-performance GraphQL client factory and OAuth2 authentication pipeline for Sitecore APIs.

## Global Invariants
- Codebases compile with nullable reference types enabled and `TreatWarningsAsErrors = true`.
- Multi-targets `.NET 8.0`, `.NET 9.0`, and `.NET 10.0`.
- Central package management enforced via `Directory.Packages.props`.

## Global Side Effects
- Builds the `SitecoreAPIGraphQLClient` NuGet package and symbol archive (`.snupkg`).

## System Verification
- Run: `dotnet test`
