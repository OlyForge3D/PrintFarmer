using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>Pure validation, canonical identity and bounded motion script construction.</summary>
public static class PrinterControlIntent
{
    private static readonly string DecimalFormat = "0." + new string('#', 339);

    public static bool IsValid(PrinterControlRequest request)
    {
        if (!Enum.IsDefined(request.Kind) ||
            new[] { request.X, request.Y, request.Z, request.F }.Any(value => value.HasValue && !double.IsFinite(value.Value)))
        {
            return false;
        }

        return request.Kind is PrinterControlKind.HomeAll or PrinterControlKind.HomeXY or PrinterControlKind.HomeZ
            ? request.X is null && request.Y is null && request.Z is null && request.F is null
            : (request.Kind == PrinterControlKind.MoveTo
                ? request.X.HasValue && request.Y.HasValue && request.Z.HasValue
                : request.X.HasValue || request.Y.HasValue || request.Z.HasValue) && (request.F is null || request.F > 0);
    }

    public static string Normalize(PrinterControlRequest request) =>
        JsonSerializer.Serialize(request with
        {
            X = NormalizeZero(request.X),
            Y = NormalizeZero(request.Y),
            Z = NormalizeZero(request.Z),
            F = NormalizeZero(request.F),
        });

    public static string ConfigurationIdentity(Printer printer) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            printer.Backend,
            printer.BackendUrl,
            printer.ApiKey,
            printer.ConfigurationRevision,
            printer.IsEnabled,
            printer.InMaintenance,
        }))));

    public static string BuildScript(PrinterControlRequest request)
    {
        if (!IsValid(request))
        {
            throw new ArgumentException("Invalid motion intent.", nameof(request));
        }

        string command = request.Kind switch
        {
            PrinterControlKind.HomeAll => "G28",
            PrinterControlKind.HomeXY => "G28 X Y",
            PrinterControlKind.HomeZ => "G28 Z",
            _ => "G1" + Axis("X", request.X) + Axis("Y", request.Y) + Axis("Z", request.Z) + Axis("F", request.F),
        };
        return request.Kind switch
        {
            // Restore the preceding coordinate/extrusion modes without a restore movement.
            PrinterControlKind.Jog => $"SAVE_GCODE_STATE NAME=printfarmer_motion\nG91\n{command}\nRESTORE_GCODE_STATE NAME=printfarmer_motion MOVE=0\nM400",
            PrinterControlKind.MoveTo => $"SAVE_GCODE_STATE NAME=printfarmer_motion\nG90\n{command}\nRESTORE_GCODE_STATE NAME=printfarmer_motion MOVE=0\nM400",
            _ => $"{command}\nM400",
        };
    }

    private static double? NormalizeZero(double? value) => value == 0 ? 0 : value;

    private static string Axis(string name, double? value) =>
        value.HasValue ? $" {name}{value.Value.ToString(DecimalFormat, CultureInfo.InvariantCulture)}" : string.Empty;
}
