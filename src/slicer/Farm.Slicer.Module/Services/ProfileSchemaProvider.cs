using Farm.Slicer.Module.Dtos;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Provides schema metadata for slicer profile types, enabling schema-driven settings editors.
///
/// Field metadata is compile-time constant (no database/async), but the provider is
/// engine-version-aware: callers can pass an OrcaSlicer engine version (e.g. "2.3.1",
/// "2.4.1") to receive the field set applicable to that engine, with fields renamed to
/// match that engine's key convention when the field went through a version-boundary rename.
///
/// Version filtering rules (issue #578):
///   - When <c>engineVersion</c> is null or unparsable, all fields are returned unfiltered.
///   - A field with <see cref="ProfileFieldMetadata.MinEngineVersion"/> set is dropped when
///     the requested engine is older than that minimum.
///   - A field with <see cref="ProfileFieldMetadata.MaxEngineVersion"/> set is dropped when
///     the requested engine is newer than that maximum.
///   - A field with <see cref="ProfileFieldMetadata.RenamedFromKey"/> is emitted with the
///     old key when the requested engine is older than <see cref="ProfileFieldMetadata.RenamedInVersion"/>.
///
/// The example field-version windows below (wallGenerator, enableArcFitting,
/// legacyPreviewSetting, bedAdhesion) are illustrative of the added/removed/renamed
/// mechanic. Real OrcaSlicer 2.3.1 vs 2.4.1 field research should refine these before
/// consumers depend on the exact windows.
/// </summary>
public static class ProfileSchemaProvider
{
    public static ProfileSchemasResponseDto GetAllSchemas(string? engineVersion = null) => new()
    {
        Process = GetProcessSchema(engineVersion),
        Machine = GetMachineSchema(engineVersion),
        Filament = GetFilamentSchema(engineVersion),
    };

    public static ProfileTypeSchemaDto GetProcessSchema(string? engineVersion = null) => new()
    {
        ProfileType = "process",
        Categories = ["quality", "strength", "speed", "support", "adhesion", "temperature", "other"],
        Fields = ApplyEngineVersion(BuildProcessFields(), engineVersion),
    };

    public static ProfileTypeSchemaDto GetMachineSchema(string? engineVersion = null) => new()
    {
        ProfileType = "machine",
        Categories = ["general", "buildVolume", "extruder", "retraction", "bed", "gcode", "motion"],
        Fields = ApplyEngineVersion(BuildMachineFields(), engineVersion),
    };

    public static ProfileTypeSchemaDto GetFilamentSchema(string? engineVersion = null) => new()
    {
        ProfileType = "filament",
        Categories = ["temperature", "flow", "retraction", "cooling", "physical", "gcode"],
        Fields = ApplyEngineVersion(BuildFilamentFields(), engineVersion),
    };

    // ── Engine-version filter/rename ─────────────────────────────────

    /// <summary>
    /// Applies the version window filter and rename resolution to a raw field list.
    /// Callers pass the requested engineVersion (e.g. "2.3.1"); unparsable or null values
    /// return the fields unchanged.
    /// </summary>
    internal static List<ProfileFieldMetadata> ApplyEngineVersion(
        List<ProfileFieldMetadata> fields,
        string? engineVersion)
    {
        if (string.IsNullOrWhiteSpace(engineVersion)
            || !Version.TryParse(engineVersion, out Version? requested))
        {
            return fields;
        }

        List<ProfileFieldMetadata> result = new(fields.Count);
        foreach (ProfileFieldMetadata field in fields)
        {
            if (field.MinEngineVersion is { } minStr
                && Version.TryParse(minStr, out Version? minVer)
                && requested < minVer)
            {
                continue;
            }

            if (field.MaxEngineVersion is { } maxStr
                && Version.TryParse(maxStr, out Version? maxVer)
                && requested > maxVer)
            {
                continue;
            }

            if (field.RenamedFromKey is { Length: > 0 } renamedFromKey
                && field.RenamedInVersion is { } renamedInStr
                && Version.TryParse(renamedInStr, out Version? renamedInVer)
                && requested < renamedInVer)
            {
                result.Add(new ProfileFieldMetadata
                {
                    Key = renamedFromKey,
                    Label = field.Label,
                    FieldType = field.FieldType,
                    Category = field.Category,
                    Description = field.Description,
                    DefaultValue = field.DefaultValue,
                    Min = field.Min,
                    Max = field.Max,
                    Step = field.Step,
                    Unit = field.Unit,
                    Options = field.Options,
                    IsAdvanced = field.IsAdvanced,
                    MinEngineVersion = field.MinEngineVersion,
                    MaxEngineVersion = field.MaxEngineVersion,
                    RenamedFromKey = field.RenamedFromKey,
                    RenamedInVersion = field.RenamedInVersion,
                });
                continue;
            }

            result.Add(field);
        }

        return result;
    }

