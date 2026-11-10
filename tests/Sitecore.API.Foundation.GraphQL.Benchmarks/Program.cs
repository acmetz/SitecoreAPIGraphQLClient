using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;

namespace Sitecore.API.Foundation.GraphQL.Benchmarks;

public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            var config = DefaultConfig.Instance
                .AddJob(Job.Default.WithRuntime(CoreRuntime.Core80))
                .AddJob(Job.Default.WithRuntime(CoreRuntime.Core90))
                .AddJob(Job.Default.WithRuntime(CoreRuntime.Core10_0));

            BenchmarkRunner.Run<GraphQLClientBenchmarks>(config);
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
