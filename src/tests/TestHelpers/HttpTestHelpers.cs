using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Farm.Web.Api.Tests.TestHelpers;

/// <summary>
/// Simple configurable HttpMessageHandler for unit tests that maps requests to HttpResponseMessage instances.
/// </summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> responder;

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
    {
        this.responder = responder ?? (_ => new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage resp;
        try
        {
            resp = responder(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        catch (Exception ex)
        {
            resp = new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(ex.Message)
            };
        }
        return Task.FromResult(resp);
    }
}
