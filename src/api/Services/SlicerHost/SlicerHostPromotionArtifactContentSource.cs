using System.Net;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Slicer.Module.Services;

namespace Farm.Web.Api.Services.SlicerHost;

/// <summary>Streams actively pinned promotion bytes from the standalone slicer host.</summary>
public sealed class SlicerHostPromotionArtifactContentSource(
    HttpClient httpClient,
    SlicerHostPromotionOptions options) : IPromotionArtifactContentSource
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly TimeSpan _streamTimeout = options.StreamTimeout;

    /// <inheritdoc />
    public async Task<PromotionArtifactContent?> OpenReadAsync(
        Guid artifactId,
        string operationKey,
        long expectedSizeBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            SlicerPromotionContract.ArtifactContentPath(artifactId));
        _ = request.Headers.TryAddWithoutValidation(
            SlicerPromotionContract.OperationKeyHeaderName,
            operationKey);

        using var ownership = new PendingResponseOwnership(_streamTimeout, cancellationToken);
        try
        {
            HttpResponseMessage response = await ownership.SendAsync(
                _httpClient,
                request,
                HttpCompletionOption.ResponseHeadersRead);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (response.StatusCode == HttpStatusCode.Forbidden &&
                string.Equals(
                    await ReadProblemCodeAsync(response, ownership.Token),
                    "promotion_pin_mismatch",
                    StringComparison.Ordinal))
            {
                throw new PromotionSourcePinMismatchException();
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new PromotionSourceTransportException(
                    $"Slicer promotion content returned HTTP {(int)response.StatusCode}.");
            }

            if (response.Content.Headers.ContentLength != expectedSizeBytes)
            {
                throw new PromotionSourceTransportException(
                    "Slicer promotion content length did not match artifact metadata.");
            }

            Stream stream = await response.Content.ReadAsStreamAsync(ownership.Token);
            return ownership.TransferToContent(stream, expectedSizeBytes);
        }
        catch (PromotionSourcePinMismatchException)
        {
            throw;
        }
        catch (PromotionSourceTransportException)
        {
            throw;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PromotionSourceTransportException("Slicer promotion content request timed out.", exception);
        }
        catch (Exception exception) when (
            exception is IOException or HttpRequestException)
        {
            throw new PromotionSourceTransportException("Slicer promotion content stream failed.", exception);
        }
    }

    private static async Task<string?> ReadProblemCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using Stream content = await response.Content.ReadAsStreamAsync(cancellationToken);
            using JsonDocument problem = await JsonDocument.ParseAsync(
                content,
                cancellationToken: cancellationToken);
            return problem.RootElement.TryGetProperty("code", out JsonElement code) &&
                code.ValueKind == JsonValueKind.String
                    ? code.GetString()
                    : null;
        }
        catch (JsonException exception)
        {
            throw new PromotionSourceTransportException(
                "Slicer promotion content returned an invalid problem response.",
                exception);
        }
    }

    private sealed class PendingResponseOwnership : IDisposable
    {
        private CancellationTokenSource? _timeoutSource;
        private HttpResponseMessage? _response;

        public PendingResponseOwnership(TimeSpan timeout, CancellationToken cancellationToken)
        {
            _timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _timeoutSource.CancelAfter(timeout);
        }

        public CancellationToken Token =>
            _timeoutSource?.Token ?? throw new ObjectDisposedException(nameof(PendingResponseOwnership));

        public async Task<HttpResponseMessage> SendAsync(
            HttpClient httpClient,
            HttpRequestMessage request,
            HttpCompletionOption completionOption)
        {
            _response = await httpClient.SendAsync(request, completionOption, Token);
            return _response;
        }

        public PromotionArtifactContent TransferToContent(Stream stream, long expectedSizeBytes)
        {
            HttpResponseMessage response =
                _response ?? throw new InvalidOperationException("No response is available to transfer.");
            CancellationTokenSource timeoutSource =
                _timeoutSource ?? throw new ObjectDisposedException(nameof(PendingResponseOwnership));
            PromotionArtifactContent content = PromotionArtifactContent.Create(
                stream,
                expectedSizeBytes,
                () =>
                {
                    response.Dispose();
                    timeoutSource.Dispose();
                    return ValueTask.CompletedTask;
                },
                timeoutSource.Token);
            _response = null;
            _timeoutSource = null;
            return content;
        }

        public void Dispose()
        {
            _response?.Dispose();
            _timeoutSource?.Dispose();
            _response = null;
            _timeoutSource = null;
        }
    }
}

/// <summary>Validated settings for the split-host promotion content adapter.</summary>
/// <param name="BaseUrl">Internal slicer-host base URL.</param>
/// <param name="StreamTimeout">Request timeout, which must remain below the 300-second proxy timeout.</param>
public sealed record SlicerHostPromotionOptions(Uri BaseUrl, TimeSpan StreamTimeout);
