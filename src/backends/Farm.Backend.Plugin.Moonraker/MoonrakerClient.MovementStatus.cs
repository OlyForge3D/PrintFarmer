using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Backend.Plugin.Moonraker;

public partial class MoonrakerClient
{
    /// <inheritdoc />
    public async Task<PrinterStatusDto> GetMovementStatusAsync(Printer printer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(printer);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeouts.StatusPollTimeout);
        using HttpRequestMessage request = CreateSafetyDiscoveryRequest(
            new Uri(
                new Uri(printer.BackendUrl.TrimEnd('/') + "/"),
                "printer/objects/query?webhooks=state&print_stats=state&toolhead=homed_axes&gcode_move=position,gcode_position"),
            printer.Credential);
        using HttpResponseMessage response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
        JsonElement status = document.RootElement.GetProperty("result").GetProperty("status");
        string? readiness = status.GetProperty("webhooks").GetProperty("state").GetString();
        string? printState = status.GetProperty("print_stats").GetProperty("state").GetString();
        string? homed = status.GetProperty("toolhead").GetProperty("homed_axes").GetString();
        bool hasPosition = TryGetFinitePosition(status, "gcode_move", "gcode_position", out double x, out double y, out double z);
        bool hasMachinePosition = TryGetFinitePosition(status, "gcode_move", "position", out double machineX, out double machineY, out double machineZ);
        DateTime observed = DateTime.UtcNow;
        return new PrinterStatusDto(printer.Id, readiness == "ready",
            readiness == "ready" ? printState : readiness,
            X: hasPosition ? x : null,
            Y: hasPosition ? y : null,
            Z: hasPosition ? z : null,
            HomedAxes: homed,
            SafetyTelemetry: PrinterSafetyTelemetryDto.Empty with
            {
                HomedAxes = new SafetyAxesTelemetryFactDto(
                    homed?.Where(char.IsAsciiLetter).Select(axis => char.ToLowerInvariant(axis).ToString()).ToArray(),
                    homed is null ? null : observed, PrinterSafetyTelemetryDto.DefaultStaleAfterSeconds, "moonraker:toolhead.homed_axes"),
                CoordinateOriginOffsetMm = new SafetyVectorTelemetryFactDto(
                    hasPosition && hasMachinePosition ? new SafetyVector3Dto(machineX - x, machineY - y, machineZ - z) : null,
                    hasPosition && hasMachinePosition ? observed : null, PrinterSafetyTelemetryDto.DefaultStaleAfterSeconds, "moonraker:gcode_move.position-gcode_position"),
            });
    }
}
