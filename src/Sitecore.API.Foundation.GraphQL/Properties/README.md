# Sitecore.API.Foundation.GraphQL.Properties

## Overview
Assembly metadata and internal symbol visibility declarations (`InternalsVisibleTo.cs`) for the `Sitecore.API.Foundation.GraphQL` project.

## Interface
- `[assembly: InternalsVisibleTo("Sitecore.API.Foundation.GraphQL.Tests")]` (`InternalsVisibleTo.cs`): Grants the xUnit test assembly access to internal types and members.
- `[assembly: InternalsVisibleTo("Sitecore.API.Foundation.GraphQL.Benchmarks")]` (`InternalsVisibleTo.cs`): Grants the BenchmarkDotNet suite access to internal types and members.
- `[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]` (`InternalsVisibleTo.cs`): Grants Castle/Moq dynamic proxy generation access to internal interfaces and members.

## Invariants
- Exposes `internal` members exclusively to the authorized unit test, benchmark, and mock proxy assemblies without widening public API visibility.

## Side Effects
- None (compile-time assembly attribute metadata only).

## Verification
- Run: `dotnet test`
