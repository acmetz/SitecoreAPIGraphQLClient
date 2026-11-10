using Microsoft.Extensions.Options;

namespace Sitecore.API.Foundation.GraphQL.Internal;

internal sealed class SitecoreGraphQLOptionsValidator : IValidateOptions<SitecoreGraphQLOptions>
{
    public ValidateOptionsResult Validate(string? name, SitecoreGraphQLOptions options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Fail("SitecoreGraphQLOptions cannot be null.");
        }

        List<string>? failures = null;

        if (options.MaxUnauthorizedRetries < 0)
        {
            (failures ??= new List<string>()).Add("MaxUnauthorizedRetries must be >= 0.");
        }

        if (options.MaxCachedClients < 1)
        {
            (failures ??= new List<string>()).Add("MaxCachedClients must be >= 1.");
        }

        if (options.MaxRetryBufferBytes <= 0)
        {
            (failures ??= new List<string>()).Add("MaxRetryBufferBytes must be > 0.");
        }

        var hasDefaultEndpoint = !string.IsNullOrWhiteSpace(options.Endpoint);
        var hasDefaultClientId = !string.IsNullOrWhiteSpace(options.ClientId);
        var hasDefaultClientSecret = !string.IsNullOrWhiteSpace(options.ClientSecret);
        var hasCompleteDefaults = hasDefaultEndpoint && hasDefaultClientId && hasDefaultClientSecret;
        var hasNamedClients = options.Clients is { Count: > 0 };

        if (!hasCompleteDefaults && !hasNamedClients)
        {
            (failures ??= new List<string>()).Add(
                "Either default Endpoint/ClientId/ClientSecret or at least one named client with Endpoint/ClientId/ClientSecret must be configured.");
        }

        if (hasDefaultEndpoint && !EndpointUriValidator.TryValidateEndpoint(options.Endpoint!, options.AllowInsecureHttp, out _, out var defaultEndpointError))
        {
            (failures ??= new List<string>()).Add($"Default Endpoint is invalid: {defaultEndpointError}");
        }

        if (options.Clients is not null)
        {
            foreach (var (clientName, clientOptions) in options.Clients)
            {
                if (string.IsNullOrWhiteSpace(clientName))
                {
                    (failures ??= new List<string>()).Add("Named client key cannot be null or whitespace.");
                    continue;
                }

                if (clientOptions is null)
                {
                    (failures ??= new List<string>()).Add($"Named client '{clientName}' configuration cannot be null.");
                    continue;
                }

                if (!options.TryResolveNamedClient(clientName, out var resolvedEndpoint, out _, out var allowInsecureHttp, out _))
                {
                    (failures ??= new List<string>()).Add(
                        $"Named client '{clientName}' must define or inherit non-empty Endpoint, ClientId, and ClientSecret.");
                    continue;
                }

                if (!EndpointUriValidator.TryValidateEndpoint(resolvedEndpoint, allowInsecureHttp, out _, out var namedEndpointError))
                {
                    (failures ??= new List<string>()).Add($"Named client '{clientName}' Endpoint is invalid: {namedEndpointError}");
                }
            }
        }

        return failures is { Count: > 0 }
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}

internal static class EndpointUriValidator
{
    public static bool TryValidateEndpoint(
        string rawEndpoint,
        bool allowInsecureHttp,
        out string normalizedEndpoint,
        out string errorMessage)
    {
        normalizedEndpoint = string.Empty;

        if (string.IsNullOrWhiteSpace(rawEndpoint))
        {
            errorMessage = "Endpoint URL cannot be null or whitespace.";
            return false;
        }

        if (IsCanonicalHttpsEndpoint(rawEndpoint))
        {
            normalizedEndpoint = rawEndpoint;
            errorMessage = string.Empty;
            return true;
        }

        if (!Uri.TryCreate(rawEndpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            errorMessage = $"Endpoint '{SanitizeForLogging(rawEndpoint)}' must be an absolute HTTP or HTTPS URI.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || ContainsAuthorityUserInfoDelimiter(rawEndpoint))
        {
            errorMessage = $"Endpoint '{SanitizeForLogging(rawEndpoint)}' must not contain embedded user credentials (UserInfo).";
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback && !allowInsecureHttp)
        {
            errorMessage = $"Endpoint '{SanitizeForLogging(rawEndpoint)}' uses cleartext HTTP on a non-loopback host. Use HTTPS or set AllowInsecureHttp = true.";
            return false;
        }

        normalizedEndpoint = uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped);
        errorMessage = string.Empty;
        return true;
    }

    public static string ValidateAndNormalizeEndpoint(
        string rawEndpoint,
        bool allowInsecureHttp,
        string paramName,
        bool throwInvalidOperation = false)
    {
        if (TryValidateEndpoint(rawEndpoint, allowInsecureHttp, out var normalizedEndpoint, out var errorMessage))
        {
            return normalizedEndpoint;
        }

        if (throwInvalidOperation)
        {
            throw new InvalidOperationException(errorMessage);
        }

        throw new ArgumentException(errorMessage, paramName);
    }

    public static string SanitizeForLogging(string? rawEndpoint)
    {
        if (string.IsNullOrWhiteSpace(rawEndpoint))
        {
            return "(unset)";
        }

        if (Uri.TryCreate(rawEndpoint, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            return uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
        }

        return "(invalid-uri)";
    }

    public static string GetRedactedAuthority(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return "unknown";
        }

        return uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
    }

    private static bool ContainsAuthorityUserInfoDelimiter(string rawEndpoint)
    {
        var schemeSeparator = rawEndpoint.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator < 0)
        {
            return false;
        }

        var authorityStart = schemeSeparator + 3;
        var authorityEnd = rawEndpoint.Length;
        for (var i = authorityStart; i < rawEndpoint.Length; i++)
        {
            var ch = rawEndpoint[i];
            if (ch is '/' or '?' or '#')
            {
                authorityEnd = i;
                break;
            }
        }

        return rawEndpoint.AsSpan(authorityStart, authorityEnd - authorityStart).Contains('@');
    }

    private static bool IsCanonicalHttpsEndpoint(string rawEndpoint)
    {
        const string HttpsPrefix = "https://";
        if (!rawEndpoint.StartsWith(HttpsPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var span = rawEndpoint.AsSpan();
        var slashIndex = span[HttpsPrefix.Length..].IndexOf('/');
        if (slashIndex <= 0)
        {
            // No explicit path slash or empty host; defer to System.Uri canonicalization.
            return false;
        }

        var authorityEnd = HttpsPrefix.Length + slashIndex;
        var host = span[HttpsPrefix.Length..authorityEnd];
        if (host[0] == '.' || host[^1] == '.')
        {
            return false;
        }

        var prevDot = false;
        foreach (var ch in host)
        {
            var isValidHostChar = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '.';
            if (!isValidHostChar || (ch == '.' && prevDot))
            {
                return false;
            }

            prevDot = ch == '.';
        }

        var pathAndQuery = span[authorityEnd..];
        if (pathAndQuery.Contains("/.", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var ch in pathAndQuery)
        {
            if (ch <= ' ' || ch >= 0x7F || ch is '#' or '\\' or '%' or '"' or '<' or '>' or '`' or '{' or '}' or '|' or '^' or '[' or ']')
            {
                return false;
            }
        }

        return true;
    }
}
