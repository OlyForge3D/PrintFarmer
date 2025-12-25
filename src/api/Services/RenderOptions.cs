using System.Numerics;
using SixLabors.ImageSharp.PixelFormats;

namespace Farm.Web.Api.Services;

public sealed class RenderOptions
{
    // Output resolution
    public int Width { get; set; } = 1024;
    public int Height { get; set; } = 1024;

    // Camera
    public Vector3 CameraPosition { get; set; } = new Vector3(1.8f, 1.8f, 1.1f);
    public Vector3 CameraTarget { get; set; } = new Vector3(0, 0, 0.2f);
    public Vector3 CameraUp { get; set; } = Vector3.UnitZ;

    // Projection
    public float OrthoSize { get; set; } = 2.2f;
    public float FovDegrees { get; set; } = 45f;
    public float NearPlane { get; set; } = 0.1f;
    public float FarPlane { get; set; } = 20f;
    public bool UseOrthographic { get; set; } = true;

    // Lighting
    public Vector3 LightDirection { get; set; } =
        Vector3.Normalize(new Vector3(-0.4f, -0.3f, 1.0f));

    // Model color
    public Rgba32 ModelBaseColor { get; set; } = new Rgba32(150, 210, 200);

    // Background
    public Rgba32 BackgroundColor { get; set; } = new Rgba32(30, 31, 34);

    // Silhouette edges
    public bool EnableSilhouetteEdges { get; set; } = true;
    public Rgba32 SilhouetteColor { get; set; } = new Rgba32(10, 12, 15);
    public float SilhouetteAngleThresholdDeg { get; set; } = 75f;
    public float SilhouetteEdgeWidth { get; set; } = 0.8f;

    // Build plate
    public bool EnableBuildPlate { get; set; } = true;
    public Rgba32 BuildPlateGridColor { get; set; } = new Rgba32(70, 72, 78);
    public Rgba32 BuildPlateBorderColor { get; set; } = new Rgba32(110, 112, 118);
    public float BuildPlateSize { get; set; } = 220f;
    public float BuildPlateGridStep { get; set; } = 5f;

    // Ambient occlusion
    public bool EnableAmbientOcclusion { get; set; } = true;
    public float AmbientOcclusionStrength { get; set; } = 0.25f;

    // Style presets will be applied by subclasses
    public enum PreviewStyle
    {
        Orca,
        Prusa
    }

    public PreviewStyle Style { get; set; } = PreviewStyle.Orca;

    // ---------------------------------------------------------------------
    // FACTORY METHODS FOR STYLE DEFAULTS
    // ---------------------------------------------------------------------

    public static RenderOptions CreateOrcaDefaults()
    {
        return new RenderOptions
        {
            Style = PreviewStyle.Orca,

            Width = 1024,
            Height = 1024,

            UseOrthographic = true,
            OrthoSize = 2.2f,

            CameraPosition = new Vector3(1.8f, 1.8f, 1.1f),
            CameraTarget = new Vector3(0, 0, 0.2f),
            CameraUp = Vector3.UnitZ,

            LightDirection = Vector3.Normalize(new Vector3(-0.4f, -0.3f, 1.0f)),

            ModelBaseColor = new Rgba32(150, 210, 200),
            BackgroundColor = new Rgba32(30, 31, 34),

            EnableSilhouetteEdges = true,
            SilhouetteColor = new Rgba32(10, 12, 15),
            SilhouetteAngleThresholdDeg = 75f,
            SilhouetteEdgeWidth = 0.8f,

            EnableBuildPlate = true,
            BuildPlateGridColor = new Rgba32(70, 72, 78),
            BuildPlateBorderColor = new Rgba32(110, 112, 118),
            BuildPlateSize = 220f,
            BuildPlateGridStep = 5f,

            EnableAmbientOcclusion = true,
            AmbientOcclusionStrength = 0.25f
        };
    }

