using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace Farm.Testing.Shared;

// A delegating handler that blocks outbound HTTP requests unless the host is localhost/127.0.0.1
public sealed class BlockingOutboundHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            if (request.RequestUri == null)
            {
                return Task.FromException<HttpResponseMessage>(new HttpRequestException("Blocked outbound HTTP: missing RequestUri"));
            }

            string host = request.RequestUri.Host;
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1" || host == "[::1]")
            {
                // Allow loopback
                return InnerHandler != null
                    ? base.SendAsync(request, cancellationToken)
                    : Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            // Block all other outbound calls
            return Task.FromException<HttpResponseMessage>(new HttpRequestException($"Blocked outbound HTTP to {request.RequestUri}"));
        }
        catch (Exception ex)
        {
            return Task.FromException<HttpResponseMessage>(ex);
        }
    }
}

// IHttpMessageHandlerBuilderFilter to inject BlockingOutboundHandler into all HttpClient pipelines
public sealed class BlockOutboundHttpFilter : IHttpMessageHandlerBuilderFilter
{
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next)
    {
        return builder =>
        {
            next(builder);
            // Prepend the blocking handler to ensure it runs before other handlers
            HttpMessageHandler currentPrimary = builder.PrimaryHandler ?? new HttpClientHandler
            {
                // This handler only ever reaches loopback (see BlockingOutboundHandler above),
                // so certificate revocation is not meaningful here, but set it explicitly rather
                // than defer CA5399 — a security-relevant analyzer rule should not be suppressed
                // just because the call site happens to be test-only.
                CheckCertificateRevocationList = true,
            };
            BlockingOutboundHandler blocking = new BlockingOutboundHandler { InnerHandler = currentPrimary };
            builder.PrimaryHandler = blocking;
        };
    }
}
