using Sitecore.API.Foundation.Authorization.Models;

namespace Sitecore.API.Foundation.GraphQL;

/// <summary>
/// Provides access to the raw access token value from a <see cref="SitecoreAuthToken"/>.
/// </summary>
public interface ITokenValueAccessor
{
    /// <summary>
    /// Extracts the access token string from a <see cref="SitecoreAuthToken"/> instance.
    /// </summary>
    /// <param name="token">The token object returned by the authorization service.</param>
    /// <returns>The access token string, or an empty string if none is available.</returns>
    string GetAccessToken(SitecoreAuthToken token);
}

internal sealed class DefaultTokenValueAccessor : ITokenValueAccessor
{
    public string GetAccessToken(SitecoreAuthToken token)
        => token.AccessToken ?? string.Empty;
}
