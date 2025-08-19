# Changelog

All notable changes to this project will be documented in this file.

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

