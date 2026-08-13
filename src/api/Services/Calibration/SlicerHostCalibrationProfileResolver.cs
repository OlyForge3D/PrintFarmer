using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.PrinterCalibration;
using Microsoft.Net.Http.Headers;

namespace Farm.Web.Api.Services.Calibration;

/// <summary>
/// Resolves calibration profiles over an authenticated internal HTTP hop to the slicer host.
/// </summary>
/// <remarks>
/// <para>
/// In split and microservices deployments the slicer host owns the profile store, so the main API
/// has no in-process resolver at all. This adapter restores the <see cref="ICalibrationProfileResolver"/>
/// contract by calling the slicer host's narrow resolution endpoint at a fixed relative route on a
/// configured, non-caller-controlled base address.
/// </para>
/// <para>
/// Security posture: the current end user's already-validated bearer token is forwarded verbatim so
/// the slicer host performs its own authentication, permission check and ownership scoping. No
/// service-to-service credential is minted here, the token is never logged, and the internal base
/// address never appears in logs or responses. Dependency failures are converted into a typed
/// <see cref="CalibrationProfileResolverUnavailableException"/> so the caller can distinguish safe
/// authentication, authorization, configuration, timeout, and availability failure codes without
/// leaking transport detail.
/// </para>
/// </remarks>
public sealed class SlicerHostCalibrationProfileResolver(
    HttpClient httpClient,
    IHttpContextAccessor httpContextAccessor,
    SlicerHostCalibrationResolverOptions options,
    ILogger<SlicerHostCalibrationProfileResolver> logger)
    : ICalibrationProfileResolver
{
    private const string BearerScheme = "Bearer";
    private const string HealthyStatus = "Healthy";
    private const int MaxHealthResponseBytes = 1024;

    /// <inheritdoc />
    /// <remarks>
    /// Uses the slicer host's dedicated no-data availability probe. It requires no end-user token,
    /// returns no profile data, and anything other than a successful <c>Healthy</c> answer is treated
    /// as unavailable so capability discovery fails closed.
    /// </remarks>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(options.HealthTimeout);

        try
        {
            using HttpRequestMessage request = new(
                HttpMethod.Get,
                CalibrationProfileResolutionContract.HealthRelativeRoute);
            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutSource.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug(
                    "Calibration profile resolver probe reported {StatusCode}",
                    (int)response.StatusCode);
                return false;
            }

            string body = await ReadBoundedStringAsync(
                response,
                MaxHealthResponseBytes,
                timeoutSource.Token);
            return string.Equals(body.Trim(), HealthyStatus, StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or OperationCanceledException or IOException
                or InvalidOperationException or CalibrationProfileResolverUnavailableException &&
            !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Calibration profile resolver probe failed ({ExceptionType})",
                exception.GetType().Name);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<ResolvedCalibrationProfiles> ResolveAsync(
        Guid machineProfileId,
        Guid processProfileId,
        Guid filamentProfileId,
        CalibrationProfileAccessScope accessScope,
        CancellationToken cancellationToken)
    {
        // The scope is re-derived from the forwarded token by the slicer host. Sending it would let a
        // compromised API path assert ownership, so it is deliberately dropped here.
        _ = accessScope;

        string bearerToken = GetCurrentBearerToken();
        using CancellationTokenSource timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(options.ResolveTimeout);

        try
        {
            using HttpRequestMessage request = new(
                HttpMethod.Post,
                CalibrationProfileResolutionContract.ResolveRelativeRoute)
            {
                Content = JsonContent.Create(
                    new ResolveCalibrationProfilesRequest(
                        machineProfileId,
                        processProfileId,
                        filamentProfileId),
                    options: CalibrationProfileResolutionContract.SerializerOptions),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue(BearerScheme, bearerToken);

            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutSource.Token);

            if (!response.IsSuccessStatusCode)
            {
                throw Unavailable(response.StatusCode);
            }

            if (!IsJsonResponse(response))
            {
                logger.LogWarning(
                    "Calibration profile resolution returned an unexpected media type");
                throw new CalibrationProfileResolverUnavailableException(
                    "The calibration profile resolver returned an unexpected media type.");
            }

            string payload = await ReadBoundedStringAsync(
                response,
                options.MaxResponseBytes,
                timeoutSource.Token);
            return Deserialize(payload);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or InvalidOperationException)
        {
            // IOException covers HttpIOException, which a truncated or reset response body raises
            // and which is not an HttpRequestException. Letting it escape would surface transport
            // detail to the caller instead of the stable unavailable signal.
            logger.LogWarning(
                "Calibration profile resolution failed ({ExceptionType})",
                exception.GetType().Name);
            throw new CalibrationProfileResolverUnavailableException(
                "The calibration profile resolver could not be reached.",
                exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Calibration profile resolution timed out");
            throw new CalibrationProfileResolverUnavailableException(
                "The calibration profile resolver did not respond in time.",
                "profile_service_timeout",
                exception);
        }
    }

    private string GetCurrentBearerToken()
    {
        string? header = httpContextAccessor.HttpContext?.Request
            .Headers[HeaderNames.Authorization]
            .ToString();
        if (!string.IsNullOrWhiteSpace(header) &&
            header.StartsWith(BearerScheme + " ", StringComparison.OrdinalIgnoreCase))
        {
            string token = header[(BearerScheme.Length + 1)..].Trim();
            if (token.Length > 0)
            {
                return token;
            }
        }

        // Without the end user's own token the slicer host cannot scope the answer, and minting a
        // service token here would bypass ownership. Fail closed instead.
        logger.LogWarning(
            "Calibration profile resolution has no forwardable end-user bearer token");
        throw new CalibrationProfileResolverUnavailableException(
            "No end-user bearer token is available to authenticate calibration profile resolution.",
            "profile_service_authentication_failed");
    }

    private CalibrationProfileResolverUnavailableException Unavailable(HttpStatusCode statusCode)
    {
        logger.LogWarning(
            "Calibration profile resolution was refused with {StatusCode}",
            (int)statusCode);
        string errorCode = statusCode switch
        {
            HttpStatusCode.Unauthorized => "profile_service_authentication_failed",
            HttpStatusCode.Forbidden => "profile_service_authorization_failed",
            HttpStatusCode.BadRequest or HttpStatusCode.NotFound =>
                "profile_service_configuration_error",
            _ => "profile_service_unavailable",
        };
        return new CalibrationProfileResolverUnavailableException(
            "The calibration profile resolver refused the request.",
            errorCode);
    }

    private static bool IsJsonResponse(HttpResponseMessage response)
    {
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        return string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase);
    }

    private static ResolvedCalibrationProfiles Deserialize(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<ResolvedCalibrationProfiles>(
                       payload,
                       CalibrationProfileResolutionContract.SerializerOptions)
                   ?? throw new CalibrationProfileResolverUnavailableException(
                       "The calibration profile resolver returned an empty document.");
        }
        catch (JsonException exception)
        {
            throw new CalibrationProfileResolverUnavailableException(
                "The calibration profile resolver returned a malformed document.",
                exception);
        }
    }

    /// <summary>
    /// Buffers at most <paramref name="maxBytes"/> from the response and rejects anything longer,
    /// so a hostile or broken peer cannot force unbounded allocation in the API process.
    /// </summary>
    private static async Task<string> ReadBoundedStringAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maxBytes)
        {
            throw new CalibrationProfileResolverUnavailableException(
                "The calibration profile resolver returned an oversized response.");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Min(maxBytes, 64 * 1024));
        using MemoryStream accumulator = new();
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (accumulator.Length + read > maxBytes)
                {
                    throw new CalibrationProfileResolverUnavailableException(
                        "The calibration profile resolver returned an oversized response.");
                }

                await accumulator.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Encoding.UTF8.GetString(accumulator.GetBuffer(), 0, (int)accumulator.Length);
    }
}
