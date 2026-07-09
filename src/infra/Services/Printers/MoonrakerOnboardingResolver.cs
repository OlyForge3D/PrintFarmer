using System.Net.Http;
using System.Text.Json;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Resolves Moonraker onboarding endpoints for stock Klipper and Snapmaker U1-style deployments.
/// </summary>
public static class MoonrakerOnboardingResolver
{
    public const int DefaultMoonrakerPort = 7125;
    public const int SnapmakerU1MoonrakerPort = 80;
    public const string PrinterInfoPath = "/printer/info";
    public const string MachineSystemInfoPath = "/machine/system_info";
    public const string SnapmakerManufacturerName = "Snapmaker";
    public const string SnapmakerU1ModelName = "Snapmaker U1";

    /// <summary>
    /// Tries known Moonraker onboarding endpoints in order, preserving stock Moonraker behavior first.
    /// </summary>
    public static async Task<MoonrakerEndpointResolution?> ResolveAsync(
        HttpClient client,
        Uri serverUrl,
        int? preferredBackendPort,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(serverUrl);

        foreach (MoonrakerEndpointCandidate candidate in GetEndpointCandidates(preferredBackendPort))
        {
            Uri endpoint = BuildEndpointUri(serverUrl, candidate.BackendPort, candidate.EndpointPath);
            try
            {
                using HttpResponseMessage response = await client.GetAsync(endpoint, cancellationToken);
                string content = await response.Content.ReadAsStringAsync(cancellationToken);
                (bool valid, int confidence, string reason) = candidate.EndpointPath == MachineSystemInfoPath
                    ? await ValidateMachineSystemInfoResponseAsync(response, content)
                    : await ValidatePrinterInfoResponseAsync(response, content);

                if (!valid)
                {
                    continue;
                }

                SnapmakerU1Metadata? u1Metadata = candidate.EndpointPath == MachineSystemInfoPath
                    ? ExtractSnapmakerU1Metadata(content)
                    : null;

                return new MoonrakerEndpointResolution(
                    candidate.BackendPort,
                    candidate.EndpointPath,
                    content,
                    confidence,
                    u1Metadata is not null ? "Snapmaker U1 Moonraker detected via /machine/system_info" : reason,
                    u1Metadata?.DeviceName,
                    u1Metadata?.Manufacturer,
                    u1Metadata?.Model,
                    u1Metadata is not null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
            }
        }

        return null;
    }

    /// <summary>
    /// Validates the standard Moonraker /printer/info response.
    /// </summary>
    public static Task<(bool IsValid, int ConfidenceScore, string Reason)> ValidatePrinterInfoResponseAsync(
        HttpResponseMessage response,
        string content)
    {
        if (!response.IsSuccessStatusCode)
        {
            return Task.FromResult((false, 0, "HTTP error"));
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return Task.FromResult((false, 0, "Empty response"));
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(content);
            JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("result", out JsonElement resultElem))
            {
                return Task.FromResult((false, 0, "Missing 'result' wrapper"));
            }

            bool hasStateMessage = resultElem.TryGetProperty("state_message", out _);
            bool hasKlipperPath = resultElem.TryGetProperty("klipper_path", out _);
            bool hasHostname = resultElem.TryGetProperty("hostname", out _);

            int fieldCount = (hasStateMessage ? 1 : 0) + (hasKlipperPath ? 1 : 0) + (hasHostname ? 1 : 0);
            if (fieldCount == 0)
            {
                return Task.FromResult((false, 0, "No Klipper fields found"));
            }

            int confidence = fieldCount == 3 ? 100 : fieldCount == 2 ? 90 : 75;
            return Task.FromResult((true, confidence, $"Moonraker detected ({fieldCount}/3 fields)"));
        }
        catch
        {
            return Task.FromResult((false, 0, "Invalid JSON"));
        }
    }

    /// <summary>
    /// Validates Moonraker /machine/system_info, including the Snapmaker U1 product_info shape.
    /// </summary>
    public static Task<(bool IsValid, int ConfidenceScore, string Reason)> ValidateMachineSystemInfoResponseAsync(
        HttpResponseMessage response,
        string content)
    {
        if (!response.IsSuccessStatusCode)
        {
            return Task.FromResult((false, 0, "HTTP error"));
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return Task.FromResult((false, 0, "Empty response"));
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(content);
            if (!TryGetSystemInfo(doc.RootElement, out JsonElement systemInfo))
            {
                return Task.FromResult((false, 0, "Missing system_info"));
            }

            SnapmakerU1Metadata? u1Metadata = ExtractSnapmakerU1Metadata(systemInfo);
            if (u1Metadata is not null)
            {
                return Task.FromResult((true, 100, "Snapmaker U1 Moonraker detected"));
            }

            bool hasProductInfo = systemInfo.TryGetProperty("product_info", out JsonElement productInfo) &&
                                  productInfo.ValueKind == JsonValueKind.Object;
            bool hasNetwork = systemInfo.TryGetProperty("network", out JsonElement network) &&
                              network.ValueKind == JsonValueKind.Object;

            int confidence = hasProductInfo && hasNetwork ? 90 : hasProductInfo || hasNetwork ? 75 : 70;
            return Task.FromResult((true, confidence, "Moonraker system_info detected"));
        }
        catch
        {
            return Task.FromResult((false, 0, "Invalid JSON"));
        }
    }

