using System.Security.Cryptography;
using System.Text;
using Sitecore.API.Foundation.Authorization.Models;

namespace Sitecore.API.Foundation.GraphQL.Internal;

internal sealed class CredentialContext
{
    public static readonly HttpRequestOptionsKey<CredentialContext> OptionsKey = new("Sitecore.API.Foundation.GraphQL.CredentialContext");

    public CredentialContext(
        SitecoreAuthClientCredentials credentials,
        string? clientName,
        string sanitizedEndpoint,
        bool isExplicitClient,
        bool allowPostRetries)
    {
        Credentials = credentials;
        ClientName = clientName;
        SanitizedEndpoint = sanitizedEndpoint;
        IsExplicitClient = isExplicitClient;
        AllowPostRetries = allowPostRetries;
        ResiliencePartitionKey = BuildPartitionKey(clientName, credentials.ClientId, isExplicitClient);
    }

    public SitecoreAuthClientCredentials Credentials { get; }

    public string? ClientName { get; }

    public string SanitizedEndpoint { get; }

    public bool IsExplicitClient { get; }

    public bool AllowPostRetries { get; }

    public string ResiliencePartitionKey { get; }

    public override string ToString()
        => $"CredentialContext {{ ClientId = {Credentials.ClientId}, ClientSecret = ***, ClientName = {ClientName ?? "(none)"}, Endpoint = {SanitizedEndpoint}, AllowPostRetries = {AllowPostRetries} }}";

    private static string BuildPartitionKey(string? clientName, string clientId, bool isExplicitClient)
    {
        if (!string.IsNullOrEmpty(clientName))
        {
            return $"named:{clientName}";
        }

        if (!isExplicitClient)
        {
            return "default";
        }

        Span<byte> utf8Buffer = stackalloc byte[256];
        byte[]? rentedBytes = null;
        int byteCount;
        if (Encoding.UTF8.TryGetBytes(clientId.AsSpan(), utf8Buffer, out byteCount))
        {
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(utf8Buffer[..byteCount], hash);
            return $"explicit:{Convert.ToHexString(hash[..8])}";
        }

        rentedBytes = Encoding.UTF8.GetBytes(clientId);
        Span<byte> fallbackHash = stackalloc byte[32];
        SHA256.HashData(rentedBytes, fallbackHash);
        return $"explicit:{Convert.ToHexString(fallbackHash[..8])}";
    }
}

internal readonly record struct ClientCacheKey(
    string NormalizedUrl,
    string ClientId,
    string ClientSecret,
    string? ClientName,
    bool AllowPostRetries)
{
    public bool Equals(ClientCacheKey other)
        => string.Equals(NormalizedUrl, other.NormalizedUrl, StringComparison.Ordinal) &&
           string.Equals(ClientId, other.ClientId, StringComparison.Ordinal) &&
           string.Equals(ClientSecret, other.ClientSecret, StringComparison.Ordinal) &&
           string.Equals(ClientName, other.ClientName, StringComparison.Ordinal) &&
           AllowPostRetries == other.AllowPostRetries;

    public override int GetHashCode()
        => HashCode.Combine(
            StringComparer.Ordinal.GetHashCode(NormalizedUrl),
            StringComparer.Ordinal.GetHashCode(ClientId),
            StringComparer.Ordinal.GetHashCode(ClientSecret),
            ClientName is null ? 0 : StringComparer.Ordinal.GetHashCode(ClientName),
            AllowPostRetries);

    public override string ToString()
        => $"ClientCacheKey {{ Url = {EndpointUriValidator.SanitizeForLogging(NormalizedUrl)}, ClientId = {ClientId}, ClientSecret = ***, ClientName = {ClientName ?? "(none)"}, AllowPostRetries = {AllowPostRetries} }}";
}
