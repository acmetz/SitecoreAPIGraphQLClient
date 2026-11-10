using System.Net;
using System.Text;
using BenchmarkDotNet.Attributes;
using GraphQL.Client.Abstractions;
using Microsoft.Extensions.Options;
using Sitecore.API.Foundation.Authorization.Abstractions;
using Sitecore.API.Foundation.Authorization.Models;
using Sitecore.API.Foundation.GraphQL.Http;
using Sitecore.API.Foundation.GraphQL.Internal;

namespace Sitecore.API.Foundation.GraphQL.Benchmarks;

[MemoryDiagnoser]
public class GraphQLClientBenchmarks : IDisposable
{
    private const int PopulatedClientCount = 64;
    private const string GraphQLPayloadJson = "{\"query\":\"mutation CreateItem($name: String!) { createItem(name: $name) { id } }\",\"variables\":{\"name\":\"Home\"}}";
    private const string GraphQLResponseJson = "{\"data\":{\"createItem\":{\"id\":\"11111111-1111-1111-1111-111111111111\"}}}";

    private SitecoreGraphQLFactory? _factory;
    private HttpClient? _okHttpClient;
    private HttpClient? _unauthorizedRetryHttpClient;
    private SitecoreTokenHandler? _okHandler;
    private SitecoreTokenHandler? _unauthorizedRetryHandler;
    private CredentialContext? _benchCredentialContext;
    private Uri? _endpointUri;

    [GlobalSetup]
    public void Setup()
    {
        _endpointUri = new Uri("https://edge.sitecorecloud.io/api/graphql/v1");

        var options = new SitecoreGraphQLOptions
        {
            Endpoint = "https://edge.sitecorecloud.io/api/graphql/v1",
            ClientId = "default-client-id",
            ClientSecret = "default-client-secret",
            EnableUnauthorizedRefresh = true,
            MaxUnauthorizedRetries = 1,
            MaxCachedClients = 128
        };

        options.Clients["content"] = new SitecoreGraphQLClientOptions
        {
            Endpoint = "https://edge.sitecorecloud.io/api/graphql/v1?sc_apikey=content",
            ClientId = "content-client-id",
            ClientSecret = "content-client-secret"
        };

        var optionsMonitor = new StaticOptionsMonitor<SitecoreGraphQLOptions>(options);
        var tokenService = new StubSitecoreTokenService();
        var coordinator = new SitecoreTokenCoordinator(tokenService, new DefaultTokenValueAccessor());
        var stubHttpClientFactory = new StubHttpClientFactory();

        _factory = new SitecoreGraphQLFactory(coordinator, optionsMonitor, stubHttpClientFactory);

        // Pre-populate 64 cached clients so lookups measure realistic steady-state dictionary hits.
        for (var i = 0; i < PopulatedClientCount; i++)
        {
            _ = _factory.CreateClientAsync(
                $"https://edge.sitecorecloud.io/api/graphql/v1?tenant={i}",
                $"tenant-client-{i}",
                $"tenant-secret-{i}").GetAwaiter().GetResult();
        }

        _ = _factory.CreateClientByNameAsync("content").GetAwaiter().GetResult();

        _benchCredentialContext = new CredentialContext(
            new SitecoreAuthClientCredentials("bench-client", "bench-secret"),
            clientName: "content",
            sanitizedEndpoint: "https://edge.sitecorecloud.io/api/graphql/v1",
            isExplicitClient: false,
            allowPostRetries: false);

        _okHandler = new SitecoreTokenHandler(coordinator, optionsMonitor)
        {
            InnerHandler = new StaticOkTransportHandler()
        };
        _okHttpClient = new HttpClient(_okHandler);

        _unauthorizedRetryHandler = new SitecoreTokenHandler(coordinator, optionsMonitor)
        {
            InnerHandler = new AlternatingUnauthorizedThenOkTransportHandler()
        };
        _unauthorizedRetryHttpClient = new HttpClient(_unauthorizedRetryHandler);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        Dispose();
    }

