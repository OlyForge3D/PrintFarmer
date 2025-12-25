using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Assimp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;

namespace Farm.Web.Api.Services;

public class BasePreviewRenderer
{
    // ---------------------------------------------------------------------
    //  VIRTUAL METHODS (subclasses override these)
    // ---------------------------------------------------------------------

    protected virtual void ApplyStyleDefaults(RenderOptions options)
    {
        // Subclasses override to apply Orca or Prusa defaults
    }

    protected virtual void DrawBackground(Image<Rgba32> img, RenderOptions options)
    {
        // Subclasses override
    }

    protected virtual void DrawBuildPlate(Image<Rgba32> img, RenderOptions options)
    {
        // Subclasses override
    }

    protected virtual Rgba32 ShadeTriangle(
        Vector3 normal,
        float ao,
        RenderOptions options)
    {
        // Subclasses override with Orca or Prusa shading
        return options.ModelBaseColor;
    }

    // ---------------------------------------------------------------------
    //  PUBLIC ENTRY POINT
    // ---------------------------------------------------------------------

    public void Render(string inputPath, string outputPath, RenderOptions options)
    {
        ApplyStyleDefaults(options);

        var mesh = LoadMesh(inputPath);
        var normalized = NormalizeMesh(mesh);
        var triangles = BuildTriangles(normalized);

        if (options.EnableAmbientOcclusion)
            ComputeAmbientOcclusion(triangles, options);

        var view = Matrix4x4.CreateLookAt(
            options.CameraPosition,
            options.CameraTarget,
            options.CameraUp
        );

        var proj = CreateProjection(options);

        var ndcTriangles = TransformToNdc(triangles, view, proj);
        ndcTriangles.Sort((a, b) => a.Depth.CompareTo(b.Depth));

        using var img = new Image<Rgba32>(options.Width, options.Height);

        DrawBackground(img, options);

        if (options.EnableBuildPlate)
            DrawBuildPlate(img, options);

        RasterizeTriangles(img, ndcTriangles, options);

        if (options.EnableSilhouetteEdges)
            DrawSilhouetteEdges(img, ndcTriangles, options);

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outputPath)) ?? ".");
        img.Save(outputPath);
    }

    // ---------------------------------------------------------------------
    //  MESH LOADING
    // ---------------------------------------------------------------------

    private Mesh LoadMesh(string inputPath)
    {
        var ctx = new AssimpContext();
        var scene = ctx.ImportFile(inputPath,
            PostProcessSteps.Triangulate |
            PostProcessSteps.GenerateNormals |
            PostProcessSteps.JoinIdenticalVertices |
            PostProcessSteps.FlipUVs);

        if (scene == null || !scene.HasMeshes)
            throw new InvalidOperationException("No mesh found in file.");

        return scene.Meshes[0];
    }

    // ---------------------------------------------------------------------
    //  NORMALIZATION (with grounding to Z=0)
    // ---------------------------------------------------------------------

    private class NormalizedMesh
    {
        public List<Vector3> Vertices { get; } = new();
        public List<Face> Faces { get; } = new();
    }

    private NormalizedMesh NormalizeMesh(Mesh mesh)
    {
        var result = new NormalizedMesh();

        // ------------------------------------------------------------
        // 1. Compute bounding box (for scale + Z grounding only)
        // ------------------------------------------------------------
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var v in mesh.Vertices)
        {
            var p = new Vector3(v.X, v.Y, v.Z);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        float minZ = min.Z;

        // ------------------------------------------------------------
        // 2. Compute TRUE centroid in XY (fixes backward tilt)
        // ------------------------------------------------------------
        Vector2 centroid = Vector2.Zero;
        foreach (var v in mesh.Vertices)
            centroid += new Vector2(v.X, v.Y);

        centroid /= mesh.Vertices.Count;

        // ------------------------------------------------------------
        // 3. Compute uniform scale
        // ------------------------------------------------------------
        float scale = 1f / Math.Max(
            max.X - min.X,
            Math.Max(max.Y - min.Y, max.Z - min.Z)
        );

        // ------------------------------------------------------------
        // 4. Apply normalization
        // ------------------------------------------------------------
        foreach (var v in mesh.Vertices)
        {
            var p = new Vector3(v.X, v.Y, v.Z);

            // Center using centroid (NOT bounding box midpoint)
            p.X -= centroid.X;
            p.Y -= centroid.Y;

            // Ground Z
            p.Z -= minZ;

            // Normalize scale
            p *= scale;

            // Slight shrink for slicer-style framing
            p *= 0.92f;

            result.Vertices.Add(p);
        }

        // ------------------------------------------------------------
        // 5. Copy faces
        // ------------------------------------------------------------
        foreach (var f in mesh.Faces)
            if (f.IndexCount == 3)
                result.Faces.Add(f);

        return result;
    }

    // ---------------------------------------------------------------------
    //  TRIANGLE BUILDING
    // ---------------------------------------------------------------------

    private class Triangle
    {
        public Vector3 World0, World1, World2;
        public Vector3 Normal;
        public float Ao;

        public Vector3 V0, V1, V2; // NDC
        public float Depth;
    }

    private List<Triangle> BuildTriangles(NormalizedMesh nmesh)
    {
        var tris = new List<Triangle>(nmesh.Faces.Count);

        foreach (var face in nmesh.Faces)
        {
            var v0 = nmesh.Vertices[face.Indices[0]];
            var v1 = nmesh.Vertices[face.Indices[1]];
            var v2 = nmesh.Vertices[face.Indices[2]];

            var normal = Vector3.Normalize(Vector3.Cross(v1 - v0, v2 - v0));

            tris.Add(new Triangle
            {
                World0 = v0,
                World1 = v1,
                World2 = v2,
                Normal = normal,
                Ao = 1f
            });
        }

        return tris;
    }

    // ---------------------------------------------------------------------
    //  PROJECTION
    // ---------------------------------------------------------------------

    private Matrix4x4 CreateProjection(RenderOptions options)
    {
        if (options.UseOrthographic)
        {
            float s = options.OrthoSize;
            return Matrix4x4.CreateOrthographic(s, s, options.NearPlane, options.FarPlane);
        }
        else
        {
            float fovRad = options.FovDegrees * (float)Math.PI / 180f;
            float aspect = (float)options.Width / options.Height;
            return Matrix4x4.CreatePerspectiveFieldOfView(
                fovRad, aspect, options.NearPlane, options.FarPlane);
        }
    }

    // ---------------------------------------------------------------------
    //  TRANSFORM TO CLIP SPACE → NDC
    // ---------------------------------------------------------------------

    private Vector4 TransformClip(Vector3 pos, Matrix4x4 view, Matrix4x4 proj)
    {
        var v = Vector4.Transform(new Vector4(pos, 1f), view);
        v = Vector4.Transform(v, proj);

        if (Math.Abs(v.W) > float.Epsilon)
        {
            v.X /= v.W;
            v.Y /= v.W;
            v.Z /= v.W;
        }

        // Tiny depth bias to avoid z-fighting
        v.Z -= 0.0001f;

        return v;
    }

    private List<Triangle> TransformToNdc(
        List<Triangle> tris,
        Matrix4x4 view,
        Matrix4x4 proj)
    {
        var result = new List<Triangle>(tris.Count);

        foreach (var t in tris)
        {
            var v0 = TransformClip(t.World0, view, proj);
            var v1 = TransformClip(t.World1, view, proj);
            var v2 = TransformClip(t.World2, view, proj);

            float depth = (v0.Z + v1.Z + v2.Z) / 3f;

            result.Add(new Triangle
            {
                World0 = t.World0,
                World1 = t.World1,
                World2 = t.World2,
                Normal = t.Normal,
                Ao = t.Ao,

                V0 = new Vector3(v0.X, v0.Y, v0.Z),
                V1 = new Vector3(v1.X, v1.Y, v1.Z),
                V2 = new Vector3(v2.X, v2.Y, v2.Z),

                Depth = depth
            });
        }

        return result;
    }

    // ---------------------------------------------------------------------
    //  NDC → SCREEN
    // ---------------------------------------------------------------------

    private static Vector2 NdcToScreen(Vector3 ndc, int width, int height)
    {
        float x = (ndc.X * 0.5f + 0.5f) * width;
        float y = (-ndc.Y * 0.5f + 0.5f) * height;
        return new Vector2(x, y);
    }

    // ---------------------------------------------------------------------
    //  AMBIENT OCCLUSION (simple triangle-density AO)
    // ---------------------------------------------------------------------

    private void ComputeAmbientOcclusion(List<Triangle> tris, RenderOptions options)
    {
        var centers = tris
            .Select(t => (t.World0 + t.World1 + t.World2) / 3f)
            .ToArray();

        float maxDensity = 0f;
        var densities = new float[tris.Count];

        for (int i = 0; i < tris.Count; i++)
        {
            float density = 0f;
            var ci = centers[i];

            for (int j = 0; j < tris.Count; j++)
            {
                if (i == j) continue;

                var cj = centers[j];
                float dist = (ci - cj).Length();

                if (dist < 0.25f)
                    density += 1f / (1f + dist * 10f);
            }

            densities[i] = density;
            if (density > maxDensity)
                maxDensity = density;
        }

        for (int i = 0; i < tris.Count; i++)
        {
            float norm = maxDensity > 0 ? densities[i] / maxDensity : 0f;
            float ao = 1f - norm * options.AmbientOcclusionStrength;
            ao = Math.Clamp(ao, 0.4f, 1f);

            tris[i].Ao = ao;
        }
    }

    // ---------------------------------------------------------------------
    //  TRIANGLE RASTERIZATION
    // ---------------------------------------------------------------------

    private void RasterizeTriangles(
        Image<Rgba32> img,
        List<Triangle> tris,
        RenderOptions options)
    {
        int w = img.Width;
        int h = img.Height;

        // Helper for screen-space winding
        static float Cross2D(Vector2 a, Vector2 b, Vector2 c)
        {
            return (b.X - a.X) * (c.Y - a.Y) -
                (b.Y - a.Y) * (c.X - a.X);
        }

        img.Mutate(ctx =>
        {
            foreach (var tri in tris)
            {
                // Convert to screen space
                var s0 = NdcToScreen(tri.V0, w, h);
                var s1 = NdcToScreen(tri.V1, w, h);
                var s2 = NdcToScreen(tri.V2, w, h);

                // ------------------------------------------------------------
                // SCREEN-SPACE BACK-FACE CULLING (the missing piece)
                // ------------------------------------------------------------
                float winding = Cross2D(s0, s1, s2);

                // If winding <= 0, triangle is facing away from the camera
                if (winding <= 0f)
                    continue;

                // Shade
                Rgba32 color = ShadeTriangle(tri.Normal, tri.Ao, options);

                // Fill triangle
                ctx.FillPolygon(
                    color,
                    new PointF(s0.X, s0.Y),
                    new PointF(s1.X, s1.Y),
                    new PointF(s2.X, s2.Y)
                );
            }
        });
    }

    // ---------------------------------------------------------------------
    //  TRUE SILHOUETTE EDGE DETECTION (A1 subtle)
    // ---------------------------------------------------------------------