    // ── Process profile fields ───────────────────────────────────────
    private static List<ProfileFieldMetadata> BuildProcessFields() =>
    [

        // Quality / Layers
        Num("layerHeight", "Layer Height", "quality", unit: "mm", min: 0.04, max: 1.0, step: 0.01, def: 0.2,
            desc: "Height of each printed layer"),
        Num("firstLayerHeight", "First Layer Height", "quality", unit: "mm", min: 0.04, max: 1.0, step: 0.01, def: 0.2),
        Int("topLayers", "Top Shell Layers", "quality", min: 0, max: 100, def: 4),
        Int("bottomLayers", "Bottom Shell Layers", "quality", min: 0, max: 100, def: 4),

        // Strength
        Int("infillPercentage", "Infill Density", "strength", unit: "%", min: 0, max: 100, def: 15),
        Enum("infillPattern", "Infill Pattern", "strength",
            opts: InfillPatterns, def: "grid"),
        Int("wallCount", "Wall Loops", "strength", min: 1, max: 20, def: 2),

        // Speed
        Int("printSpeed", "Print Speed", "speed", unit: "mm/s", min: 1, max: 1000, def: 100),
        Int("firstLayerPrintSpeed", "First Layer Speed", "speed", unit: "mm/s", min: 1, max: 200, def: 50),
        Int("outerWallSpeed", "Outer Wall Speed", "speed", unit: "mm/s", min: 1, max: 500, def: 50, adv: true),
        Int("innerWallSpeed", "Inner Wall Speed", "speed", unit: "mm/s", min: 1, max: 500, def: 100, adv: true),
        Int("infillSpeed", "Infill Speed", "speed", unit: "mm/s", min: 1, max: 500, def: 100, adv: true),
        Int("topSurfaceSpeed", "Top Surface Speed", "speed", unit: "mm/s", min: 1, max: 500, def: 50, adv: true),
        Int("travelSpeed", "Travel Speed", "speed", unit: "mm/s", min: 1, max: 1000, def: 200, adv: true),

        // Adhesion
        Enum("bedAdhesion", "Bed Adhesion", "adhesion",
            opts: [("none", "None"), ("skirt", "Skirt"), ("brim", "Brim"), ("raft", "Raft")], def: "skirt"),

        // Supports
        Bool("supports", "Enable Supports", "support", def: false),
        Enum("supportType", "Support Type", "support",
            opts: [("normal", "Normal"), ("tree", "Tree"), ("tree_auto", "Tree (Auto)")], def: "normal"),
        Int("supportDensity", "Support Density", "support", unit: "%", min: 5, max: 100, def: 20, adv: true),
        Int("supportAngle", "Support Overhang Angle", "support", unit: "°", min: 0, max: 90, def: 45, adv: true),

        // Seam
        Enum("seamPosition", "Seam Position", "quality",
            opts: [("random", "Random"), ("aligned", "Aligned"), ("back", "Back"), ("nearest", "Nearest")],
            def: "aligned", adv: true),
        Bool("enableIroning", "Enable Ironing", "quality", def: false, adv: true),

        // Temperature
        Int("nozzleTemp", "Nozzle Temperature", "temperature", unit: "°C", min: 150, max: 500, def: 200),
        Int("bedTemp", "Bed Temperature", "temperature", unit: "°C", min: 0, max: 150, def: 60),
        Int("firstLayerNozzleTemp", "First Layer Nozzle Temp", "temperature", unit: "°C", min: 150, max: 500, adv: true),
        Int("firstLayerBedTemp", "First Layer Bed Temp", "temperature", unit: "°C", min: 0, max: 150, adv: true),

        // Retraction
        Num("retractionLength", "Retraction Length", "other", unit: "mm", min: 0, max: 15, step: 0.1, def: 0.8, adv: true),
        Int("retractionSpeed", "Retraction Speed", "other", unit: "mm/s", min: 1, max: 200, def: 30, adv: true),

        // Line widths
        Num("lineWidthDefault", "Default Line Width", "quality", unit: "mm", min: 0.1, max: 2.0, step: 0.01, def: 0.4, adv: true),
        Num("lineWidthOuterWall", "Outer Wall Line Width", "quality", unit: "mm", min: 0.1, max: 2.0, step: 0.01, adv: true),
        Num("lineWidthInnerWall", "Inner Wall Line Width", "quality", unit: "mm", min: 0.1, max: 2.0, step: 0.01, adv: true),

        // Acceleration
        Int("defaultAcceleration", "Default Acceleration", "speed", unit: "mm/s²", min: 0, max: 50000, adv: true),
        Int("outerWallAcceleration", "Outer Wall Acceleration", "speed", unit: "mm/s²", min: 0, max: 50000, adv: true),
        Int("innerWallAcceleration", "Inner Wall Acceleration", "speed", unit: "mm/s²", min: 0, max: 50000, adv: true),
        Int("topSurfaceAcceleration", "Top Surface Acceleration", "speed", unit: "mm/s²", min: 0, max: 50000, adv: true),

        // ── Engine-version-scoped example fields (issue #578) ────────
        // These illustrate the added/removed/renamed mechanic that lets the schema
        // provider emit different field sets for OrcaSlicer 2.3.1 vs 2.4.1. The exact
        // version windows are placeholders; real OrcaSlicer field research should
        // refine them before production consumers depend on the specific fields.

        // Added in 2.4.0 (Arachne wall generator, arc fitting)
        Enum("wallGenerator", "Wall Generator", "quality",
            opts: [("classic", "Classic"), ("arachne", "Arachne")],
            def: "classic", adv: true, minEngineVersion: "2.4.0",
            desc: "Perimeter generation strategy — Arachne available in OrcaSlicer 2.4+."),
        Bool("enableArcFitting", "Enable Arc Fitting", "quality",
            def: false, adv: true, minEngineVersion: "2.4.0",
            desc: "Emit G2/G3 arc moves instead of many small linear moves — 2.4+."),

        // Removed in 2.4.0 (retired legacy preview toggle)
        Bool("legacyPreviewSetting", "Legacy Preview Setting", "other",
            def: false, adv: true, maxEngineVersion: "2.3.99",
            desc: "Deprecated preview toggle from 2.3.x; retired in 2.4.0."),

        // Renamed in 2.4.0: 2.3.x used "firstLayerAdhesion", 2.4.x uses "bedAdhesionOverride"
        Enum("bedAdhesionOverride", "Bed Adhesion Override", "adhesion",
            opts: [("none", "None"), ("skirt", "Skirt"), ("brim", "Brim"), ("raft", "Raft")],
            def: "skirt", adv: true,
            renamedFromKey: "firstLayerAdhesion", renamedInVersion: "2.4.0",
            desc: "2.3.x key: firstLayerAdhesion. Renamed to bedAdhesionOverride in 2.4.0."),
    ];

