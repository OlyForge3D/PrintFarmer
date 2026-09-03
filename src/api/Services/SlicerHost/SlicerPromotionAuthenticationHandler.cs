using Farm.Infrastructure;

namespace Farm.Web.Api.Services.SlicerHost;

/// <summary>Attaches only the dedicated promotion credential to slicer-host content requests.</summary>
public sealed class SlicerPromotionAuthenticationHandler(IConfiguration configuration) : DelegatingHandler
{
    private readonly string _sharedKey =
        configuration[SlicerPromotionContract.SharedKeyPath] ??
        throw new InvalidOperationException($"Configure {SlicerPromotionContract.SharedKeyPath}.");

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.Authorization = null;
        _ = request.Headers.Remove(SlicerPromotionContract.ApiKeyHeaderName);
        _ = request.Headers.TryAddWithoutValidation(
            SlicerPromotionContract.ApiKeyHeaderName,
            _sharedKey);
        return base.SendAsync(request, cancellationToken);
    }
}
