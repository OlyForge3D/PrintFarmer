using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>Plugin-owned declarations for implemented semantic controls, not firmware safety guarantees.</summary>
public interface ISupportsPrinterControlCapabilities
{
    /// <summary>Controls implemented by this plugin. Missing declarations are unavailable.</summary>
    PrinterControlCapabilities ControlCapabilities { get; }
}

/// <summary>Fine-grained transport support; verified safety is discovered separately for each printer.</summary>
public sealed record PrinterControlCapabilities
{
    public bool SupportsHoming { get; init; }

    public bool SupportsHomingXY { get; init; }

    public bool SupportsHomingZ { get; init; }

    public bool SupportsHotendTemperature { get; init; }

    public bool SupportsBedTemperature { get; init; }

    public bool SupportsDisableMotors { get; init; }

    public bool SupportsExtrusion { get; init; }

    public IReadOnlyList<string> SupportedAxes { get; init; } = [];
}

/// <summary>Semantic stepper-motor control implemented by the backend plugin.</summary>
public interface ISupportsMotorControl
{
    /// <summary>Disables motors using the backend's own command transport.</summary>
    Task<bool> DisableMotorsAsync(string baseUrl, PrinterCredential? credential, CancellationToken ct = default);
}

/// <summary>Semantic relative extrusion implemented by the backend plugin.</summary>
public interface ISupportsExtrusionControl
{
    /// <summary>Extrudes a signed distance in millimetres at the requested feedrate in millimetres/minute.</summary>
    Task<bool> ExtrudeAsync(string baseUrl, double distanceMm, int feedrateMmPerMinute, PrinterCredential? credential, CancellationToken ct = default);
}