    // ── Machine profile fields ───────────────────────────────────────
    private static List<ProfileFieldMetadata> BuildMachineFields() =>
    [

        // General
        Str("name", "Profile Name", "general"),
        Str("manufacturer", "Manufacturer", "general"),
        Str("printerModel", "Printer Model", "general"),
        Str("printerVariant", "Variant", "general"),

        // Build volume
        Int("buildVolumeX", "Build Volume X", "buildVolume", unit: "mm", min: 1, max: 2000),
        Int("buildVolumeY", "Build Volume Y", "buildVolume", unit: "mm", min: 1, max: 2000),
        Int("buildVolumeZ", "Build Volume Z", "buildVolume", unit: "mm", min: 1, max: 2000),
        Str("printableArea", "Printable Area", "buildVolume", desc: "Polygon defining the printable area", adv: true),

        // Extruder
        Num("nozzleDiameter", "Nozzle Diameter", "extruder", unit: "mm", min: 0.1, max: 2.0, step: 0.05, def: 0.4),
        Int("maxPrintSpeed", "Max Print Speed", "extruder", unit: "mm/s", min: 1, max: 2000),
        Int("extruderCount", "Extruder Count", "extruder", min: 1, max: 16, def: 1, adv: true),
        Enum("motionType", "Motion System", "extruder",
            opts: [("cartesian", "Cartesian"), ("corexy", "CoreXY"), ("delta", "Delta"), ("belt", "Belt")],
            def: "cartesian"),

        // Retraction
        Num("retractionLength", "Retraction Length", "retraction", unit: "mm", min: 0, max: 15, step: 0.1, def: 0.8),
        Int("retractionSpeed", "Retraction Speed", "retraction", unit: "mm/s", min: 1, max: 200, def: 30),
        Num("retractionLiftZ", "Z Lift on Retraction", "retraction", unit: "mm", min: 0, max: 5, step: 0.05, adv: true),
        Int("detractionSpeed", "Detraction Speed", "retraction", unit: "mm/s", min: 1, max: 200, adv: true),

        // Bed
        Bool("hasHeatedBed", "Heated Bed", "bed", def: true),
        Int("maxBedTemperature", "Max Bed Temperature", "bed", unit: "°C", min: 0, max: 200),
        Int("maxHotendTemperature", "Max Hotend Temperature", "bed", unit: "°C", min: 150, max: 500),

        // G-code
        Str("startGcode", "Start G-code", "gcode", adv: true),
        Str("endGcode", "End G-code", "gcode", adv: true),

        // Motion limits
        Int("maxAccelerationX", "Max Accel X", "motion", unit: "mm/s²", min: 0, max: 100000, adv: true),
        Int("maxAccelerationY", "Max Accel Y", "motion", unit: "mm/s²", min: 0, max: 100000, adv: true),
        Int("maxFeedrateX", "Max Feedrate X", "motion", unit: "mm/s", min: 0, max: 5000, adv: true),
        Int("maxFeedrateY", "Max Feedrate Y", "motion", unit: "mm/s", min: 0, max: 5000, adv: true),
    ];

