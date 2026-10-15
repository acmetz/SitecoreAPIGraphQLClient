using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using BenchmarkDotNet.Exporters;

namespace Sitecore.API.Foundation.GraphQL.Benchmarks;

public class Program
{
    public static void Main(string[] args)
    {
        var config = DefaultConfig.Instance
            .AddJob(Job.ShortRun.WithToolchain(InProcessEmitToolchain.Instance))
            .AddExporter(MarkdownExporter.GitHub);

        BenchmarkRunner.Run<SitecoreGraphQLFactoryBenchmarks>(config);
    }
}
