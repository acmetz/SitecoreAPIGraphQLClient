using Sitecore.API.Foundation.Authorization.Models;

namespace Sitecore.API.Foundation.GraphQL.Internal;

internal static class SitecoreGraphQLOptionsExtensions
{
    public static bool TryBuildCredentials(this SitecoreGraphQLOptions options, out SitecoreAuthClientCredentials credentials)
    {
        if (options != null && !string.IsNullOrWhiteSpace(options.ClientId) && !string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            credentials = new SitecoreAuthClientCredentials(options.ClientId, options.ClientSecret);
            return true;
        }

        credentials = default;
        return false;
    }
}
