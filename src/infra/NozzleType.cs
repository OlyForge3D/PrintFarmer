namespace Farm.Infrastructure;

/// <summary>
/// Nozzle material type affecting heat resistance and material compatibility.
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

    /// <summary>
    /// Unknown or unspecified nozzle type.
    /// </summary>
    Unknown = 99
}
