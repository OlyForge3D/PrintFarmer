using System;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// Version information for a specific printer backend.
/// Values are best-effort and may be null/empty when not available.
/// </summary>
public sealed record PrinterVersionInfoDto(
    Guid PrinterId,
    PrinterBackend Backend,
    bool Supported,
    string? FirmwareVersion,
    string? BackendVersion,
    string? ApiVersion,
    DateTime RetrievedAtUtc,
    string? Message = null);
