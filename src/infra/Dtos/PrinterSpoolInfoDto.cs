using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

// Printer spool information for Moonraker printers
/// <summary>
/// Snapshot of active spool information attached to a printer (Moonraker + Spoolman bridge).
/// </summary>
public record PrinterSpoolInfoDto(
    bool HasActiveSpool,
    int? ActiveSpoolId = null,
    string? SpoolName = null,
    string? Material = null,
    string? ColorHex = null,
    string? FilamentName = null,
    string? Vendor = null,
    double? RemainingWeightG = null,
    double? InitialWeightG = null,
    bool? SpoolInUse = null);
