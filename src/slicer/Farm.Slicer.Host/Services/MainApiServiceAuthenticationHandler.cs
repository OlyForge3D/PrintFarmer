using System.Net;
using Farm.Infrastructure;
using Farm.Slicer.Module.Services.Configuration;

namespace Farm.Slicer.Host.Services;

/// <summary>
/// Attaches the configured slicer service credential to Main API lookup requests.
/// </summary>
public sealed class MainApiServiceAuthenticationHandler(
    IConfiguration configuration) : DelegatingHandler
{
    private readonly string _sharedKey = ResolveSharedKey(configuration);

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        _ = request.Headers.Remove(SlicerHostLookupContract.ApiKeyHeaderName);
        _ = request.Headers.TryAddWithoutValidation(
            SlicerHostLookupContract.ApiKeyHeaderName,
            _sharedKey);
        HttpResponseMessage response =
            await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        try
        {
            MainApiResponseGuard.ThrowIfAuthenticationFailed(response);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static string ResolveSharedKey(IConfiguration configuration)
    {
        WorkerAuthKeyResolution? resolution =
            WorkerAuthConfiguration.ResolveSharedKey(configuration);
        return resolution is not null
            ? resolution.Value
            : throw new InvalidOperationException(
                $"Configure {WorkerAuthConfiguration.SharedKeyPath} " +
                "before using Main API cross-domain lookups.");
    }
}

internal static class MainApiResponseGuard
{
    public static void ThrowIfAuthenticationFailed(HttpResponseMessage response)
    {
        if (IsAuthenticationFailure(response.StatusCode))
        {
            throw new HttpRequestException(
                $"Main API service authentication failed with HTTP {(int)response.StatusCode}.",
                inner: null,
                response.StatusCode);
        }
    }

    public static bool IsAuthenticationFailure(HttpRequestException exception) =>
        exception.StatusCode is HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden
            or HttpStatusCode.ServiceUnavailable;

    private static bool IsAuthenticationFailure(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden
            or HttpStatusCode.ServiceUnavailable;
}
