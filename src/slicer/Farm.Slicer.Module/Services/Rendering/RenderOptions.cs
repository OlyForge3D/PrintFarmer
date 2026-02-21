using System.Collections.Generic;
using System.Numerics;
using SixLabors.ImageSharp.PixelFormats;

namespace Farm.Slicer.Module.Services.Rendering;

// ------------------------------------------------------------
// RENDER OPTIONS
// ------------------------------------------------------------
public sealed class RenderOptions
{
    /// <summary>
    /// View mode for front/back/left/right views: Isometric (diagonal) or Straight (perpendicular)
    /// </summary>
    public enum ViewMode
    {
        /// <summary>
        /// Isometric (diagonal) view mode
        /// </summary>
        Isometric = 0,

        /// <summary>
        /// Straight (perpendicular) view mode
        /// </summary>
        Straight = 1
    }

    public int Width { get; set; } = 800;

    public int Height { get; set; } = 600;

    public bool UseOrthographic { get; set; } = true;

    public float OrthoSize { get; set; } = 1.65f;

    public Vector3 CameraPosition { get; set; } = new Vector3(1.75f, 1.75f, 1.35f);

    public Vector3 CameraTarget { get; set; } = new Vector3(0f, 0f, 0.55f);

    public Vector3 CameraUp { get; set; } = Vector3.UnitZ;

    // Model vertical bounds (min/max Z after normalization) - updated after mesh loading
    public float ModelBoundsMinZ { get; set; } = 0f;

    public float ModelBoundsMaxZ { get; set; } = 1f;

    // Camera view name to apply after mesh bounds are known
    // Used to defer SetCameraView until after mesh normalization and bounds calculation
    public string? PendingCameraView { get; set; }

    public ViewMode CameraViewMode { get; set; } = ViewMode.Isometric;

    /// <summary>
    /// Gets the vertical center of the model based on its bounding box.
    /// Computed after mesh normalization to dynamically calculate optimal camera targeting.
    /// </summary>
    public float ModelVerticalCenter => (ModelBoundsMinZ + ModelBoundsMaxZ) / 2f;

    /// <summary>
    /// Camera view presets: front, back, left, right, top, bottom
    /// Supports both isometric (diagonal) and straight (perpendicular) viewing angles.
    /// </summary>
    private static readonly Dictionary<string, (Vector3 Position, Vector3 Target)> ViewPresetsIsometric =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // Diagonal isometric views (front/back)
        { "front", (new Vector3(-1.75f, -1.75f, 1.35f), new Vector3(0f, 0f, 0.35f)) },
        { "back", (new Vector3(1.75f, 1.75f, 1.35f), new Vector3(0f, 0f, 0.35f)) },

        // Isometric side views (45-degree angle)
        { "left", (new Vector3(-1.75f, -1.75f, 1.35f), new Vector3(0f, 0f, 0.35f)) },
        { "right", (new Vector3(1.75f, 1.75f, 1.35f), new Vector3(0f, 0f, 0.35f)) },

