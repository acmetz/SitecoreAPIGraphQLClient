namespace Sitecore.API.Foundation.GraphQL.Internal;

/// <summary>
/// Socket-free forwarding <see cref="HttpMessageHandler"/> that stamps per-client <see cref="CredentialContext"/>
/// onto a shallow request envelope and dispatches via <see cref="IHttpClientFactory"/> so pooled transport handlers
/// continue rotating every 2 minutes for DNS updates.
/// </summary>
internal sealed class CredentialForwardingHandler : HttpMessageHandler
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _httpClientName;
    private readonly CredentialContext _credentialContext;

    public CredentialForwardingHandler(
        IHttpClientFactory httpClientFactory,
        string httpClientName,
        CredentialContext credentialContext)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _httpClientName = httpClientName ?? throw new ArgumentNullException(nameof(httpClientName));
        _credentialContext = credentialContext ?? throw new ArgumentNullException(nameof(credentialContext));
    }

    public CredentialContext CredentialContext => _credentialContext;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var originalContent = request.Content;
        var forwardedRequest = CreateForwardedRequest(request, _credentialContext);
        try
        {
#pragma warning disable CA2000 // IHttpClientFactory manages pooled handler lifetimes; disposing here would break response streaming or custom shared-client test doubles.
            var client = _httpClientFactory.CreateClient(_httpClientName);
#pragma warning restore CA2000
            return await client.SendAsync(forwardedRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (ReferenceEquals(forwardedRequest.Content, originalContent))
            {
                // Detach caller-owned content before disposing the shallow forwarding envelope.
                forwardedRequest.Content = null;
            }

            forwardedRequest.Dispose();
        }
    }

    private static HttpRequestMessage CreateForwardedRequest(HttpRequestMessage source, CredentialContext credentialContext)
    {
        var forwarded = new HttpRequestMessage(source.Method, source.RequestUri)
        {
            Version = source.Version,
            VersionPolicy = source.VersionPolicy,
            Content = source.Content
        };

        foreach (var header in source.Headers)
        {
            forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var option in source.Options)
        {
            forwarded.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        }

        forwarded.Options.Set(CredentialContext.OptionsKey, credentialContext);
        return forwarded;
    }
}
