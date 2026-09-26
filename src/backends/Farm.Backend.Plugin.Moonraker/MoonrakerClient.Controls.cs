using System.Globalization;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.Moonraker;

public partial class MoonrakerClient : ISupportsPrinterControlCapabilities, ISupportsMotorControl, ISupportsExtrusionControl, ISupportsMmuControl, ISupportsZOffsetCalibration
{
    public PrinterControlCapabilities ControlCapabilities { get; } = new()
    {
        SupportsHoming = true,
        SupportsHomingXY = true,
        SupportsHomingZ = true,
        SupportsHotendTemperature = true,
        SupportsBedTemperature = true,
        SupportsDisableMotors = true,
        SupportsExtrusion = true,
        SupportedAxes = Array.AsReadOnly(new[] { "x", "y", "z" }),
    };

    public Task<bool> DisableMotorsAsync(string baseUrl, PrinterCredential? credential, CancellationToken ct = default) =>
        SendControlScriptAsync(baseUrl, "M84", credential, ct);

    /// <summary>Sends the legacy Klipper sequence; SAVE_CONFIG alone does not prove SET_GCODE_OFFSET persistence.</summary>
    public async Task<bool> SaveZOffsetAsync(string baseUrl, decimal offsetMm, PrinterCredential? credential, CancellationToken ct = default)
    {
        if (offsetMm is < -5 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(offsetMm));
        }

        string command = string.Create(CultureInfo.InvariantCulture, $"SET_GCODE_OFFSET Z={offsetMm:F3}");
        return await SendControlScriptAsync(baseUrl, command, credential, ct).ConfigureAwait(false) &&
            await SendControlScriptAsync(baseUrl, "SAVE_CONFIG", credential, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> ExecuteMmuAsync(string baseUrl, MmuControlRequest request, PrinterCredential? credential, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendControlScriptAsync(baseUrl, BuildMmuCommand(request), credential, ct);
    }

    private static string BuildMmuCommand(MmuControlRequest request)
    {
        string? command = request.Protocol switch
        {
            MmuControlProtocol.HappyHare => request.Action switch
            {
                MmuControlAction.ChangeTool when request.Tool is >= 0 and <= 16 =>
                    string.Create(CultureInfo.InvariantCulture, $"MMU_CHANGE_TOOL TOOL={request.Tool}"),
                MmuControlAction.SelectTool when request.Tool is >= 0 and <= 16 =>
                    string.Create(CultureInfo.InvariantCulture, $"MMU_SELECT_TOOL TOOL={request.Tool}"),
                MmuControlAction.Home => "MMU_HOME",
                MmuControlAction.Recover => "MMU_RECOVER",
                MmuControlAction.Load => "MMU_LOAD",
                MmuControlAction.Eject => "MMU_EJECT",
                _ => null,
            },
            MmuControlProtocol.Qidibox when request.GateIndex is >= 0 and <= 16 => request.Action switch
            {
                MmuControlAction.Load => string.Create(CultureInfo.InvariantCulture, $"T{request.GateIndex}"),
                MmuControlAction.Unload => string.Create(CultureInfo.InvariantCulture, $"UNLOAD_T{request.GateIndex}"),
                MmuControlAction.Eject => string.Create(CultureInfo.InvariantCulture, $"EJECT_T{request.GateIndex}"),
                _ => null,
            },
            MmuControlProtocol.Afc when request.LaneName is { Length: > 0 and <= 64 } lane &&
                lane.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-') => request.Action switch
                {
                    MmuControlAction.Load => $"CHANGE_TOOL LANE={lane}",
                    MmuControlAction.Unload => $"TOOL_UNLOAD LANE={lane}",
                    _ => null,
                },
            _ => null,
        };
        return command ?? throw new ArgumentException("Unsupported or invalid MMU action.", nameof(request));
    }

    public Task<bool> ExtrudeAsync(string baseUrl, double distanceMm, int feedrateMmPerMinute, PrinterCredential? credential, CancellationToken ct = default)
    {
        if (!double.IsFinite(distanceMm))
        {
            throw new ArgumentOutOfRangeException(nameof(distanceMm));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(feedrateMmPerMinute);

        string script = string.Create(
            CultureInfo.InvariantCulture,
            $"M83\nG1 E{distanceMm:0.###} F{feedrateMmPerMinute}\nM82");
        return SendControlScriptAsync(baseUrl, script, credential, ct);
    }

    private Task<bool> SetControlTemperaturesAsync(string baseUrl, double? hotend, double? bed, PrinterCredential? credential, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        List<string> commands = [];
        if (hotend is not null)
        {
            commands.Add(string.Create(CultureInfo.InvariantCulture, $"M104 S{hotend:0}"));
        }

        if (bed is not null)
        {
            commands.Add(string.Create(CultureInfo.InvariantCulture, $"M140 S{bed:0}"));
        }

        return commands.Count == 0
            ? Task.FromResult(true)
            : SendControlScriptAsync(baseUrl, string.Join("\n", commands), credential, ct);
    }

    private async Task<bool> SendControlScriptAsync(string baseUrl, string script, PrinterCredential? credential, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeouts.CommandTimeout);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), "printer/gcode/script"));
        if (credential?.HasApiKey == true)
        {
            request.Headers.Add("X-Api-Key", credential.ApiKey);
        }

        request.Content = new StringContent(JsonSerializer.Serialize(new { script }), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            throw new PrinterBackendBusyException("Moonraker refused the control command (409 Conflict).");
        }

        if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable &&
            IsMoonrakerBusyPrintingBody(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false)))
        {
            throw new PrinterBackendBusyException("Moonraker refused the control command (503 printing-busy).");
        }

        return response.IsSuccessStatusCode;
    }
}
