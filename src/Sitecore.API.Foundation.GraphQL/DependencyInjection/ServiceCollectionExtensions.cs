using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sitecore.API.Foundation.GraphQL.Http;
using Sitecore.API.Foundation.GraphQL.Internal;
using System.Linq;

namespace Sitecore.API.Foundation.GraphQL.DependencyInjection;

/// <summary>
/// Dependency injection extensions for configuring the Sitecore GraphQL client factory and supporting services.
/// </summary>
public static class ServiceCollectionExtensions
{
    private const string SectionName = "Sitecore:GraphQL";

    internal static void TryAddInternalLogging(this IServiceCollection services)
    {
        services.TryAddSingleton<ILoggerFactory, LoggerFactory>();
        services.TryAdd(ServiceDescriptor.Singleton(typeof(ILogger<>), typeof(Logger<>)));
    }

    /// <summary>
    /// Registers the Sitecore GraphQL client factory, HTTP pipeline, and options from configuration.
    /// </summary>
    public static IServiceCollection AddSitecoreGraphQL(this IServiceCollection services, IConfiguration configuration)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));

        // Configure options via configuration and add core services
        ConfigureOptions(services, optsBuilder => optsBuilder.Bind(configuration.GetSection(SectionName)));

        // Decide logging based on configuration toggle (defaults to true if not set)
        var tmpOptions = new SitecoreGraphQLOptions();
        configuration.GetSection(SectionName).Bind(tmpOptions);
        var enableLogging = tmpOptions.EnableInternalLoggingSetup;

        return AddCore(services, enableLogging);
    }

    /// <summary>
    /// Registers the Sitecore GraphQL client factory, HTTP pipeline, and options using an optional configuration delegate.
    /// This overload does not require an IConfiguration instance.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration delegate for SitecoreGraphQLOptions.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddSitecoreGraphQL(this IServiceCollection services, Action<SitecoreGraphQLOptions>? configure)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));

        // Pre-evaluate the logging toggle using a temporary options instance
        var tmp = new SitecoreGraphQLOptions();
        configure?.Invoke(tmp);
        var enableLogging = tmp.EnableInternalLoggingSetup;

        // Register options into DI with the provided delegate and validations
        ConfigureOptions(services, optsBuilder =>
        {
            if (configure is not null)
            {
                return optsBuilder.Configure(configure);
            }
            return optsBuilder;
        });

        return AddCore(services, enableLogging);
    }

    private static void ConfigureOptions(IServiceCollection services, Func<OptionsBuilder<SitecoreGraphQLOptions>, OptionsBuilder<SitecoreGraphQLOptions>> configure)
    {
        var builder = services.AddOptions<SitecoreGraphQLOptions>();
        builder = configure(builder);
        builder
            // Either defaults or at least one fully configured named client
            .Validate(o =>
            {
                bool HasDefaults() =>
                    !string.IsNullOrWhiteSpace(o.Endpoint) &&
                    !string.IsNullOrWhiteSpace(o.ClientId) &&
                    !string.IsNullOrWhiteSpace(o.ClientSecret);

                bool HasValidNamed() =>
                    o.Clients != null && o.Clients.Values.Any(c =>
                        !string.IsNullOrWhiteSpace(c.Endpoint) &&
                        !string.IsNullOrWhiteSpace(c.ClientId) &&
                        !string.IsNullOrWhiteSpace(c.ClientSecret));

                return HasDefaults() || HasValidNamed();
            }, "Either default Endpoint/ClientId/ClientSecret or at least one named client with Endpoint/ClientId/ClientSecret must be configured.")
            // Default endpoint format if provided
            .Validate(o => string.IsNullOrWhiteSpace(o.Endpoint) || IsValidHttpUrl(o.Endpoint),
                      "Default Endpoint must be a valid http/https URL.")
            // Every named client must be fully specified with valid endpoint
            .Validate(o => o.Clients == null || o.Clients.Count == 0 || o.Clients.Values.All(c =>
                          !string.IsNullOrWhiteSpace(c.Endpoint) && IsValidHttpUrl(c.Endpoint!) &&
                          !string.IsNullOrWhiteSpace(c.ClientId) &&
                          !string.IsNullOrWhiteSpace(c.ClientSecret)),
                      "All named clients must define Endpoint (valid http/https), ClientId, and ClientSecret.")
            .Validate(o => o.MaxUnauthorizedRetries >= 0, "MaxUnauthorizedRetries must be >= 0.")
            .ValidateOnStart();
    }

    private static IServiceCollection AddCore(IServiceCollection services, bool enableLogging)
    {
        if (enableLogging)
        {
            services.TryAddInternalLogging();
        }

        services.AddSingleton<ITokenValueAccessor, DefaultTokenValueAccessor>();
        services.AddSingleton<ISitecoreTokenCache, SitecoreTokenCache>();

        services.AddHttpClient(SitecoreGraphQLFactory.NamedHttpClient)
                .AddHttpMessageHandler<SitecoreTokenHandler>();

        services.AddTransient<SitecoreTokenHandler>();
        services.AddSingleton<ISitecoreGraphQLFactory, SitecoreGraphQLFactory>();
        return services;
    }

    private static bool IsValidHttpUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
}
