using System.Collections.Generic;
using System.Numerics;
using SixLabors.ImageSharp.PixelFormats;

namespace Farm.Web.Api.Services;

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
