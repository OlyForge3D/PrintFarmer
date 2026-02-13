using System.Text.Json.Serialization;
using Farm.Infrastructure.Json;

namespace Farm.Infrastructure.Domain;

// ============================================================================
// PRINTER & JOB ENUMERATIONS
// Domain enums for printer configuration, job lifecycle, and hardware types.
// ============================================================================
#region Job Lifecycle

/// <summary>
/// Formal job lifecycle states following the pattern:
/// queued → dispatched → processing → (succeeded | failed | cancelled | dead-letter).
/// Used by SliceJob, PrintJob, and other background processing entities.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JobState
{
    /// <summary>Job has been created and is waiting to be assigned.</summary>
    Queued = 0,

    /// <summary>Job has been assigned to a processor but not yet started.</summary>
    Dispatched = 1,

    /// <summary>Job is actively being processed.</summary>
    Processing = 2,

    /// <summary>Job completed successfully (terminal state).</summary>
    Succeeded = 3,

    /// <summary>Job failed during processing (terminal state).</summary>
    Failed = 4,

    /// <summary>Job was cancelled by user or system (terminal state).</summary>
    Cancelled = 5,

    /// <summary>Job failed in an unrecoverable way and cannot be retried (terminal state).</summary>
    DeadLetter = 6
}

#endregion

#region Printer Backend

/// <summary>
/// Printer communication backend/firmware type.
/// Determines which API protocol is used to communicate with the printer.
/// </summary>
/// <remarks>
/// Custom tolerant converter accepts both numeric and string input for backward compatibility.
/// </remarks>
[JsonConverter(typeof(PrinterBackendJsonConverter))]
public enum PrinterBackend
{
    /// <summary>Unknown or unspecified printer backend.</summary>
    Unknown = 0,

    /// <summary>Moonraker API (Klipper firmware).</summary>
    Moonraker = 1,

    /// <summary>PrusaLink API (Prusa firmware).</summary>
    PrusaLink = 2,

    /// <summary>SDCP protocol (Elegoo/Anycubic networked printers).</summary>
    SDCP = 3,

    /// <summary>OctoPrint API (OctoPrint server).</summary>
    OctoPrint = 4
}

/// <summary>
/// Connection state for a printer backend, used by connection health monitoring.
/// Tracks the lifecycle of a connection from initial connect through disconnection.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PrinterConnectionState
{
    /// <summary>Printer is connected and communicating normally.</summary>
    Connected = 0,

    /// <summary>Connection was lost but reconnection is in progress. Controls remain enabled.</summary>
    Reconnecting = 1,

    /// <summary>Printer is confirmed offline after grace period or max retries.</summary>
    Offline = 2,

    /// <summary>Connection is unstable — frequent disconnects/reconnects detected.</summary>
    Degraded = 3
}

#endregion

#region Printer Hardware Types

/// <summary>
/// Printer movement mechanism type defining the kinematic configuration.
/// Affects print speed capabilities and calibration requirements.
/// </summary>
public enum MotionType
{
    /// <summary>Traditional 3-axis Cartesian system with independent XYZ movement.</summary>
    Cartesian = 0,

    /// <summary>CoreXY kinematics where X and Y motors work together for diagonal movement.</summary>
    CoreXY = 1,

    /// <summary>Delta kinematics with 3 towers and effector for precise movement.</summary>
    Delta = 2,

    /// <summary>Unknown or unspecified motion type.</summary>
    Unknown = 99
}

/// <summary>
/// Nozzle material type affecting heat resistance and filament compatibility.
/// Different materials offer trade-offs between thermal conductivity and wear resistance.
/// </summary>
public enum NozzleType
{
    /// <summary>
    /// Standard brass nozzle - good thermal conductivity, not abrasion-resistant.
    /// Best for: PLA, PETG, ABS, TPU.
    /// </summary>
    Brass = 0,

    /// <summary>
    /// Hardened steel nozzle - abrasion-resistant for filled filaments.
    /// Best for: Carbon fiber, glass fiber, metal-filled filaments.
    /// </summary>
    HardenedSteel = 1,

    /// <summary>
    /// Stainless steel nozzle - food-safe and corrosion-resistant.
    /// Best for: Food-safe applications, corrosive environments.
    /// </summary>
    StainlessSteel = 2,

    /// <summary>
    /// Tungsten carbide nozzle - extreme abrasion resistance.
    /// Best for: Highly abrasive filaments, industrial use.
    /// </summary>
    TungstenCarbide = 3,

    /// <summary>
    /// Ruby-tipped or other abrasive-resistant nozzle.
    /// Best for: Long-term use with abrasive filaments.
    /// </summary>
    Abrasive = 4,

    /// <summary>Unknown or unspecified nozzle type.</summary>
    Unknown = 99
}

#endregion
