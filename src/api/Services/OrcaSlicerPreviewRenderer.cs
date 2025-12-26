using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;

namespace Farm.Web.Api.Services;

// ------------------------------------------------------------
// ORCA RENDERER
// ------------------------------------------------------------
public sealed class OrcaPreviewRenderer : BasePreviewRenderer
{
    // ---------------------------------------------------------------------
    //  APPLY ORCA DEFAULTS
    // ---------------------------------------------------------------------
    protected override void ApplyStyleDefaults(RenderOptions options)
    {
        var d = OrcaPreset.Create();

        options.Width = d.Width;
        options.Height = d.Height;

        options.UseOrthographic = d.UseOrthographic;
        options.OrthoSize = d.OrthoSize;

        options.CameraPosition = d.CameraPosition;
        options.CameraTarget = d.CameraTarget;
        options.CameraUp = d.CameraUp;

        options.LightDirection = d.LightDirection;

        options.ModelBaseColor = d.ModelBaseColor;
        options.BackgroundColor = d.BackgroundColor;

        options.EnableSilhouetteEdges = d.EnableSilhouetteEdges;
        options.SilhouetteColor = d.SilhouetteColor;
        options.SilhouetteAngleThresholdDeg = d.SilhouetteAngleThresholdDeg;
        options.SilhouetteEdgeWidth = d.SilhouetteEdgeWidth;

        options.EnableAmbientOcclusion = d.EnableAmbientOcclusion;
        options.AmbientOcclusionStrength = d.AmbientOcclusionStrength;
    }

    // ---------------------------------------------------------------------
    //  ORCA BACKGROUND
    // ---------------------------------------------------------------------
    protected override void DrawBackground(Image<Rgba32> img, RenderOptions options)
    {
        int w = img.Width, h = img.Height;

        // Orca-ish dark slate gradient (tweak to taste)
        var top = new Vector3(0.16f, 0.17f, 0.19f);
        var bot = new Vector3(0.10f, 0.11f, 0.13f);

        // Vignette
        float vignetteStrength = 0.22f;
        float invW = 1f / Math.Max(1, w - 1);
        float invH = 1f / Math.Max(1, h - 1);

        var frame = img.Frames.RootFrame;

        static byte ToSRGB(float x)
        {
            x = Math.Clamp(x, 0f, 1f);
            return (byte)(MathF.Pow(x, 1f / 2.2f) * 255f + 0.5f);
        }

        for (int y = 0; y < h; y++)
        {
            float t = y * invH; // 0 top -> 1 bottom
            var baseLin = Vector3.Lerp(top, bot, t);

            for (int x = 0; x < w; x++)
            {
                float nx = (x * invW) * 2f - 1f;
                float ny = (y * invH) * 2f - 1f;

                // radial vignette (soft)
                float r2 = nx * nx + ny * ny;
                float vig = 1f - vignetteStrength * SmoothStep(0.0f, 1.6f, r2);

                var c = baseLin * vig;

                frame[x, y] = new Rgba32(ToSRGB(c.X), ToSRGB(c.Y), ToSRGB(c.Z), 255);
            }
        }
    }

    // ---------------------------------------------------------------------
    //  ORCA SHADING MODEL
    // ---------------------------------------------------------------------
    protected override Rgba32 ShadeTriangle(Vector3 normal, float ao, RenderOptions options)
    {
        var n = Vector3.Normalize(normal);
        
        // Use VIEW-SPACE light direction since normals are in view space
        var l = Vector3.Normalize(options.ViewSpaceLightDirection);

        // View direction (from surface toward camera). In View Space, this is +Z.
        var v = Vector3.UnitZ;

        // Wrap diffuse (softens terminator)
        float ndotl = Vector3.Dot(n, -l);
        float wrap = options.DiffuseWrap;

        float diffuseWrapped = (ndotl + wrap) / (1f + wrap);
        diffuseWrapped = Math.Clamp(diffuseWrapped, 0f, 1f);

        float lightTerm = options.AmbientFactor + options.DiffuseFactor * diffuseWrapped;
        lightTerm = Math.Clamp(lightTerm, 0f, 1f);

        // AO shaping (Orca-like “velvet”)
        float aoClamped = Math.Clamp(ao, options.AOMin, options.AOMax);
        float aoTerm = options.EnableAmbientOcclusion
            ? MathF.Pow(aoClamped, options.AOPower) * options.AmbientOcclusionStrength
            + (1f - options.AmbientOcclusionStrength)
            : 1f;

        // Subtle specular sheen (half-vector)
        var h = Vector3.Normalize(-l + v);
        float ndoth = Math.Clamp(Vector3.Dot(n, h), 0f, 1f);
        float spec = options.SpecularStrength * MathF.Pow(ndoth, options.SpecularPower);

        // Final linear color
        var baseColor = options.BaseColorLinear;
        var lit = baseColor * lightTerm * aoTerm + new Vector3(spec);

        // Linear -> sRGB
        static byte ToSRGB(float x)
        {
            x = Math.Clamp(x, 0f, 1f);
            float srgb = MathF.Pow(x, 1f / 2.2f);
            return (byte)(srgb * 255f + 0.5f);
        }

        return new Rgba32(
            ToSRGB(lit.X),
            ToSRGB(lit.Y),
            ToSRGB(lit.Z),
            255);
    }
}