    [Benchmark(Description = "CreateClientAsync (Explicit Cache Hit)")]
    public Task<IGraphQLClient> CreateClientAsync_CachedHit()
        => _factory!.CreateClientAsync(
            "https://edge.sitecorecloud.io/api/graphql/v1?tenant=32",
            "tenant-client-32",
            "tenant-secret-32");

    [Benchmark(Description = "CreateClientByNameAsync (Named Cache Hit)")]
    public Task<IGraphQLClient> CreateClientByNameAsync_CachedHit()
        => _factory!.CreateClientByNameAsync("content");

    [Benchmark(Description = "SitecoreTokenHandler.SendAsync (200 OK POST)")]
    public async Task<HttpStatusCode> TokenHandler_SendAsync_200Ok()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpointUri)
        {
            Content = new StringContent(GraphQLPayloadJson, Encoding.UTF8, "application/json")
        };
        request.Options.Set(CredentialContext.OptionsKey, _benchCredentialContext!);

        using var response = await _okHttpClient!.SendAsync(request).ConfigureAwait(false);
        return response.StatusCode;
    }

    [Benchmark(Description = "SitecoreTokenHandler.SendAsync (401 Refresh + POST Replay)")]
    public async Task<HttpStatusCode> TokenHandler_SendAsync_401_RefreshAndPostReplay()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpointUri)
        {
            Content = new StringContent(GraphQLPayloadJson, Encoding.UTF8, "application/json")
        };
        request.Options.Set(CredentialContext.OptionsKey, _benchCredentialContext!);

        using var response = await _unauthorizedRetryHttpClient!.SendAsync(request).ConfigureAwait(false);
        return response.StatusCode;
    }

    public void Dispose()
    {
        _okHttpClient?.Dispose();
        _unauthorizedRetryHttpClient?.Dispose();
        _okHandler?.Dispose();
        _unauthorizedRetryHandler?.Dispose();
        _factory?.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class StubSitecoreTokenService : ISitecoreTokenService
    {
        private static readonly SitecoreAuthToken CachedToken = new("bench-access-token-v1", DateTimeOffset.UtcNow.AddHours(1));
        private static readonly SitecoreAuthToken RefreshedToken = new("bench-access-token-v2", DateTimeOffset.UtcNow.AddHours(2));

        public Task<SitecoreAuthToken> GetSitecoreAuthToken(SitecoreAuthClientCredentials credentials)
            => Task.FromResult(CachedToken);

        public Task<SitecoreAuthToken> GetSitecoreAuthToken(SitecoreAuthClientCredentials credentials, CancellationToken cancellationToken)
            => Task.FromResult(CachedToken);

        public Task<SitecoreAuthToken> TryRefreshSitecoreAuthToken(SitecoreAuthToken token)
            => Task.FromResult(RefreshedToken);

        public Task<SitecoreAuthToken> TryRefreshSitecoreAuthToken(SitecoreAuthToken token, CancellationToken cancellationToken)
            => Task.FromResult(RefreshedToken);
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T currentValue) => CurrentValue = currentValue;

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client = new(new StaticOkTransportHandler(), disposeHandler: true);

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class StaticOkTransportHandler : HttpMessageHandler
    {
        private static readonly byte[] ResponseBytes = Encoding.UTF8.GetBytes(GraphQLResponseJson);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ReadOnlyMemoryContent(ResponseBytes)
            });
    }

    private sealed class AlternatingUnauthorizedThenOkTransportHandler : HttpMessageHandler
    {
        private static readonly byte[] ResponseBytes = Encoding.UTF8.GetBytes(GraphQLResponseJson);
        private int _counter;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                _ = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            }

            var call = Interlocked.Increment(ref _counter);
            if ((call & 1) == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ReadOnlyMemoryContent(ResponseBytes)
            };
        }
    }
}
