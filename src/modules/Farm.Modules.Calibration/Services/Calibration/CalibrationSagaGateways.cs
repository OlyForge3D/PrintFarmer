using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Farm.Modules.Calibration.Services.Calibration;

/// <summary>A slice job submission carried out on the caller's behalf.</summary>
/// <param name="RequestBody">
/// The exact JSON body posted to <c>POST /api/slice</c>. Built by the caller from the calibration
/// attempt's own recorded input, with the <c>calibration.method</c>/<c>calibration.params</c>
/// fields overlaid - this gateway never invents slicing parameters itself.
/// </param>
public sealed record CalibrationSliceSubmission(JsonNode RequestBody);

/// <summary>Outcome of submitting a slice job for a calibration step.</summary>
/// <param name="Success">Whether the submission was accepted.</param>
/// <param name="SliceJobId">The accepted slice job's ID, when <paramref name="Success"/> is <c>true</c>.</param>
/// <param name="ErrorCode">A stable machine-readable failure code, when <paramref name="Success"/> is <c>false</c>.</param>
/// <param name="ErrorDetail">A human-readable failure detail, when <paramref name="Success"/> is <c>false</c>.</param>
/// <param name="IsTerminal">
/// Set when the gateway determined the failure is deterministic given the same request body -
/// currently, an HTTP 400 from <c>POST /api/slice</c> - so retrying (which resubmits the exact
/// same body) can never succeed. <see cref="CalibrationOrchestrationSagaService"/> fails the step
/// immediately instead of entering the exponential-backoff retry loop when this is set, so a
/// deterministic rejection (e.g. an unknown or unsupported calibration method)
/// surfaces to the operator right away rather than after minutes of guaranteed-to-repeat retries.
/// </param>
public sealed record SliceSubmissionResult(bool Success, Guid? SliceJobId, string? ErrorCode, string? ErrorDetail, bool IsTerminal = false)
{
    public static SliceSubmissionResult Ok(Guid sliceJobId) => new(true, sliceJobId, null, null);

    public static SliceSubmissionResult Failed(string errorCode, string? detail = null, bool isTerminal = false) =>
        new(false, null, errorCode, detail, isTerminal);
}

/// <summary>
/// Outcome of polling a previously submitted slice job. There is no gcode-artifact identifier on
/// the real <c>GET /api/slice/{id}</c> response (<c>SliceJobStatusResponse</c>) - once a slice
/// reports <c>Completed</c>, its gcode is resolved through <c>GET /api/artifacts/job/{jobId}</c>
/// downstream, which this saga never needs to do itself because
/// <c>SlicePrintBridgeController.SendToPrinterAsync</c> only needs the slice job ID, not a
/// resolved gcode/artifact ID, to dispatch a print.
/// </summary>
public sealed record SliceStatusResult(
    bool Success,
    string? SliceStatus,
    string? ErrorCode,
    string? ErrorDetail)
{
    public static SliceStatusResult Ok(string sliceStatus) => new(true, sliceStatus, null, null);

    public static SliceStatusResult Failed(string errorCode, string? detail = null) =>
        new(false, null, errorCode, detail);
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
                // A 400 means SliceJobController rejected this exact request body as invalid
                // (e.g. issue #2139's missing/unsupported input-shaping firmware flavor) -
                // resubmitting the same body will always fail the same way, so this is terminal,
                // not retryable. Any other non-success status (5xx, etc.) is left retryable, since
                // those can plausibly succeed on a later attempt.
                bool isTerminal = response.StatusCode == System.Net.HttpStatusCode.BadRequest;
                return SliceSubmissionResult.Failed("slice_submission_rejected", body, isTerminal);
            }

            // POST /api/slice returns SubmitSliceJobResponse, whose job identifier is serialized
            // as "jobId" (camelCase of SubmitSliceJobResponse.JobId) - not "id".
            using JsonDocument document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("jobId", out JsonElement idElement) ||
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

            return SliceStatusResult.Ok(status);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(exception, "Slice status polling for the calibration saga failed transiently.");
            return SliceStatusResult.Failed("slice_status_transport_error", exception.Message);
        }
    }

    /// <summary>
    /// Retrieves the shared, DI-configured internal <see cref="HttpClient"/> and forwards the
    /// caller's own bearer token so the internal call is authorized exactly as the caller's own
    /// permissions allow - never any more. The client's <see cref="HttpClient.BaseAddress"/> is
    /// pinned once via <c>Program.cs</c>'s <c>AddHttpClient</c> registration from trusted
    /// configuration; it is deliberately never derived from the inbound request's own
    /// <c>Host</c>/<c>Scheme</c>, which would let a caller redirect this server's own bearer-token
    /// bearing calls to an arbitrary host it controls.
    /// </summary>
    private HttpClient CreateClient()
    {
        HttpClient client = _httpClientFactory.CreateClient(HttpClientName);
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
            // The client's BaseAddress is pinned via Program.cs's AddHttpClient registration from
            // trusted configuration - never derived from the inbound request's own Host/Scheme,
            // which would let a caller redirect this server's own bearer-token bearing calls to an
            // arbitrary host it controls.
            HttpClient client = _httpClientFactory.CreateClient(InternalApiSliceSubmissionGateway.HttpClientName);
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
