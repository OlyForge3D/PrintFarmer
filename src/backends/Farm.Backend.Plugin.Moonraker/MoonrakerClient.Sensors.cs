using System.Net.Http.Json;
using System.Text.Json;
using Farm.Infrastructure.Domain;
using Microsoft.Extensions.Logging;

namespace Farm.Backend.Plugin.Moonraker;

public partial class MoonrakerClient
{
    private async Task<JsonDocument?> TryParseSensorDiscoveryAsync(HttpResponseMessage response, string sensorId, CancellationToken ct)
    {
        try
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Moonraker sensor discovery for {SensorId} returned invalid JSON", sensorId);
            return null;
        }
    }

    /// <summary>Queries authenticated sensor/configuration snapshots using the same mapper as subscriptions.</summary>
    internal async Task<MoonrakerSensorTracker?> QuerySensorStateAsync(
        string baseUrl,
        PrinterCredential? credential,
        CancellationToken ct)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeouts.StatusPollTimeout);
        Uri root = new(baseUrl.TrimEnd('/') + "/");
        try
        {
            using HttpRequestMessage listRequest = CreateSafetyDiscoveryRequest(new Uri(root, "printer/objects/list"), credential);
            using HttpResponseMessage listResponse = await _http.SendAsync(listRequest, timeout.Token).ConfigureAwait(false);
            if (!listResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("Moonraker sensor discovery returned {StatusCode}", (int)listResponse.StatusCode);
                return null;
            }

            using JsonDocument? list = await TryParseSensorDiscoveryAsync(listResponse, "telemetry", timeout.Token).ConfigureAwait(false);
            if (list?.RootElement.ValueKind != JsonValueKind.Object ||
                !list.RootElement.TryGetProperty("result", out JsonElement result) ||
                !MoonrakerSubscriptionService.TryReadObjectNames(result, out List<string> names))
            {
                _logger.LogWarning("Moonraker sensor discovery returned a malformed object list");
                return null;
            }

            Dictionary<string, string[]?> objects = names.Where(name => name != "menu")
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(name => name, _ => (string[]?)null, StringComparer.Ordinal);
            objects["configfile"] = ["settings"];
            objects["toolhead"] = ["extruder"];
            using HttpRequestMessage query = CreateSafetyDiscoveryRequest(new Uri(root, "printer/objects/query"), credential);
            query.Method = HttpMethod.Post;
            query.Content = JsonContent.Create(new { objects });
            using HttpResponseMessage response = await _http.SendAsync(query, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Moonraker sensor query returned {StatusCode}", (int)response.StatusCode);
                return null;
            }

            using JsonDocument? document = await TryParseSensorDiscoveryAsync(response, "telemetry", timeout.Token).ConfigureAwait(false);
            if (document?.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("result", out JsonElement queryResult) ||
                queryResult.ValueKind != JsonValueKind.Object ||
                !queryResult.TryGetProperty("status", out JsonElement status) || status.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning("Moonraker sensor query returned malformed status");
                return null;
            }

            MoonrakerSensorTracker tracker = new();
            tracker.Discover(names);
            _ = tracker.Apply(status, DateTime.UtcNow);
            return tracker;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Moonraker sensor discovery timed out");
            return null;
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "Moonraker sensor discovery failed");
            return null;
        }
    }
}
