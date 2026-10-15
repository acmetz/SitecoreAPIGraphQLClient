using System.Threading.Tasks;

namespace PerformanceTests
{
    class Program
    {
        static async Task Main(string[] args)
        {
            await SitecoreGraphQLFactoryBenchmark.RunAsync();
        }
    }
}
