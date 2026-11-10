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
        targetFrameworks.ShouldBe(["net8.0", "net9.0", "net10.0"]);

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

        var nupkgsDir = Path.Combine(RepoRoot, "nupkgs");
        if (!Directory.Exists(nupkgsDir))
        {
            return;
        }

        foreach (var nupkgPath in Directory.GetFiles(nupkgsDir, "SitecoreAPIGraphQLClient.*.nupkg"))
        {
            using var archive = ZipFile.OpenRead(nupkgPath);
            var nuspecEntry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
            nuspecEntry.ShouldNotBeNull();

            using var stream = nuspecEntry.Open();
            var nuspecDoc = XDocument.Load(stream);
            var groups = nuspecDoc.Descendants().Where(e => e.Name.LocalName == "group").ToArray();
            groups.Length.ShouldBe(3);

            foreach (var group in groups)
            {
                var deps = group.Elements()
                    .Where(e => e.Name.LocalName == "dependency")
                    .Select(e => e.Attribute("id")!.Value)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToArray();

                deps.ShouldBe(ExpectedRuntimeDependencies);
            }
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