    // ── Filament profile fields ──────────────────────────────────────
    private static List<ProfileFieldMetadata> BuildFilamentFields() =>
    [

        // Temperature
        Int("nozzleTemperature", "Nozzle Temperature", "temperature", unit: "°C", min: 150, max: 500, def: 200),
        Int("bedTemperature", "Bed Temperature", "temperature", unit: "°C", min: 0, max: 150, def: 60),
        Int("firstLayerNozzleTemperature", "First Layer Nozzle Temp", "temperature", unit: "°C", min: 150, max: 500, adv: true),
        Int("firstLayerBedTemperature", "First Layer Bed Temp", "temperature", unit: "°C", min: 0, max: 150, adv: true),
        Int("chamberTemperature", "Chamber Temperature", "temperature", unit: "°C", min: 0, max: 100, adv: true),

        // Flow
        Num("flowRatio", "Flow Ratio", "flow", min: 0.5, max: 2.0, step: 0.01, def: 1.0),
        Int("printSpeed", "Print Speed", "flow", unit: "mm/s", min: 1, max: 1000, adv: true),
        Bool("enablePressureAdvance", "Pressure Advance", "flow", def: false, adv: true),
        Num("pressureAdvance", "PA Value", "flow", min: 0, max: 2.0, step: 0.001, adv: true),
        Num("maxVolumetricSpeed", "Max Volumetric Speed", "flow", unit: "mm³/s", min: 0, max: 100, step: 0.5, adv: true),

        // Retraction
        Num("retractionLength", "Retraction Length", "retraction", unit: "mm", min: 0, max: 15, step: 0.1, def: 0.8),
        Int("retractionSpeed", "Retraction Speed", "retraction", unit: "mm/s", min: 1, max: 200, def: 30),
        Int("detractionSpeed", "Detraction Speed", "retraction", unit: "mm/s", min: 1, max: 200, adv: true),

        // Cooling
        Bool("enableFanCooling", "Part Cooling Fan", "cooling", def: true),
        Int("minFanSpeed", "Min Fan Speed", "cooling", unit: "%", min: 0, max: 100, def: 35),
        Int("maxFanSpeed", "Max Fan Speed", "cooling", unit: "%", min: 0, max: 100, def: 100),
        Int("bridgeFanSpeed", "Bridge Fan Speed", "cooling", unit: "%", min: 0, max: 100, def: 100, adv: true),

        // Physical
        Num("density", "Density", "physical", unit: "g/cm³", min: 0.5, max: 10.0, step: 0.01, def: 1.24),
        Num("cost", "Cost", "physical", unit: "$/kg", min: 0, max: 1000, step: 0.01),

        // G-code
        Str("startGcode", "Start G-code", "gcode", adv: true),
        Str("endGcode", "End G-code", "gcode", adv: true),
    ];

