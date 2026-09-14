using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>Pure validation and canonical identity for semantic motion intents.</summary>
public static class PrinterControlIntent
{
    public static bool IsValid(PrinterControlRequest request)
    {
        if (!Enum.IsDefined(request.Kind) ||
            new[] { request.X, request.Y, request.Z, request.F }.Any(value => value.HasValue && !double.IsFinite(value.Value)))
        {
            return false;
        }

        return request.Kind is PrinterControlKind.HomeAll or PrinterControlKind.HomeXY or PrinterControlKind.HomeZ
            ? request.X is null && request.Y is null && request.Z is null && request.F is null
            : (request.X.HasValue || request.Y.HasValue || request.Z.HasValue) && (request.F is null || request.F > 0);
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

    private static double? NormalizeZero(double? value) => value == 0 ? 0 : value;
}
