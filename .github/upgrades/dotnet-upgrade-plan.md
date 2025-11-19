# .NET 10.0 Upgrade Plan

## Execution Steps

Execute steps below sequentially one by one in the order they are listed.

1. Validate that an .NET 10.0 SDK required for this upgrade is installed on the machine and if not, help to get it installed.
2. Ensure that the SDK version specified in global.json files is compatible with the .NET 10.0 upgrade.
3. Upgrade src\Sitecore.API.Foundation.GraphQL\Sitecore.API.Foundation.GraphQL.csproj
4. Upgrade tests\Sitecore.API.Foundation.GraphQL.Tests\Sitecore.API.Foundation.GraphQL.Tests.csproj
5. Run unit tests to validate upgrade in the projects listed below:
  tests\Sitecore.API.Foundation.GraphQL.Tests\Sitecore.API.Foundation.GraphQL.Tests.csproj

## Settings

This section contains settings and data used by execution steps.

### Excluded projects

Table below contains projects that do belong to the dependency graph for selected projects and should not be included in the upgrade.

| Project name                                   | Description                 |
|:-----------------------------------------------|:---------------------------:|

### Aggregate NuGet packages modifications across all projects

NuGet packages used across all selected projects or their dependencies that need version update in projects that reference them.

| Package Name                                   | Current Version | New Version | Description                                   |
|:-----------------------------------------------|:---------------:|:-----------:|:----------------------------------------------|
| Microsoft.Extensions.Configuration             |                 | 10.0.0      | Recommended for .NET 10.0                     |
| Microsoft.Extensions.Configuration.Binder      | 9.0.8           | 10.0.0      | Recommended for .NET 10.0                     |
| Microsoft.Extensions.DependencyInjection       | 9.0.8           | 10.0.0      | Recommended for .NET 10.0                     |
| Microsoft.Extensions.Http                      | 9.0.8           | 10.0.0      | Recommended for .NET 10.0                     |
| Microsoft.Extensions.Options                   | 9.0.8           | 10.0.0      | Recommended for .NET 10.0                     |
| Microsoft.Extensions.Options.ConfigurationExtensions | 9.0.8      | 10.0.0      | Recommended for .NET 10.0                     |

### Project upgrade details
This section contains details about each project upgrade and modifications that need to be done in the project.

#### src\Sitecore.API.Foundation.GraphQL\Sitecore.API.Foundation.GraphQL.csproj modifications

Project properties changes:
  - Target frameworks should be changed from `net8.0;net9.0` to `net8.0;net9.0;net10.0`

NuGet packages changes:
  - Microsoft.Extensions.Http should be updated from `9.0.8` to `10.0.0` (recommended for .NET 10.0)
  - Microsoft.Extensions.Options should be updated from `9.0.8` to `10.0.0` (recommended for .NET 10.0)
  - Microsoft.Extensions.Options.ConfigurationExtensions should be updated from `9.0.8` to `10.0.0` (recommended for .NET 10.0)
  - Microsoft.Extensions.Configuration.Binder should be updated from `9.0.8` to `10.0.0` (recommended for .NET 10.0)

Other changes:
  - None identified by analysis.

#### tests\Sitecore.API.Foundation.GraphQL.Tests\Sitecore.API.Foundation.GraphQL.Tests.csproj modifications

Project properties changes:
  - Target frameworks should be changed from `net8.0;net9.0` to `net8.0;net9.0;net10.0`

NuGet packages changes:
  - Microsoft.Extensions.Configuration should be added/updated to `10.0.0` (recommended for .NET 10.0)
  - Microsoft.Extensions.Configuration.Binder should be updated from `9.0.8` to `10.0.0` (recommended for .NET 10.0)
  - Microsoft.Extensions.DependencyInjection should be updated from `9.0.8` to `10.0.0` (recommended for .NET 10.0)

Other changes:
  - None identified by analysis.
