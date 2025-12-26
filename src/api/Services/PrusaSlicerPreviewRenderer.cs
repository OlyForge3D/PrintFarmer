using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
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

        options.EnableBuildPlate = d.EnableBuildPlate;
        options.BuildPlateGridColor = d.BuildPlateGridColor;
        options.BuildPlateBorderColor = d.BuildPlateBorderColor;
        options.BuildPlateSize = d.BuildPlateSize;
        options.BuildPlateGridStep = d.BuildPlateGridStep;

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
    //  PRUSA BUILD PLATE
    // ---------------------------------------------------------------------
    protected override void DrawBuildPlate(Image<Rgba32> img, RenderOptions options)
    {
        int w = img.Width;
        int h = img.Height;

        img.Mutate(ctx =>
        {
            // Plate rectangle (slightly inset, lighter tone)
            var plateRect = new Rectangle(
                (int)(w * 0.05f),
                (int)(h * 0.55f),
                (int)(w * 0.90f),
                (int)(h * 0.30f)
            );

            // Light gradient plate
            ctx.Fill(new LinearGradientBrush(
                new PointF(0, plateRect.Top),
                new PointF(0, plateRect.Bottom),
                GradientRepetitionMode.None,
                new ColorStop(0f, new Rgba32(225, 225, 230)),
                new ColorStop(1f, new Rgba32(210, 210, 215))
            ));

            // Border
            ctx.Draw(options.BuildPlateBorderColor, 2f, plateRect);

            // Grid
            int gridLines = (int)(options.BuildPlateSize / options.BuildPlateGridStep);

            for (int i = 1; i < gridLines; i++)
            {
                float t = (float)i / gridLines;

                int x = (int)(plateRect.Left + t * plateRect.Width);
                int y = (int)(plateRect.Top + t * plateRect.Height);

                var pbV = new PathBuilder();
                pbV.AddLine(new PointF(x, plateRect.Top), new PointF(x, plateRect.Bottom));
                ctx.Draw(options.BuildPlateGridColor, 0.8f, pbV.Build());

                var pbH = new PathBuilder();
                pbH.AddLine(new PointF(plateRect.Left, y), new PointF(plateRect.Right, y));
                ctx.Draw(options.BuildPlateGridColor, 0.8f, pbH.Build());
            }
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

