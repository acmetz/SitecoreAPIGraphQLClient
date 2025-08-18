# Changelog

All notable changes to this project will be documented in this file.

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