    public static RenderOptions CreateOrcaPreset()
    {
        var o = new RenderOptions();

        // ------------------------------------------------------------
        // CAMERA (Orca-accurate)
        // ------------------------------------------------------------
        o.UseOrthographic = true;
        o.OrthoSize = 1.65f;

        o.CameraPosition = new Vector3(1.75f, 1.75f, 1.35f);
        o.CameraTarget   = new Vector3(0f, 0f, 0.55f);
        o.CameraUp       = Vector3.UnitZ;

        // ------------------------------------------------------------
        // LIGHTING (soft, flat, Orca-style)
        // ------------------------------------------------------------
        o.LightDirection = Vector3.Normalize(new Vector3(-0.6f, -0.5f, -1f));

        // Orca base color (cool, desaturated green-blue)
        o.ModelBaseColor = new Rgba32(150, 210, 200);

        // ------------------------------------------------------------
        // SILHOUETTE EDGES (A1 subtle)
        // ------------------------------------------------------------
        o.EnableSilhouetteEdges = true;
        o.SilhouetteColor = new Rgba32(20, 20, 25, 180);
        o.SilhouetteEdgeWidth = 1.0f;
        o.SilhouetteAngleThresholdDeg = 82f;

        // ------------------------------------------------------------
        // AMBIENT OCCLUSION (softened)
        // ------------------------------------------------------------
        o.EnableAmbientOcclusion = true;
        o.AmbientOcclusionStrength = 0.65f; // softened AO

        // ------------------------------------------------------------
        // BACKGROUND (Orca gradient)
        // ------------------------------------------------------------
        o.BackgroundColor = new Rgba32(34, 36, 40);

        // ------------------------------------------------------------
        // BUILD PLATE (Orca grid)
        // ------------------------------------------------------------
        o.EnableBuildPlate = true;
        o.BuildPlateSize = 200f;
        o.BuildPlateGridStep = 5f;

        o.BuildPlateGridColor   = new Rgba32(70, 75, 85, 255);
        o.BuildPlateBorderColor = new Rgba32(90, 95, 105, 255);

        return o;
    }

    public static RenderOptions CreatePrusaDefaults()
    {
        return new RenderOptions
        {
            Style = PreviewStyle.Prusa,

            Width = 1024,
            Height = 1024,

            UseOrthographic = true,
            OrthoSize = 2.4f,

            CameraPosition = new Vector3(2.0f, 2.0f, 2.0f),
            CameraTarget = new Vector3(0, 0, 0.3f),
            CameraUp = Vector3.UnitZ,

            LightDirection = Vector3.Normalize(new Vector3(-0.2f, -0.3f, 1.0f)),

            ModelBaseColor = new Rgba32(210, 205, 200),
            BackgroundColor = new Rgba32(235, 235, 238),

            EnableSilhouetteEdges = true,
            SilhouetteColor = new Rgba32(180, 180, 185),
            SilhouetteAngleThresholdDeg = 65f,
            SilhouetteEdgeWidth = 0.6f,

            EnableBuildPlate = true,
            BuildPlateGridColor = new Rgba32(200, 200, 205),
            BuildPlateBorderColor = new Rgba32(160, 160, 165),
            BuildPlateSize = 250f,
            BuildPlateGridStep = 10f,

            EnableAmbientOcclusion = true,
            AmbientOcclusionStrength = 0.18f
        };
    }

    public static RenderOptions CreatePrusaPreset()
    {
        var o = new RenderOptions();

        // ------------------------------------------------------------
        // CAMERA (Prusa-style: slightly higher, softer angle)
        // ------------------------------------------------------------
        o.UseOrthographic = true;
        o.OrthoSize = 1.70f;

        o.CameraPosition = new Vector3(1.65f, 1.65f, 1.45f);
        o.CameraTarget   = new Vector3(0f, 0f, 0.60f);
        o.CameraUp       = Vector3.UnitZ;

        // ------------------------------------------------------------
        // LIGHTING (soft, warm, low-contrast)
        // ------------------------------------------------------------
        o.LightDirection = Vector3.Normalize(new Vector3(-0.55f, -0.55f, -1f));

        // Prusa base color (warm neutral gray)
        o.ModelBaseColor = new Rgba32(205, 205, 210);

        // ------------------------------------------------------------
        // SILHOUETTE EDGES (very subtle)
        // ------------------------------------------------------------
        o.EnableSilhouetteEdges = true;
        o.SilhouetteColor = new Rgba32(40, 40, 45, 160);
        o.SilhouetteEdgeWidth = 1.0f;
        o.SilhouetteAngleThresholdDeg = 84f;

        // ------------------------------------------------------------
        // AMBIENT OCCLUSION (softest of all slicers)
        // ------------------------------------------------------------
        o.EnableAmbientOcclusion = true;
        o.AmbientOcclusionStrength = 0.55f;

        // ------------------------------------------------------------
        // BACKGROUND (Prusa light gradient)
        // ------------------------------------------------------------
        o.BackgroundColor = new Rgba32(245, 245, 248);

        // ------------------------------------------------------------
        // BUILD PLATE (light gray, soft grid)
        // ------------------------------------------------------------
        o.EnableBuildPlate = true;
        o.BuildPlateSize = 200f;
        o.BuildPlateGridStep = 5f;

        o.BuildPlateGridColor   = new Rgba32(180, 180, 185, 255);
        o.BuildPlateBorderColor = new Rgba32(160, 160, 165, 255);

        return o;
    }
}

