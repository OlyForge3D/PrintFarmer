using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Farm.Infrastructure.Network;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.FailureDetection;

/// <summary>Shared, egress-guarded prediction endpoint probe for administration and system health.</summary>
public sealed class ObicoConnectivityProbe(
    IHttpClientFactory httpClientFactory,
    IEgressGuard egressGuard,
    ILogger<ObicoConnectivityProbe> logger) : IObicoConnectivityProbe
{
#pragma warning disable S5332 // Deliberately unreachable compatibility probe URL, not a printer image or credential.
    private const string SnapshotUrl = "http://printfarmer.local/obico-health-probe.jpg";
#pragma warning restore S5332

    /// <inheritdoc />
    public async Task<ObicoConnectivityResult> ProbeAsync(string url, string? apiKey, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        string? error = null;
        try
        {
            EgressCheckResult check = await egressGuard.CheckAsync(url, ct).WaitAsync(ct);
            if (!check.IsAllowed)
            {
                return new(false, stopwatch.ElapsedMilliseconds, check.DenyReason ?? "Configured server URL is not allowed");
            }

            using HttpClient client = httpClientFactory.CreateClient("VettedEgress");
            client.Timeout = TimeSpan.FromSeconds(10);

            // Pin the vetted address, preserving Host for virtual hosting and TLS SNI.
            if (check.ResolvedAddress is not null && check.Uri is not null)
            {
                Uri pinnedUri = EgressGuard.CreatePinnedUri(check.Uri, check.ResolvedAddress);
                client.BaseAddress = new Uri(pinnedUri.ToString().TrimEnd('/') + "/");
                client.DefaultRequestHeaders.Host = check.Uri.IsDefaultPort
                    ? check.Uri.Host
                    : $"{check.Uri.Host}:{check.Uri.Port}";
            }
            else
            {
                client.BaseAddress = new Uri(url.TrimEnd('/') + "/");
            }

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            }

            (bool validated, string? upstreamError) = await TryValidateUpstreamAsync(client, ct);
            error = validated ? upstreamError : await ValidateLegacyAsync(client, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (UriFormatException)
        {
            error = "Configured server URL is invalid";
        }
        catch (HttpRequestException)
        {
            error = "Connection failed";
        }
        catch (TaskCanceledException)
        {
            error = "Request timeout — server did not respond within 10 seconds";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unexpected Obico connectivity probe failure");
            error = "Unexpected health-check failure";
        }

        return new(error is null, stopwatch.ElapsedMilliseconds, error);
    }

    private static async Task<(bool Validated, string? Error)> TryValidateUpstreamAsync(HttpClient client, CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage response = await client.GetAsync($"p/?img={Uri.EscapeDataString(SnapshotUrl)}", ct);
            string body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.BadRequest && IsExpectedProbeSnapshotFailure(body))
                {
                    return (true, null);
                }

                if (ObicoSnapshotFallbackDetector.ShouldFallbackToLegacyUpload(response.StatusCode)
                    || ObicoSnapshotFallbackDetector.ShouldFallbackBecauseSnapshotWasUnreachable(response.StatusCode, body))
                {
                    return (false, null);
                }

                return (true, $"Prediction endpoint /p/ returned HTTP {(int)response.StatusCode}");
            }

            return (HasDetectionsArray(body), null);
        }
        catch (HttpRequestException)
        {
            return (true, "Cannot reach prediction endpoint /p/");
        }
    }

    private static async Task<string?> ValidateLegacyAsync(HttpClient client, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "p/")
        {
            Content = new StringContent(string.Empty)
        };
        using HttpResponseMessage response = await client.SendAsync(request, ct);
        return response.IsSuccessStatusCode
            || response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnsupportedMediaType
            ? null
            : $"Prediction endpoint /p/ returned HTTP {(int)response.StatusCode}";
    }

    private static bool HasDetectionsArray(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.EnumerateObject().Any(property =>
                    string.Equals(property.Name, "detections", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Array);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsExpectedProbeSnapshotFailure(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;

            // The upstream deliberately cannot fetch this synthetic URL; no image is submitted.
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("detections", out JsonElement detections)
                && detections.ValueKind == JsonValueKind.Array
                && detections.GetArrayLength() == 0
                && root.TryGetProperty("message", out JsonElement message)
                && message.ValueKind == JsonValueKind.String
                && message.GetString() is string text
                && text.StartsWith("Failed to get image ", StringComparison.Ordinal)
                && text.Contains(SnapshotUrl, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
