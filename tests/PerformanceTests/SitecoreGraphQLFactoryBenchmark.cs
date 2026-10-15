using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Sitecore.API.Foundation.GraphQL;
using Sitecore.API.Foundation.Authorization.Abstractions;
using GraphQL.Client.Abstractions;

namespace PerformanceTests
{
    public class SitecoreGraphQLFactoryBenchmark
    {
        public static async Task RunAsync()
        {
            var tokenServiceMock = new Mock<ISitecoreTokenService>();
            var tokenCacheMock = new Mock<Sitecore.API.Foundation.GraphQL.ISitecoreTokenCache>();

            var options = new SitecoreGraphQLOptions
            {
                Endpoint = "https://example.com/graphql",
                ClientId = "test-client",
                ClientSecret = "test-secret"
            };
            var optionsWrapper = new OptionsWrapper<SitecoreGraphQLOptions>(options);

            var httpClientFactoryMock = new Mock<System.Net.Http.IHttpClientFactory>();
            httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new System.Net.Http.HttpClient());

            var factory = new SitecoreGraphQLFactory(
                tokenServiceMock.Object,
                optionsWrapper,
                httpClientFactoryMock.Object,
                tokenCacheMock.Object,
                null); // no logger for benchmark

            // Warmup
            await factory.CreateClientAsync("https://example.com/graphql", "test-client", "test-secret");

            int iterations = 10_000_000;

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                await factory.CreateClientAsync("https://example.com/graphql", "test-client", "test-secret");
            }
            sw.Stop();

            Console.WriteLine($"Elapsed: {sw.ElapsedMilliseconds} ms for {iterations} calls.");
        }
    }
}
