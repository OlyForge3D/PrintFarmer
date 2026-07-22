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

        bool hasExplicitUrlPort = !serverUrl.IsDefaultPort;
        bool hasCustomPreferredPort = preferredBackendPort.HasValue && preferredBackendPort.Value != DefaultMoonrakerPort;
        int? portToProbe = hasExplicitUrlPort
            ? serverUrl.Port
            : hasCustomPreferredPort
                ? preferredBackendPort
                : null;
        bool isAuthoritativePort = hasExplicitUrlPort || hasCustomPreferredPort;

        foreach (MoonrakerEndpointCandidate candidate in GetEndpointCandidates(portToProbe, isAuthoritativePort))
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

            if (ExtractSnapmakerU1Metadata(systemInfo) is not null)
            {
                return Task.FromResult((true, 100, "Snapmaker U1 Moonraker detected"));
            }

            return Task.FromResult((false, 0, "Moonraker system_info did not identify a Snapmaker U1"));
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
        bool isAuthoritativePort = preferredBackendPort.HasValue && preferredBackendPort.Value != DefaultMoonrakerPort;

        return GetEndpointCandidates(preferredBackendPort, isAuthoritativePort);
    }

    private static IEnumerable<MoonrakerEndpointCandidate> GetEndpointCandidates(
        int? preferredBackendPort,
        bool isAuthoritativePort)
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

            if (isAuthoritativePort)
            {
                yield break;
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
        string? manufacturer = FirstNonWhiteSpace(
            TryGetString(productInfo, "manufacturer"),
            TryGetString(productInfo, "vendor"),
            TryGetString(productInfo, "brand"));
        string? machineType = TryGetString(productInfo, "machine_type");
        string? productName = TryGetString(productInfo, "product_name");
        string? model = TryGetString(productInfo, "model");
        string? productModel = TryGetString(productInfo, "product_model");

        // SnapCon/U1Hub prove port-80 /machine/system_info use, but exact stock-U1
        // product_info field names are inferred rather than real-hardware verified.
        // Only model/product identity fields participate so serial/firmware text cannot
        // accidentally turn a non-U1 Snapmaker into a U1 catalog match.
        string?[] identityFields = [machineType, productName, model, productModel];
        bool hasSnapmaker = ContainsIgnoreCase(manufacturer, SnapmakerManufacturerName) ||
                            identityFields.Any(static value => ContainsIgnoreCase(value, SnapmakerManufacturerName));
        bool hasU1Model = identityFields.Any(ContainsU1Token);

        if (!hasSnapmaker || !hasU1Model)
        {
            return null;
        }

        return new SnapmakerU1Metadata(
            FirstNonWhiteSpace(deviceName, productName, model, productModel) ?? SnapmakerU1ModelName,
            SnapmakerManufacturerName,
            SnapmakerU1ModelName);
    }

    private static bool ContainsIgnoreCase(string? value, string expected)
    {
        return value?.Contains(expected, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool ContainsU1Token(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        ReadOnlySpan<char> span = value.AsSpan();
        for (int i = 0; i < span.Length - 1; i++)
        {
            if (char.ToUpperInvariant(span[i]) == 'U' &&
                span[i + 1] == '1' &&
                IsTokenBoundary(span, i - 1) &&
                IsTokenBoundary(span, i + 2))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTokenBoundary(ReadOnlySpan<char> value, int index)
    {
        return index < 0 || index >= value.Length || !char.IsLetterOrDigit(value[index]);
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