    // ── Shared enum option lists ─────────────────────────────────────
    private static readonly List<EnumOptionDto> InfillPatterns =
    [
        new() { Value = "grid", Label = "Grid" },
        new() { Value = "triangles", Label = "Triangles" },
        new() { Value = "stars", Label = "Stars" },
        new() { Value = "cubic", Label = "Cubic" },
        new() { Value = "line", Label = "Line" },
        new() { Value = "concentric", Label = "Concentric" },
        new() { Value = "honeycomb", Label = "Honeycomb" },
        new() { Value = "honeycomb3d", Label = "3D Honeycomb" },
        new() { Value = "gyroid", Label = "Gyroid" },
        new() { Value = "hilbertcurve", Label = "Hilbert Curve" },
        new() { Value = "archimedeanchords", Label = "Archimedean Chords" },
        new() { Value = "octagramspiral", Label = "Octagram Spiral" },
        new() { Value = "adaptivecubic", Label = "Adaptive Cubic" },
        new() { Value = "supportcubic", Label = "Support Cubic" },
        new() { Value = "lightning", Label = "Lightning" },
        new() { Value = "crosshatch", Label = "Cross Hatch" },
    ];

    // ── Field builder helpers ────────────────────────────────────────
    private static ProfileFieldMetadata Num(
        string key, string label, string category,
        string? unit = null, double? min = null, double? max = null,
        double? step = null, double? def = null, string? desc = null, bool adv = false) => new()
        {
            Key = key,
            Label = label,
            FieldType = "number",
            Category = category,
            Unit = unit,
            Min = min,
            Max = max,
            Step = step,
            DefaultValue = def,
            Description = desc,
            IsAdvanced = adv,
        };

    private static ProfileFieldMetadata Int(
        string key, string label, string category,
        string? unit = null, int? min = null, int? max = null,
        int? def = null, string? desc = null, bool adv = false) => new()
        {
            Key = key,
            Label = label,
            FieldType = "integer",
            Category = category,
            Unit = unit,
            Min = min,
            Max = max,
            Step = 1,
            DefaultValue = def,
            Description = desc,
            IsAdvanced = adv,
        };

    private static ProfileFieldMetadata Bool(
        string key, string label, string category,
        bool? def = null, string? desc = null, bool adv = false,
        string? minEngineVersion = null, string? maxEngineVersion = null,
        string? renamedFromKey = null, string? renamedInVersion = null) => new()
        {
            Key = key,
            Label = label,
            FieldType = "boolean",
            Category = category,
            DefaultValue = def,
            Description = desc,
            IsAdvanced = adv,
            MinEngineVersion = minEngineVersion,
            MaxEngineVersion = maxEngineVersion,
            RenamedFromKey = renamedFromKey,
            RenamedInVersion = renamedInVersion,
        };

    private static ProfileFieldMetadata Str(
        string key, string label, string category,
        string? desc = null, bool adv = false,
        string? minEngineVersion = null, string? maxEngineVersion = null,
        string? renamedFromKey = null, string? renamedInVersion = null) => new()
        {
            Key = key,
            Label = label,
            FieldType = "string",
            Category = category,
            Description = desc,
            IsAdvanced = adv,
            MinEngineVersion = minEngineVersion,
            MaxEngineVersion = maxEngineVersion,
            RenamedFromKey = renamedFromKey,
            RenamedInVersion = renamedInVersion,
        };

    private static ProfileFieldMetadata Enum(
        string key, string label, string category,
        List<EnumOptionDto>? opts = null,
        (string val, string lbl)[]? tuples = null,
        string? def = null, string? desc = null, bool adv = false,
        string? minEngineVersion = null, string? maxEngineVersion = null,
        string? renamedFromKey = null, string? renamedInVersion = null) => new()
        {
            Key = key,
            Label = label,
            FieldType = "enum",
            Category = category,
            DefaultValue = def,
            Description = desc,
            IsAdvanced = adv,
            Options = opts ?? tuples?.Select(t => new EnumOptionDto { Value = t.val, Label = t.lbl }).ToList(),
            MinEngineVersion = minEngineVersion,
            MaxEngineVersion = maxEngineVersion,
            RenamedFromKey = renamedFromKey,
            RenamedInVersion = renamedInVersion,
        };

    // Overload accepting tuple array directly (used by most callsites)
    private static ProfileFieldMetadata Enum(
        string key, string label, string category,
        (string val, string lbl)[] opts,
        string? def = null, string? desc = null, bool adv = false,
        string? minEngineVersion = null, string? maxEngineVersion = null,
        string? renamedFromKey = null, string? renamedInVersion = null) =>
        Enum(key, label, category, opts: null, tuples: opts, def: def, desc: desc, adv: adv,
            minEngineVersion: minEngineVersion, maxEngineVersion: maxEngineVersion,
            renamedFromKey: renamedFromKey, renamedInVersion: renamedInVersion);
}
