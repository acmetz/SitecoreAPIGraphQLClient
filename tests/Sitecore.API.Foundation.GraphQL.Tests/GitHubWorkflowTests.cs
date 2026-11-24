using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Shouldly;

namespace Sitecore.API.Foundation.GraphQL.Tests;

/// <summary>
/// Contract tests guarding solution structure, GitHub Actions workflow invariants, OIDC Trusted Publishing settings,
/// and NuGet dependency graph hygiene.
/// </summary>
public class GitHubWorkflowTests
{
    private static readonly string RepoRoot = FindRepositoryRoot();
    private static readonly string SolutionPath = Path.Combine(RepoRoot, "Sitecore.API.Foundation.GraphQL.slnx");
    private static readonly string NugetReleaseWorkflowPath = Path.Combine(RepoRoot, ".github", "workflows", "nuget-release.yml");
    private static readonly string PrCheckWorkflowPath = Path.Combine(RepoRoot, ".github", "workflows", "pr-check.yml");
    private static readonly string MainCsprojPath = Path.Combine(RepoRoot, "src", "Sitecore.API.Foundation.GraphQL", "Sitecore.API.Foundation.GraphQL.csproj");
    private static readonly string DirectoryBuildPropsPath = Path.Combine(RepoRoot, "Directory.Build.props");
    private static readonly string GlobalJsonPath = Path.Combine(RepoRoot, "global.json");

    private static readonly string[] ExpectedRuntimeDependencies =
    [
        "GraphQL.Client",
        "GraphQL.Client.Abstractions",
        "GraphQL.Client.Serializer.Newtonsoft",
        "Microsoft.Extensions.Http",
        "Microsoft.Extensions.Http.Resilience",
        "Microsoft.Extensions.Logging.Abstractions",
        "Microsoft.Extensions.Options",
        "Microsoft.Extensions.Options.ConfigurationExtensions",
        "SitecoreAPIAuthorization"
    ];

