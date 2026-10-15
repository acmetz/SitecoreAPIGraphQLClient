using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Options;
using Moq;
using Sitecore.API.Foundation.Authorization.Abstractions;
using Sitecore.API.Foundation.GraphQL;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Sitecore.API.Foundation.GraphQL.Benchmarks;

[MemoryDiagnoser]
public class SitecoreGraphQLFactoryBenchmarks
{
    private SitecoreGraphQLFactory _factory;
    private ISitecoreTokenService _tokenService;
    private IOptions<SitecoreGraphQLOptions> _options;
    private IHttpClientFactory _httpClientFactory;
    private ISitecoreTokenCache _tokenCache;

    [GlobalSetup]
    public void Setup()
    {
        var tokenServiceMock = new Mock<ISitecoreTokenService>();
        _tokenService = tokenServiceMock.Object;

        var optionsMock = new Mock<IOptions<SitecoreGraphQLOptions>>();
        optionsMock.Setup(o => o.Value).Returns(new SitecoreGraphQLOptions
        {
            Endpoint = "https://default.endpoint/graphql",
            ClientId = "default-client",
            ClientSecret = "default-secret"
        });
        _options = optionsMock.Object;

        var httpClientFactoryMock = new Mock<IHttpClientFactory>();
        // Return a single static HttpClient to avoid socket exhaustion during benchmark iterations
        var staticClient = new HttpClient();
        httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(staticClient);
        _httpClientFactory = httpClientFactoryMock.Object;

        var tokenCacheMock = new Mock<ISitecoreTokenCache>();
        _tokenCache = tokenCacheMock.Object;

        _factory = new SitecoreGraphQLFactory(_tokenService, _options, _httpClientFactory, _tokenCache);

        // Pre-cache for the cached benchmark
        _factory.CreateClientAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    [Benchmark]
    public async Task CreateClientAsync_Default()
    {
        // Create a new factory each time to measure the actual creation cost
        var factory = new SitecoreGraphQLFactory(_tokenService, _options, _httpClientFactory, _tokenCache);
        var client = await factory.CreateClientAsync(CancellationToken.None);
    }

    [Benchmark]
    public async Task CreateClientAsync_Cached()
    {
        // The client is already cached in GlobalSetup, this measures retrieval speed
        var client = await _factory.CreateClientAsync(CancellationToken.None);
    }

    [Benchmark]
    public async Task CreateClientAsync_WithUrlAndCredentials()
    {
        var client = await _factory.CreateClientAsync("https://custom.endpoint/graphql", "client-id", "client-secret", CancellationToken.None);
    }
}
