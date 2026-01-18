using System.Collections.Generic;
using System.Numerics;
using SixLabors.ImageSharp.PixelFormats;

namespace Farm.Web.Api.Services;

// ------------------------------------------------------------
// RENDER OPTIONS
// ------------------------------------------------------------
public sealed class RenderOptions
{
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

    /// <summary>
    /// View mode for front/back/left/right views: Isometric (diagonal) or Straight (perpendicular)
    /// </summary>
    public enum ViewMode
    {
        Isometric = 0,  // Diagonal view (45-degree angle)
        Straight = 1    // Perpendicular/straight-on view
    }

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
    private bool ApplyCameraView(string viewName)
    {
        if (!ViewPresets.TryGetValue(viewName, out var preset))
        {
            return false;
        }

        var oldPos = CameraPosition;
        var oldTarget = CameraTarget;
        var oldUp = CameraUp;

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

// ------------------------------------------------------------
// MESH TYPES
// ------------------------------------------------------------
public sealed class Mesh
{
    public List<Vector3> Vertices { get; } = new();
    public List<Face> Faces { get; } = new();
}

public sealed class Face
{
    public int[] Indices { get; set; } = Array.Empty<int>();
    public int FaceIndex { get; set; }
    public int IndexCount => Indices?.Length ?? 0;
}

public sealed class NormalizedMesh
{
    public List<Vector3> Vertices { get; } = new();
    public List<Face> Faces { get; } = new();
    public float[] Ao { get; set; } = Array.Empty<float>();

    public List<Vector3> Normals { get; } = new();
}

#pragma warning disable CA1051
#pragma warning disable CA1815
public struct Triangle
{
    public Vector4 V0;
    public Vector4 V1;
    public Vector4 V2;

    // Shading
    public Vector3 Normal;        // keep if you want (optional after per-vertex)
    public float Ao;              // keep if you want (optional after per-vertex)

    // Silhouette / facing - stored in VIEW SPACE for correct comparisons
    public Vector3 FaceNormal;
    public Vector3 ViewSpaceFaceNormal;

    // Per-vertex shading inputs (view-space normals + AO)
    public Vector3 N0, N1, N2;
    public float Ao0, Ao1, Ao2;

    // Clip-space (pre-divide), used for perspective-correct depth
    public float Cz0, Cw0;
    public float Cz1, Cw1;
    public float Cz2, Cw2;

    public float D0, D1, D2; // depth in [0,1]
}

public struct ClipVertex
{
    public Vector4 C;   // clip-space position
    public Vector3 N;   // view-space normal
    public float Ao;
}

#pragma warning restore CA1051
#pragma warning restore CA1815

public static class VectorExtensions
{
    public static Vector3 XYZ(this Vector4 v)
    {
        return new Vector3(v.X, v.Y, v.Z);
    }
}

public sealed class MeshLoadOptions
{
    public bool MergeMeshes { get; set; } = true;
    public bool UseZUp { get; set; } = true;   // false = Y-up
}

// ---------------------------------------------------------------------
// FACTORY METHODS FOR STYLE DEFAULTS
// ---------------------------------------------------------------------
public static class OrcaPreset
{
    public static RenderOptions Create()
    {
        return new RenderOptions()
        {
            Width = 1024,
            Height = 1024,

            // ------------------------------------------------------------
            // CAMERA (Orca-accurate)
            // ------------------------------------------------------------
            UseOrthographic = true,
            OrthoSize = 0.69f,

            CameraPosition = new Vector3(-1.75f, -1.75f, 1.35f),
            CameraTarget = new Vector3(0f, 0f, 0.35f),
            CameraUp = Vector3.UnitZ,

            // ------------------------------------------------------------
            // LIGHTING (soft, flat, Orca-style)
            // ------------------------------------------------------------
            LightDirection = Vector3.Normalize(new Vector3(-0.4f, -0.8f, -0.4f)),

            // Orca base color (cool, desaturated green-blue)
            ModelBaseColor = new Rgba32(150, 210, 200),

            // BACKGROUND (Orca gradient) - much darker
            BackgroundColor = new Rgba32(15, 16, 18),

            // ------------------------------------------------------------
            // SILHOUETTE EDGES (A1 subtle)
            // ------------------------------------------------------------
            EnableSilhouetteEdges = false,
            SilhouetteColor = new Rgba32(18, 20, 26, 90),  // softer edge
            SilhouetteDepthEpsilon = 0.0025f,             // avoid over-drawing coplanar edges

            SilhouetteEdgeWidth = 0.6f,                   // thinner lines
            SilhouetteAngleThresholdDeg = 82f,

            // ------------------------------------------------------------
            // AMBIENT OCCLUSION (softened)
            // ------------------------------------------------------------
            EnableAmbientOcclusion = true,
            AmbientOcclusionStrength = 0.55f, // was 0.65

            AmbientFactor = 0.34f,   // was 0.30
            DiffuseFactor = 0.66f,   // was 0.70
            DiffuseWrap = 0.38f,   // was 0.32

            AOMin = 0.78f,   // was 0.70
            AOPower = 1.45f,   // was 1.25

            SpecularStrength = 0.06f,
            SpecularPower = 56f,

            EnableGroundShadow = false
        };
    }
}

public static class PrusaPreset
{
    public static RenderOptions Create()
    {
        return new RenderOptions()
        {
            // ------------------------------------------------------------
            // CAMERA (Prusa-style: slightly higher, softer angle)
            // ------------------------------------------------------------
            UseOrthographic = true,
            OrthoSize = 1.25f,

            CameraPosition = new Vector3(1.65f, 1.65f, 1.45f),
            CameraTarget = new Vector3(0f, 0f, 0.35f),
            CameraUp = Vector3.UnitZ,

            // ------------------------------------------------------------
            // LIGHTING (soft, warm, low-contrast)
            // ------------------------------------------------------------
            LightDirection = Vector3.Normalize(new Vector3(-0.55f, -0.55f, -1f)),

            // Prusa base color (warm neutral gray)
            ModelBaseColor = new Rgba32(205, 205, 210),

            // ------------------------------------------------------------
            // SILHOUETTE EDGES (very subtle)
            // ------------------------------------------------------------
            EnableSilhouetteEdges = true,
            SilhouetteColor = new Rgba32(40, 40, 45, 160),
            SilhouetteEdgeWidth = 1.0f,
            SilhouetteAngleThresholdDeg = 84f,

            // ------------------------------------------------------------
            // AMBIENT OCCLUSION (softest of all slicers)
            // ------------------------------------------------------------
            EnableAmbientOcclusion = true,
            AmbientOcclusionStrength = 0.55f,

            // ------------------------------------------------------------
            // BACKGROUND (Prusa light gradient)
            // ------------------------------------------------------------
            BackgroundColor = new Rgba32(245, 245, 248),

            EnableGroundShadow = false
        };
    }
}