    // .NET 11 SDK AddPrunePackageReferences prunes Microsoft.Extensions.Logging.Abstractions
    // and Microsoft.Extensions.Options (included in Microsoft.NETCore.App 11.0 PackageOverrides.txt),
    // matching SitecoreAPIAuthorization 1.1.0's packed net11.0 .nuspec dependency group.
    private static readonly string[] ExpectedNet11RuntimeDependencies =
    [
        "GraphQL.Client",
        "GraphQL.Client.Abstractions",
        "GraphQL.Client.Serializer.Newtonsoft",
        "Microsoft.Extensions.Http",
        "Microsoft.Extensions.Http.Resilience",
        "Microsoft.Extensions.Options.ConfigurationExtensions",
        "SitecoreAPIAuthorization"
    ];

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sitecore.API.Foundation.GraphQL.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate repository root containing Sitecore.API.Foundation.GraphQL.slnx.");
    }

    [Fact]
    public void SolutionFile_AllReferencedProjectsAndFiles_ShouldExistOnDisk()
    {
        File.Exists(SolutionPath).ShouldBeTrue("Sitecore.API.Foundation.GraphQL.slnx must exist at repository root.");
        File.Exists(Path.Combine(RepoRoot, "Sitecore.API.Foundation.GraphQL.sln")).ShouldBeFalse("Legacy .sln file must not remain alongside .slnx.");

        var slnxDoc = XDocument.Load(SolutionPath);
        var referencedPaths = slnxDoc.Descendants("Project")
            .Concat(slnxDoc.Descendants("File"))
            .Select(e => e.Attribute("Path")?.Value)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToArray();

        referencedPaths.ShouldNotBeEmpty();

        foreach (var relativePath in referencedPaths)
        {
            var fullPath = Path.Combine(RepoRoot, relativePath!.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(fullPath).ShouldBeTrue($"Path '{relativePath}' referenced in Sitecore.API.Foundation.GraphQL.slnx does not exist on disk.");
        }
    }

    [Fact]
    public void WorkflowFiles_ShouldExist_UseSpaces_AndAvoidFragileFoldedContinuations()
    {
        File.Exists(NugetReleaseWorkflowPath).ShouldBeTrue("nuget-release.yml must exist.");
        File.Exists(PrCheckWorkflowPath).ShouldBeTrue("pr-check.yml must exist.");

        foreach (var workflowFile in Directory.GetFiles(Path.Combine(RepoRoot, ".github", "workflows"), "*.yml"))
        {
            var lines = File.ReadAllLines(workflowFile);
            lines.ShouldNotBeEmpty();

            for (var i = 0; i < lines.Length; i++)
            {
                lines[i].StartsWith('\t').ShouldBeFalse(
                    $"{Path.GetFileName(workflowFile)} line {i + 1} uses tab indentation, which is invalid in YAML.");
            }

            var content = File.ReadAllText(workflowFile);
            Regex.IsMatch(content, @"run:\s*>\s*\r?\n(?:.*\r?\n)*?.*\\\s*\r?\n").ShouldBeFalse(
                $"{Path.GetFileName(workflowFile)} should use 'run: |' instead of 'run: >' for multi-line shell scripts.");
            content.ShouldNotContain("TreatWarningsAsErrors=false", Case.Insensitive);
        }
    }

    [Fact]
    public void Workflows_ShouldInstallAllTargetFrameworkSdks_AndMatchGlobalJson()
    {
        var csprojDoc = XDocument.Load(MainCsprojPath);
        var targetFrameworksRaw = csprojDoc.Descendants("TargetFrameworks").First().Value;
        var targetFrameworks = targetFrameworksRaw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        targetFrameworks.ShouldBe(["net10.0", "net11.0"]);

        var nugetWorkflow = File.ReadAllText(NugetReleaseWorkflowPath);
        var prCheckWorkflow = File.ReadAllText(PrCheckWorkflowPath);

        foreach (var tfm in targetFrameworks)
        {
            var versionPrefix = tfm["net".Length..];
            var sdkChannel = $"{versionPrefix}.x";

            nugetWorkflow.ShouldContain(sdkChannel, Case.Sensitive);
            prCheckWorkflow.ShouldContain(sdkChannel, Case.Sensitive);
            prCheckWorkflow.ShouldContain($"tfm: '{tfm}'", Case.Sensitive);
            nugetWorkflow.ShouldContain($"tfm: '{tfm}'", Case.Sensitive);
        }

        using var globalJson = JsonDocument.Parse(File.ReadAllText(GlobalJsonPath));
        var sdkVersion = globalJson.RootElement.GetProperty("sdk").GetProperty("version").GetString()!;
        var majorMinor = string.Join('.', sdkVersion.Split('.').Take(2));
        nugetWorkflow.ShouldContain($"{majorMinor}.x", Case.Sensitive);
        prCheckWorkflow.ShouldContain($"{majorMinor}.x", Case.Sensitive);
    }

    [Fact]
    public void NugetReleaseWorkflow_ShouldPreserveOidcTrustedPublishing_NugetEnvironment_AndSymbolArtifacts()
    {
        var nugetWorkflow = File.ReadAllText(NugetReleaseWorkflowPath);
        var propsDoc = XDocument.Load(DirectoryBuildPropsPath);

        propsDoc.Descendants("IncludeSymbols").First().Value.ShouldBe("true");
        propsDoc.Descendants("SymbolPackageFormat").First().Value.ShouldBe("snupkg");

        Regex.IsMatch(nugetWorkflow, @"^permissions:\s*\r?\n\s+contents:\s*read", RegexOptions.Multiline)
            .ShouldBeTrue("Root workflow permissions in nuget-release.yml must be 'contents: read'.");

        nugetWorkflow.ShouldContain("if: startsWith(github.ref, 'refs/tags/v')");
        nugetWorkflow.ShouldContain("environment: nuget");
        nugetWorkflow.ShouldContain("id-token: write");
        nugetWorkflow.ShouldContain("uses: NuGet/login@v1");
        nugetWorkflow.ShouldContain("user: ${{ vars.NUGET_USER }}");
        nugetWorkflow.ShouldNotContain("secrets.NUGET_API_KEY");
        nugetWorkflow.ShouldContain("-getProperty:PackageVersion");
        nugetWorkflow.ShouldContain("--skip-duplicate");
        nugetWorkflow.ShouldContain("./nupkgs/SitecoreAPIGraphQLClient*.nupkg");
        nugetWorkflow.ShouldContain("./nupkgs/SitecoreAPIGraphQLClient*.snupkg");
        nugetWorkflow.ShouldContain("bodyFile: CHANGELOG.md");
    }

    [Fact]
    public void LibraryCsproj_AndPackedNuspecIfPresent_ShouldContainStrictlyIntendedRuntimeDependencies()
    {
        var csprojDoc = XDocument.Load(MainCsprojPath);
        var directPackageRefs = csprojDoc.Descendants("PackageReference")
            .Select(e => e.Attribute("Include")?.Value)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        directPackageRefs.ShouldBe(ExpectedRuntimeDependencies);
        directPackageRefs.ShouldNotContain("GraphQL");
        directPackageRefs.ShouldNotContain("Microsoft.Extensions.Configuration.Binder");

        var packageVersion = csprojDoc.Descendants("PackageVersion").First().Value.Trim();

        var nupkgsDir = Path.Combine(RepoRoot, "nupkgs");
        if (!Directory.Exists(nupkgsDir))
        {
            return;
        }

        var nupkgPaths = Directory.GetFiles(nupkgsDir, "SitecoreAPIGraphQLClient.*.nupkg");
        nupkgPaths.ShouldNotBeEmpty("Expected packed .nupkg file when ./nupkgs directory exists.");

        File.Exists(Path.Combine(nupkgsDir, $"SitecoreAPIGraphQLClient.{packageVersion}.nupkg")).ShouldBeTrue();
        File.Exists(Path.Combine(nupkgsDir, $"SitecoreAPIGraphQLClient.{packageVersion}.snupkg")).ShouldBeTrue();

        foreach (var nupkgPath in nupkgPaths)
        {
            using var archive = ZipFile.OpenRead(nupkgPath);
            var nuspecEntry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
            nuspecEntry.ShouldNotBeNull();

            using var stream = nuspecEntry.Open();
            var nuspecDoc = XDocument.Load(stream);
            var groups = nuspecDoc.Descendants().Where(e => e.Name.LocalName == "group").ToArray();
            groups.Length.ShouldBe(2);

            var foundTfms = new List<string>(groups.Length);
            foreach (var group in groups)
            {
                var rawTfm = group.Attribute("targetFramework")?.Value;
                rawTfm.ShouldNotBeNullOrWhiteSpace();
                var normalizedTfm = rawTfm switch
                {
                    ".NETCoreApp10.0" => "net10.0",
                    ".NETCoreApp11.0" => "net11.0",
                    _ => rawTfm
                };
                foundTfms.Add(normalizedTfm);

                var depElements = group.Elements()
                    .Where(e => e.Name.LocalName == "dependency")
                    .ToArray();

                var deps = depElements
                    .Select(e => e.Attribute("id")!.Value)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToArray();

                var expectedDeps = normalizedTfm == "net11.0"
                    ? ExpectedNet11RuntimeDependencies
                    : ExpectedRuntimeDependencies;
                deps.ShouldBe(expectedDeps);

                var authDep = depElements.Single(e => e.Attribute("id")!.Value == "SitecoreAPIAuthorization");
                var rawVersion = authDep.Attribute("version")?.Value;
                rawVersion.ShouldNotBeNullOrWhiteSpace();

                var lowerBound = rawVersion.Split(',')[0].Trim().Trim('[', ']', '(', ')', ' ');
                var withoutBuildMetadata = lowerBound.Split('+')[0];
                var dashIndex = withoutBuildMetadata.IndexOf('-');
                var numericPart = dashIndex >= 0 ? withoutBuildMetadata[..dashIndex] : withoutBuildMetadata;
                var hasPrerelease = dashIndex >= 0;

                var parsed = Version.Parse(numericPart);
                var minRequired = new Version(1, 1, 0);
                (parsed > minRequired || (parsed == minRequired && !hasPrerelease)).ShouldBeTrue(
                    $"SitecoreAPIAuthorization dependency version '{rawVersion}' in TFM group '{rawTfm}' must be >= 1.1.0 GA.");
            }

            foundTfms.Count.ShouldBe(2);
            foundTfms.OrderBy(t => t, StringComparer.Ordinal).ToArray().ShouldBe(["net10.0", "net11.0"]);
        }
    }

    [Fact]
    public void ReadmeFile_ShouldNotContainRelativeLinks_ThatBreakOnNuGetOrg()
    {
        var readmePath = Path.Combine(RepoRoot, "README.md");
        File.Exists(readmePath).ShouldBeTrue();

        var readmeContent = File.ReadAllText(readmePath);
        readmeContent.EndsWith('\n').ShouldBeTrue("README.md should end with a trailing newline per .editorconfig.");

        var markdownWithoutFencedCode = Regex.Replace(readmeContent, @"```[\s\S]*?```", string.Empty);
        var linkMatches = Regex.Matches(markdownWithoutFencedCode, @"\]\(([^)]+)\)");
        linkMatches.Count.ShouldBeGreaterThanOrEqualTo(10);

        foreach (Match match in linkMatches)
        {
            var target = match.Groups[1].Value.Trim();
            (target.StartsWith("https://", StringComparison.Ordinal) ||
             target.StartsWith('#')).ShouldBeTrue(
                $"README.md is packed into the NuGet package and must not use relative link '{target}' (use full https://github.com/acmetz/SitecoreAPIGraphQLClient/blob/main/... URL).");
        }
    }
}
