using System.Buffers;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Slicer.Module.Services.Configuration;
using Farm.Web.Api.Services.Calibration;

namespace Farm.Web.Api.Services.Calibration.Generation;

/// <summary>
/// Answers worker/version compatibility for <see cref="ICalibrationGenerationCapabilityProbe"/> over
/// an authenticated internal HTTP hop to the slicer host, for deployments that run the slicer module
/// as a separate peer process (issue #1848).
/// </summary>
/// <remarks>
/// <para>
/// A monolith host answers this question in-process against a local
/// <c>IDbContextFactory&lt;SlicerDbContext&gt;</c>. A split or microservices host never registers
/// that factory, because this host owns the worker registry instead. Without this adapter the probe
/// silently fell back to <see cref="WorkerCompatibilitySnapshotDto.Empty"/> forever, misreporting
/// <c>calibrationGenerationEnabled: false</c> even when a fully attested worker was online.
/// </para>
/// <para>
/// Security posture: unlike <see cref="ICalibrationProfileResolver"/>, this is not an end-user
/// request scoped by a forwarded bearer token — it is a plain service-to-service query with no
/// per-caller data, so it authenticates with the shared worker registration key
/// (<c>WorkerAuth:SharedKey</c>) sent as <c>X-Slicer-ApiKey</c>, the same credential worker processes
/// already present to this slicer host. This method never throws: any failure (missing key, network
/// error, timeout, malformed response) degrades to <see cref="WorkerCompatibilitySnapshotDto.Empty"/>,
/// which is already a valid, expected answer from the probe's perspective.
/// </para>
/// </remarks>
public sealed class SlicerHostCapabilityClient(
    HttpClient httpClient,
    IConfiguration configuration,
    SlicerHostCalibrationResolverOptions options,
    ILogger<SlicerHostCapabilityClient> logger)
    : ISlicerHostCapabilityClient
{
    private readonly HttpClient _httpClient =
        httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    private readonly IConfiguration _configuration =
        configuration ?? throw new ArgumentNullException(nameof(configuration));

    private readonly SlicerHostCalibrationResolverOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    private readonly ILogger<SlicerHostCapabilityClient> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task<WorkerCompatibilitySnapshotDto> GetWorkerCompatibilityAsync(
        string? requiredSlicerVersion,
        CancellationToken cancellationToken)
    {
        WorkerAuthKeyResolution? sharedKey = WorkerAuthConfiguration.ResolveSharedKey(_configuration);
        if (sharedKey is null)
        {
            _logger.LogWarning(
                "Worker compatibility client has no configured {SharedKeyPath}; treating the peer " +
                "slicer host as unavailable.",
                WorkerAuthConfiguration.SharedKeyPath);
            return WorkerCompatibilitySnapshotDto.Empty;
        }

        using CancellationTokenSource timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_options.ResolveTimeout);

        try
        {
            string relativeRoute = string.IsNullOrWhiteSpace(requiredSlicerVersion)
                ? WorkerCompatibilityContract.WorkerCompatibilityRelativeRoute
                : $"{WorkerCompatibilityContract.WorkerCompatibilityRelativeRoute}" +
                  $"?{WorkerCompatibilityContract.RequiredSlicerVersionQueryParam}=" +
                  Uri.EscapeDataString(requiredSlicerVersion);

            using HttpRequestMessage request = new(HttpMethod.Get, relativeRoute);
            request.Headers.Add(WorkerCompatibilityContract.ApiKeyHeaderName, sharedKey.Value);

            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutSource.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Worker compatibility hop was refused with {StatusCode}",
                    (int)response.StatusCode);
                return WorkerCompatibilitySnapshotDto.Empty;
            }

            if (!IsJsonResponse(response))
            {
                _logger.LogWarning("Worker compatibility hop returned an unexpected media type");
                return WorkerCompatibilitySnapshotDto.Empty;
            }

            string payload = await ReadBoundedStringAsync(
                response,
                WorkerCompatibilityContract.MaxResponseBytes,
                timeoutSource.Token);
            return Deserialize(payload);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or InvalidOperationException)
        {
            // IOException covers HttpIOException, which a truncated or reset response body raises
            // and which is not an HttpRequestException.
            _logger.LogWarning(
                "Worker compatibility hop failed ({ExceptionType})",
                exception.GetType().Name);
            return WorkerCompatibilitySnapshotDto.Empty;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Worker compatibility hop timed out");
            return WorkerCompatibilitySnapshotDto.Empty;
        }
    }

    private static bool IsJsonResponse(HttpResponseMessage response)
    {
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        return string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase);
    }

    private WorkerCompatibilitySnapshotDto Deserialize(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<WorkerCompatibilitySnapshotDto>(
                payload,
                WorkerCompatibilityContract.SerializerOptions) ?? WorkerCompatibilitySnapshotDto.Empty;
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(
                "Worker compatibility hop returned a malformed document ({ExceptionType})",
                exception.GetType().Name);
            return WorkerCompatibilitySnapshotDto.Empty;
        }
    }

    /// <summary>
    /// Buffers at most <paramref name="maxBytes"/> from the response and rejects anything longer, so
    /// a hostile or broken peer cannot force unbounded allocation in the API process.
    /// </summary>
    private static async Task<string> ReadBoundedStringAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maxBytes)
        {
            throw new InvalidOperationException("The worker compatibility hop returned an oversized response.");
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
                    throw new InvalidOperationException(
                        "The worker compatibility hop returned an oversized response.");
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

/// <summary>
/// Resolves worker/version compatibility from the peer slicer host in split and microservices
/// deployments (issue #1848).
/// </summary>
public interface ISlicerHostCapabilityClient
{
    /// <summary>
    /// Finds the eligible pinned worker identity and observed upstream slicer versions from the
    /// slicer host's worker registry.
    /// </summary>
    /// <param name="requiredSlicerVersion">
    /// An optional exact slicer version the eligible worker must report, or <see langword="null"/> to
    /// accept any allow-listed supported version.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The compatibility snapshot, or <see cref="WorkerCompatibilitySnapshotDto.Empty"/> when the
    /// peer could not be reached or answered unexpectedly. Never throws.
    /// </returns>
    Task<WorkerCompatibilitySnapshotDto> GetWorkerCompatibilityAsync(
        string? requiredSlicerVersion,
        CancellationToken cancellationToken);
}
