# Sitecore.API.Foundation.GraphQL.Properties

## Overview
Assembly metadata and internal symbol visibility declarations for the `Sitecore.API.Foundation.GraphQL` project.

## Interface
- `[assembly: InternalsVisibleTo("Sitecore.API.Foundation.GraphQL.Tests")]`: Grants test assembly access to internal members.
- `[assembly: InternalsVisibleTo("Sitecore.API.Foundation.GraphQL.Benchmarks")]`: Grants benchmark assembly access to internal members.
- `[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]`: Grants Castle/Moq dynamic proxy generation access to internal members.

## Invariants
- Exposes internal types exclusively to authorized test, benchmark, and proxy assemblies.

## Side Effects
- None.

## Verification
- Run: `dotnet test`
