using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace Farm.Web.Api.Services.Calibration;

/// <summary>A slice job submission carried out on the caller's behalf.</summary>
/// <param name="RequestBody">
/// The exact JSON body posted to <c>POST /api/slice</c>. Built by the caller from the calibration
/// attempt's own recorded input, with the <c>calibration.method</c>/<c>calibration.params</c>
/// fields overlaid - this gateway never invents slicing parameters itself.
/// </param>
public sealed record CalibrationSliceSubmission(JsonNode RequestBody);

/// <summary>Outcome of submitting a slice job for a calibration step.</summary>
public sealed record SliceSubmissionResult(bool Success, Guid? SliceJobId, string? ErrorCode, string? ErrorDetail)
{
    public static SliceSubmissionResult Ok(Guid sliceJobId) => new(true, sliceJobId, null, null);

    public static SliceSubmissionResult Failed(string errorCode, string? detail = null) =>
        new(false, null, errorCode, detail);
}

/// <summary>Outcome of polling a previously submitted slice job.</summary>
public sealed record SliceStatusResult(
    bool Success,
    string? SliceStatus,
    Guid? GcodeFileId,
    string? ErrorCode,
    string? ErrorDetail)
{
    public static SliceStatusResult Ok(string sliceStatus, Guid? gcodeFileId) =>
        new(true, sliceStatus, gcodeFileId, null, null);

    public static SliceStatusResult Failed(string errorCode, string? detail = null) =>
        new(false, null, null, errorCode, detail);
}

/// <summary>Outcome of dispatching a completed slice job's gcode to a printer.</summary>
public sealed record PrintDispatchResult(bool Success, string? ErrorCode, string? ErrorDetail)
{
    public static PrintDispatchResult Ok() => new(true, null, null);

    public static PrintDispatchResult Failed(string errorCode, string? detail = null) =>
        new(false, errorCode, detail);
}

/// <summary>
/// Submits and polls slice jobs on behalf of the filament-calibration saga by calling the
/// existing <c>SliceJobController</c> HTTP contract, never by re-implementing its submission,
/// rate-limiting, or profile-resolution logic.
/// </summary>
public interface ISliceSubmissionGateway
{
    Task<SliceSubmissionResult> SubmitAsync(CalibrationSliceSubmission submission, CancellationToken ct);

    Task<SliceStatusResult> GetStatusAsync(Guid sliceJobId, CancellationToken ct);
}

/// <summary>
/// Sends a completed slice job's gcode to a printer on behalf of the filament-calibration saga by
/// calling the existing <c>SlicePrintBridgeController</c> HTTP contract, never by re-implementing
/// its upload, safety-validation, or dispatch logic.
/// </summary>
public interface IPrintDispatchGateway
{
    Task<PrintDispatchResult> SendToPrinterAsync(Guid sliceJobId, Guid printerId, CancellationToken ct);
}