        // Top and bottom views (same for both modes)
        { "top", (new Vector3(0f, 0f, 2.5f), new Vector3(0f, 0f, 0.35f)) },
        { "bottom", (new Vector3(0f, 0f, -1.5f), new Vector3(0f, 0f, 0.35f)) }
    };

    private static readonly Dictionary<string, (Vector3 Position, Vector3 Target)> ViewPresetsStraight =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // Straight-on perpendicular views
        { "front", (new Vector3(0f, -2.5f, 1.35f), new Vector3(0f, 0f, 0.35f)) },
        { "back", (new Vector3(0f, 2.5f, 1.35f), new Vector3(0f, 0f, 0.35f)) },

        // Pure side views - straight-on with centering offsets
        { "left", (new Vector3(-2.5f, 0f, 1.35f), new Vector3(0.3f, 0f, 0.35f)) },
        { "right", (new Vector3(2.5f, 0f, 1.35f), new Vector3(-0.3f, 0f, 0.35f)) },

        // Top and bottom views (same for both modes)
        { "top", (new Vector3(0f, 0f, 2.5f), new Vector3(0f, 0f, 0.35f)) },
        { "bottom", (new Vector3(0f, 0f, -1.5f), new Vector3(0f, 0f, 0.35f)) }
    };

    /// <summary>
    /// Gets the appropriate ViewPresets dictionary based on current view mode
    /// </summary>
    private Dictionary<string, (Vector3 Position, Vector3 Target)> ViewPresets =>
        CameraViewMode == ViewMode.Isometric ? ViewPresetsIsometric : ViewPresetsStraight;

    public Vector3 LightDirection { get; set; } = Vector3.Normalize(new Vector3(-0.6f, -0.5f, -1f));

    // View-space light direction (computed at render time from LightDirection and view matrix)
    // This ensures consistent lighting when normals are in view space
    public Vector3 ViewSpaceLightDirection { get; set; } = Vector3.Normalize(new Vector3(-0.6f, -0.5f, -1f));

    // Base material color in linear space (Orca-ish teal)
    public Vector3 BaseColorLinear { get; set; } = new(0.20f, 0.70f, 0.90f);

    public Rgba32 ModelBaseColor { get; set; } = new Rgba32(150, 210, 200);

    public Rgba32 BackgroundColor { get; set; } = new Rgba32(34, 36, 40);

    public bool EnableSilhouetteEdges { get; set; } = true;

    public Rgba32 SilhouetteColor { get; set; } = new Rgba32(20, 20, 25, 180);

    public float SilhouetteEdgeWidth { get; set; } = 1.0f;

    public float SilhouetteAngleThresholdDeg { get; set; } = 82f;

    public float SilhouetteDepthEpsilon { get; set; } = 0.003f;

    public bool EnableAmbientOcclusion { get; set; } = true;

    public float AmbientOcclusionStrength { get; set; } = 0.65f;

    // Render both windings to avoid holes when viewing from back/inside
    public bool TwoSided { get; set; } = true;

    // Limit normal smoothing to preserve sharp interior detail
    public float NormalSmoothingAngleDeg { get; set; } = 70f;

    // Lifted diffuse base
    public float AmbientFactor { get; set; } = 0.30f;

    public float DiffuseFactor { get; set; } = 0.70f;

    // NEW: wrap lighting (soft terminator)
    public float DiffuseWrap { get; set; } = 0.32f; // 0 = Lambert, 0.25-0.40 = Orca-ish

    // NEW: AO shaping
    public float AOMin { get; set; } = 0.70f;

    public float AOMax { get; set; } = 1.00f;

    public float AOPower { get; set; } = 1.25f;     // >1 makes AO less linear / more Orca-like

    // NEW: subtle sheen
    public float SpecularStrength { get; set; } = 0.08f;

    public float SpecularPower { get; set; } = 48f;

    public bool EnableGroundShadow { get; set; } = true;

    public float GroundShadowOpacity { get; set; } = 0.08f;

    public int GroundShadowBlurRadiusPx { get; set; } = 10;

    public int GroundShadowOffsetXPx { get; set; } = 2;

    public int GroundShadowOffsetYPx { get; set; } = 25;

    public bool AntiAlias2x { get; set; } = true;

    /// <summary>
    /// Applies a zoom percentage relative to a preset baseline.
    /// Example: if defaultPercent is 40 (orca) and requestedPercent is 80, OrthoSize is halved (zoom in).
    /// Only invoke when a user value is provided; preset defaults remain untouched otherwise.
    /// </summary>
    /// <param name="defaultPercent">The baseline zoom percentage (e.g., 40 for orca preset).</param>
    /// <param name="requestedPercent">The requested zoom percentage to apply.</param>
    public void SetZoomPercent(int defaultPercent, int requestedPercent)
    {
        int percent = requestedPercent <= 0 ? defaultPercent : requestedPercent;
        OrthoSize = OrthoSize * (defaultPercent / (float)percent);
    }

    /// <summary>
    /// Sets the camera position and target based on a named view preset
    /// Supports: front, back, left, right, top, bottom
    /// Both position and target are updated to maintain proper centering of the model.
    ///
    /// Note: If called before mesh bounds are known, stores the view name in PendingCameraView
    /// to be applied later in Render() after bounds are calculated.
    /// </summary>
    /// <param name="viewName">The name of the view preset (front, back, left, right, top, bottom).</param>
    public bool SetCameraView(string viewName)
    {
        // Store for deferred application if bounds haven't been calculated yet
        // (bounds are 0-1 initially, then updated to actual values after mesh load)
        if (Math.Abs(ModelBoundsMaxZ - 1f) < 0.001f)
        {
            PendingCameraView = viewName;
            Console.WriteLine($"[CAMERA] View '{viewName}' queued (awaiting mesh bounds)");
            return true;
        }

        return ApplyCameraView(viewName);
    }

    /// <summary>
    /// Internal method that applies camera view once bounds are known.
    /// Called either directly from SetCameraView (after bounds) or from UpdateMeshBounds.
    /// </summary>
    /// <param name="viewName">The name of the view preset to apply.</param>
    private bool ApplyCameraView(string viewName)
    {
        if (!ViewPresets.TryGetValue(viewName, out (Vector3 Position, Vector3 Target) preset))
        {
            return false;
        }

        Vector3 oldPos = CameraPosition;
        Vector3 oldTarget = CameraTarget;
        Vector3 oldUp = CameraUp;

        CameraPosition = preset.Position;

        // Calculate camera target Z dynamically based on model vertical center
        // For top/bottom views, use the preset Z (0.35 for optimal view)
        // For other views, use the model's vertical center plus an offset to center in frame
        float targetZ;
        if (viewName.Equals("top", StringComparison.OrdinalIgnoreCase) ||
            viewName.Equals("bottom", StringComparison.OrdinalIgnoreCase))
        {
            targetZ = 0.35f;
        }
        else
        {
            // Use the model's vertical center plus a fixed offset to keep it centered in frame
            // Different offset logic for diagonal vs. pure side views
            float modelCenter = ModelVerticalCenter;
            float modelHeight = ModelBoundsMaxZ - ModelBoundsMinZ;

            // Offset depends on view mode and direction
            float offset;
            if (CameraViewMode == ViewMode.Isometric)
            {
                // Isometric views: all use larger offset for perspective
                offset = Math.Max(modelHeight * 0.33f, 0.08f);
            }
            else
            {
                // Straight views: side views (left/right) use smaller offset
                if (viewName.Equals("left", StringComparison.OrdinalIgnoreCase) ||
                    viewName.Equals("right", StringComparison.OrdinalIgnoreCase))
                {
                    offset = modelHeight * 0.15f;
                }
                else
                {
                    // Front/back straight views use moderate offset
                    offset = Math.Max(modelHeight * 0.25f, 0.06f);
                }
            }

            targetZ = modelCenter + offset;
        }

        // Update target with calculated Z, preserving X and Y from preset
        CameraTarget = new Vector3(preset.Target.X, preset.Target.Y, targetZ);

        // Compute the appropriate up vector based on view direction
        // This ensures top/bottom views don't have degenerate view matrices
        Vector3 viewDir = Vector3.Normalize(CameraTarget - CameraPosition);
        CameraUp = ComputeCameraUpVector(viewDir);

        // Disable ground shadow for bottom view (looking up, shadow doesn't make sense)
        // Also disable ambient occlusion which darkens the view significantly
        if (viewName.Equals("bottom", StringComparison.OrdinalIgnoreCase))
        {
            EnableGroundShadow = false;
            EnableAmbientOcclusion = false;

            // Significantly increase ambient factor to brighten the view when looking from below
            // Bottom view has inverted normals relative to camera, making it naturally darker
            AmbientFactor = 0.80f;
            DiffuseFactor = 0.20f;
        }
        else
        {
            // Reset to defaults for other views
            EnableAmbientOcclusion = true;
            AmbientFactor = 0.30f;
            DiffuseFactor = 0.70f;
        }

        Console.WriteLine($"[CAMERA] Changed view to '{viewName}':");
        Console.WriteLine($"  Position: {oldPos} → {CameraPosition}");
        Console.WriteLine($"  Target:   {oldTarget} → {CameraTarget}");
        Console.WriteLine($"  Up:       {oldUp} → {CameraUp}");
        Console.WriteLine($"  Model Z bounds: {ModelBoundsMinZ:F3} to {ModelBoundsMaxZ:F3}, center: {ModelVerticalCenter:F3}");

        return true;
    }

    /// <summary>
    /// Computes an appropriate up vector given a view direction.
    /// For most views, returns +Z. For top/bottom views (where Z is the view direction),
    /// returns -Y instead to avoid degenerate matrices.
    /// </summary>
    /// <param name="viewDirection">The normalized view direction vector.</param>
    private static Vector3 ComputeCameraUpVector(Vector3 viewDirection)
    {
        // If view direction is nearly parallel to +Z or -Z (top/bottom views),
        // use -Y as the up vector. Otherwise, use +Z (standard for all other views).
        float absZ = Math.Abs(viewDirection.Z);

        // Threshold: if the view is pointing mostly in Z direction (> 0.9), use -Y as up
        return absZ > 0.9f ? -Vector3.UnitY : Vector3.UnitZ;
    }

    /// <summary>
    /// Gets the list of available camera view presets
    /// </summary>
    public IEnumerable<string> AvailableViews => ViewPresets.Keys;
}
