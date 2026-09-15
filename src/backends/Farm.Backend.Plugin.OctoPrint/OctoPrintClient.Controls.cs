using System.Globalization;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.OctoPrint;

public partial class OctoPrintClient : ISupportsPrinterControlCapabilities, ISupportsEmergencyStop, ISupportsZOffsetCalibration
{
    public PrinterControlCapabilities ControlCapabilities { get; } = new()
    {
        SupportsHoming = true,
        SupportsHomingXY = true,
        SupportsHomingZ = true,
        SupportsHotendTemperature = true,
        SupportsBedTemperature = true,
        SupportedAxes = Array.AsReadOnly(new[] { "x", "y", "z" }),
    };

    /// <summary>Preserves OctoPrint's M112 command path; unlike Moonraker this does not guarantee queue bypass.</summary>
    public Task<bool> EmergencyStopAsync(string baseUrl, PrinterCredential? credential, CancellationToken ct = default) =>
        SendControlCommandAsync(baseUrl, "M112", credential, ct);

    /// <summary>Sends the legacy Marlin sequence without asserting the connected firmware supports persistence.</summary>
    public async Task<bool> SaveZOffsetAsync(string baseUrl, decimal offsetMm, PrinterCredential? credential, CancellationToken ct = default)
    {
        if (offsetMm is < -5 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(offsetMm));
        }

        string command = string.Create(CultureInfo.InvariantCulture, $"M851 Z{offsetMm:F3}");
        return await SendControlCommandAsync(baseUrl, command, credential, ct).ConfigureAwait(false) &&
            await SendControlCommandAsync(baseUrl, "M500", credential, ct).ConfigureAwait(false);
    }

    private async Task<bool> SendControlCommandAsync(string baseUrl, string command, PrinterCredential? credential, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeouts.CommandTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{NormalizeBaseUrl(baseUrl)}/api/printer/command");
        if (credential?.HasApiKey == true)
        {
            request.Headers.Add("X-Api-Key", credential.ApiKey);
        }

        request.Content = new StringContent(JsonSerializer.Serialize(new { command }), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }
}
