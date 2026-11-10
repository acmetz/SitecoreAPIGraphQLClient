using Sitecore.API.Foundation.Authorization.Models;

namespace Sitecore.API.Foundation.GraphQL.Internal;

internal static class SitecoreGraphQLOptionsExtensions
{
    public static bool TryBuildCredentials(this SitecoreGraphQLOptions? options, out SitecoreAuthClientCredentials credentials)
    {
        if (options is not null &&
            !string.IsNullOrWhiteSpace(options.ClientId) &&
            !string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            credentials = new SitecoreAuthClientCredentials(options.ClientId, options.ClientSecret);
            return true;
        }

        credentials = default;
        return false;
    }

    public static bool TryResolveNamedClient(
        this SitecoreGraphQLOptions? options,
        string clientName,
        out string endpoint,
        out SitecoreAuthClientCredentials credentials,
        out bool allowInsecureHttp,
        out bool allowPostRetries)
    {
        endpoint = string.Empty;
        credentials = default;
        allowInsecureHttp = false;
        allowPostRetries = false;

        if (options?.Clients is null ||
            string.IsNullOrWhiteSpace(clientName) ||
            !options.Clients.TryGetValue(clientName, out var clientOpts) ||
            clientOpts is null)
        {
            return false;
        }

        var resolvedEndpoint = !string.IsNullOrWhiteSpace(clientOpts.Endpoint) ? clientOpts.Endpoint : options.Endpoint;
        var resolvedId = !string.IsNullOrWhiteSpace(clientOpts.ClientId) ? clientOpts.ClientId : options.ClientId;
        var resolvedSecret = !string.IsNullOrWhiteSpace(clientOpts.ClientSecret) ? clientOpts.ClientSecret : options.ClientSecret;

        allowInsecureHttp = clientOpts.AllowInsecureHttp ?? options.AllowInsecureHttp;
        allowPostRetries = clientOpts.AllowPostRetries ?? options.AllowPostRetries;

        if (string.IsNullOrWhiteSpace(resolvedEndpoint) ||
            string.IsNullOrWhiteSpace(resolvedId) ||
            string.IsNullOrWhiteSpace(resolvedSecret))
        {
            return false;
        }

        endpoint = resolvedEndpoint;
        credentials = new SitecoreAuthClientCredentials(resolvedId, resolvedSecret);
        return true;
    }
}
