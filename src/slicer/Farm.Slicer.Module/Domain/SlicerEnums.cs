namespace Farm.Slicer.Module.Domain;

// ===========================================================================
// Slicer Module Enumerations
// ===========================================================================
// NOTE: FileHealthStatus and ModelFileFormat have been moved to
// Farm.Infrastructure.Domain.StoredFileEnums.  Consumer files import them
// via 'using Farm.Infrastructure.Domain;'.
// ===========================================================================
#region Slicing and Profiles

/// <summary>
/// Supported slicer engine types for G-code generation.
/// </summary>
public enum SlicerType
{
    /// <summary>Prusa Research's PrusaSlicer.</summary>
    PrusaSlicer = 0,

    /// <summary>OrcaSlicer - Bambu Lab's fork with advanced features.</summary>
    OrcaSlicer = 1,

    /// <summary>Ultimaker Cura slicer.</summary>
    Cura = 2,

    /// <summary>SuperSlicer - community fork of PrusaSlicer.</summary>
    SuperSlicer = 3,
}

/// <summary>
/// Print quality presets affecting layer height and detail level.
/// </summary>
public enum ProfileQuality
{
    /// <summary>Fast printing with lower detail - suitable for prototypes.</summary>
    Draft = 0,

    /// <summary>Balanced speed and quality - suitable for most prints.</summary>
    Standard = 1,

    /// <summary>High detail with slower print speeds - for display models.</summary>
    Fine = 2,
}

#endregion
