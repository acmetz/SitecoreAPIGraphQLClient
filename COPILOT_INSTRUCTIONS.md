# Copilot Instructions — Sitecore.API.Foundation.GraphQL

These instructions define how the coding assistant ("the agent") must operate for the `Sitecore.API.Foundation.GraphQL` library on **each prompt**.

> **Scope**: C#/.NET class library distributed via NuGet (`SitecoreAPIGraphQLClient`).
>
> **Solution/Project**: `Sitecore.API.Foundation.GraphQL.slnx`
>
> **Paths**: `src/Sitecore.API.Foundation.GraphQL/` (library), `tests/Sitecore.API.Foundation.GraphQL.Tests/` (xUnit test project), `tests/Sitecore.API.Foundation.GraphQL.Benchmarks/` (BenchmarkDotNet suite)

---

## Agent Rules — Apply on Every Prompt

1. **TDD First**
   - Start with a failing xUnit test in `tests/Sitecore.API.Foundation.GraphQL.Tests`.
   - Write minimal code in `src/Sitecore.API.Foundation.GraphQL` to pass.
   - Refactor only after tests pass.

2. **Multi-Target Compatibility**
   - Always compile for `net8.0`, `net9.0`, and `net10.0` (`.NET 8.0` through the latest supported `.NET` version).
   - Tests must run and pass for **all targeted frameworks**.
   - Manage package versions centrally in `Directory.Packages.props`.

3. **Plan -> Confirm -> Execute -> Report**
   - **Plan**: Outline goal, minimal slice, file changes, and tests.
   - **Confirm**: Ask explicit approval before making changes.
   - **Execute**: Follow approved plan exactly.
   - **Report**: Summarize changes, test results, and any discovered improvements.
   - If a better approach emerges mid-execution, pause, propose new plan, request approval.

4. **Always Compiles**
   - No step should leave the solution in an uncompilable state (`0` warnings, `0` errors under `TreatWarningsAsErrors=true`).

5. **Documentation Required**
   - Update `README.md` in repo root (concise overview + benchmarks) and `docs/README.md` (detailed guide) with each completed feature.
   - Keep the EGM architectural contract files under `src/` (`src/README.md` and `src/Sitecore.API.Foundation.GraphQL/**/README.md`) synchronized whenever public APIs, internal contracts, invariants, or side effects change.
   - Update `CHANGELOG.md` in repo root for every notable change.
   - No emojis in any documentation.

6. **Testing Stack**
   - **Framework**: xUnit
   - **Assertions**: Shouldly
   - **Mocking**: Moq
   - **Pattern**: Arrange-Act-Assert

7. **CI Expectations**
   - CI matrix runs on all targeted .NET versions (`net8.0`, `net9.0`, `net10.0`) plus `actionlint` workflow validation.
   - All tests must pass; line coverage >= 80% on `Sitecore.API.Foundation.GraphQL` and public API types.

8. **Git Hygiene**
   - Branch naming: `feature/<short-name>` or `fix/<short-name>`.
   - Conventional commits (e.g., `feat(graphql): add per-client resilience partitioning`).
   - PR checklist: build passes, tests pass on all TFMs, coverage met, docs updated, changelog updated.

9. **Coding Standards (Microsoft)**
   - Adopt the **official Microsoft .NET/C# coding conventions** and **.NET API design guidelines**.
   - **General**
     - Enable nullable reference types; treat warnings as errors across all projects.
     - Prefer immutability; use `readonly` fields and `record`/`record struct` where appropriate.
     - Avoid public mutable state and statics; favor DI-friendly designs.
     - Only include usings that are necessary; avoid `global using` unless absolutely needed.
   - **Naming**
     - PascalCase: public types, methods, properties, events, constants, `static readonly` fields, enums, namespaces.
     - `_camelCase`: `private` instance fields (`const` and `static` fields are excluded from `_camelCase`).
     - camelCase: parameters and locals.
     - Async methods end with `Async`.
   - **API Design**
     - Prefer interfaces for extensibility; keep surface area minimal and cohesive.
     - Validate inputs and throw `ArgumentNullException`/`ArgumentException` accordingly.
     - Enforce HTTPS (or loopback HTTP unless `AllowInsecureHttp = true`), reject `Uri.UserInfo`, sanitize logged URIs, and mask secrets in `ToString()`.
     - Use `CancellationToken` on async APIs; honor cooperative cancellation.
     - Return `Task`/`ValueTask` (avoid `async void` except event handlers).
   - **Style**
     - Allman braces (opening brace on a new line).
     - Use `var` when type is obvious; otherwise spell the type.
     - File-scoped namespaces.
     - Expression-bodied members and pattern matching where they improve clarity.
     - XML documentation comments on all public APIs.
   - **Serialization & HTTP**
     - Use `Newtonsoft.Json` (`GraphQL.Client.Serializer.Newtonsoft`) for GraphQL request/response serialization; use typed DTOs or `Newtonsoft.Json.Linq.JObject` rather than `System.Text.Json.JsonDocument`.
     - Do not create standalone socket handlers per request; dispatch via `IHttpClientFactory` (`SitecoreGraphQLFactory.NamedHttpClient`) so pooled handlers rotate every 2 minutes.
   - **Analyzers & Formatting**
     - Enable **Microsoft.CodeAnalysis.NetAnalyzers** (`AnalysisLevel=latest`, `EnforceCodeStyleInBuild=true`, `TreatWarningsAsErrors=true`) and `[LoggerMessage]` source generation.
     - Keep formatting consistent via `.editorconfig` committed at repo root.

10. **NuGet Package Info**
    - The package ID is `SitecoreAPIGraphQLClient`; do not ever change it.
    - Ensure the package description is clear and concise, suitable for NuGet and GitHub.
    - Ensure `README.md` and `LICENSE` are packed via `Directory.Build.targets` and `.snupkg` portable symbols are enabled.
    - Ensure repository and project URLs are set and `.nuspec` contains strictly the intended runtime dependencies.

   **Reference `.editorconfig` naming excerpt**
   ```editorconfig
   dotnet_naming_symbols.private_const_fields.applicable_kinds = field
   dotnet_naming_symbols.private_const_fields.applicable_accessibilities = private
   dotnet_naming_symbols.private_const_fields.required_modifiers = const
   dotnet_naming_rule.private_const_fields_should_be_pascal_case.symbols = private_const_fields
   dotnet_naming_rule.private_const_fields_should_be_pascal_case.style = pascal_case_style

   dotnet_naming_symbols.private_instance_fields.applicable_kinds = field
   dotnet_naming_symbols.private_instance_fields.applicable_accessibilities = private
   dotnet_naming_rule.private_fields_should_be_camel_with_underscore.symbols = private_instance_fields
   dotnet_naming_rule.private_fields_should_be_camel_with_underscore.style = camel_with_underscore
   ```

---

**On every prompt**, the agent must:
- Apply these rules in full.
- Confirm the plan with the user before execution.
- Break large plans into smaller **phases** and seek approval for each.
- Ensure the solution builds and tests pass for **all targeted frameworks**.
- Update relevant documentation before marking work as complete.