private void DrawSilhouetteEdges(
    Image<Rgba32> img,
    List<Triangle> tris,
    RenderOptions options)
{
    int w = img.Width;
    int h = img.Height;

    // Same helper used in RasterizeTriangles()
    static float Cross2D(Vector2 a, Vector2 b, Vector2 c)
    {
        return (b.X - a.X) * (c.Y - a.Y) -
               (b.Y - a.Y) * (c.X - a.X);
    }

    img.Mutate(ctx =>
    {
        foreach (var tri in tris)
        {
            // Convert to screen space
            var s0 = NdcToScreen(tri.V0, w, h);
            var s1 = NdcToScreen(tri.V1, w, h);
            var s2 = NdcToScreen(tri.V2, w, h);

            // ------------------------------------------------------------
            // SCREEN-SPACE BACK-FACE CULLING (match rasterizer)
            // ------------------------------------------------------------
            float winding = Cross2D(s0, s1, s2);
            if (winding <= 0f)
                continue;

            // ------------------------------------------------------------
            // TRUE SILHOUETTE EDGE TEST
            //
            // A silhouette edge occurs when the triangle is front-facing
            // in screen space, but its normal is nearly perpendicular
            // to the view direction (dot ≈ 0).
            // ------------------------------------------------------------

            // Depth fade (subtle A1 style)
            if (tri.Depth > 0.9f)
                continue;

            // Angle threshold
            float ndotv = Math.Abs(Vector3.Dot(tri.Normal,
                Vector3.Normalize(options.CameraTarget - options.CameraPosition)));

            float threshold = (float)Math.Cos(options.SilhouetteAngleThresholdDeg * Math.PI / 180f);
            if (ndotv > threshold)
                continue;

            // Build the triangle outline path
            var pb = new PathBuilder();
            pb.AddLine(new PointF(s0.X, s0.Y), new PointF(s1.X, s1.Y));
            pb.AddLine(new PointF(s1.X, s1.Y), new PointF(s2.X, s2.Y));
            pb.AddLine(new PointF(s2.X, s2.Y), new PointF(s0.X, s0.Y));

            ctx.Draw(
                options.SilhouetteColor,
                options.SilhouetteEdgeWidth,
                pb.Build()
            );
        }
    });
}

    // ---------------------------------------------------------------------
    //  UTILITY: LINE DRAWING (for debugging or future features)
    // ---------------------------------------------------------------------

    protected void DrawLine(
        Image<Rgba32> img,
        Vector2 a,
        Vector2 b,
        Rgba32 color,
        float thickness = 1f)
    {
        img.Mutate(ctx =>
        {
            var pb = new PathBuilder();
            pb.AddLine(new PointF(a.X, a.Y), new PointF(b.X, b.Y));
            ctx.Draw(color, thickness, pb.Build());
        });
    }

    // ---------------------------------------------------------------------
    //  END OF CLASS
    // ---------------------------------------------------------------------
}

