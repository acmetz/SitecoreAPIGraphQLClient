using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using GraphQL;
using GraphQL.Client.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;
using Sitecore.API.Foundation.Authorization.Abstractions;
using Sitecore.API.Foundation.Authorization.Models;
using Sitecore.API.Foundation.GraphQL.DependencyInjection;
using Sitecore.API.Foundation.GraphQL.Internal;
using AuthTokenCache = Sitecore.API.Foundation.Authorization.Abstractions.ISitecoreTokenCache;

namespace Sitecore.API.Foundation.GraphQL.Tests;

public class TypedServiceCollectionExtensionsTests
{
    private sealed class ClientA;

    private sealed class ClientB;

    private sealed class GenericWrapper<T>;

    private sealed class TypedHttpConsumer(HttpClient httpClient)
    {
        public HttpClient HttpClient { get; } = httpClient;
    }

    private sealed class TestHeaderHandler(string headerName, string headerValue) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.Headers.Contains(headerName))
            {
                request.Headers.TryAddWithoutValidation(headerName, headerValue);
            }

            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class CustomTypedTokenService(
        SitecoreAuthToken initialToken,
        SitecoreAuthToken refreshedToken) : ISitecoreTokenService<ClientA>
    {
        public int GetCalls { get; private set; }
        public int RefreshCalls { get; private set; }

        public Task<SitecoreAuthToken> GetSitecoreAuthToken(SitecoreAuthClientCredentials clientCredentials)
        {
            GetCalls++;
            return Task.FromResult(initialToken);
        }

        public Task<SitecoreAuthToken> GetSitecoreAuthToken(
            SitecoreAuthClientCredentials clientCredentials,
            CancellationToken cancellationToken)
            => GetSitecoreAuthToken(clientCredentials);

        public Task<SitecoreAuthToken> TryRefreshSitecoreAuthToken(SitecoreAuthToken staleToken)
        {
            RefreshCalls++;
            return Task.FromResult(refreshedToken);
        }

        public Task<SitecoreAuthToken> TryRefreshSitecoreAuthToken(
            SitecoreAuthToken staleToken,
            CancellationToken cancellationToken)
            => TryRefreshSitecoreAuthToken(staleToken);
    }

    private sealed class DelegateHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> onSend)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => onSend(request, cancellationToken);
    }

    private sealed class SpyTokenCache : AuthTokenCache
    {
        private readonly ConcurrentDictionary<(string ClientId, string ClientSecret, string AuthUrl), SitecoreAuthToken> _entries = new();

        public int OneArgGetCalls { get; private set; }
        public int TwoArgGetCalls { get; private set; }

        public int CacheSize => _entries.Count;

        public SitecoreAuthToken? GetToken(SitecoreAuthClientCredentials clientCredentials)
        {
            OneArgGetCalls++;
            return null;
        }

        public SitecoreAuthToken? GetToken(SitecoreAuthClientCredentials clientCredentials, string? authTokenUrl)
        {
            TwoArgGetCalls++;
            return _entries.TryGetValue((clientCredentials.ClientId, clientCredentials.ClientSecret, authTokenUrl ?? string.Empty), out var token)
                ? token
                : null;
        }

        public void SetToken(SitecoreAuthClientCredentials clientCredentials, SitecoreAuthToken token)
            => _entries[(clientCredentials.ClientId, clientCredentials.ClientSecret, string.Empty)] = token;

        public void SetToken(SitecoreAuthClientCredentials clientCredentials, SitecoreAuthToken token, string? authTokenUrl)
            => _entries[(clientCredentials.ClientId, clientCredentials.ClientSecret, authTokenUrl ?? string.Empty)] = token;

        public SitecoreAuthClientCredentials? RemoveToken(SitecoreAuthToken token) => null;

        public SitecoreAuthClientCredentials? RemoveToken(SitecoreAuthToken token, string? authTokenUrl)
        {
            foreach (var kvp in _entries)
            {
                if (kvp.Value.AccessToken == token.AccessToken &&
                    string.Equals(kvp.Key.AuthUrl, authTokenUrl ?? string.Empty, StringComparison.Ordinal))
                {
                    _entries.TryRemove(kvp.Key, out _);
                    return new SitecoreAuthClientCredentials(kvp.Key.ClientId, kvp.Key.ClientSecret);
                }
            }

            return null;
        }

        public void ClearCache() => _entries.Clear();

        public void PerformCleanup()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed record CapturedLogEntry(LogLevel Level, int EventId, string Message);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<CapturedLogEntry> _entries = new();

        public IReadOnlyCollection<CapturedLogEntry> Entries => _entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<CapturedLogEntry> entries) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => entries.Enqueue(new CapturedLogEntry(logLevel, eventId.Id, formatter(state, exception)));

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose()
                {
                }
            }
        }
    }

    private sealed class ReloadableConfigurationSource : IConfigurationSource
    {
        public ReloadableConfigurationProvider Provider { get; } = new();

        public IConfigurationProvider Build(IConfigurationBuilder builder) => Provider;
    }

    private sealed class ReloadableConfigurationProvider : ConfigurationProvider
    {
        public void Update(IDictionary<string, string?> values)
        {
            Data = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
            OnReload();
        }
    }

    private static DelegateHttpHandler CreateJsonOkHandler(Action<HttpRequestMessage>? inspect = null)
        => new((request, _) =>
        {
            inspect?.Invoke(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":{\"ok\":true}}", Encoding.UTF8, "application/json")
            });
        });

    private static DelegateHttpHandler CreateOAuthHandler(string accessToken, Action<HttpRequestMessage>? inspect = null)
        => new((request, _) =>
        {
            inspect?.Invoke(request);
            var json = $"{{\"access_token\":\"{accessToken}\",\"expires_in\":3600,\"token_type\":\"Bearer\"}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        });

    private static Task<GraphQLResponse<Newtonsoft.Json.Linq.JObject>> SendTestQueryAsync(IGraphQLClient client)
        => client.SendQueryAsync<Newtonsoft.Json.Linq.JObject>(new GraphQLRequest { Query = "{ ok }" });

    [Fact]
    public async Task Distinct_typed_clients_have_isolated_options_http_pipelines_and_resilience()
    {
        var clientARequests = new List<(string? Uri, string? CustomHeader, string? NamedHeader, string? Bearer)>();
        var clientBRequests = new List<(string? Uri, string? CustomHeader, string? NamedHeader, string? Bearer)>();
        var clientAAttempts = 0;
        var clientBAttempts = 0;

        var services = new ServiceCollection();

        services.AddSitecoreGraphQL<ClientA>(o =>
        {
            o.Endpoint = "https://a.sitecore.test/graphql";
            o.ClientId = "id-a";
            o.ClientSecret = "secret-a";
            o.AllowPostRetries = true;
            o.Clients["preview"] = new SitecoreGraphQLClientOptions
            {
                Endpoint = "https://a-preview.sitecore.test/graphql",
                ClientId = "id-a-prev",
                ClientSecret = "secret-a-prev"
            };
        })
        .ConfigureResilience(r =>
        {
            r.Retry.MaxRetryAttempts = 1;
            r.Retry.Delay = TimeSpan.FromMilliseconds(1);
            r.Retry.UseJitter = false;
        })
        .ConfigureHttpClient(b => b
            .AddHttpMessageHandler(() => new TestHeaderHandler("X-Client", "A"))
            .ConfigurePrimaryHttpMessageHandler(() => new DelegateHttpHandler((req, _) =>
            {
                clientARequests.Add((
                    req.RequestUri?.ToString(),
                    req.Headers.TryGetValues("X-Client", out var hv) ? hv.Single() : null,
                    req.Headers.TryGetValues("X-Client-B", out var hvb) ? hvb.Single() : null,
                    req.Headers.Authorization?.Parameter));

                if (Interlocked.Increment(ref clientAAttempts) == 1)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":{\"ok\":true}}", Encoding.UTF8, "application/json")
                });
            })));

        services.AddSitecoreGraphQL<ClientB>(o =>
        {
            o.Endpoint = "https://b.sitecore.test/graphql";
            o.ClientId = "id-b";
            o.ClientSecret = "secret-b";
            o.AllowPostRetries = false;
        })
        .ConfigureResilience(r =>
        {
            r.Retry.MaxRetryAttempts = 2;
            r.Retry.Delay = TimeSpan.FromMilliseconds(1);
            r.Retry.UseJitter = false;
        })
        .ConfigureHttpClient(b => b
            .AddHttpMessageHandler(() => new TestHeaderHandler("X-Client-B", "B"))
            .ConfigurePrimaryHttpMessageHandler(() => new DelegateHttpHandler((req, _) =>
            {
                clientBRequests.Add((
                    req.RequestUri?.ToString(),
                    req.Headers.TryGetValues("X-Client", out var hv) ? hv.Single() : null,
                    req.Headers.TryGetValues("X-Client-B", out var hvb) ? hvb.Single() : null,
                    req.Headers.Authorization?.Parameter));

                Interlocked.Increment(ref clientBAttempts);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            })));

        var tokenService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        tokenService
            .Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SitecoreAuthClientCredentials c, CancellationToken _) =>
                new SitecoreAuthToken($"tok-{c.ClientId}", DateTimeOffset.UtcNow.AddHours(1)));
        services.AddSingleton(tokenService.Object);

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var factoryA = provider.GetRequiredService<ISitecoreGraphQLFactory<ClientA>>();
        var factoryB = provider.GetRequiredService<ISitecoreGraphQLFactory<ClientB>>();

        ReferenceEquals(factoryA, factoryB).ShouldBeFalse();

        var clientA = await factoryA.CreateClientAsync();
        var responseA = await SendTestQueryAsync(clientA);
        responseA.Data!["ok"]!.ToObject<bool>().ShouldBeTrue();
        clientAAttempts.ShouldBe(2);
        clientARequests.ShouldAllBe(r => r.Uri == "https://a.sitecore.test/graphql" && r.CustomHeader == "A" && r.NamedHeader == null && r.Bearer == "tok-id-a");

        var previewA = await factoryA.CreateClientByNameAsync("preview");
        await SendTestQueryAsync(previewA);
        clientARequests[^1].Uri.ShouldBe("https://a-preview.sitecore.test/graphql");
        clientARequests[^1].Bearer.ShouldBe("tok-id-a-prev");

        var clientB = await factoryB.CreateClientAsync();
        await Should.ThrowAsync<global::GraphQL.Client.Http.GraphQLHttpRequestException>(() => SendTestQueryAsync(clientB));
        clientBAttempts.ShouldBe(1);
        clientBRequests.Single().Uri.ShouldBe("https://b.sitecore.test/graphql");
        clientBRequests.Single().CustomHeader.ShouldBeNull();
        clientBRequests.Single().NamedHeader.ShouldBe("B");
        clientBRequests.Single().Bearer.ShouldBe("tok-id-b");
    }

    [Fact]
    public async Task Typed_factory_coexists_with_AddHttpClient_of_TClient_without_collision()
    {
        var consumerSawAuthHeader = false;
        var graphqlSawConsumerHeader = false;

        var services = new ServiceCollection();

        services.AddHttpClient<TypedHttpConsumer>(c => c.BaseAddress = new Uri("https://consumer.sitecore.test/"))
            .AddHttpMessageHandler(() => new TestHeaderHandler("X-Consumer", "only-consumer"))
            .ConfigurePrimaryHttpMessageHandler(() => new DelegateHttpHandler((req, _) =>
            {
                consumerSawAuthHeader = req.Headers.Authorization is not null;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }));

        services.AddSitecoreGraphQL<TypedHttpConsumer>(o =>
        {
            o.Endpoint = "https://gql.sitecore.test/graphql";
            o.ClientId = "gql-id";
            o.ClientSecret = "gql-secret";
        })
        .ConfigureHttpClient(b => b.ConfigurePrimaryHttpMessageHandler(() => CreateJsonOkHandler(req =>
        {
            graphqlSawConsumerHeader = req.Headers.Contains("X-Consumer");
            req.Headers.Authorization?.Parameter.ShouldBe("gql-token");
        })));

        var tokenService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        tokenService
            .Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SitecoreAuthToken("gql-token", DateTimeOffset.UtcNow.AddHours(1)));
        services.AddSingleton(tokenService.Object);

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        var consumer = provider.GetRequiredService<TypedHttpConsumer>();
        using var consumerResp = await consumer.HttpClient.GetAsync("status");
        consumerResp.StatusCode.ShouldBe(HttpStatusCode.OK);

        var factory = provider.GetRequiredService<ISitecoreGraphQLFactory<TypedHttpConsumer>>();
        var gqlClient = await factory.CreateClientAsync();
        await SendTestQueryAsync(gqlClient);

        consumerSawAuthHeader.ShouldBeFalse();
        graphqlSawConsumerHeader.ShouldBeFalse();
    }

    [Fact]
    public async Task ConfigureAuthentication_and_ConfigureAuthenticationSingleton_isolate_endpoints_with_same_credentials()
    {
        string? observedBearerA = null;
        string? observedBearerB = null;
        var authACalls = 0;
        var authBCalls = 0;

        var services = new ServiceCollection();

        services.AddSitecoreGraphQL<ClientA>(o =>
        {
            o.Endpoint = "https://gql-a.sitecore.test/graphql";
            o.ClientId = "shared-client-id";
            o.ClientSecret = "shared-client-secret";
        })
        .ConfigureAuthentication(auth => auth
            .ConfigureOptions(o => o.AuthTokenUrl = "https://auth-a.sitecore.test/oauth/token")
            .ConfigureHttpClient(b => b.ConfigurePrimaryHttpMessageHandler(() => CreateOAuthHandler("token-from-a", req =>
            {
                req.RequestUri?.ToString().ShouldBe("https://auth-a.sitecore.test/oauth/token");
                Interlocked.Increment(ref authACalls);
            }))))
        .ConfigureHttpClient(b => b.ConfigurePrimaryHttpMessageHandler(() =>
            CreateJsonOkHandler(req => observedBearerA = req.Headers.Authorization?.Parameter)));

        services.AddSitecoreGraphQL<ClientB>(o =>
        {
            o.Endpoint = "https://gql-b.sitecore.test/graphql";
            o.ClientId = "shared-client-id";
            o.ClientSecret = "shared-client-secret";
        })
        .ConfigureAuthenticationSingleton(auth => auth
            .ConfigureOptions(o => o.AuthTokenUrl = "https://auth-b.sitecore.test/oauth/token")
            .ConfigureHttpClient(b => b.ConfigurePrimaryHttpMessageHandler(() => CreateOAuthHandler("token-from-b", req =>
            {
                req.RequestUri?.ToString().ShouldBe("https://auth-b.sitecore.test/oauth/token");
                Interlocked.Increment(ref authBCalls);
            }))))
        .ConfigureHttpClient(b => b.ConfigurePrimaryHttpMessageHandler(() =>
            CreateJsonOkHandler(req => observedBearerB = req.Headers.Authorization?.Parameter)));

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        var factoryA = provider.GetRequiredService<ISitecoreGraphQLFactory<ClientA>>();
        var factoryB = provider.GetRequiredService<ISitecoreGraphQLFactory<ClientB>>();

        await SendTestQueryAsync(await factoryA.CreateClientAsync());
        await SendTestQueryAsync(await factoryB.CreateClientAsync());

        observedBearerA.ShouldBe("token-from-a");
        observedBearerB.ShouldBe("token-from-b");
        authACalls.ShouldBe(1);
        authBCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Typed_coordinator_falls_back_to_untyped_service_logs_once_and_staggered_401_uses_cache()
    {
        var loggerProvider = new CapturingLoggerProvider();
        var untypedTokenServiceCalls = 0;
        var credentials = new SitecoreAuthClientCredentials("shared-id", "shared-secret");
        var refreshedToken = new SitecoreAuthToken("fresh-from-cache", DateTimeOffset.UtcNow.AddHours(1));

        var authCache = new Mock<AuthTokenCache>(MockBehavior.Strict);
        authCache.Setup(c => c.GetToken(credentials)).Returns(refreshedToken);

        var untypedTokenService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        untypedTokenService
            .Setup(s => s.GetSitecoreAuthToken(credentials))
            .ReturnsAsync(() =>
            {
                Interlocked.Increment(ref untypedTokenServiceCalls);
                return new SitecoreAuthToken("initial", DateTimeOffset.UtcNow.AddHours(1));
            });
        untypedTokenService
            .Setup(s => s.GetSitecoreAuthToken(credentials, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Interlocked.Increment(ref untypedTokenServiceCalls);
                return new SitecoreAuthToken("initial", DateTimeOffset.UtcNow.AddHours(1));
            });

        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(loggerProvider);
        });
        services.AddSingleton(authCache.Object);
        services.AddScoped(_ => untypedTokenService.Object);
        services.AddSitecoreGraphQL<ClientA>(o =>
        {
            o.Endpoint = "https://fallback.sitecore.test/graphql";
            o.ClientId = credentials.ClientId;
            o.ClientSecret = credentials.ClientSecret;
        });

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var coordinator = provider.GetRequiredService<ISitecoreTokenCoordinator<ClientA>>();

        var firstGet = await coordinator.GetTokenAsync(credentials, null, CancellationToken.None);
        firstGet.AccessToken.ShouldBe("initial");

        var secondGet = await coordinator.GetTokenAsync(credentials, null, CancellationToken.None);
        secondGet.AccessToken.ShouldBe("initial");

        // Fallback log EventId 1202 must be emitted only once per coordinator instance.
        var fallbackLogs = loggerProvider.Entries.Where(e => e.EventId == 1202).ToArray();
        fallbackLogs.Length.ShouldBe(1);
        fallbackLogs[0].Level.ShouldBe(LogLevel.Information);

        // Staggered 401 refresh must reuse the already-refreshed ISitecoreTokenCache entry without resolving Scoped ISitecoreTokenService.
        var callsBeforeRefresh = untypedTokenServiceCalls;
        var failed = new SitecoreAuthToken("stale", DateTimeOffset.UtcNow.AddMinutes(5));
        var refreshResult = await coordinator.RefreshTokenAsync(credentials, failed, "stale", null, CancellationToken.None);
        refreshResult.AccessToken.ShouldBe("fresh-from-cache");
        untypedTokenServiceCalls.ShouldBe(callsBeforeRefresh);

        // Same staggered 401 cache fast path holds for the untyped coordinator.
        var untypedServices = new ServiceCollection();
        untypedServices.AddLogging();
        untypedServices.AddSingleton(authCache.Object);
        untypedServices.AddScoped(_ => untypedTokenService.Object);
        untypedServices.AddSitecoreGraphQL(o =>
        {
            o.Endpoint = "https://untyped.sitecore.test/graphql";
            o.ClientId = credentials.ClientId;
            o.ClientSecret = credentials.ClientSecret;
        });

        await using var untypedProvider = untypedServices.BuildServiceProvider();
        var untypedCoordinator = untypedProvider.GetRequiredService<ISitecoreTokenCoordinator>();
        var untypedStaggered = await untypedCoordinator.RefreshTokenAsync(credentials, failed, "stale", null, CancellationToken.None);
        untypedStaggered.AccessToken.ShouldBe("fresh-from-cache");
        untypedTokenServiceCalls.ShouldBe(callsBeforeRefresh);
    }

    [Fact]
    public async Task Missing_both_typed_and_untyped_token_service_throws_actionable_InvalidOperationException()
    {
        var services = new ServiceCollection();
        services.AddSitecoreGraphQL<ClientA>(o =>
        {
            o.Endpoint = "https://missing-auth.sitecore.test/graphql";
            o.ClientId = "id";
            o.ClientSecret = "secret";
        });

        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ISitecoreGraphQLFactory<ClientA>>();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => factory.CreateClientAsync().ContinueWith(async t =>
        {
            var client = await t;
            await SendTestQueryAsync(client);
        }).Unwrap());

        ex.Message.ShouldContain(".ConfigureAuthentication");
        ex.Message.ShouldContain("AddSitecoreAuthentication<");
        ex.Message.ShouldContain("services.AddSitecoreAuthentication");
    }

    [Fact]
    public async Task Typed_coordinator_with_SitecoreTokenService_never_probes_one_arg_cache_and_handles_empty_and_warm_refresh_paths()
    {
        var spyCache = new SpyTokenCache();
        var oauthCalls = 0;
        var tokenCounter = 0;
        var refreshesMetric = 0L;
        var durationMetricCount = 0;
        var loggerProvider = new CapturingLoggerProvider();

        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == SitecoreGraphQLDiagnostics.InstrumentationName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        var expectedClientType = typeof(ClientA).ToString();
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            if (instrument.Name != "sitecore.graphql.token.refreshes")
            {
                return;
            }

            foreach (var tag in tags)
            {
                if (tag.Key == "client.type" && Equals(tag.Value, expectedClientType))
                {
                    Interlocked.Add(ref refreshesMetric, measurement);
                    break;
                }
            }
        });
        meterListener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
        {
            if (instrument.Name != "sitecore.graphql.token.refresh.duration")
            {
                return;
            }

            foreach (var tag in tags)
            {
                if (tag.Key == "client.type" && Equals(tag.Value, expectedClientType))
                {
                    Interlocked.Increment(ref durationMetricCount);
                    break;
                }
            }
        });
        meterListener.Start();

        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(loggerProvider);
        });
        services.AddSingleton<AuthTokenCache>(spyCache);

        services.AddSitecoreGraphQL<ClientA>(o =>
        {
            o.Endpoint = "https://gql.sitecore.test/graphql";
            o.ClientId = "typed-id";
            o.ClientSecret = "typed-secret";
        })
        .ConfigureAuthentication(auth => auth
            .ConfigureOptions(o => o.AuthTokenUrl = "https://auth.sitecore.test/oauth/token")
            .ConfigureHttpClient(b => b.ConfigurePrimaryHttpMessageHandler(() => new DelegateHttpHandler((_, _) =>
            {
                Interlocked.Increment(ref oauthCalls);
                var seq = Interlocked.Increment(ref tokenCounter);
                var json = $"{{\"access_token\":\"tok-{seq}\",\"expires_in\":3600,\"token_type\":\"Bearer\"}}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }))));

        await using var provider = services.BuildServiceProvider();
        var coordinator = provider.GetRequiredService<ISitecoreTokenCoordinator<ClientA>>();
        var creds = new SitecoreAuthClientCredentials("typed-id", "typed-secret");

        // 1. Globally empty (CacheSize == 0) manual refresh -> 1 OAuth call, 1 TokenRefreshes/Duration recording, 0 one-arg GetToken calls.
        var r1 = await coordinator.RefreshTokenAsync(creds, null, null, null, CancellationToken.None);
        r1.AccessToken.ShouldBe("tok-1");
        oauthCalls.ShouldBe(1);
        refreshesMetric.ShouldBe(1);
        durationMetricCount.ShouldBe(1);
        spyCache.OneArgGetCalls.ShouldBe(0);
        spyCache.TwoArgGetCalls.ShouldBeGreaterThan(0);

        // 2. Globally empty after ClearCache() on 401 -> 1 OAuth call, 1 TokenRefreshes/Duration recording.
        spyCache.ClearCache();
        spyCache.CacheSize.ShouldBe(0);
        var r2 = await coordinator.RefreshTokenAsync(creds, new SitecoreAuthToken("tok-1", DateTimeOffset.UtcNow.AddHours(1)), "tok-1", null, CancellationToken.None);
        r2.AccessToken.ShouldBe("tok-2");
        oauthCalls.ShouldBe(2);
        refreshesMetric.ShouldBe(2);
        durationMetricCount.ShouldBe(2);
        spyCache.OneArgGetCalls.ShouldBe(0);

        // 3. Warm manual refresh -> 1 OAuth call (CacheSize > 0 and creds are warm).
        var r3 = await coordinator.RefreshTokenAsync(creds, null, null, null, CancellationToken.None);
        r3.AccessToken.ShouldBe("tok-3");
        oauthCalls.ShouldBe(3);
        refreshesMetric.ShouldBe(3);
        durationMetricCount.ShouldBe(3);

        // 4. Cold-partition manual refresh (CacheSize > 0, new credential set) -> 2 OAuth calls (Get + TryRefresh, matching standard RefreshWithServiceAsync behavior).
        var coldCreds = new SitecoreAuthClientCredentials("cold-id", "cold-secret");
        var r4 = await coordinator.RefreshTokenAsync(coldCreds, null, null, null, CancellationToken.None);
        r4.AccessToken.ShouldBe("tok-5");
        oauthCalls.ShouldBe(5);
        spyCache.OneArgGetCalls.ShouldBe(0);

        // 5. Expired-token manual refresh (expired token seeded in cache) -> up to 2 OAuth calls.
        var expiredCreds = new SitecoreAuthClientCredentials("exp-id", "exp-secret");
        spyCache.SetToken(expiredCreds, new SitecoreAuthToken("exp-tok", DateTimeOffset.UtcNow.AddMinutes(-5)), "https://auth.sitecore.test/oauth/token");
        var callsBeforeExpired = oauthCalls;
        await coordinator.RefreshTokenAsync(expiredCreds, null, null, null, CancellationToken.None);
        (oauthCalls - callsBeforeExpired).ShouldBeInRange(1, 2);

        // 6. Warm staggered 401 after manual refresh -> short-circuits via InvokeGetTokenAsync, logs 1203 Debug, 0 extra OAuth/refresh metrics, 0 warnings.
        var callsBeforeStaggered = oauthCalls;
        var refreshesBeforeStaggered = refreshesMetric;
        var durationsBeforeStaggered = durationMetricCount;
        var warningsBefore = loggerProvider.Entries.Count(e => e.Level >= LogLevel.Warning);

        var staleFailed = new SitecoreAuthToken("tok-2", DateTimeOffset.UtcNow.AddHours(1));
        var r6 = await coordinator.RefreshTokenAsync(creds, staleFailed, "tok-2", null, CancellationToken.None);
        r6.AccessToken.ShouldBe("tok-3");
        oauthCalls.ShouldBe(callsBeforeStaggered);
        refreshesMetric.ShouldBe(refreshesBeforeStaggered);
        durationMetricCount.ShouldBe(durationsBeforeStaggered);
        loggerProvider.Entries.Count(e => e.Level >= LogLevel.Warning).ShouldBe(warningsBefore);
        loggerProvider.Entries.ShouldContain(e => e.EventId == 1203 && e.Level == LogLevel.Debug);
        spyCache.OneArgGetCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Custom_ISitecoreTokenService_not_deriving_from_SitecoreTokenService_receives_TryRefresh_even_when_cache_empty()
    {
        var emptyCache = new SpyTokenCache();
        emptyCache.CacheSize.ShouldBe(0);

        var stale = new SitecoreAuthToken("custom-stale", DateTimeOffset.UtcNow.AddMinutes(5));
        var fresh = new SitecoreAuthToken("custom-fresh", DateTimeOffset.UtcNow.AddHours(1));
        var creds = new SitecoreAuthClientCredentials("custom-id", "custom-secret");

        var customService = new CustomTypedTokenService(stale, fresh);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<AuthTokenCache>(emptyCache);
        services.AddScoped<ISitecoreTokenService<ClientA>>(_ => customService);
        services.AddSitecoreGraphQL<ClientA>(o =>
        {
            o.Endpoint = "https://custom.sitecore.test/graphql";
            o.ClientId = creds.ClientId;
            o.ClientSecret = creds.ClientSecret;
        });

        await using var provider = services.BuildServiceProvider();
        var coordinator = provider.GetRequiredService<ISitecoreTokenCoordinator<ClientA>>();

        var on401 = await coordinator.RefreshTokenAsync(creds, stale, "custom-stale", null, CancellationToken.None);
        on401.AccessToken.ShouldBe("custom-fresh");

        var onManual = await coordinator.RefreshTokenAsync(creds, null, null, null, CancellationToken.None);
        onManual.AccessToken.ShouldBe("custom-fresh");

        customService.RefreshCalls.ShouldBe(2);
        emptyCache.OneArgGetCalls.ShouldBe(0);
    }

    [Fact]
    public async Task RefreshTokenAsync_is_scoped_per_type_and_disposing_one_factory_does_not_disrupt_siblings()
    {
        var refreshedClientIds = new ConcurrentBag<string>();

        var services = new ServiceCollection();
        services.AddSitecoreGraphQL<ClientA>(o =>
        {
            o.Endpoint = "https://a.sitecore.test/graphql";
            o.ClientId = "cred-a";
            o.ClientSecret = "sec-a";
            o.Clients["preview"] = new SitecoreGraphQLClientOptions
            {
                Endpoint = "https://a-preview.sitecore.test/graphql",
                ClientId = "cred-a-preview",
                ClientSecret = "sec-a-preview"
            };
        });
        services.AddSitecoreGraphQL<ClientB>(o =>
        {
            o.Endpoint = "https://b.sitecore.test/graphql";
            o.ClientId = "cred-b";
            o.ClientSecret = "sec-b";
        });

        var tokenService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        tokenService
            .Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>()))
            .ReturnsAsync((SitecoreAuthClientCredentials c) =>
            {
                refreshedClientIds.Add(c.ClientId);
                return new SitecoreAuthToken($"tok-{c.ClientId}", DateTimeOffset.UtcNow.AddHours(1));
            });
        tokenService
            .Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SitecoreAuthClientCredentials c, CancellationToken _) =>
            {
                refreshedClientIds.Add(c.ClientId);
                return new SitecoreAuthToken($"tok-{c.ClientId}", DateTimeOffset.UtcNow.AddHours(1));
            });
        tokenService
            .Setup(s => s.TryRefreshSitecoreAuthToken(It.IsAny<SitecoreAuthToken>()))
            .ReturnsAsync((SitecoreAuthToken t) => new SitecoreAuthToken($"{t.AccessToken}-r", DateTimeOffset.UtcNow.AddHours(1)));
        tokenService
            .Setup(s => s.TryRefreshSitecoreAuthToken(It.IsAny<SitecoreAuthToken>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SitecoreAuthToken t, CancellationToken _) => new SitecoreAuthToken($"{t.AccessToken}-r", DateTimeOffset.UtcNow.AddHours(1)));
        services.AddSingleton(tokenService.Object);

        await using var provider = services.BuildServiceProvider();
        var factoryA = provider.GetRequiredService<ISitecoreGraphQLFactory<ClientA>>();
        var factoryB = provider.GetRequiredService<ISitecoreGraphQLFactory<ClientB>>();

        (await factoryA.RefreshTokenAsync()).ShouldBeTrue();
        refreshedClientIds.ShouldContain("cred-a");
        refreshedClientIds.ShouldContain("cred-a-preview");
        refreshedClientIds.ShouldNotContain("cred-b");

        refreshedClientIds.Clear();
        (await factoryA.RefreshTokenByNameAsync("preview")).ShouldBeTrue();
        refreshedClientIds.ShouldContain("cred-a-preview");
        refreshedClientIds.ShouldNotContain("cred-a");
        refreshedClientIds.ShouldNotContain("cred-b");

        ((IDisposable)factoryA).Dispose();
        await Should.ThrowAsync<ObjectDisposedException>(() => factoryA.CreateClientAsync());

        var clientB = await factoryB.CreateClientAsync();
        clientB.ShouldNotBeNull();
    }

    [Fact]
    public async Task Telemetry_tags_include_client_type_without_Version_metadata_and_omit_client_name_on_evictions()
    {
        var recordedTags = new ConcurrentBag<(string Instrument, Dictionary<string, object?> Tags)>();
        var activities = new ConcurrentBag<(string OperationName, Dictionary<string, object?> Tags)>();

        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == SitecoreGraphQLDiagnostics.InstrumentationName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            var dict = new Dictionary<string, object?>();
            foreach (var tag in tags)
            {
                dict[tag.Key] = tag.Value;
            }
            recordedTags.Add((instrument.Name, dict));
        });
        meterListener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
        {
            var dict = new Dictionary<string, object?>();
            foreach (var tag in tags)
            {
                dict[tag.Key] = tag.Value;
            }
            recordedTags.Add((instrument.Name, dict));
        });
        meterListener.Start();

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == SitecoreGraphQLDiagnostics.InstrumentationName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                var dict = new Dictionary<string, object?>();
                foreach (var tag in activity.TagObjects)
                {
                    dict[tag.Key] = tag.Value;
                }
                activities.Add((activity.OperationName, dict));
            }
        };
        ActivitySource.AddActivityListener(activityListener);

        var sendCount = 0;
        var services = new ServiceCollection();

        // Untyped factory (should omit client.type)
        services.AddSitecoreGraphQL(o =>
        {
            o.Endpoint = "https://untyped.sitecore.test/graphql";
            o.ClientId = "u-id";
            o.ClientSecret = "u-sec";
        }).AddHttpClient(SitecoreGraphQLFactory.NamedHttpClient)
          .ConfigurePrimaryHttpMessageHandler(() => CreateJsonOkHandler());

        // Regular typed client with 401 retry + MaxCachedClients = 1 for eviction
        services.AddSitecoreGraphQL<ClientA>(o =>
        {
            o.Endpoint = "https://typed-a.sitecore.test/graphql";
            o.ClientId = "a-id";
            o.ClientSecret = "a-sec";
            o.MaxCachedClients = 1;
            o.MaxUnauthorizedRetries = 1;
        })
        .ConfigureHttpClient(b => b.ConfigurePrimaryHttpMessageHandler(() => new DelegateHttpHandler((_, _) =>
        {
            if (Interlocked.Increment(ref sendCount) == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":{\"ok\":true}}", Encoding.UTF8, "application/json")
            });
        })));

        // Closed-generic and generic-array TClient types
        services.AddSitecoreGraphQL<GenericWrapper<ClientA>>(o =>
        {
            o.Endpoint = "https://typed-gen.sitecore.test/graphql";
            o.ClientId = "g-id";
            o.ClientSecret = "g-sec";
        })
        .ConfigureHttpClient(b => b.ConfigurePrimaryHttpMessageHandler(() => CreateJsonOkHandler()));

        services.AddSitecoreGraphQL<GenericWrapper<ClientA>[]>(o =>
        {
            o.Endpoint = "https://typed-arr.sitecore.test/graphql";
            o.ClientId = "arr-id";
            o.ClientSecret = "arr-sec";
        })
        .ConfigureHttpClient(b => b.ConfigurePrimaryHttpMessageHandler(() => CreateJsonOkHandler()));

        var tokenService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        tokenService
            .Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SitecoreAuthClientCredentials c, CancellationToken _) =>
                new SitecoreAuthToken($"tok-{c.ClientId}", DateTimeOffset.UtcNow.AddHours(1)));
        tokenService
            .Setup(s => s.TryRefreshSitecoreAuthToken(It.IsAny<SitecoreAuthToken>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SitecoreAuthToken("tok-refreshed", DateTimeOffset.UtcNow.AddHours(1)));
        services.AddSingleton(tokenService.Object);

        await using var provider = services.BuildServiceProvider();

        // Execute untyped
        var untypedFactory = provider.GetRequiredService<ISitecoreGraphQLFactory>();
        await SendTestQueryAsync(await untypedFactory.CreateClientAsync());

        // Execute ClientA with 401 retry + eviction
        var factoryA = provider.GetRequiredService<ISitecoreGraphQLFactory<ClientA>>();
        await SendTestQueryAsync(await factoryA.CreateClientAsync());
        await factoryA.CreateClientAsync("https://dyn1.sitecore.test/graphql", "d1", "s1");
        await factoryA.CreateClientAsync("https://dyn2.sitecore.test/graphql", "d2", "s2");

        // Execute generic & array clients
        var genFactory = provider.GetRequiredService<ISitecoreGraphQLFactory<GenericWrapper<ClientA>>>();
        await SendTestQueryAsync(await genFactory.CreateClientAsync());

        var arrFactory = provider.GetRequiredService<ISitecoreGraphQLFactory<GenericWrapper<ClientA>[]>>();
        await SendTestQueryAsync(await arrFactory.CreateClientAsync());

        var clientAType = typeof(ClientA).ToString();
        var genType = typeof(GenericWrapper<ClientA>).ToString();
        var arrType = typeof(GenericWrapper<ClientA>[]).ToString();

        genType.ShouldNotContain("Version=");
        arrType.ShouldNotContain("Version=");

        // Untyped metric should not have client.type
        recordedTags.ShouldContain(t =>
            t.Instrument == "sitecore.graphql.token.requests" &&
            !t.Tags.ContainsKey("client.type"));

        // Typed metrics & activities must have client.type
        recordedTags.ShouldContain(t => t.Instrument == "sitecore.graphql.token.requests" && Equals(t.Tags.GetValueOrDefault("client.type"), clientAType));
        recordedTags.ShouldContain(t => t.Instrument == "sitecore.graphql.token.refreshes" && Equals(t.Tags.GetValueOrDefault("client.type"), clientAType));
        recordedTags.ShouldContain(t => t.Instrument == "sitecore.graphql.token.refresh.duration" && Equals(t.Tags.GetValueOrDefault("client.type"), clientAType));
        recordedTags.ShouldContain(t => t.Instrument == "sitecore.graphql.auth.retries" && Equals(t.Tags.GetValueOrDefault("client.type"), clientAType));
        recordedTags.ShouldContain(t =>
            t.Instrument == "sitecore.graphql.client_cache.evictions" &&
            Equals(t.Tags.GetValueOrDefault("client.type"), clientAType) &&
            !t.Tags.ContainsKey("client.name"));

        recordedTags.ShouldContain(t => t.Instrument == "sitecore.graphql.token.requests" && Equals(t.Tags.GetValueOrDefault("client.type"), genType));
        recordedTags.ShouldContain(t => t.Instrument == "sitecore.graphql.token.requests" && Equals(t.Tags.GetValueOrDefault("client.type"), arrType));

        activities.ShouldContain(a => a.OperationName == "SitecoreGraphQL.GetToken" && Equals(a.Tags.GetValueOrDefault("client.type"), clientAType));
        activities.ShouldContain(a => a.OperationName == "SitecoreGraphQL.RefreshToken" && Equals(a.Tags.GetValueOrDefault("client.type"), clientAType));
    }

    [Fact]
    public async Task Repeated_AddSitecoreGraphQL_and_ConfigureResilience_are_idempotent_without_stacking_handlers()
    {
        var tokenHandlerExecutions = 0;
        var services = new ServiceCollection();

        var firstBuilder = services.AddSitecoreGraphQL<ClientA>(o =>
        {
            o.Endpoint = "https://idempotent.sitecore.test/graphql";
            o.ClientId = "id-1";
            o.ClientSecret = "sec-1";
            o.AllowPostRetries = true;
        });

        var secondBuilder = services.AddSitecoreGraphQL<ClientA>(o =>
        {
            o.MaxUnauthorizedRetries = 2;
        });

        ReferenceEquals(firstBuilder, secondBuilder).ShouldBeTrue();

        var resilienceConfigCount = 0;
        firstBuilder.ConfigureResilience(r =>
        {
            resilienceConfigCount++;
            r.Retry.MaxRetryAttempts = 1;
            r.Retry.Delay = TimeSpan.FromMilliseconds(1);
            r.Retry.UseJitter = false;
        });

        await using var intermediateProvider = services.BuildServiceProvider();
        var typeName = typeof(ClientA).AssemblyQualifiedName ?? typeof(ClientA).FullName ?? typeof(ClientA).Name;
        var httpClientName = $"{SitecoreGraphQLFactory.NamedHttpClient}.{typeName}";
        var actionsAfterFirstResilience = intermediateProvider
            .GetRequiredService<IOptionsMonitor<Microsoft.Extensions.Http.HttpClientFactoryOptions>>()
            .Get(httpClientName)
            .HttpMessageHandlerBuilderActions.Count;

        firstBuilder
            .ConfigureResilience(r =>
            {
                resilienceConfigCount++;
                r.Retry.MaxRetryAttempts = 2;
            })
            .ConfigureHttpClient(b => b.ConfigurePrimaryHttpMessageHandler(() => CreateJsonOkHandler(_ =>
                Interlocked.Increment(ref tokenHandlerExecutions))));

        var tokenService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        tokenService
            .Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SitecoreAuthToken("tok-idem", DateTimeOffset.UtcNow.AddHours(1)));
        services.AddSingleton(tokenService.Object);

        await using var provider = services.BuildServiceProvider();
        var httpClientFactoryOptions = provider
            .GetRequiredService<IOptionsMonitor<Microsoft.Extensions.Http.HttpClientFactoryOptions>>()
            .Get(httpClientName);

        // Second ConfigureResilience reconfigures without adding duplicate resilience handlers;
        // primary handler adds 1 action after intermediateProvider snapshot.
        httpClientFactoryOptions.HttpMessageHandlerBuilderActions.Count.ShouldBe(actionsAfterFirstResilience + 1);

        var factory = provider.GetRequiredService<ISitecoreGraphQLFactory<ClientA>>();
        await SendTestQueryAsync(await factory.CreateClientAsync());

        tokenHandlerExecutions.ShouldBe(1);
        resilienceConfigCount.ShouldBe(2);
        tokenService.Verify(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Configuration_binding_hot_reload_logging_toggle_and_validation_prefix_work_as_specified()
    {
        var reloadSource = new ReloadableConfigurationSource();
        reloadSource.Provider.Update(new Dictionary<string, string?>
        {
            ["CustomSection:Endpoint"] = "https://cfg-v1.sitecore.test/graphql",
            ["CustomSection:ClientId"] = "cfg-id",
            ["CustomSection:ClientSecret"] = "cfg-secret",
            ["CustomSection:EnableInternalLoggingSetup"] = "false"
        });

        var cfg = new ConfigurationBuilder().Add(reloadSource).Build();
        var services = new ServiceCollection();

        var builder = services.AddSitecoreGraphQL<ClientA>(cfg, "CustomSection")
            .ConfigureOptions(o => o.MaxCachedClients = 7);

        // Verify EnableInternalLoggingSetup = false skipped TryAddInternalLogging() prior to IValidateOptions registration.
        var loggerFactoryIndex = -1;
        var validatorIndex = -1;
        for (var i = 0; i < services.Count; i++)
        {
            if (loggerFactoryIndex < 0 && services[i].ServiceType == typeof(ILoggerFactory))
            {
                loggerFactoryIndex = i;
            }
            if (validatorIndex < 0 &&
                services[i].ServiceType == typeof(IValidateOptions<SitecoreGraphQLOptions>) &&
                services[i].ImplementationType == typeof(SitecoreGraphQLOptionsValidator))
            {
                validatorIndex = i;
            }
        }

        validatorIndex.ShouldBeGreaterThanOrEqualTo(0);
        (loggerFactoryIndex < 0 || loggerFactoryIndex > validatorIndex).ShouldBeTrue();

        var seenUris = new List<string?>();
        builder.ConfigureHttpClient(b => b.ConfigurePrimaryHttpMessageHandler(() =>
            CreateJsonOkHandler(req => seenUris.Add(req.RequestUri?.ToString()))));

        var tokenService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        tokenService
            .Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SitecoreAuthToken("cfg-tok", DateTimeOffset.UtcNow.AddHours(1)));
        services.AddSingleton(tokenService.Object);

        await using var provider = services.BuildServiceProvider();
        var typeName = typeof(ClientA).AssemblyQualifiedName ?? typeof(ClientA).FullName ?? typeof(ClientA).Name;
        var optionsName = $"Sitecore.API.Foundation.GraphQL.Options.{typeName}";
        var monitor = provider.GetRequiredService<IOptionsMonitor<SitecoreGraphQLOptions>>();

        monitor.Get(optionsName).EnableInternalLoggingSetup.ShouldBeFalse();
        monitor.Get(optionsName).MaxCachedClients.ShouldBe(7);

        var factory = provider.GetRequiredService<ISitecoreGraphQLFactory<ClientA>>();
        await SendTestQueryAsync(await factory.CreateClientAsync());
        seenUris[^1].ShouldBe("https://cfg-v1.sitecore.test/graphql");

        // Trigger hot-reload and confirm delta-tracking preserves MaxCachedClients = 7 while updating Endpoint.
        reloadSource.Provider.Update(new Dictionary<string, string?>
        {
            ["CustomSection:Endpoint"] = "https://cfg-v2.sitecore.test/graphql",
            ["CustomSection:ClientId"] = "cfg-id",
            ["CustomSection:ClientSecret"] = "cfg-secret",
            ["CustomSection:EnableInternalLoggingSetup"] = "false"
        });

        monitor.Get(optionsName).MaxCachedClients.ShouldBe(7);
        await SendTestQueryAsync(await factory.CreateClientAsync());
        seenUris[^1].ShouldBe("https://cfg-v2.sitecore.test/graphql");

        // Validation error prefix check: typed options failures include [optionsName], untyped does not start with '['.
        var invalidTypedServices = new ServiceCollection();
        invalidTypedServices.AddSitecoreGraphQL<ClientA>(_ => { });
        await using var invalidTypedProvider = invalidTypedServices.BuildServiceProvider();
        var invalidTypedFactory = invalidTypedProvider.GetRequiredService<ISitecoreGraphQLFactory<ClientA>>();
        var typedEx = await Should.ThrowAsync<OptionsValidationException>(() => invalidTypedFactory.CreateClientAsync());
        typedEx.Failures.ShouldContain(f => f.StartsWith($"[{optionsName}] ", StringComparison.Ordinal));

        var invalidUntypedServices = new ServiceCollection();
        invalidUntypedServices.AddSitecoreGraphQL(_ => { });
        await using var invalidUntypedProvider = invalidUntypedServices.BuildServiceProvider();
        var invalidUntypedFactory = invalidUntypedProvider.GetRequiredService<ISitecoreGraphQLFactory>();
        var untypedEx = await Should.ThrowAsync<OptionsValidationException>(() => invalidUntypedFactory.CreateClientAsync());
        untypedEx.Failures.ShouldAllBe(f => !f.StartsWith("[", StringComparison.Ordinal));
    }

    [Fact]
    public void Argument_guards_validate_before_mutating_services_or_builders()
    {
        var cfg = new ConfigurationBuilder().Build();
        var emptyServices = new ServiceCollection();

        Should.Throw<ArgumentNullException>(() =>
            ServiceCollectionExtensions.AddSitecoreGraphQL<ClientA>(null!));
        Should.Throw<ArgumentNullException>(() =>
            ServiceCollectionExtensions.AddSitecoreGraphQL<ClientA>(null!, cfg));
        Should.Throw<ArgumentNullException>(() =>
            emptyServices.AddSitecoreGraphQL<ClientA>((IConfiguration)null!));
        Should.Throw<ArgumentException>(() =>
            emptyServices.AddSitecoreGraphQL<ClientA>(cfg, "  "));

        // Pre-mutation check: failed guards must not add descriptors.
        emptyServices.Count.ShouldBe(0);

        Should.Throw<ArgumentNullException>(() =>
            ServiceCollectionExtensions.ConfigureAuthentication<ClientA>(null!));
        Should.Throw<ArgumentNullException>(() =>
            ServiceCollectionExtensions.ConfigureAuthenticationSingleton<ClientA>(null!));

        var builder = new ServiceCollection().AddSitecoreGraphQL<ClientA>();
        Should.Throw<ArgumentNullException>(() => builder.ConfigureOptions((Action<SitecoreGraphQLOptions>)null!));
        Should.Throw<ArgumentNullException>(() => builder.ConfigureOptions((IConfiguration)null!));
        Should.Throw<ArgumentException>(() => builder.ConfigureOptions(cfg, " "));
        Should.Throw<ArgumentNullException>(() => builder.ConfigureHttpClient(null!));
    }

    [Fact]
    public async Task Untyped_and_typed_registrations_coexist_in_same_container()
    {
        string? untypedUri = null;
        string? untypedBearer = null;
        string? typedUri = null;
        string? typedBearer = null;

        var services = new ServiceCollection();

        services.AddSitecoreGraphQL(o =>
        {
            o.Endpoint = "https://shared-untyped.sitecore.test/graphql";
            o.ClientId = "untyped-id";
            o.ClientSecret = "untyped-sec";
        })
        .AddHttpClient(SitecoreGraphQLFactory.NamedHttpClient)
        .ConfigurePrimaryHttpMessageHandler(() => CreateJsonOkHandler(req =>
        {
            untypedUri = req.RequestUri?.ToString();
            untypedBearer = req.Headers.Authorization?.Parameter;
        }));

        services.AddSitecoreGraphQL<ClientA>(o =>
        {
            o.Endpoint = "https://isolated-typed.sitecore.test/graphql";
            o.ClientId = "typed-id";
            o.ClientSecret = "typed-sec";
        })
        .ConfigureHttpClient(b => b.ConfigurePrimaryHttpMessageHandler(() => CreateJsonOkHandler(req =>
        {
            typedUri = req.RequestUri?.ToString();
            typedBearer = req.Headers.Authorization?.Parameter;
        })));

        var tokenService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        tokenService
            .Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SitecoreAuthClientCredentials c, CancellationToken _) =>
                new SitecoreAuthToken($"tok-{c.ClientId}", DateTimeOffset.UtcNow.AddHours(1)));
        services.AddSingleton(tokenService.Object);

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        var untypedFactory = provider.GetRequiredService<ISitecoreGraphQLFactory>();
        var typedFactory = provider.GetRequiredService<ISitecoreGraphQLFactory<ClientA>>();

        await SendTestQueryAsync(await untypedFactory.CreateClientAsync());
        await SendTestQueryAsync(await typedFactory.CreateClientAsync());

        untypedUri.ShouldBe("https://shared-untyped.sitecore.test/graphql");
        untypedBearer.ShouldBe("tok-untyped-id");
        typedUri.ShouldBe("https://isolated-typed.sitecore.test/graphql");
        typedBearer.ShouldBe("tok-typed-id");
    }
}
