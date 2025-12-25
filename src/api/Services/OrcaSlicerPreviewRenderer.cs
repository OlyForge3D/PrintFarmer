using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;

namespace Farm.Web.Api.Services;

    public sealed class OrcaPreviewRenderer : BasePreviewRenderer
    {
        // ---------------------------------------------------------------------
        //  APPLY ORCA DEFAULTS
        // ---------------------------------------------------------------------
        protected override void ApplyStyleDefaults(RenderOptions options)
        {
            var d = RenderOptions.CreateOrcaDefaults();

            options.Width = d.Width;
            options.Height = d.Height;

            options.UseOrthographic = d.UseOrthographic;
            options.OrthoSize = d.OrthoSize;

            options.CameraPosition = d.CameraPosition;
            //options.CameraTarget = d.CameraTarget;
            options.CameraTarget = new Vector3(0, 0, 0.55f);

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
        //  ORCA BACKGROUND
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
                    new ColorStop(0f, new Rgba32(28, 29, 32)),
                    new ColorStop(1f, new Rgba32(34, 36, 40))
                ));
            });
        }
        // ---------------------------------------------------------------------
        //  ORCA BUILD PLATE
        // ---------------------------------------------------------------------
        protected override void DrawBuildPlate(Image<Rgba32> img, RenderOptions options)
        {
            int w = img.Width;
            int h = img.Height;

            img.Mutate(ctx =>
            {
                // Plate rectangle (lower portion of the image)
                var plateRect = new Rectangle(
                    0,
                    (int)(h * 0.55f),
                    w,
                    (int)(h * 0.35f)
                );

                // Dark gradient plate
                ctx.Fill(new LinearGradientBrush(
                    new PointF(0, plateRect.Top),
                    new PointF(0, plateRect.Bottom),
                    GradientRepetitionMode.None,
                    new ColorStop(0f, new Rgba32(28, 29, 32)),
                    new ColorStop(1f, new Rgba32(22, 23, 26))
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
                    ctx.Draw(options.BuildPlateGridColor, 1f, pbV.Build());

                    var pbH = new PathBuilder();
                    pbH.AddLine(new PointF(plateRect.Left, y), new PointF(plateRect.Right, y));
                    ctx.Draw(options.BuildPlateGridColor, 1f, pbH.Build());
                }
            });
        }

        // ---------------------------------------------------------------------
        //  ORCA SHADING MODEL
        // ---------------------------------------------------------------------
protected override Rgba32 ShadeTriangle(
    Vector3 normal,
    float ao,
    RenderOptions options)
{
    // Normalize lighting vectors
    Vector3 lightDir = Vector3.Normalize(-options.LightDirection);
    Vector3 fillLight = Vector3.Normalize(new Vector3(0.3f, 0.2f, 1f));

    // Primary lambert
    float lambert = Math.Max(0.15f, Vector3.Dot(normal, lightDir));

    // Stronger fill light (brightens shadowed side)
    lambert += Math.Max(0f, Vector3.Dot(normal, fillLight)) * 0.25f;

    // Upward boost (keeps top surfaces bright)
    lambert += Math.Max(0f, Vector3.Dot(normal, Vector3.UnitZ)) * 0.10f;

    // Brighter ambient term
    lambert = lambert * 0.80f + 0.20f;

    // Apply ambient occlusion
    lambert *= ao;

    lambert = Math.Clamp(lambert, 0f, 1f);

    // Slightly brightened base color
    var baseColor = new Rgba32(
        (byte)Math.Clamp(options.ModelBaseColor.R * 1.05f, 0, 255),
        (byte)Math.Clamp(options.ModelBaseColor.G * 1.05f, 0, 255),
        (byte)Math.Clamp(options.ModelBaseColor.B * 1.05f, 0, 255)
    );

    // Final shaded color
    return new Rgba32(
        (byte)(baseColor.R * lambert),
        (byte)(baseColor.G * lambert),
        (byte)(baseColor.B * lambert)
    );
}

    }

