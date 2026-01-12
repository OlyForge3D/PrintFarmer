using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Farm.Web.Api.Services;

// ------------------------------------------------------------
// PRUSA RENDERER
// ------------------------------------------------------------
public sealed class PrusaPreviewRenderer : BasePreviewRenderer
{
    // ---------------------------------------------------------------------
    //  APPLY PRUSA DEFAULTS
    // ---------------------------------------------------------------------
    protected override void ApplyStyleDefaults(RenderOptions options)
    {
        var d = PrusaPreset.Create();

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
    //  PRUSA BACKGROUND
    // ---------------------------------------------------------------------
    protected override void DrawBackground(Image<Rgba32> img, RenderOptions options)
    {
        int h = img.Height;

        img.Mutate(ctx =>
        {
            ctx.Fill(new LinearGradientBrush(
                new PointF(0, 0),
                new PointF(0, h),
                GradientRepetitionMode.None,
                new ColorStop(0f, new Rgba32(245, 245, 248)),
                new ColorStop(1f, options.BackgroundColor)
            ));
        });
    }

    // ---------------------------------------------------------------------
    //  PRUSA SHADING MODEL
    // ---------------------------------------------------------------------
    protected override Rgba32 ShadeTriangle(Vector3 normal, float ao, RenderOptions options)
    {
        // Use VIEW-SPACE light direction since normals are in view space
        Vector3 lightDir = Vector3.Normalize(-options.ViewSpaceLightDirection);

        float lambert = Math.Max(0.2f, Vector3.Dot(normal, lightDir));
        lambert *= 0.85f;

        lambert = lambert * 0.90f + 0.10f;

        if (ao < 1f)
        {
            lambert *= 1f - (1f - ao) * (options.AmbientOcclusionStrength * 0.55f);
            lambert = Math.Clamp(lambert, 0.60f, 1f);
        }

        lambert = Math.Clamp(lambert, 0f, 1f);

        var baseColor = options.ModelBaseColor;

        return new Rgba32(
            (byte)(baseColor.R * lambert),
            (byte)(baseColor.G * lambert),
            (byte)(baseColor.B * lambert)
        );
    }
}

