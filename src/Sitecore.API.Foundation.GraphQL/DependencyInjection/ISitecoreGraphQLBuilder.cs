using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace Sitecore.API.Foundation.GraphQL.DependencyInjection;

/// <summary>
/// Fluent builder for configuring an isolated <typeparamref name="TClient"/> Sitecore GraphQL registration.
/// </summary>
/// <typeparam name="TClient">The consumer class type associated with this typed GraphQL registration.</typeparam>
public interface ISitecoreGraphQLBuilder<TClient>
    where TClient : class
{
    /// <summary>
    /// Gets the underlying <see cref="IServiceCollection"/> where services are registered.
    /// </summary>
    IServiceCollection Services { get; }

    /// <summary>
    /// Configures named <see cref="SitecoreGraphQLOptions"/> for <typeparamref name="TClient"/> using a delegate.
    /// </summary>
    /// <param name="configure">The delegate that mutates <see cref="SitecoreGraphQLOptions"/>.</param>
    /// <returns>The same <see cref="ISitecoreGraphQLBuilder{TClient}"/> instance for chaining.</returns>
    /// <remarks>
    /// Setting <see cref="SitecoreGraphQLOptions.EnableInternalLoggingSetup"/> to <c>false</c> inside this delegate does
    /// not suppress internal logging registration; use <see cref="ServiceCollectionExtensions.AddSitecoreGraphQL{TClient}(IServiceCollection, IConfiguration, string)"/>
    /// when <c>EnableInternalLoggingSetup = false</c> is required.
    /// </remarks>
    ISitecoreGraphQLBuilder<TClient> ConfigureOptions(Action<SitecoreGraphQLOptions> configure);

    /// <summary>
    /// Binds named <see cref="SitecoreGraphQLOptions"/> for <typeparamref name="TClient"/> from configuration.
    /// </summary>
    /// <param name="configuration">The application configuration instance.</param>
    /// <param name="sectionName">The configuration section path to bind.</param>
    /// <returns>The same <see cref="ISitecoreGraphQLBuilder{TClient}"/> instance for chaining.</returns>
    /// <remarks>
    /// <see cref="SitecoreGraphQLOptions.EnableInternalLoggingSetup"/> is evaluated only during the initial
    /// <see cref="ServiceCollectionExtensions.AddSitecoreGraphQL{TClient}(IServiceCollection, IConfiguration, string)"/> call,
    /// not on chained <see cref="ConfigureOptions(IConfiguration, string)"/> or delegate entry points.
    /// </remarks>
    ISitecoreGraphQLBuilder<TClient> ConfigureOptions(
        IConfiguration configuration,
        string sectionName = ServiceCollectionExtensions.SectionName);

    /// <summary>
    /// Enables or reconfigures the standard HTTP resilience pipeline for <typeparamref name="TClient"/>'s GraphQL <see cref="HttpClient"/>.
    /// </summary>
    /// <param name="configureResilience">Optional delegate to customize <see cref="HttpStandardResilienceOptions"/>.</param>
    /// <returns>The same <see cref="ISitecoreGraphQLBuilder{TClient}"/> instance for chaining.</returns>
    /// <remarks>
    /// Repeated calls reconfigure the same resilience pipeline without stacking duplicate handlers. Call
    /// <see cref="ConfigureResilience"/> before adding inner delegating handlers via <see cref="ConfigureHttpClient"/>
    /// so resilience wraps outbound attempts at the intended position in the pipeline.
    /// </remarks>
    ISitecoreGraphQLBuilder<TClient> ConfigureResilience(
        Action<HttpStandardResilienceOptions>? configureResilience = null);

    /// <summary>
    /// Customizes the underlying <see cref="IHttpClientBuilder"/> for <typeparamref name="TClient"/>'s GraphQL <see cref="HttpClient"/>.
    /// </summary>
    /// <param name="configureClient">Delegate invoked with the named <see cref="IHttpClientBuilder"/>.</param>
    /// <returns>The same <see cref="ISitecoreGraphQLBuilder{TClient}"/> instance for chaining.</returns>
    /// <remarks>
    /// Call <see cref="ConfigureResilience"/> before adding inner delegating handlers via <see cref="ConfigureHttpClient"/>
    /// to preserve intended outer-to-inner handler ordering.
    /// </remarks>
    ISitecoreGraphQLBuilder<TClient> ConfigureHttpClient(Action<IHttpClientBuilder> configureClient);
}

internal sealed class SitecoreGraphQLBuilder<TClient> : ISitecoreGraphQLBuilder<TClient>
    where TClient : class
{
    private IHttpStandardResiliencePipelineBuilder? _resiliencePipelineBuilder;

    internal SitecoreGraphQLBuilder(
        IServiceCollection services,
        IHttpClientBuilder httpClientBuilder,
        string optionsName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(httpClientBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(optionsName);

        Services = services;
        HttpClientBuilder = httpClientBuilder;
        OptionsName = optionsName;
    }

    public IServiceCollection Services { get; }

    internal IHttpClientBuilder HttpClientBuilder { get; }

    internal string OptionsName { get; }

    public ISitecoreGraphQLBuilder<TClient> ConfigureOptions(Action<SitecoreGraphQLOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        ServiceCollectionExtensions.ConfigureOptionsWithDeltaTracking(
            Services.AddOptions<SitecoreGraphQLOptions>(OptionsName),
            configure);
        return this;
    }

    public ISitecoreGraphQLBuilder<TClient> ConfigureOptions(
        IConfiguration configuration,
        string sectionName = ServiceCollectionExtensions.SectionName)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);

        Services.Configure<SitecoreGraphQLOptions>(OptionsName, configuration.GetSection(sectionName));
        return this;
    }

    public ISitecoreGraphQLBuilder<TClient> ConfigureResilience(
        Action<HttpStandardResilienceOptions>? configureResilience = null)
    {
        _resiliencePipelineBuilder ??= ServiceCollectionExtensions.AddStandardResiliencePipeline(
            HttpClientBuilder,
            OptionsName);

        if (configureResilience is not null)
        {
            _resiliencePipelineBuilder.Configure(configureResilience);
        }

        return this;
    }

    public ISitecoreGraphQLBuilder<TClient> ConfigureHttpClient(Action<IHttpClientBuilder> configureClient)
    {
        ArgumentNullException.ThrowIfNull(configureClient);

        configureClient(HttpClientBuilder);
        return this;
    }
}