/// <summary>
/// Calls the real <c>/api/slice</c> HTTP contract on the current host, so slicing behaves
/// identically whether the slicer module is loaded in-process (monolith) or reached through the
/// gateway/nginx boundary (microservices), and so this saga never duplicates
/// <c>SliceJobController</c>'s validation, rate-limiting, or profile-resolution logic.
/// </summary>
public sealed class InternalApiSliceSubmissionGateway(
    IHttpClientFactory httpClientFactory,
    IHttpContextAccessor httpContextAccessor,
    ILogger<InternalApiSliceSubmissionGateway> logger) : ISliceSubmissionGateway
{
    /// <summary>Name of the named <see cref="HttpClient"/> registered for internal same-host calls.</summary>
    public const string HttpClientName = "CalibrationSagaInternalApi";

    private readonly IHttpClientFactory _httpClientFactory =
        httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));

    private readonly IHttpContextAccessor _httpContextAccessor =
        httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));

    private readonly ILogger<InternalApiSliceSubmissionGateway> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task<SliceSubmissionResult> SubmitAsync(CalibrationSliceSubmission submission, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(submission);
        try
        {
            HttpClient client = CreateClient();
            using HttpRequestMessage request = new(HttpMethod.Post, "api/slice")
            {
                Content = new StringContent(
                    submission.RequestBody.ToJsonString(),
                    Encoding.UTF8,
                    "application/json"),
            };
            using HttpResponseMessage response = await client.SendAsync(request, ct);
            string body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                return SliceSubmissionResult.Failed("slice_submission_rejected", body);
            }

            using JsonDocument document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("id", out JsonElement idElement) ||
                !idElement.TryGetGuid(out Guid sliceJobId))
            {
                return SliceSubmissionResult.Failed("slice_submission_response_invalid");
            }

            return SliceSubmissionResult.Ok(sliceJobId);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(exception, "Slice submission for the calibration saga failed transiently.");
            return SliceSubmissionResult.Failed("slice_submission_transport_error", exception.Message);
        }
    }

    /// <inheritdoc />
    public async Task<SliceStatusResult> GetStatusAsync(Guid sliceJobId, CancellationToken ct)
    {
        try
        {
            HttpClient client = CreateClient();
            using HttpResponseMessage response = await client.GetAsync($"api/slice/{sliceJobId:D}", ct);
            string body = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return SliceStatusResult.Failed("slice_job_not_found");
            }

            if (!response.IsSuccessStatusCode)
            {
                return SliceStatusResult.Failed("slice_status_query_failed", body);
            }

            using JsonDocument document = JsonDocument.Parse(body);
            string? status = document.RootElement.TryGetProperty("status", out JsonElement statusElement)
                ? statusElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(status))
            {
                return SliceStatusResult.Failed("slice_status_response_invalid");
            }

            Guid? gcodeFileId = document.RootElement.TryGetProperty("gcodeFileId", out JsonElement gcodeElement) &&
                gcodeElement.ValueKind == JsonValueKind.String &&
                gcodeElement.TryGetGuid(out Guid parsedGcodeId)
                ? parsedGcodeId
                : null;

            return SliceStatusResult.Ok(status, gcodeFileId);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(exception, "Slice status polling for the calibration saga failed transiently.");
            return SliceStatusResult.Failed("slice_status_transport_error", exception.Message);
        }
    }

    private HttpClient CreateClient()
    {
        HttpClient client = _httpClientFactory.CreateClient(HttpClientName);
        HttpRequest? inboundRequest = _httpContextAccessor.HttpContext?.Request;
        if (client.BaseAddress is null && inboundRequest is not null)
        {
            client.BaseAddress = new Uri($"{inboundRequest.Scheme}://{inboundRequest.Host}");
        }

        string? authorization = _httpContextAccessor.HttpContext?.Request.Headers.Authorization;
        if (!string.IsNullOrEmpty(authorization) &&
            AuthenticationHeaderValue.TryParse(authorization, out AuthenticationHeaderValue? parsedHeader))
        {
            client.DefaultRequestHeaders.Authorization = parsedHeader;
        }

        return client;
    }
}

/// <summary>
/// Calls the real <c>/api/slice/{id}/send-to-printer</c> HTTP contract on the current host so this
/// saga never duplicates <c>SlicePrintBridgeController</c>'s upload, safety-validation, or
/// dispatch logic.
/// </summary>
public sealed class InternalApiPrintDispatchGateway(
    IHttpClientFactory httpClientFactory,
    IHttpContextAccessor httpContextAccessor,
    ILogger<InternalApiPrintDispatchGateway> logger) : IPrintDispatchGateway
{
    private readonly IHttpClientFactory _httpClientFactory =
        httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));

    private readonly IHttpContextAccessor _httpContextAccessor =
        httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));

    private readonly ILogger<InternalApiPrintDispatchGateway> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task<PrintDispatchResult> SendToPrinterAsync(Guid sliceJobId, Guid printerId, CancellationToken ct)
    {
        try
        {
            HttpClient client = _httpClientFactory.CreateClient(InternalApiSliceSubmissionGateway.HttpClientName);
            HttpRequest? inboundRequest = _httpContextAccessor.HttpContext?.Request;
            if (client.BaseAddress is null && inboundRequest is not null)
            {
                client.BaseAddress = new Uri($"{inboundRequest.Scheme}://{inboundRequest.Host}");
            }

            string? authorization = _httpContextAccessor.HttpContext?.Request.Headers.Authorization;
            if (!string.IsNullOrEmpty(authorization) &&
                AuthenticationHeaderValue.TryParse(authorization, out AuthenticationHeaderValue? parsedHeader))
            {
                client.DefaultRequestHeaders.Authorization = parsedHeader;
            }

            var payload = new JsonObject
            {
                ["printerId"] = printerId,
                ["startPrint"] = true,
            };
            using HttpRequestMessage request = new(HttpMethod.Post, $"api/slice/{sliceJobId:D}/send-to-printer")
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            using HttpResponseMessage response = await client.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                return PrintDispatchResult.Ok();
            }

            string body = await response.Content.ReadAsStringAsync(ct);
            return PrintDispatchResult.Failed("send_to_printer_rejected", body);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(exception, "Send-to-printer dispatch for the calibration saga failed transiently.");
            return PrintDispatchResult.Failed("send_to_printer_transport_error", exception.Message);
        }
    }
}