    /// <summary>
    /// Builds a URI for a Moonraker endpoint at a specific backend port.
    /// </summary>
    public static Uri BuildEndpointUri(Uri serverUrl, int backendPort, string endpointPath)
    {
        ArgumentNullException.ThrowIfNull(serverUrl);

        return new UriBuilder(serverUrl)
        {
            Port = backendPort,
            Path = endpointPath.TrimStart('/'),
            Query = string.Empty
        }.Uri;
    }

    /// <summary>
    /// Applies Snapmaker U1 catalog metadata when system_info product data identifies the printer.
    /// </summary>
    public static SnapmakerU1Metadata? ExtractSnapmakerU1Metadata(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(content);
            return TryGetSystemInfo(doc.RootElement, out JsonElement systemInfo)
                ? ExtractSnapmakerU1Metadata(systemInfo)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Enumerates Moonraker probe candidates, starting with an explicitly supplied backend port when present.
    /// </summary>
    public static IEnumerable<MoonrakerEndpointCandidate> GetEndpointCandidates(int? preferredBackendPort)
    {
        HashSet<string> yielded = [];

        if (preferredBackendPort.HasValue)
        {
            yield return new MoonrakerEndpointCandidate(preferredBackendPort.Value, PrinterInfoPath);
            yielded.Add($"{preferredBackendPort.Value}:{PrinterInfoPath}");

            if (preferredBackendPort.Value == SnapmakerU1MoonrakerPort)
            {
                yield return new MoonrakerEndpointCandidate(SnapmakerU1MoonrakerPort, MachineSystemInfoPath);
                yielded.Add($"{SnapmakerU1MoonrakerPort}:{MachineSystemInfoPath}");
            }
        }

        foreach (MoonrakerEndpointCandidate candidate in new[]
                 {
                     new MoonrakerEndpointCandidate(DefaultMoonrakerPort, PrinterInfoPath),

                     // Port 80 /machine/system_info is inferred from SnapCon/U1Hub; real U1 hardware still needs verification.
                     new MoonrakerEndpointCandidate(SnapmakerU1MoonrakerPort, MachineSystemInfoPath)
                 })
        {
            if (yielded.Add($"{candidate.BackendPort}:{candidate.EndpointPath}"))
            {
                yield return candidate;
            }
        }
    }

    private static SnapmakerU1Metadata? ExtractSnapmakerU1Metadata(JsonElement systemInfo)
    {
        if (!systemInfo.TryGetProperty("product_info", out JsonElement productInfo) ||
            productInfo.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? deviceName = TryGetString(productInfo, "device_name");
        string? productName = TryGetString(productInfo, "product_name");
        string? model = TryGetString(productInfo, "model");

        // SnapCon/U1Hub prove port-80 /machine/system_info use, but exact stock-U1
        // product_info keys are inferred rather than real-hardware verified. Scan all
        // string metadata values so manufacturer/model split across fields still matches.
        string combined = string.Join(' ', EnumerateStringValues(productInfo));

        if (!combined.Contains("snapmaker", StringComparison.OrdinalIgnoreCase) ||
            !combined.Contains("u1", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new SnapmakerU1Metadata(
            FirstNonWhiteSpace(deviceName, productName, model) ?? SnapmakerU1ModelName,
            SnapmakerManufacturerName,
            SnapmakerU1ModelName);
    }

    private static IEnumerable<string> EnumerateStringValues(JsonElement element)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                string? value = property.Value.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    yield return value;
                }
            }
        }
    }

    private static string? FirstNonWhiteSpace(params string?[] values)
    {
        return values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
    }

    private static bool TryGetSystemInfo(JsonElement root, out JsonElement systemInfo)
    {
        systemInfo = default;
        return root.TryGetProperty("result", out JsonElement result) &&
               result.ValueKind == JsonValueKind.Object &&
               result.TryGetProperty("system_info", out systemInfo) &&
               systemInfo.ValueKind == JsonValueKind.Object;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}

/// <summary>
/// A port/path pair to try while resolving a Moonraker endpoint.
/// </summary>
/// <param name="BackendPort">Backend API port to probe.</param>
/// <param name="EndpointPath">Endpoint path to request.</param>
public sealed record MoonrakerEndpointCandidate(int BackendPort, string EndpointPath);

/// <summary>
/// Result of a successful Moonraker onboarding endpoint probe.
/// </summary>
/// <param name="BackendPort">Resolved backend API port.</param>
/// <param name="EndpointPath">Endpoint that validated the printer.</param>
/// <param name="ResponseContent">Raw response content used for follow-up metadata extraction.</param>
/// <param name="ConfidenceScore">Confidence score for discovery ranking.</param>
/// <param name="Reason">Human-readable reason for the match.</param>
/// <param name="DeviceName">Device name extracted from product metadata, when available.</param>
/// <param name="Manufacturer">Catalog manufacturer name, when recognized.</param>
/// <param name="Model">Catalog model name, when recognized.</param>
/// <param name="IsSnapmakerU1">Whether product metadata identified the printer as a Snapmaker U1.</param>
public sealed record MoonrakerEndpointResolution(
    int BackendPort,
    string EndpointPath,
    string ResponseContent,
    int ConfidenceScore,
    string Reason,
    string? DeviceName,
    string? Manufacturer,
    string? Model,
    bool IsSnapmakerU1);

/// <summary>
/// Recognized Snapmaker U1 metadata extracted from Moonraker system_info.
/// </summary>
/// <param name="DeviceName">Device display name.</param>
/// <param name="Manufacturer">Catalog manufacturer name.</param>
/// <param name="Model">Catalog model name.</param>
public sealed record SnapmakerU1Metadata(string? DeviceName, string Manufacturer, string Model);
