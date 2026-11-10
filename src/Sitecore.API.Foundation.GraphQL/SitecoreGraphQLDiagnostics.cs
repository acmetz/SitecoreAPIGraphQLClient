using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Sitecore.API.Foundation.GraphQL;

/// <summary>
/// Exposes <see cref="System.Diagnostics.ActivitySource"/> and <see cref="System.Diagnostics.Metrics.Meter"/>
/// instrumentation names emitted by <c>Sitecore.API.Foundation.GraphQL</c>.
/// </summary>
public static class SitecoreGraphQLDiagnostics
{
    /// <summary>
    /// The name used for the library's <see cref="System.Diagnostics.ActivitySource"/> and <see cref="System.Diagnostics.Metrics.Meter"/>.
    /// </summary>
    public const string InstrumentationName = "Sitecore.API.Foundation.GraphQL";

    internal static readonly ActivitySource ActivitySource = new(InstrumentationName, "1.0.0");
    internal static readonly Meter Meter = new(InstrumentationName, "1.0.0");

    internal static readonly Counter<long> TokenRequests = Meter.CreateCounter<long>(
        "sitecore.graphql.token.requests",
        description: "Number of Sitecore GraphQL bearer token acquisitions.");

    internal static readonly Counter<long> TokenRefreshes = Meter.CreateCounter<long>(
        "sitecore.graphql.token.refreshes",
        description: "Number of Sitecore GraphQL token refresh operations.");

    internal static readonly Counter<long> AuthRetries = Meter.CreateCounter<long>(
        "sitecore.graphql.auth.retries",
        description: "Number of HTTP 401 Unauthorized retry attempts.");

    internal static readonly Counter<long> ClientCacheEvictions = Meter.CreateCounter<long>(
        "sitecore.graphql.client_cache.evictions",
        description: "Number of dynamic GraphQL clients evicted from the factory cache.");

    internal static readonly Histogram<double> TokenRefreshDurationMs = Meter.CreateHistogram<double>(
        "sitecore.graphql.token.refresh.duration",
        unit: "ms",
        description: "Duration of Sitecore GraphQL token refresh operations in milliseconds.");

    internal static string GetClientTag(string? clientName, bool isExplicit = false)
    {
        if (!string.IsNullOrEmpty(clientName))
        {
            return clientName;
        }

        return isExplicit ? "explicit" : "default";
    }
}
