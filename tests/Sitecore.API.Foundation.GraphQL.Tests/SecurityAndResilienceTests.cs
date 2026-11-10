using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using GraphQL;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Newtonsoft.Json.Linq;
using Polly.CircuitBreaker;
using Shouldly;
using Sitecore.API.Foundation.Authorization.Abstractions;
using Sitecore.API.Foundation.Authorization.DependencyInjection;
using Sitecore.API.Foundation.Authorization.Models;
using Sitecore.API.Foundation.GraphQL.DependencyInjection;
using Sitecore.API.Foundation.GraphQL.Http;
using Sitecore.API.Foundation.GraphQL.Internal;

namespace Sitecore.API.Foundation.GraphQL.Tests;

public class SecurityAndResilienceTests
{
    [Fact]
    public async Task Concurrent_MultiTenant_ExplicitAndNamedClients_IsolateCredentials_AndPreserveQueryStrings()
    {
        var observedRequests = new ConcurrentBag<(string? Uri, string? BearerToken)>();

        var services = new ServiceCollection();
        services.AddSitecoreGraphQL(o =>
        {
            o.Endpoint = "https://default.sitecore/graphql?sc_apikey=default-key";
            o.ClientId = "default-id";
            o.ClientSecret = "default-secret";
            o.Clients["content"] = new SitecoreGraphQLClientOptions
            {
                Endpoint = "https://content.sitecore/graphql?sc_apikey=content-key",
                ClientId = "content-id",
                ClientSecret = "content-secret"
            };
            o.Clients["search"] = new SitecoreGraphQLClientOptions
            {
                Endpoint = "https://search.sitecore/graphql?sc_apikey=search-key",
                ClientId = "search-id",
                ClientSecret = "search-secret"
            };
        });

        var tokenService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        tokenService
            .Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SitecoreAuthClientCredentials creds, CancellationToken _) =>
                new SitecoreAuthToken($"tok:{creds.ClientId}:{creds.ClientSecret}", DateTimeOffset.UtcNow.AddHours(1)));
        services.AddSingleton(tokenService.Object);

        services.AddHttpClient(SitecoreGraphQLFactory.NamedHttpClient)
            .ConfigurePrimaryHttpMessageHandler(() => new DelegateTransportHandler((req, _) =>
            {
                observedRequests.Add((req.RequestUri?.AbsoluteUri, req.Headers.Authorization?.Parameter));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":{\"ok\":true}}", Encoding.UTF8, "application/json")
                });
            }));

        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ISitecoreGraphQLFactory>();

        // Verify secret rotation on the same URL + ClientId produces distinct isolated clients
        var rotatedV1 = await factory.CreateClientAsync("https://explicit.sitecore/graphql?sc_apikey=exp", "exp-id", "secret-v1");
        var rotatedV2 = await factory.CreateClientAsync("https://explicit.sitecore/graphql?sc_apikey=exp", "exp-id", "secret-v2");
        ReferenceEquals(rotatedV1, rotatedV2).ShouldBeFalse();

        // Verify uppercase scheme/host, explicit default :443 port, and #fragment canonicalize to the exact same cached instance
        var nonCanonicalV1 = await factory.CreateClientAsync("HTTPS://EXPLICIT.SITECORE:443/graphql?sc_apikey=exp#ignored-fragment", "exp-id", "secret-v1");
        ReferenceEquals(rotatedV1, nonCanonicalV1).ShouldBeTrue();

        var gqlQuery = new GraphQLRequest { Query = "query { ok }" };

        var tasks = Enumerable.Range(0, 24).Select(async i =>
        {
            switch (i % 4)
            {
                case 0:
                {
                    var client = await factory.CreateClientByNameAsync("content");
                    await client.SendQueryAsync<JObject>(gqlQuery);
                    break;
                }
                case 1:
                {
                    var client = await factory.CreateClientByNameAsync("search");
                    await client.SendQueryAsync<JObject>(gqlQuery);
                    break;
                }
                case 2:
                    await rotatedV1.SendQueryAsync<JObject>(gqlQuery);
                    break;
                default:
                    await rotatedV2.SendQueryAsync<JObject>(gqlQuery);
                    break;
            }
        });

        await Task.WhenAll(tasks);

        observedRequests.Count.ShouldBe(24);
        foreach (var (uri, bearer) in observedRequests)
        {
            if (uri!.Contains("content.sitecore", StringComparison.Ordinal))
            {
                uri.ShouldContain("sc_apikey=content-key");
                bearer.ShouldBe("tok:content-id:content-secret");
            }
            else if (uri.Contains("search.sitecore", StringComparison.Ordinal))
            {
                uri.ShouldContain("sc_apikey=search-key");
                bearer.ShouldBe("tok:search-id:search-secret");
            }
            else
            {
                uri.ShouldContain("sc_apikey=exp");
                (bearer == "tok:exp-id:secret-v1" || bearer == "tok:exp-id:secret-v2").ShouldBeTrue();
            }
        }
    }

    [Fact]
    public async Task CachedGraphQLClient_DelegatesEveryRequestThroughHttpClientFactory_ForDnsHandlerRotation()
    {
        var createClientCalls = 0;
        using var backingClient = new HttpClient(new DelegateTransportHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":{\"ping\":\"pong\"}}", Encoding.UTF8, "application/json")
            })));

        var httpClientFactory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        httpClientFactory
            .Setup(f => f.CreateClient(SitecoreGraphQLFactory.NamedHttpClient))
            .Returns(() =>
            {
                Interlocked.Increment(ref createClientCalls);
                return backingClient;
            });

        var optionsMonitor = new StaticOptionsMonitor(new SitecoreGraphQLOptions
        {
            Endpoint = "https://edge.sitecorecloud.io/api/graphql/v1",
            ClientId = "id",
            ClientSecret = "secret"
        });
        var coordinator = new Mock<ISitecoreTokenCoordinator>(MockBehavior.Strict);
        using var factory = new SitecoreGraphQLFactory(coordinator.Object, optionsMonitor, httpClientFactory.Object);

        var c1 = await factory.CreateClientAsync();
        var c2 = await factory.CreateClientAsync();
        ReferenceEquals(c1, c2).ShouldBeTrue();

        var request = new GraphQLRequest { Query = "query { ping }" };
        await c1.SendQueryAsync<JObject>(request);
        await c2.SendQueryAsync<JObject>(request);
        await c1.SendQueryAsync<JObject>(request);

        createClientCalls.ShouldBe(3);
    }

    [Fact]
    public async Task Concurrent_401_Responses_CoalesceIntoSingleOAuthRefresh_OnSuccessAndFailure()
    {
        var initialToken = new SitecoreAuthToken("revoked-token", DateTimeOffset.UtcNow.AddMinutes(30));
        var refreshedToken = new SitecoreAuthToken("fresh-token", DateTimeOffset.UtcNow.AddMinutes(60));
        var refreshGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshCalls = 0;

        var tokenService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        tokenService
            .Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(initialToken);
        tokenService
            .Setup(s => s.TryRefreshSitecoreAuthToken(initialToken, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                Interlocked.Increment(ref refreshCalls);
                await refreshGate.Task;
                return refreshedToken;
            });

        var optionsMonitor = new StaticOptionsMonitor(new SitecoreGraphQLOptions
        {
            Endpoint = "https://unit.test/graphql",
            ClientId = "tenant-a",
            ClientSecret = "secret-a",
            EnableUnauthorizedRefresh = true,
            MaxUnauthorizedRetries = 1
        });
        var coordinator = new SitecoreTokenCoordinator(tokenService.Object, new DefaultTokenValueAccessor());

        var entered401Count = 0;
        var allEntered401 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var handler = new SitecoreTokenHandler(coordinator, optionsMonitor)
        {
            InnerHandler = new DelegateTransportHandler(async (req, ct) =>
            {
                var body = await req.Content!.ReadAsStringAsync(ct);
                body.ShouldContain("mutation CreateItem");

                if (req.Headers.Authorization?.Parameter == "revoked-token")
                {
                    if (Interlocked.Increment(ref entered401Count) == 20)
                    {
                        allEntered401.TrySetResult();
                    }

                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }

                req.Headers.Authorization?.Parameter.ShouldBe("fresh-token");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json")
                };
            })
        };
        using var httpClient = new HttpClient(handler);

        var burstTasks = Enumerable.Range(0, 20).Select(async i =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://unit.test/graphql")
            {
                Content = new StringContent($"{{\"query\":\"mutation CreateItem {{ id }}\",\"idx\":{i}}}", Encoding.UTF8, "application/json")
            };
            return await httpClient.SendAsync(req);
        }).ToArray();

        await allEntered401.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Give all 20 callers a moment to join the in-flight refresh before releasing the gate
        await Task.Delay(30);
        refreshGate.SetResult();

        var responses = await Task.WhenAll(burstTasks);
        foreach (var resp in responses)
        {
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);
            resp.Dispose();
        }

        refreshCalls.ShouldBe(1);

        // Now verify failure coalescing: if OAuth refresh throws, all concurrent callers observe the failure from 1 call and transient entry cleans up
        var failGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failCalls = 0;
        tokenService
            .Setup(s => s.TryRefreshSitecoreAuthToken(initialToken, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                Interlocked.Increment(ref failCalls);
                await failGate.Task;
                throw new HttpRequestException("OAuth endpoint unavailable");
            });

        using var failCts = new CancellationTokenSource();
        var creds = new SitecoreAuthClientCredentials("tenant-a", "secret-a");
        var failTasks = Enumerable.Range(0, 10)
            .Select(_ => coordinator.RefreshTokenAsync(creds, initialToken, "revoked-token", clientName: null, failCts.Token))
            .ToArray();

        await Task.Delay(25);
        failGate.SetResult();

        foreach (var t in failTasks)
        {
            await Should.ThrowAsync<HttpRequestException>(async () => await t);
        }

        failCalls.ShouldBe(1);
    }

    [Fact]
    public async Task LargePayload_ExceedingMaxRetryBufferBytes_SendsUnbufferedOn200Ok_AndSkipsReplayOn401()
    {
        var options = new SitecoreGraphQLOptions
        {
            Endpoint = "https://unit.test/graphql",
            ClientId = "id",
            ClientSecret = "secret",
            EnableUnauthorizedRefresh = true,
            MaxUnauthorizedRetries = 1,
            MaxRetryBufferBytes = 64 // tiny 64-byte cap for test
        };
        var optionsMonitor = new StaticOptionsMonitor(options);
        var tokenService = new Mock<ISitecoreTokenService>();
        tokenService
            .Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SitecoreAuthToken("token-1", DateTimeOffset.UtcNow.AddHours(1)));

        var coordinator = new SitecoreTokenCoordinator(tokenService.Object, new DefaultTokenValueAccessor());
        var logger = new CapturingLogger<SitecoreTokenHandler>();

        var nextStatus = HttpStatusCode.OK;
        var transportCalls = 0;
        using var handler = new SitecoreTokenHandler(coordinator, optionsMonitor, logger: logger)
        {
            InnerHandler = new DelegateTransportHandler(async (req, ct) =>
            {
                Interlocked.Increment(ref transportCalls);
                var body = await req.Content!.ReadAsStringAsync(ct);
                body.Length.ShouldBeGreaterThan(64);
                return new HttpResponseMessage(nextStatus);
            })
        };
        using var client = new HttpClient(handler);

        var largePayload = new string('x', 256);

        // 1) 200 OK with > MaxRetryBufferBytes dispatches unbuffered and succeeds
        using (var okReq = new HttpRequestMessage(HttpMethod.Post, "https://unit.test/graphql?sc_apikey=super-secret")
        {
            Content = new StringContent(largePayload, Encoding.UTF8, "application/json")
        })
        {
            using var okResp = await client.SendAsync(okReq);
            okResp.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        transportCalls.ShouldBe(1);

        // 2) 401 with > MaxRetryBufferBytes returns 401 cleanly without crashing and logs redacted URL warning
        nextStatus = HttpStatusCode.Unauthorized;
        using (var unauthReq = new HttpRequestMessage(HttpMethod.Post, "https://unit.test/graphql?sc_apikey=super-secret")
        {
            Content = new StringContent(largePayload, Encoding.UTF8, "application/json")
        })
        {
            using var unauthResp = await client.SendAsync(unauthReq);
            unauthResp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        transportCalls.ShouldBe(2);
        logger.Messages.ShouldContain(m => m.Contains("MaxRetryBufferBytes", StringComparison.Ordinal));
        logger.Messages.ShouldNotContain(m => m.Contains("super-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DependencyInjection_ValidateScopesAndValidateOnBuild_SucceedsForScopedSingletonAndAsyncDisposableTokenServices()
    {
        // 1) Default AddSitecoreAuthentication registers ISitecoreTokenService as Scoped
        var scopedServices = new ServiceCollection();
        scopedServices.AddSitecoreAuthentication(o => o.AuthTokenUrl = "https://auth.sitecorecloud.io/oauth/token");
        scopedServices.AddSitecoreGraphQL(o =>
        {
            o.Endpoint = "https://edge.sitecorecloud.io/api/graphql/v1";
            o.ClientId = "cid";
            o.ClientSecret = "csecret";
        });

        await using (var sp = scopedServices.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        }))
        {
            var factory = sp.GetRequiredService<ISitecoreGraphQLFactory>();
            var client = await factory.CreateClientAsync();
            client.ShouldNotBeNull();
        }

        // 2) AddSitecoreAuthenticationSingleton registers ISitecoreTokenService as Singleton
        var singletonServices = new ServiceCollection();
        singletonServices.AddSitecoreAuthenticationSingleton(o => o.AuthTokenUrl = "https://auth.sitecorecloud.io/oauth/token");
        singletonServices.AddSitecoreGraphQL(o =>
        {
            o.Endpoint = "https://edge.sitecorecloud.io/api/graphql/v1";
            o.ClientId = "cid";
            o.ClientSecret = "csecret";
        });

        await using (var sp = singletonServices.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        }))
        {
            var factory = sp.GetRequiredService<ISitecoreGraphQLFactory>();
            (await factory.CreateClientAsync()).ShouldNotBeNull();
        }

        // 3) Scoped IAsyncDisposable ISitecoreTokenService disposes cleanly via CreateAsyncScope
        var asyncDisposed = false;
        var asyncDisposableServices = new ServiceCollection();
        asyncDisposableServices.AddScoped<ISitecoreTokenService>(_ => new AsyncDisposableTokenService(() => asyncDisposed = true));
        asyncDisposableServices.AddSitecoreGraphQL(o =>
        {
            o.Endpoint = "https://edge.sitecorecloud.io/api/graphql/v1";
            o.ClientId = "cid";
            o.ClientSecret = "csecret";
        });

        await using (var sp = asyncDisposableServices.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        }))
        {
            var factory = sp.GetRequiredService<ISitecoreGraphQLFactory>();
            (await factory.RefreshTokenAsync()).ShouldBeTrue();
        }

        asyncDisposed.ShouldBeTrue();
    }

    [Fact]
    public void SecurityValidation_RejectsInsecureHttp_UserInfo_AndIncompleteNamedClients_AndMasksSecretsInToString()
    {
        var validator = new SitecoreGraphQLOptionsValidator();

        // Non-loopback http:// rejected by default
        validator.Validate(null, new SitecoreGraphQLOptions
        {
            Endpoint = "http://cm/sitecore/api/graph/edge",
            ClientId = "id",
            ClientSecret = "secret"
        }).Failed.ShouldBeTrue();

        // Loopback http:// accepted by default
        validator.Validate(null, new SitecoreGraphQLOptions
        {
            Endpoint = "http://localhost:8081/sitecore/api/graph/edge",
            ClientId = "id",
            ClientSecret = "secret"
        }).Succeeded.ShouldBeTrue();

        // Non-loopback http:// accepted when AllowInsecureHttp = true
        validator.Validate(null, new SitecoreGraphQLOptions
        {
            Endpoint = "http://cm/sitecore/api/graph/edge",
            ClientId = "id",
            ClientSecret = "secret",
            AllowInsecureHttp = true
        }).Succeeded.ShouldBeTrue();

        // Embedded UserInfo unconditionally rejected even when AllowInsecureHttp = true
        validator.Validate(null, new SitecoreGraphQLOptions
        {
            Endpoint = "https://admin:supersecret@cm/sitecore/api/graph/edge",
            ClientId = "id",
            ClientSecret = "secret",
            AllowInsecureHttp = true
        }).Failed.ShouldBeTrue();

        validator.Validate(null, new SitecoreGraphQLOptions
        {
            Endpoint = "https://@cm/sitecore/api/graph/edge",
            ClientId = "id",
            ClientSecret = "secret"
        }).Failed.ShouldBeTrue();

        // Second named client missing secret when no top-level default exists must fail validation
        var optionsWithIncompleteSecondNamedClient = new SitecoreGraphQLOptions();
        optionsWithIncompleteSecondNamedClient.Clients["valid"] = new SitecoreGraphQLClientOptions
        {
            Endpoint = "https://edge.sitecorecloud.io/api/graphql/v1",
            ClientId = "id-1",
            ClientSecret = "secret-1"
        };
        optionsWithIncompleteSecondNamedClient.Clients["incomplete"] = new SitecoreGraphQLClientOptions
        {
            Endpoint = "https://authoring.sitecorecloud.io/sitecore/api/authoring/graphql/v1",
            ClientId = "id-2"
            // Missing ClientSecret and no top-level default
        };
        validator.Validate(null, optionsWithIncompleteSecondNamedClient).Failed.ShouldBeTrue();

        // Verify secret masking and query-string redaction across all ToString() implementations
        var topOptions = new SitecoreGraphQLOptions
        {
            Endpoint = "https://edge.sitecorecloud.io/api/graphql/v1?sc_apikey=top-secret-apikey",
            ClientId = "my-client",
            ClientSecret = "ultra-secret-value"
        };
        topOptions.ToString().ShouldNotContain("ultra-secret-value");
        topOptions.ToString().ShouldNotContain("top-secret-apikey");
        topOptions.ToString().ShouldContain("***");

        new SitecoreGraphQLOptions { Clients = null! }.ToString().ShouldContain("(unset)");

        var namedOptions = new SitecoreGraphQLClientOptions
        {
            Endpoint = "https://edge.sitecorecloud.io/api/graphql/v1?sc_apikey=named-secret-apikey",
            ClientId = "named-client",
            ClientSecret = "named-ultra-secret",
            AllowInsecureHttp = false,
            AllowPostRetries = true
        };
        namedOptions.ToString().ShouldNotContain("named-ultra-secret");
        namedOptions.ToString().ShouldNotContain("named-secret-apikey");
        namedOptions.ToString().ShouldContain("***");
        new SitecoreGraphQLClientOptions().ToString().ShouldContain("(inherit)");

        var ctx = new CredentialContext(
            new SitecoreAuthClientCredentials("cid", "ctx-ultra-secret"),
            "delivery",
            EndpointUriValidator.SanitizeForLogging(topOptions.Endpoint),
            isExplicitClient: false,
            allowPostRetries: false);
        ctx.ToString().ShouldNotContain("ctx-ultra-secret");
        ctx.ToString().ShouldNotContain("top-secret-apikey");
        ctx.ToString().ShouldContain("***");

        var cacheKey = new ClientCacheKey(
            "https://edge.sitecorecloud.io/api/graphql/v1?sc_apikey=key-secret",
            "cid",
            "key-ultra-secret",
            "delivery",
            false);
        cacheKey.ToString().ShouldNotContain("key-ultra-secret");
        cacheKey.ToString().ShouldNotContain("key-secret");
        cacheKey.ToString().ShouldContain("***");
    }

    [Fact]
    public async Task OptInHttpResilience_DisablesPostRetriesByDefault_AllowsReadOnlyNamedClientOptIn_AndIsolatesCircuitBreakersPerClient()
    {
        var authoringCalls = 0;
        var deliveryCalls = 0;

        var services = new ServiceCollection();
        services.AddSitecoreGraphQL(
            o =>
            {
                o.EnableUnauthorizedRefresh = false;
                o.AllowPostRetries = false; // default: mutations do not retry POST
                o.Clients["authoring"] = new SitecoreGraphQLClientOptions
                {
                    Endpoint = "https://xmcloud.sitecore/sitecore/api/authoring/graphql/v1",
                    ClientId = "authoring-id",
                    ClientSecret = "authoring-secret"
                };
                o.Clients["delivery"] = new SitecoreGraphQLClientOptions
                {
                    Endpoint = "https://edge.sitecorecloud.io/api/graphql/v1",
                    ClientId = "delivery-id",
                    ClientSecret = "delivery-secret",
                    AllowPostRetries = true // opt-in for read-only delivery client
                };
            },
            configureResilience: resilience =>
            {
                resilience.Retry.MaxRetryAttempts = 2;
                resilience.Retry.Delay = TimeSpan.FromMilliseconds(1);
                resilience.Retry.UseJitter = false;
            });

        var tokenService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        tokenService
            .Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SitecoreAuthToken("resilience-token", DateTimeOffset.UtcNow.AddHours(1)));
        services.AddSingleton(tokenService.Object);

        services.AddHttpClient(SitecoreGraphQLFactory.NamedHttpClient)
            .ConfigurePrimaryHttpMessageHandler(() => new DelegateTransportHandler((req, _) =>
            {
                if (req.RequestUri!.Host.Contains("xmcloud", StringComparison.Ordinal))
                {
                    var call = Interlocked.Increment(ref authoringCalls);
                    return Task.FromResult(call == 1
                        ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                        : new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent("{\"data\":{\"ok\":true}}", Encoding.UTF8, "application/json")
                        });
                }

                var dCall = Interlocked.Increment(ref deliveryCalls);
                return Task.FromResult(dCall == 1
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"data\":{\"ok\":true}}", Encoding.UTF8, "application/json")
                    });
            }));

        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ISitecoreGraphQLFactory>();

        // Delivery client (AllowPostRetries = true) retries 503 on POST and succeeds on attempt 2
        var deliveryClient = await factory.CreateClientByNameAsync("delivery");
        var deliveryResult = await deliveryClient.SendQueryAsync<JObject>(new GraphQLRequest { Query = "query { ok }" });
        deliveryResult.Data["ok"]!.Value<bool>().ShouldBeTrue();
        deliveryCalls.ShouldBe(2);
        // Verify SitecoreTokenHandler was outermost (only 1 token acquisition across inner resilience retry!)
        tokenService.Verify(
            s => s.GetSitecoreAuthToken(
                It.Is<SitecoreAuthClientCredentials>(c => c.ClientId == "delivery-id"),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Authoring client (AllowPostRetries = false) does NOT retry POST on 503 (only 1 transport call)
        var authoringClient = await factory.CreateClientByNameAsync("authoring");
        await Should.ThrowAsync<Exception>(async () =>
            await authoringClient.SendMutationAsync<JObject>(new GraphQLRequest { Query = "mutation { ok }" }));
        authoringCalls.ShouldBe(1);
    }

    [Fact]
    public async Task OptInHttpResilience_CircuitBreaker_IsPartitionedPerTenantOnSharedAuthority()
    {
        var services = new ServiceCollection();
        services.AddSitecoreGraphQL(
            o =>
            {
                o.Endpoint = "https://edge.sitecorecloud.io/api/graphql/v1";
                o.ClientId = "default-id";
                o.ClientSecret = "default-secret";
                o.EnableUnauthorizedRefresh = false;
                o.MaxCachedClients = 4;
            },
            configureResilience: resilience =>
            {
                resilience.Retry.MaxRetryAttempts = 1;
                resilience.Retry.ShouldHandle = _ => ValueTask.FromResult(false); // isolate circuit breaker behavior
                resilience.CircuitBreaker.MinimumThroughput = 2;
                resilience.CircuitBreaker.FailureRatio = 1.0;
                resilience.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
                resilience.CircuitBreaker.BreakDuration = TimeSpan.FromMinutes(5);
            });

        var tokenService = new Mock<ISitecoreTokenService>();
        tokenService
            .Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SitecoreAuthClientCredentials c, CancellationToken _) =>
                new SitecoreAuthToken($"tok:{c.ClientId}", DateTimeOffset.UtcNow.AddHours(1)));
        services.AddSingleton(tokenService.Object);

        services.AddHttpClient(SitecoreGraphQLFactory.NamedHttpClient)
            .ConfigurePrimaryHttpMessageHandler(() => new DelegateTransportHandler((req, _) =>
            {
                // Tenant A gets 503; Tenant B gets 200 OK on the exact same authority!
                if (req.Headers.Authorization?.Parameter == "tok:tenant-a")
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":{\"healthy\":true}}", Encoding.UTF8, "application/json")
                });
            }));

        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ISitecoreGraphQLFactory>();

        var tenantA = await factory.CreateClientAsync("https://edge.sitecorecloud.io/api/graphql/v1", "tenant-a", "secret-a");
        var tenantB = await factory.CreateClientAsync("https://edge.sitecorecloud.io/api/graphql/v1", "tenant-b", "secret-b");

        // Trip Tenant A's circuit breaker with 2 consecutive 503 failures
        await Should.ThrowAsync<Exception>(async () => await tenantA.SendQueryAsync<JObject>(new GraphQLRequest { Query = "query { healthy }" }));
        await Should.ThrowAsync<Exception>(async () => await tenantA.SendQueryAsync<JObject>(new GraphQLRequest { Query = "query { healthy }" }));

        // 3rd call on Tenant A now fails fast with BrokenCircuitException
        await Should.ThrowAsync<BrokenCircuitException>(async () =>
            await tenantA.SendQueryAsync<JObject>(new GraphQLRequest { Query = "query { healthy }" }));

        // Tenant B on the SAME https://edge.sitecorecloud.io authority remains completely healthy!
        var tenantBResponse = await tenantB.SendQueryAsync<JObject>(new GraphQLRequest { Query = "query { healthy }" });
        tenantBResponse.Data["healthy"]!.Value<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task MaxCachedClients_EvictsOldestDynamicClientsFifo_PreservesConfiguredClients_AndSupportsDiagnosticsAndDispose()
    {
        var evictionMeasurements = 0L;
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == SitecoreGraphQLDiagnostics.InstrumentationName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "sitecore.graphql.client_cache.evictions")
            {
                Interlocked.Add(ref evictionMeasurements, measurement);
            }
        });
        meterListener.Start();

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == SitecoreGraphQLDiagnostics.InstrumentationName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(activityListener);

        var services = new ServiceCollection();
        services.AddSitecoreGraphQL(o =>
        {
            o.Endpoint = "https://default.sitecore/graphql";
            o.ClientId = "default-id";
            o.ClientSecret = "default-secret";
            o.MaxCachedClients = 2;
            o.Clients["pinned"] = new SitecoreGraphQLClientOptions
            {
                Endpoint = "https://pinned.sitecore/graphql",
                ClientId = "pinned-id",
                ClientSecret = "pinned-secret"
            };
            o.Clients["pinned-2"] = new SitecoreGraphQLClientOptions
            {
                Endpoint = "https://pinned2.sitecore/graphql",
                ClientId = "pinned2-id",
                ClientSecret = "pinned2-secret"
            };
        });

        var tokenService = new Mock<ISitecoreTokenService>();
        tokenService
            .Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SitecoreAuthToken("tok", DateTimeOffset.UtcNow.AddHours(1)));
        services.AddSingleton(tokenService.Object);

        services.AddHttpClient(SitecoreGraphQLFactory.NamedHttpClient)
            .ConfigurePrimaryHttpMessageHandler(() => new DelegateTransportHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":{\"ok\":true}}", Encoding.UTF8, "application/json")
                })));

        var provider = services.BuildServiceProvider();
        var factory = (SitecoreGraphQLFactory)provider.GetRequiredService<ISitecoreGraphQLFactory>();

        // Populate 3 configured clients (> MaxCachedClients = 2) and verify they do NOT cause dynamic clients to self-evict
        var defaultBefore = await factory.CreateClientAsync();
        var pinnedBefore = await factory.CreateClientByNameAsync("pinned");
        var pinned2Before = await factory.CreateClientByNameAsync("pinned-2");

        var dyn1Before = await factory.CreateClientAsync("https://dyn1.sitecore/graphql", "d1", "s1");
        var dyn1Hit = await factory.CreateClientAsync("https://dyn1.sitecore/graphql", "d1", "s1");
        ReferenceEquals(dyn1Before, dyn1Hit).ShouldBeTrue();

        var dyn2Before = await factory.CreateClientAsync("https://dyn2.sitecore/graphql", "d2", "s2");
        var dyn2Hit = await factory.CreateClientAsync("https://dyn2.sitecore/graphql", "d2", "s2");
        ReferenceEquals(dyn2Before, dyn2Hit).ShouldBeTrue();

        // Adding 3rd dynamic client exceeds MaxCachedClients (2), evicting oldest dynamic client dyn1 while preserving configured clients
        var dyn3 = await factory.CreateClientAsync("https://dyn3.sitecore/graphql", "d3", "s3");
        _ = dyn3;

        evictionMeasurements.ShouldBeGreaterThanOrEqualTo(1);

        // Configured default and named clients are preserved
        ReferenceEquals(defaultBefore, await factory.CreateClientAsync()).ShouldBeTrue();
        var pinnedAfter = await factory.CreateClientByNameAsync("pinned");
        ReferenceEquals(pinnedBefore, pinnedAfter).ShouldBeTrue();
        ReferenceEquals(pinned2Before, await factory.CreateClientByNameAsync("pinned-2")).ShouldBeTrue();

        // dyn2 is still cached, whereas dyn1 was evicted (re-creating dyn1 returns a new instance)
        var dyn2After = await factory.CreateClientAsync("https://dyn2.sitecore/graphql", "d2", "s2");
        ReferenceEquals(dyn2Before, dyn2After).ShouldBeTrue();

        // Evicted dyn1Before wrapper was NOT disposed mid-flight, so an in-flight query on dyn1Before still succeeds!
        var inFlightResult = await dyn1Before.SendQueryAsync<JObject>(new GraphQLRequest { Query = "query { ok }" });
        inFlightResult.Data["ok"]!.Value<bool>().ShouldBeTrue();

        var dyn1Recreated = await factory.CreateClientAsync("https://dyn1.sitecore/graphql", "d1", "s1");
        ReferenceEquals(dyn1Before, dyn1Recreated).ShouldBeFalse();

        // Disposing the factory disposes cached wrappers and rejects subsequent operations with ObjectDisposedException
        factory.Dispose();
        await Should.ThrowAsync<ObjectDisposedException>(async () => await factory.CreateClientAsync());
        await Should.ThrowAsync<ObjectDisposedException>(async () => await factory.RefreshTokenAsync());
    }

    [Fact]
    public async Task EdgeCases_RefreshCancellation_ArgumentExceptionFallback_ValidatorBounds_AndRefreshFailurePaths()
    {
        // 1) Validator numeric bounds & null named client key/value
        var validator = new SitecoreGraphQLOptionsValidator();
        var invalidBounds = new SitecoreGraphQLOptions
        {
            Endpoint = "https://edge.sitecorecloud.io/api/graphql/v1",
            ClientId = "id",
            ClientSecret = "secret",
            MaxUnauthorizedRetries = -1,
            MaxCachedClients = 0,
            MaxRetryBufferBytes = 0
        };
        var boundsResult = validator.Validate(null, invalidBounds);
        boundsResult.Failed.ShouldBeTrue();
        boundsResult.Failures.Count().ShouldBe(3);

        // 2) TryRefreshSitecoreAuthToken throwing ArgumentException falls back cleanly to GetSitecoreAuthToken
        var evictedToken = new SitecoreAuthToken("already-evicted", DateTimeOffset.UtcNow.AddMinutes(5));
        var fallbackToken = new SitecoreAuthToken("from-get-fallback", DateTimeOffset.UtcNow.AddHours(1));

        var fallbackService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        fallbackService
            .Setup(s => s.TryRefreshSitecoreAuthToken(evictedToken))
            .ThrowsAsync(new ArgumentException("Token not in cache"));
        fallbackService
            .Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>()))
            .ReturnsAsync(fallbackToken);

        var coordinator = new SitecoreTokenCoordinator(fallbackService.Object, new DefaultTokenValueAccessor());
        var (_, access) = await coordinator.RefreshTokenAsync(
            new SitecoreAuthClientCredentials("id", "secret"),
            evictedToken,
            "already-evicted",
            clientName: "content",
            CancellationToken.None);
        access.ShouldBe("from-get-fallback");

        // 3) Canceling all waiters of an in-flight refresh cancels the underlying refresh token
        var hangTcs = new TaskCompletionSource<SitecoreAuthToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelableService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        cancelableService
            .Setup(s => s.TryRefreshSitecoreAuthToken(evictedToken, It.IsAny<CancellationToken>()))
            .Returns(async (SitecoreAuthToken _, CancellationToken ct) => await hangTcs.Task.WaitAsync(ct));

        var cancelCoordinator = new SitecoreTokenCoordinator(cancelableService.Object, new DefaultTokenValueAccessor());
        using var waiterCts = new CancellationTokenSource();
        var refreshTask = cancelCoordinator.RefreshTokenAsync(
            new SitecoreAuthClientCredentials("id", "secret"),
            evictedToken,
            "already-evicted",
            clientName: null,
            waiterCts.Token);

        await Task.Delay(15);
        await waiterCts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(async () => await refreshTask);

        // 4) RefreshTokenAsync and RefreshTokenByNameAsync return false (instead of throwing) when OAuth service throws non-cancellation exceptions
        var failingService = new Mock<ISitecoreTokenService>();
        failingService
            .Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>()))
            .ThrowsAsync(new InvalidOperationException("OAuth failure"));

        var failingCoordinator = new SitecoreTokenCoordinator(failingService.Object, new DefaultTokenValueAccessor());
        var failingOpts = new SitecoreGraphQLOptions
        {
            Endpoint = "https://edge.sitecorecloud.io/api/graphql/v1",
            ClientId = "id",
            ClientSecret = "secret"
        };
        failingOpts.Clients["broken"] = new SitecoreGraphQLClientOptions
        {
            Endpoint = "https://edge.sitecorecloud.io/api/graphql/v1",
            ClientId = "broken-id",
            ClientSecret = "broken-secret"
        };
        using var failingFactory = new SitecoreGraphQLFactory(
            failingCoordinator,
            new StaticOptionsMonitor(failingOpts),
            Mock.Of<IHttpClientFactory>(),
            new CapturingLogger<SitecoreGraphQLFactory>());

        (await failingFactory.RefreshTokenAsync()).ShouldBeFalse();
        (await failingFactory.RefreshTokenByNameAsync("broken")).ShouldBeFalse();

        // 5) Default interface method on ISitecoreGraphQLFactory delegates RefreshTokenByNameAsync to RefreshTokenAsync
        ISitecoreGraphQLFactory customImpl = new MinimalCustomFactory();
        (await customImpl.RefreshTokenByNameAsync("any")).ShouldBeTrue();
    }

    [Fact]
    public async Task UnseekableStreamContent_WithNullContentLength_ReplaysWhenUnderLimit_AndStitchesIntactPayloadWhenOverLimit()
    {
        var initialToken = new SitecoreAuthToken("stale-stream-tok", DateTimeOffset.UtcNow.AddMinutes(30));
        var refreshedToken = new SitecoreAuthToken("fresh-stream-tok", DateTimeOffset.UtcNow.AddHours(1));

        var tokenService = new Mock<ISitecoreTokenService>();
        tokenService
            .Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(initialToken);
        tokenService
            .Setup(s => s.TryRefreshSitecoreAuthToken(initialToken, It.IsAny<CancellationToken>()))
            .ReturnsAsync(refreshedToken);

        var optionsMonitor = new StaticOptionsMonitor(new SitecoreGraphQLOptions
        {
            Endpoint = "https://unit.test/graphql",
            ClientId = "id",
            ClientSecret = "secret",
            EnableUnauthorizedRefresh = true,
            MaxUnauthorizedRetries = 1,
            MaxRetryBufferBytes = 64
        });
        var coordinator = new SitecoreTokenCoordinator(tokenService.Object, new DefaultTokenValueAccessor());
        var logger = new CapturingLogger<SitecoreTokenHandler>();

        var observedPayloads = new List<string>();
        var return401OnFirstAttempt = true;
        var attemptCounter = 0;

        using var handler = new SitecoreTokenHandler(coordinator, optionsMonitor, logger: logger)
        {
            InnerHandler = new DelegateTransportHandler(async (req, ct) =>
            {
                var body = await req.Content!.ReadAsStringAsync(ct);
                observedPayloads.Add(body);
                var currentAttempt = Interlocked.Increment(ref attemptCounter);
                if (return401OnFirstAttempt && currentAttempt == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }

                return new HttpResponseMessage(HttpStatusCode.OK);
            })
        };
        using var client = new HttpClient(handler);

        // 1) Non-seekable stream <= MaxRetryBufferBytes (42 bytes <= 64): buffers and replays cleanly across 401!
        const string smallPayload = "{\"query\":\"mutation { smallUnseekable }\"}";
        using (var smallReq = new HttpRequestMessage(HttpMethod.Post, "https://unit.test/graphql")
        {
            Content = new StreamContent(new NonSeekableReadStream(Encoding.UTF8.GetBytes(smallPayload)))
        })
        {
            smallReq.Content.Headers.ContentLength.ShouldBeNull();
            using var smallResp = await client.SendAsync(smallReq);
            smallResp.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        observedPayloads.ShouldBe([smallPayload, smallPayload]);

        // 2) Non-seekable stream > MaxRetryBufferBytes (300 bytes > 64) on 200 OK: stitches prefix + tail with 0 lost bytes!
        observedPayloads.Clear();
        return401OnFirstAttempt = false;
        attemptCounter = 0;
        var largePayload = "{\"query\":\"" + new string('Z', 280) + "\"}";
        using (var largeOkReq = new HttpRequestMessage(HttpMethod.Post, "https://unit.test/graphql")
        {
            Content = new StreamContent(new NonSeekableReadStream(Encoding.UTF8.GetBytes(largePayload)))
        })
        {
            largeOkReq.Content.Headers.ContentLength.ShouldBeNull();
            using var largeOkResp = await client.SendAsync(largeOkReq);
            largeOkResp.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        observedPayloads.ShouldBe([largePayload]);

        // 3) Non-seekable stream > MaxRetryBufferBytes (300 bytes > 64) on 401: sends full 300 bytes once, logs warning, returns 401 cleanly
        observedPayloads.Clear();
        return401OnFirstAttempt = true;
        attemptCounter = 0;
        using (var large401Req = new HttpRequestMessage(HttpMethod.Post, "https://unit.test/graphql")
        {
            Content = new StreamContent(new NonSeekableReadStream(Encoding.UTF8.GetBytes(largePayload)))
        })
        {
            using var large401Resp = await client.SendAsync(large401Req);
            large401Resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        observedPayloads.ShouldBe([largePayload]);
        logger.Messages.ShouldContain(m => m.Contains("MaxRetryBufferBytes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Coordinator_InFlightCancellation_NeverFailsSubsequentUncanceledCaller_AndPreservesTimeoutInnerException()
    {
        var staleToken = new SitecoreAuthToken("stale-tok", DateTimeOffset.UtcNow.AddMinutes(10));
        var freshToken = new SitecoreAuthToken("fresh-for-caller-2", DateTimeOffset.UtcNow.AddHours(1));
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowUnwind = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callIndex = 0;

        var tokenService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        tokenService
            .Setup(s => s.TryRefreshSitecoreAuthToken(staleToken, It.IsAny<CancellationToken>()))
            .Returns(async (SitecoreAuthToken _, CancellationToken ct) =>
            {
                var current = Interlocked.Increment(ref callIndex);
                if (current == 1)
                {
                    firstEntered.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        // Hold unwind open so Caller 2 arrives while Caller 1's canceled refresh is still unwinding!
                        await allowUnwind.Task;
                        throw;
                    }
                }

                return freshToken;
            });
        tokenService
            .Setup(s => s.TryRefreshSitecoreAuthToken(staleToken))
            .ReturnsAsync(freshToken);

        var coordinator = new SitecoreTokenCoordinator(tokenService.Object, new DefaultTokenValueAccessor());
        var creds = new SitecoreAuthClientCredentials("race-client", "race-secret");

        using var cts1 = new CancellationTokenSource();
        var caller1Task = coordinator.RefreshTokenAsync(creds, staleToken, "stale-tok", clientName: null, cts1.Token);

        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cts1.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(async () => await caller1Task);

        // Caller 2 arrives with CancellationToken.None BEFORE Caller 1's underlying task finishes unwinding!
        var caller2Task = coordinator.RefreshTokenAsync(creds, staleToken, "stale-tok", clientName: null, CancellationToken.None);
        var (_, caller2Access) = await caller2Task.WaitAsync(TimeSpan.FromSeconds(2));
        caller2Access.ShouldBe("fresh-for-caller-2");

        allowUnwind.TrySetResult();

        // Also verify an internal OAuth OperationCanceledException (not triggered by the caller's token) propagates cleanly to uncanceled waiters
        using var internalTimeoutCts = new CancellationTokenSource();
        await internalTimeoutCts.CancelAsync();
        var timeoutService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        timeoutService
            .Setup(s => s.TryRefreshSitecoreAuthToken(staleToken, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("OAuth endpoint timed out.", new TimeoutException("100s elapsed"), internalTimeoutCts.Token));

        var timeoutCoordinator = new SitecoreTokenCoordinator(timeoutService.Object, new DefaultTokenValueAccessor());
        using var uncanceledCts = new CancellationTokenSource();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await timeoutCoordinator.RefreshTokenAsync(creds, staleToken, "stale-tok", clientName: null, uncanceledCts.Token));
        uncanceledCts.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public async Task TokenHandler_Disposes401ResponsesOnFailureAndBackoffCancel_StripsStaleTraceHeaders_AndPreservesWinningRetryRequest()
    {
        var stale = new SitecoreAuthToken("stale-t", DateTimeOffset.UtcNow.AddMinutes(10));
        var fresh = new SitecoreAuthToken("fresh-t", DateTimeOffset.UtcNow.AddHours(1));

        // 1) Refresh failure disposes the initial 401 HttpResponseMessage
        var failingRefreshService = new Mock<ISitecoreTokenService>(MockBehavior.Strict);
        failingRefreshService
            .Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(stale);
        failingRefreshService
            .Setup(s => s.TryRefreshSitecoreAuthToken(stale, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("OAuth 500"));

        var trackedFailure401 = new TrackableResponseMessage(HttpStatusCode.Unauthorized);
        using (var failHandler = new SitecoreTokenHandler(
            new SitecoreTokenCoordinator(failingRefreshService.Object, new DefaultTokenValueAccessor()),
            new StaticOptionsMonitor(new SitecoreGraphQLOptions { ClientId = "id", ClientSecret = "secret" }))
        {
            InnerHandler = new DelegateTransportHandler((_, _) => Task.FromResult<HttpResponseMessage>(trackedFailure401))
        })
        {
            using var failClient = new HttpClient(failHandler);
            await Should.ThrowAsync<HttpRequestException>(async () => await failClient.GetAsync("https://unit.test/graphql"));
        }
        trackedFailure401.IsDisposed.ShouldBeTrue();

        // 2) Cancellation during retry backoff disposes ALL prior 401 HttpResponseMessage instances
        var backoffResponses = new ConcurrentQueue<TrackableResponseMessage>();
        var second401Sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fakeTime = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();

        var backoffService = new Mock<ISitecoreTokenService>();
        backoffService
            .Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(stale);
        backoffService
            .Setup(s => s.TryRefreshSitecoreAuthToken(It.IsAny<SitecoreAuthToken>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fresh);

        using (var backoffHandler = new SitecoreTokenHandler(
            new SitecoreTokenCoordinator(backoffService.Object, new DefaultTokenValueAccessor()),
            new StaticOptionsMonitor(new SitecoreGraphQLOptions
            {
                ClientId = "id",
                ClientSecret = "secret",
                MaxUnauthorizedRetries = 2
            }),
            fakeTime)
        {
            InnerHandler = new DelegateTransportHandler((_, _) =>
            {
                var r = new TrackableResponseMessage(HttpStatusCode.Unauthorized);
                backoffResponses.Enqueue(r);
                if (backoffResponses.Count == 2)
                {
                    second401Sent.TrySetResult();
                }

                return Task.FromResult<HttpResponseMessage>(r);
            })
        })
        {
            using var backoffClient = new HttpClient(backoffHandler) { Timeout = Timeout.InfiniteTimeSpan };
            using var backoffCts = new CancellationTokenSource();
            var sendTask = backoffClient.GetAsync("https://unit.test/graphql", backoffCts.Token);

            await second401Sent.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(15);
            await backoffCts.CancelAsync();

            await Should.ThrowAsync<OperationCanceledException>(async () => await sendTask);
        }
        backoffResponses.Count.ShouldBe(2);
        backoffResponses.ShouldAllBe(r => r.IsDisposed);

        // 3) Stale per-attempt traceparent/tracestate injected during attempt 1 are stripped on retry clone,
        // and winning retryRequest.Content remains readable on returned response.RequestMessage
        var attempt = 0;
        var retryHadStaleTraceParent = true;
        var retryHadStaleTraceState = true;

        var okRetryService = new Mock<ISitecoreTokenService>();
        okRetryService
            .Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(stale);
        okRetryService
            .Setup(s => s.TryRefreshSitecoreAuthToken(stale, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fresh);

        using var traceHandler = new SitecoreTokenHandler(
            new SitecoreTokenCoordinator(okRetryService.Object, new DefaultTokenValueAccessor()),
            new StaticOptionsMonitor(new SitecoreGraphQLOptions { ClientId = "id", ClientSecret = "secret" }))
        {
            InnerHandler = new DelegateTransportHandler((req, _) =>
            {
                var current = Interlocked.Increment(ref attempt);
                if (current == 1)
                {
                    // Simulate DiagnosticsHandler stamping attempt-1 W3C trace headers onto the initial request
                    req.Headers.TryAddWithoutValidation("traceparent", "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
                    req.Headers.TryAddWithoutValidation("tracestate", "vendor=attempt1");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                }

                retryHadStaleTraceParent = req.Headers.Contains("traceparent");
                retryHadStaleTraceState = req.Headers.Contains("tracestate");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = req,
                    Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json")
                });
            })
        };

        using var traceClient = new HttpClient(traceHandler);
        using var postReq = new HttpRequestMessage(HttpMethod.Post, "https://unit.test/graphql")
        {
            Content = new StringContent("{\"query\":\"{ ok }\"}", Encoding.UTF8, "application/json")
        };
        using var finalResp = await traceClient.SendAsync(postReq);

        finalResp.StatusCode.ShouldBe(HttpStatusCode.OK);
        retryHadStaleTraceParent.ShouldBeFalse();
        retryHadStaleTraceState.ShouldBeFalse();
        (await finalResp.RequestMessage!.Content!.ReadAsStringAsync()).ShouldBe("{\"query\":\"{ ok }\"}");
    }

    [Fact]
    public async Task Staggered401WithRealAuthorizationCache_ShortCircuitsRedundantRefresh_AndEmitsExplicitClientTelemetryTag()
    {
        var observedClientTags = new ConcurrentBag<string>();
        var refreshMetricCount = 0L;

        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == SitecoreGraphQLDiagnostics.InstrumentationName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "client.name" && tag.Value is string s)
                {
                    observedClientTags.Add(s);
                    if (instrument.Name == "sitecore.graphql.token.refreshes" && s == "explicit")
                    {
                        Interlocked.Add(ref refreshMetricCount, measurement);
                    }
                }
            }
        });
        meterListener.Start();

        var oauthCalls = 0;
        var services = new ServiceCollection();
        services.AddSitecoreAuthentication(
            configureOptions: o => o.AuthTokenUrl = "https://auth.sitecorecloud.io/oauth/token",
            configureClient: b => b.ConfigurePrimaryHttpMessageHandler(() => new DelegateTransportHandler((_, _) =>
            {
                var n = Interlocked.Increment(ref oauthCalls);
                var json = $"{{\"access_token\":\"oauth-tok-{n}\",\"expires_in\":3600}}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            })));

        services.AddSitecoreGraphQL(o =>
        {
            o.Endpoint = "https://edge.sitecorecloud.io/api/graphql/v1";
            o.ClientId = "default-id";
            o.ClientSecret = "default-secret";
        });

        var rejectFirstTwoWithFirstToken = 0;
        services.AddHttpClient(SitecoreGraphQLFactory.NamedHttpClient)
            .ConfigurePrimaryHttpMessageHandler(() => new DelegateTransportHandler((req, _) =>
            {
                if (req.Headers.Authorization?.Parameter == "oauth-tok-1" &&
                    Interlocked.Increment(ref rejectFirstTwoWithFirstToken) <= 2)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":{\"ok\":true}}", Encoding.UTF8, "application/json")
                });
            }));

        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ISitecoreGraphQLFactory>();
        var coordinator = provider.GetRequiredService<ISitecoreTokenCoordinator>();

        // 1) Explicit client query acquires oauth-tok-1, hits 401, refreshes to oauth-tok-2, and emits client.name = "explicit"
        var explicitCreds = new SitecoreAuthClientCredentials("exp-tenant-unique", "exp-secret-unique");
        var explicitClient = await factory.CreateClientAsync(
            "https://edge.sitecorecloud.io/api/graphql/v1",
            explicitCreds.ClientId,
            explicitCreds.ClientSecret);

        var res = await explicitClient.SendQueryAsync<JObject>(new GraphQLRequest { Query = "query { ok }" });
        res.Data["ok"]!.Value<bool>().ShouldBeTrue();
        observedClientTags.ShouldContain("explicit");
        oauthCalls.ShouldBe(2); // 1 initial + 1 refresh
        refreshMetricCount.ShouldBe(1);

        // 2) Staggered 401 arriving 2ms later still carrying stale oauth-tok-1 sees oauth-tok-2 in Authorization's ISitecoreTokenCache
        // and returns oauth-tok-2 directly without another OAuth call or refresh metric increment!
        var staleAuthToken = new SitecoreAuthToken("oauth-tok-1", DateTimeOffset.UtcNow.AddMinutes(30));
        var (_, staggeredAccess) = await coordinator.RefreshTokenAsync(
            explicitCreds,
            staleAuthToken,
            "oauth-tok-1",
            clientName: null,
            CancellationToken.None,
            isExplicitClient: true);

        staggeredAccess.ShouldBe("oauth-tok-2");
        oauthCalls.ShouldBe(2);
        refreshMetricCount.ShouldBe(1);
    }

    [Fact]
    public async Task ConcurrentOptionsMonitorHotReload_AndDynamicResiliencePartitionCap_RemainThreadSafeUnderContention()
    {
        // 1) Concurrent IOptionsMonitor hot-reload while CreateClientByNameAsync, SendQueryAsync, and RefreshTokenAsync execute
        var reloadableMonitor = new ReloadableOptionsMonitor(CreateHotReloadSnapshot(version: 0));
        var tokenService = new Mock<ISitecoreTokenService>();
        tokenService
            .Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SitecoreAuthClientCredentials c, CancellationToken _) =>
                new SitecoreAuthToken($"tok:{c.ClientId}:{c.ClientSecret}", DateTimeOffset.UtcNow.AddHours(1)));
        tokenService
            .Setup(s => s.GetSitecoreAuthToken(It.IsAny<SitecoreAuthClientCredentials>()))
            .ReturnsAsync((SitecoreAuthClientCredentials c) =>
                new SitecoreAuthToken($"tok:{c.ClientId}:{c.ClientSecret}", DateTimeOffset.UtcNow.AddHours(1)));
        tokenService
            .Setup(s => s.TryRefreshSitecoreAuthToken(It.IsAny<SitecoreAuthToken>()))
            .ReturnsAsync((SitecoreAuthToken t) =>
                new SitecoreAuthToken($"refreshed:{t.AccessToken}", DateTimeOffset.UtcNow.AddHours(1)));
        tokenService
            .Setup(s => s.TryRefreshSitecoreAuthToken(It.IsAny<SitecoreAuthToken>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SitecoreAuthToken t, CancellationToken _) =>
                new SitecoreAuthToken($"refreshed:{t.AccessToken}", DateTimeOffset.UtcNow.AddHours(1)));

        var coordinator = new SitecoreTokenCoordinator(tokenService.Object, new DefaultTokenValueAccessor());
        using var handler = new SitecoreTokenHandler(coordinator, reloadableMonitor)
        {
            InnerHandler = new DelegateTransportHandler((req, _) =>
            {
                req.Headers.Authorization?.Parameter.ShouldNotBeNullOrWhiteSpace();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":{\"ok\":true}}", Encoding.UTF8, "application/json")
                });
            })
        };
        using var backingHttpClient = new HttpClient(handler);
        var clientFactoryMock = new Mock<IHttpClientFactory>();
        clientFactoryMock.Setup(f => f.CreateClient(SitecoreGraphQLFactory.NamedHttpClient)).Returns(backingHttpClient);

        using var factory = new SitecoreGraphQLFactory(coordinator, reloadableMonitor, clientFactoryMock.Object);

        var reloadWriter = Task.Run(async () =>
        {
            for (var v = 1; v <= 20; v++)
            {
                reloadableMonitor.Update(CreateHotReloadSnapshot(v));
                await Task.Yield();
            }
        });

        var readers = Enumerable.Range(0, 16).Select(async i =>
        {
            for (var iter = 0; iter < 5; iter++)
            {
                if (i % 3 == 0)
                {
                    (await factory.RefreshTokenAsync()).ShouldBeTrue();
                }
                else
                {
                    var named = await factory.CreateClientByNameAsync("delivery");
                    var resp = await named.SendQueryAsync<JObject>(new GraphQLRequest { Query = "query { ok }" });
                    resp.Data["ok"]!.Value<bool>().ShouldBeTrue();
                }
            }
        });

        await Task.WhenAll(readers.Append(reloadWriter));

        // 2) Dynamic explicit-client resilience partition cap (> MaxCachedClients routes overflow tenants to explicit:shared without affecting registered tenants)
        var capServices = new ServiceCollection();
        capServices.AddSitecoreGraphQL(
            o =>
            {
                o.Endpoint = "https://edge.sitecorecloud.io/api/graphql/v1";
                o.ClientId = "default-id";
                o.ClientSecret = "default-secret";
                o.EnableUnauthorizedRefresh = false;
                o.MaxCachedClients = 2;
            },
            configureResilience: r =>
            {
                r.Retry.ShouldHandle = _ => ValueTask.FromResult(false);
                r.CircuitBreaker.MinimumThroughput = 2;
                r.CircuitBreaker.FailureRatio = 1.0;
                r.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
                r.CircuitBreaker.BreakDuration = TimeSpan.FromMinutes(5);
            });

        capServices.AddSingleton(tokenService.Object);
        capServices.AddHttpClient(SitecoreGraphQLFactory.NamedHttpClient)
            .ConfigurePrimaryHttpMessageHandler(() => new DelegateTransportHandler((req, _) =>
            {
                var bearer = req.Headers.Authorization?.Parameter ?? string.Empty;
                if (bearer.Contains("overflow-", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":{\"ok\":true}}", Encoding.UTF8, "application/json")
                });
            }));

        await using var capProvider = capServices.BuildServiceProvider();
        var capFactory = capProvider.GetRequiredService<ISitecoreGraphQLFactory>();

        // Register 2 healthy dynamic tenants filling MaxCachedClients = 2 partition slots
        var slot1 = await capFactory.CreateClientAsync("https://edge.sitecorecloud.io/api/graphql/v1", "slot-1", "sec-1");
        var slot2 = await capFactory.CreateClientAsync("https://edge.sitecorecloud.io/api/graphql/v1", "slot-2", "sec-2");
        (await slot1.SendQueryAsync<JObject>(new GraphQLRequest { Query = "query { ok }" })).Data["ok"]!.Value<bool>().ShouldBeTrue();
        (await slot2.SendQueryAsync<JObject>(new GraphQLRequest { Query = "query { ok }" })).Data["ok"]!.Value<bool>().ShouldBeTrue();

        // Overflow tenants 3 & 4 share "{authority}|explicit:shared"; two 503s across overflow-3 and overflow-4 trip explicit:shared!
        var overflow3 = await capFactory.CreateClientAsync("https://edge.sitecorecloud.io/api/graphql/v1", "overflow-3", "sec-3");
        var overflow4 = await capFactory.CreateClientAsync("https://edge.sitecorecloud.io/api/graphql/v1", "overflow-4", "sec-4");
        var overflow5 = await capFactory.CreateClientAsync("https://edge.sitecorecloud.io/api/graphql/v1", "overflow-5", "sec-5");

        await Should.ThrowAsync<Exception>(async () => await overflow3.SendQueryAsync<JObject>(new GraphQLRequest { Query = "query { ok }" }));
        await Should.ThrowAsync<Exception>(async () => await overflow4.SendQueryAsync<JObject>(new GraphQLRequest { Query = "query { ok }" }));
        await Should.ThrowAsync<BrokenCircuitException>(async () =>
            await overflow5.SendQueryAsync<JObject>(new GraphQLRequest { Query = "query { ok }" }));

        // Meanwhile slot-1 and slot-2 remain completely healthy on their dedicated partitions!
        (await slot1.SendQueryAsync<JObject>(new GraphQLRequest { Query = "query { ok }" })).Data["ok"]!.Value<bool>().ShouldBeTrue();
        (await slot2.SendQueryAsync<JObject>(new GraphQLRequest { Query = "query { ok }" })).Data["ok"]!.Value<bool>().ShouldBeTrue();
    }

    [Theory]
    [InlineData("https://999.999.999.999/graphql")]
    [InlineData("https://256.0.0.1/graphql")]
    [InlineData("https://-leadinghyphen.io/graphql")]
    [InlineData("https://trailinghyphen-.io/graphql")]
    [InlineData("https://a.-badlabel.io/graphql")]
    public async Task InvalidIpv4AndMalformedDnsHosts_AreRejectedByValidatorAndFactory_WithoutUriFormatException(string malformedUrl)
    {
        var validator = new SitecoreGraphQLOptionsValidator();
        validator.Validate(null, new SitecoreGraphQLOptions
        {
            Endpoint = malformedUrl,
            ClientId = "id",
            ClientSecret = "secret"
        }).Failed.ShouldBeTrue();

        var overlongLabelUrl = $"https://{new string('a', 64)}.sitecorecloud.io/graphql";
        validator.Validate(null, new SitecoreGraphQLOptions
        {
            Endpoint = overlongLabelUrl,
            ClientId = "id",
            ClientSecret = "secret"
        }).Failed.ShouldBeTrue();

        var coordinator = new Mock<ISitecoreTokenCoordinator>(MockBehavior.Strict);
        var optionsMonitor = new StaticOptionsMonitor(new SitecoreGraphQLOptions
        {
            Endpoint = "https://edge.sitecorecloud.io/api/graphql/v1",
            ClientId = "id",
            ClientSecret = "secret"
        });
        using var factory = new SitecoreGraphQLFactory(coordinator.Object, optionsMonitor, Mock.Of<IHttpClientFactory>());

        var ex1 = await Should.ThrowAsync<ArgumentException>(async () =>
            await factory.CreateClientAsync(malformedUrl, "id", "secret"));
        ex1.ShouldNotBeOfType<UriFormatException>();

        var ex2 = await Should.ThrowAsync<ArgumentException>(async () =>
            await factory.CreateClientAsync(overlongLabelUrl, "id", "secret"));
        ex2.ShouldNotBeOfType<UriFormatException>();
    }

    private static SitecoreGraphQLOptions CreateHotReloadSnapshot(int version)
    {
        var opts = new SitecoreGraphQLOptions
        {
            Endpoint = "https://default.sitecore/graphql",
            ClientId = "default-id",
            ClientSecret = $"default-secret-v{version}"
        };
        opts.Clients["delivery"] = new SitecoreGraphQLClientOptions
        {
            Endpoint = $"https://edge.sitecorecloud.io/api/graphql/v1?ver={version}",
            ClientId = "delivery-id",
            ClientSecret = $"delivery-secret-v{version}",
            AllowPostRetries = (version % 2) == 0
        };
        return opts;
    }

    private sealed class MinimalCustomFactory : ISitecoreGraphQLFactory
    {
        public Task<global::GraphQL.Client.Abstractions.IGraphQLClient> CreateClientAsync(string url, string clientId, string clientSecret, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<global::GraphQL.Client.Abstractions.IGraphQLClient> CreateClientAsync(string url, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<global::GraphQL.Client.Abstractions.IGraphQLClient> CreateClientAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<global::GraphQL.Client.Abstractions.IGraphQLClient> CreateClientByNameAsync(string clientName, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> RefreshTokenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    private sealed class DelegateTransportHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public DelegateTransportHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
            => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _handler(request, cancellationToken);
    }

    private sealed class StaticOptionsMonitor : IOptionsMonitor<SitecoreGraphQLOptions>
    {
        public StaticOptionsMonitor(SitecoreGraphQLOptions value) => CurrentValue = value;

        public SitecoreGraphQLOptions CurrentValue { get; }

        public SitecoreGraphQLOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<SitecoreGraphQLOptions, string?> listener) => null;
    }

    private sealed class ReloadableOptionsMonitor : IOptionsMonitor<SitecoreGraphQLOptions>
    {
        private volatile SitecoreGraphQLOptions _current;

        public ReloadableOptionsMonitor(SitecoreGraphQLOptions initial) => _current = initial;

        public SitecoreGraphQLOptions CurrentValue => _current;

        public SitecoreGraphQLOptions Get(string? name) => _current;

        public void Update(SitecoreGraphQLOptions next) => _current = next;

        public IDisposable? OnChange(Action<SitecoreGraphQLOptions, string?> listener) => null;
    }

    private sealed class TrackableResponseMessage : HttpResponseMessage
    {
        public TrackableResponseMessage(HttpStatusCode statusCode) : base(statusCode)
        {
        }

        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                IsDisposed = true;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class NonSeekableReadStream : Stream
    {
        private readonly byte[] _data;
        private int _offset;

        public NonSeekableReadStream(byte[] data) => _data = data;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var toCopy = Math.Min(count, _data.Length - _offset);
            if (toCopy <= 0)
            {
                return 0;
            }

            Buffer.BlockCopy(_data, _offset, buffer, offset, toCopy);
            _offset += toCopy;
            return toCopy;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class AsyncDisposableTokenService : ISitecoreTokenService, IAsyncDisposable
    {
        private readonly Action _onDispose;

        public AsyncDisposableTokenService(Action onDispose) => _onDispose = onDispose;

        public Task<SitecoreAuthToken> GetSitecoreAuthToken(SitecoreAuthClientCredentials credentials)
            => Task.FromResult(new SitecoreAuthToken("async-scoped-token", DateTimeOffset.UtcNow.AddHours(1)));

        public Task<SitecoreAuthToken> GetSitecoreAuthToken(SitecoreAuthClientCredentials credentials, CancellationToken cancellationToken)
            => Task.FromResult(new SitecoreAuthToken("async-scoped-token", DateTimeOffset.UtcNow.AddHours(1)));

        public Task<SitecoreAuthToken> TryRefreshSitecoreAuthToken(SitecoreAuthToken token)
            => Task.FromResult(new SitecoreAuthToken("async-scoped-refreshed", DateTimeOffset.UtcNow.AddHours(1)));

        public Task<SitecoreAuthToken> TryRefreshSitecoreAuthToken(SitecoreAuthToken token, CancellationToken cancellationToken)
            => Task.FromResult(new SitecoreAuthToken("async-scoped-refreshed", DateTimeOffset.UtcNow.AddHours(1)));

        public ValueTask DisposeAsync()
        {
            _onDispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Enqueue(formatter(state, exception));
        }
    }
}
