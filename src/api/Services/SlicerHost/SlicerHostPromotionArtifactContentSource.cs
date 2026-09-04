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

        CancellationTokenSource? timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_streamTimeout);
        HttpResponseMessage? response = null;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutSource.Token);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (response.StatusCode == HttpStatusCode.Forbidden &&
                string.Equals(
                    await ReadProblemCodeAsync(response, timeoutSource.Token),
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

            Stream stream = await response.Content.ReadAsStreamAsync(timeoutSource.Token);
            HttpResponseMessage ownedResponse = response;
            CancellationTokenSource ownedTimeoutSource = timeoutSource;
            response = null;
            timeoutSource = null;
            return PromotionArtifactContent.Create(
                stream,
                expectedSizeBytes,
                () =>
                {
                    ownedResponse.Dispose();
                    ownedTimeoutSource.Dispose();
                    return ValueTask.CompletedTask;
                },
                ownedTimeoutSource.Token);
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
        finally
        {
            response?.Dispose();
            timeoutSource?.Dispose();
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
}

/// <summary>Validated settings for the split-host promotion content adapter.</summary>
/// <param name="BaseUrl">Internal slicer-host base URL.</param>
/// <param name="StreamTimeout">Request timeout, which must remain below the 300-second proxy timeout.</param>
public sealed record SlicerHostPromotionOptions(Uri BaseUrl, TimeSpan StreamTimeout);
