# Copilot Instructions — Sitecore.API.Foundation.GraphQL

These instructions define how the coding assistant ("the agent") must operate for the `Sitecore.API.Foundation.GraphQL` library on **each prompt**.

> **Scope**: C#/.NET class library distributed via NuGet.
>
> **Solution/Project**: `Sitecore.API.Foundation.GraphQL` (solution and project)
>
> **Paths**: `src/Sitecore.API.Foundation.GraphQL/` (library), `tests/Sitecore.API.Foundation.GraphQL.Tests/` (xUnit test project)

---

## Agent Rules — Apply on Every Prompt

1. **TDD First**
   - Start with a failing xUnit test in `tests/Sitecore.API.Foundation.GraphQL.Tests`.
   - Write minimal code in `src/Sitecore.API.Foundation.GraphQL` to pass.
   - Refactor only after tests pass.

2. **Multi‑Target Compatibility**
   - Always compile for `.NET 8.0` and the latest supported `.NET` version.
   - Tests must run for **all targeted frameworks**.
   - Update `TargetFrameworks` in both the library and test `.csproj` files when new .NET versions are released.

3. **Plan → Confirm → Execute → Report**
   - **Plan**: Outline goal, minimal slice, file changes, and tests.
   - **Confirm**: Ask explicit approval before making changes.
   - **Execute**: Follow approved plan exactly.
   - **Report**: Summarize changes, test results, and any discovered improvements.
   - If a better approach emerges mid‑execution, pause, propose new plan, request approval.

4. **Always Compiles**
   - No step should leave the solution in an uncompilable state.

5. **Documentation Required**
   - Update `README.md` in repo root (short) and `/docs/README.md` (detailed) with each completed feature.
   - Update `/docs/changelog/CHANGELOG.md` for every change.
   - No emojis in any documentation.

6. **Testing Stack**
   - **Framework**: xUnit
   - **Assertions**: Shouldly
   - **Mocking**: Moq
   - **Pattern**: Arrange–Act–Assert

7. **CI Expectations**
   - CI matrix runs on all targeted .NET versions (≥ 8.0).
   - All tests must pass; coverage ≥ 80% for public API.

8. **Git Hygiene**
   - Branch naming: `feature/<short-name>` or `fix/<short-name>`.
   - Conventional commits (e.g., `feat(client): add query batching`).
   - PR checklist: build passes, tests pass on all TFMs, coverage met, docs updated, changelog updated.

9. **Coding Standards (Microsoft)**
   - Adopt the **official Microsoft .NET/C# coding conventions** and **.NET API design guidelines**.
   - **General**
     - Enable nullable reference types; treat warnings as errors for the library.
     - Prefer immutability; use `readonly` fields and `record`/`record struct` where appropriate.
     - Avoid public mutable state and statics; favor DI-friendly designs.
     - Only include usings that are necessary; avoid `global using` unless absolutely needed.
   - **Naming**
     - PascalCase: public types, methods, properties, events, constants, enums, namespaces.
     - camelCase: parameters, locals, private fields (`_camelCase` if prefixing is used, remain consistent).
     - Async methods end with `Async`.
     - Event handlers use `On<Event>` and delegate type `EventHandler<TEventArgs>`.
   - **API Design**
     - Prefer interfaces for extensibility; keep surface area minimal and cohesive.
     - Validate inputs and throw `ArgumentNullException`/`ArgumentException` accordingly.
     - Use `CancellationToken` on async APIs; honor cooperative cancellation.
     - Return `Task`/`ValueTask` (avoid `async void` except event handlers).
     - Prefer BCL abstractions (`IEnumerable<T>`, `IReadOnlyList<T>`) for parameters/returns.
   - **Style**
     - Allman braces (opening brace on a new line).
     - Use `var` when type is obvious; otherwise spell the type.
     - File-scoped namespaces.
     - Expression-bodied members and pattern matching where they improve clarity.
     - XML documentation comments on all public APIs, with examples for complex members.
   - **Serialization & HTTP**
     - Use `System.Text.Json` by default; allow injection of custom options/converters.
     - Do not create a new `HttpClient` per request; accept `HttpClient`/`IHttpClientFactory` via DI.
   - **Analyzers & Formatting**
     - Enable **Microsoft.CodeAnalysis.NetAnalyzers**; consider `TreatWarningsAsErrors=true`.
     - Keep formatting consistent via `.editorconfig` committed at repo root.

 10. **Nuget Package Info**
    - The package ID is SitecoreAPIGraphQLClient
    - ensure the package description is clear and concise, suitable for NuGet and GitHub.
    - ensure the README is included in the NuGet package to satisfy `PackageReadmeFile` (fixes NU5039).
    - ensure the package has repository and project URLs set

   **Starter `.editorconfig` excerpt**
   ```editorconfig
   root = true

   [*.{cs,csproj}]
   build_property.Nullable = enable

   # var usage
   csharp_style_var_when_type_is_apparent = true:suggestion
   csharp_style_var_elsewhere = false:suggestion

   # expression-bodied members
   csharp_style_expression_bodied_methods = when_on_single_line:suggestion
   csharp_style_expression_bodied_properties = when_on_single_line:suggestion

   # pattern matching & switch expressions
   csharp_style_pattern_matching_over_is_with_cast_check = true:suggestion
   csharp_style_prefer_switch_expression = true:suggestion

   # naming example: private fields _camelCase
   dotnet_naming_rule.private_fields_should_be_camel_with_underscore.symbols = private_fields
   dotnet_naming_symbols.private_fields.applicable_kinds = field
   dotnet_naming_symbols.private_fields.applicable_accessibilities = private
   dotnet_naming_style.camel_with_underscore.capitalization = camel_case
   dotnet_naming_style.camel_with_underscore.required_prefix = _
   dotnet_naming_rule.private_fields_should_be_camel_with_underscore.style = camel_with_underscore

   # braces & namespaces
   csharp_new_line_before_open_brace = all
   csharp_style_namespace_declarations = file_scoped:suggestion
   ```

---

**On every prompt**, the agent must:
- Apply these rules in full.
- Confirm the plan with the user before execution.
- Break large plans into smaller **phases** and seek approval for each.
- Ensure the solution builds and tests pass for **all targeted frameworks**.
- Update relevant documentation before marking work as complete.
