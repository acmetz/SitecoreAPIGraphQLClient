using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Sitecore.API.Foundation.GraphQL.Internal;

[ExcludeFromCodeCoverage]
internal static partial class Log
{
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Debug,
        Message = "Creating new GraphQL client for {Endpoint} and clientId {ClientId}.")]
    public static partial void CreatingGraphQLClient(ILogger logger, string endpoint, string clientId);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Trace,
        Message = "Reusing cached GraphQL client for {Endpoint} and clientId {ClientId}.")]
    public static partial void ReusingCachedGraphQLClient(ILogger logger, string endpoint, string clientId);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Debug,
        Message = "Evicted oldest dynamic GraphQL client for {Endpoint} because cache exceeded MaxCachedClients ({MaxCachedClients}).")]
    public static partial void EvictedDynamicGraphQLClient(ILogger logger, string endpoint, int maxCachedClients);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Error,
        Message = "Default ClientId/ClientSecret not configured in options when creating GraphQL client for {Endpoint}.")]
    public static partial void MissingDefaultCredentialsForClient(ILogger logger, string endpoint);

    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Error,
        Message = "Default Endpoint not configured in options when creating default GraphQL client.")]
    public static partial void MissingDefaultEndpoint(ILogger logger);

    [LoggerMessage(
        EventId = 1005,
        Level = LogLevel.Warning,
        Message = "Manual token refresh skipped because no Sitecore GraphQL client credentials are configured.")]
    public static partial void ManualRefreshMissingCredentials(ILogger logger);

    [LoggerMessage(
        EventId = 1006,
        Level = LogLevel.Information,
        Message = "Manual token refresh {Result} ({RefreshedCount}/{TotalCount} credential sets).")]
    public static partial void ManualRefreshCompleted(ILogger logger, string result, int refreshedCount, int totalCount);

    [LoggerMessage(
        EventId = 1007,
        Level = LogLevel.Warning,
        Message = "Manual token refresh failed for clientId {ClientId}.")]
    public static partial void ManualRefreshCredentialFailed(ILogger logger, Exception exception, string clientId);

    [LoggerMessage(
        EventId = 1100,
        Level = LogLevel.Warning,
        Message = "Sitecore GraphQL options missing ClientId/ClientSecret; bearer token cannot be acquired.")]
    public static partial void HandlerMissingCredentials(ILogger logger);

    [LoggerMessage(
        EventId = 1101,
        Level = LogLevel.Debug,
        Message = "HTTP 401 received from {Endpoint}; attempting token refresh (attempt {Attempt}/{MaxRetries}).")]
    public static partial void UnauthorizedRefreshAttempt(ILogger logger, string endpoint, int attempt, int maxRetries);

    [LoggerMessage(
        EventId = 1102,
        Level = LogLevel.Trace,
        Message = "HTTP 401 persisted from {Endpoint}. Waiting {DelayMs:F1}ms before next retry.")]
    public static partial void UnauthorizedRetryBackoff(ILogger logger, string endpoint, double delayMs);

    [LoggerMessage(
        EventId = 1103,
        Level = LogLevel.Warning,
        Message = "Skipping HTTP 401 retry for {Endpoint} because request content exceeded MaxRetryBufferBytes ({MaxRetryBufferBytes} bytes).")]
    public static partial void UnauthorizedRetrySkippedUnbufferedPayload(ILogger logger, string endpoint, long maxRetryBufferBytes);

    [LoggerMessage(
        EventId = 1200,
        Level = LogLevel.Debug,
        Message = "Coalescing concurrent token refresh for clientId {ClientId}.")]
    public static partial void CoalescedTokenRefresh(ILogger logger, string clientId);

    [LoggerMessage(
        EventId = 1201,
        Level = LogLevel.Debug,
        Message = "Token refresh via TryRefreshSitecoreAuthToken fell back to GetSitecoreAuthToken for clientId {ClientId}.")]
    public static partial void TokenRefreshFallbackToGet(ILogger logger, string clientId);

    [LoggerMessage(
        EventId = 1300,
        Level = LogLevel.Warning,
        Message = "Cannot refresh Sitecore token via obsolete ISitecoreTokenCache adapter due to missing ClientId/ClientSecret in options.")]
    public static partial void LegacyCacheMissingCredentials(ILogger logger);
}
