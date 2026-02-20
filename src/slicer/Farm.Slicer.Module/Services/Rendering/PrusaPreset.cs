using System.Collections.Generic;
using System.Numerics;
using SixLabors.ImageSharp.PixelFormats;

namespace Farm.Slicer.Module.Services.Rendering;

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
