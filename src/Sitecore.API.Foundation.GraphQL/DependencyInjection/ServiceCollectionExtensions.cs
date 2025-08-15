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
    /// Registers the Sitecore GraphQL client factory, HTTP pipeline, and options.
    /// Binds configuration from the <c>Sitecore:GraphQL</c> section and validates required values.
    /// Also ensures optional logging infrastructure is available so downstream services like the
    /// Sitecore Token Service can receive ILogger&lt;T&gt; if not already configured by the host.
    /// </summary>
    /// <param name="services">The DI service collection.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="services"/> or <paramref name="configuration"/> is null.</exception>
    public static IServiceCollection AddSitecoreGraphQL(this IServiceCollection services, IConfiguration configuration)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (configuration is null) throw new ArgumentNullException(nameof(configuration));

        // Bind options with validation that will be enforced on first resolution (ValidateOnStart)
        services.AddOptions<SitecoreGraphQLOptions>()
                .Bind(configuration.GetSection(SectionName))
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

        // Optional logging wiring (honors EnableInternalLoggingSetup) without building a provider or triggering validation
        var tmpOptions = new SitecoreGraphQLOptions();
        configuration.GetSection(SectionName).Bind(tmpOptions);
        if (tmpOptions.EnableInternalLoggingSetup)
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
