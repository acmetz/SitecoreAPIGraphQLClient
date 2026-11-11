# Contributing to Sitecore.API.Foundation.GraphQL

Contributions to `Sitecore.API.Foundation.GraphQL` (`SitecoreAPIGraphQLClient`) are welcome. This guide covers local setup, build and test verification, coding standards, benchmarking, and release workflows.

## Prerequisites

- `.NET 10.0` SDK (`10.0.100` or later feature band per `global.json`)
- `.NET 8.0`, `.NET 9.0`, and `.NET 10.0` runtimes (for multi-target test and benchmark execution)
- Git
- Optional local CI tools:
  - [`actionlint`](https://github.com/rhysd/actionlint) for linting `.github/workflows/*.yml`
  - [`act`](https://github.com/nektos/act) for local workflow verification (`.actrc` is preconfigured)

Downstream NuGet consumers only require `.NET 8.0`, `.NET 9.0`, or `.NET 10.0`.

## Getting Started

```bash
git clone https://github.com/acmetz/SitecoreAPIGraphQLClient.git
cd SitecoreAPIGraphQLClient

# Restore packages
dotnet restore Sitecore.API.Foundation.GraphQL.slnx

# Build in Release mode (TreatWarningsAsErrors=true across net8.0, net9.0, net10.0)
dotnet build Sitecore.API.Foundation.GraphQL.slnx --configuration Release --no-restore

# Run unit and contract tests across all target frameworks
dotnet test tests/Sitecore.API.Foundation.GraphQL.Tests/Sitecore.API.Foundation.GraphQL.Tests.csproj --configuration Release --no-build
```

## Project Structure

```text
SitecoreAPIGraphQLClient/
  Sitecore.API.Foundation.GraphQL.slnx
  Directory.Build.props
  Directory.Build.targets
  Directory.Packages.props
  global.json
  .editorconfig
  src/
    README.md                                        # EGM source architectural blueprint
    Sitecore.API.Foundation.GraphQL/                 # Main NuGet class library (net8.0;net9.0;net10.0) and EGM component contracts
  tests/
    Sitecore.API.Foundation.GraphQL.Tests/           # xUnit + Shouldly + Moq unit and contract tests
    Sitecore.API.Foundation.GraphQL.Benchmarks/      # BenchmarkDotNet multi-runtime benchmark suite
  docs/
    README.md                                        # Extended developer and architecture guide
  .github/
    workflows/
      pr-check.yml                                   # Pull request lint, multi-TFM build/test/coverage/pack check
      nuget-release.yml                              # Main CI and v* tag OIDC NuGet + GitHub Release pipeline
```

## Verification Commands

### Code Coverage

Line coverage on `Sitecore.API.Foundation.GraphQL` (and across public API types) must remain at or above `80%`:

```bash
dotnet test tests/Sitecore.API.Foundation.GraphQL.Tests/Sitecore.API.Foundation.GraphQL.Tests.csproj \
  --configuration Release \
  --collect:"XPlat Code Coverage" \
  --results-directory ./TestResults
```

### NuGet and Symbol Packaging

```bash
dotnet pack src/Sitecore.API.Foundation.GraphQL/Sitecore.API.Foundation.GraphQL.csproj \
  --configuration Release \
  --output ./nupkgs
```

Verify both `SitecoreAPIGraphQLClient.<version>.nupkg` and `SitecoreAPIGraphQLClient.<version>.snupkg` are produced without warnings.

### Benchmarks

Run the full `.NET 8.0`, `.NET 9.0`, and `.NET 10.0` memory-diagnosed benchmark suite in `Release` mode:

```bash
dotnet run --configuration Release --framework net10.0 \
  --project tests/Sitecore.API.Foundation.GraphQL.Benchmarks/Sitecore.API.Foundation.GraphQL.Benchmarks.csproj
```

## Coding Standards

- **Compiler and Analyzers**: `LangVersion` is `14.0`, `Nullable` is `enable`, and `TreatWarningsAsErrors` is `true` in `Directory.Build.props`. All code must build with zero warnings.
- **Formatting and Naming**: Follow `.editorconfig` at repository root:
  - 4 spaces for C#, 2 spaces for XML/JSON/YAML, UTF-8, LF line endings
  - File-scoped namespaces and Allman braces
  - `PascalCase` for public members, constants, and `static readonly` fields; `_camelCase` for `private` instance fields
  - Complete XML documentation comments on all public types and members
- **Dependencies**: Manage all NuGet versions centrally in `Directory.Packages.props`. Never add `Version` attributes to `<PackageReference>` items in `.csproj` files, and keep test-only packages scoped under `Condition="'$(IsPackable)' != 'true'"` so they never leak into the packed `.nuspec`.
- **Serialization and HTTP**: `GraphQLHttpClient` uses `NewtonsoftJsonSerializer` (`Newtonsoft.Json`). Never instantiate long-lived `SocketsHttpHandler` pipelines per client; route outbound HTTP calls through `IHttpClientFactory`.
- **Security**: Never log raw secrets, bearer tokens, `Uri.UserInfo`, or endpoint query strings (`?sc_apikey=...`).
- **Documentation**: Update `README.md`, `docs/README.md`, `CHANGELOG.md`, and the EGM architectural contract READMEs under `src/` (`src/README.md` and `src/Sitecore.API.Foundation.GraphQL/**/README.md`) when public APIs, internal contracts, or invariants change. Do not use emojis in repository documentation.

## Pull Request and Release Process

1. Create a branch named `feature/<short-name>` or `fix/<short-name>` from `main`.
2. Use Conventional Commits (`feat:`, `fix:`, `perf:`, `test:`, `docs:`, `build:`, `ci:`).
3. Ensure `PR Check` passes across `lint-workflows` and all `net8.0`, `net9.0`, and `net10.0` matrix legs.
4. Releases are published from `main` by pushing a semantic version tag matching `<PackageVersion>` (for example `v1.0.0`), which triggers `.github/workflows/nuget-release.yml` using NuGet.org OIDC Trusted Publishing (`NuGet/login@v1` in the `nuget` GitHub Environment).
