using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Assimp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Numerics = System.Numerics;

namespace Farm.Web.Api.Services;

// ------------------------------------------------------------
// BASE PREVIEW RENDERER (FULL TRIANGLE PIPELINE)
// ------------------------------------------------------------
public abstract class BasePreviewRenderer
{
    public void Render(string inputPath, string outputPath, RenderOptions options)
    {
        options ??= new RenderOptions();

        // Capture all user-provided property values BEFORE ApplyStyleDefaults overwrites them
        var userProperties = CaptureRenderOptions(options);

        // Create default options for comparison
        var defaultOptions = new RenderOptions();
        ApplyStyleDefaults(defaultOptions);

        // Apply preset defaults to working options
        ApplyStyleDefaults(options);

        // Restore user settings that differ from preset defaults (automatic for all properties)
        RestoreUserSettings(options, userProperties, defaultOptions);

        Mesh mesh;
        try
        {
            mesh = LoadMesh(inputPath);
        }
        catch (AssimpException)
        {
            mesh = CreateFallbackMesh();
        }
        catch (InvalidOperationException)
        {
            mesh = CreateFallbackMesh();
        }
        var normalized = NormalizeMesh(mesh);

        // Calculate and store mesh bounds for dynamic camera targeting
        UpdateMeshBounds(normalized, options);

        // AO can be injected here; for now assume Ao array is precomputed or flat
        if (normalized.Ao == null || normalized.Ao.Length != normalized.Faces.Count)
        {
            normalized.Ao = new float[normalized.Faces.Count];
            for (int i = 0; i < normalized.Ao.Length; i++)
            {
                normalized.Ao[i] = 1f;
            }
        }

        // Compute smoothed vertex normals in model space with angle threshold
        ComputeVertexNormals(normalized, options);

        var (view, proj) = BuildCameraMatrices(options);

        // Transform light direction to view space for correct shading
        options.ViewSpaceLightDirection = Vector3.Normalize(
            Vector3.TransformNormal(options.LightDirection, view)
        );

        var tris = BuildTriangleList(normalized, view, proj);

        var img = new Image<Rgba32>(options.Width, options.Height);

        DrawBackground(img, options);

        var depth01 = RasterizeTriangles(img, tris, options);

        if (options.EnableGroundShadow)
        {
            DrawGroundShadow(img, depth01, options);
        }

        if (options.EnableSilhouetteEdges)
        {
            DrawSilhouetteEdges(img, tris, depth01, options);
        }


        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outputPath)) ?? ".");
        img.Save(outputPath);
    }

    private static Mesh CreateFallbackMesh()
    {
        var mesh = new Mesh();

        mesh.Vertices.AddRange(new[]
        {
            new Vector3(-0.5f, -0.5f, 0f),
            new Vector3(0.5f, -0.5f, 0f),
            new Vector3(0.5f, 0.5f, 0f),
            new Vector3(-0.5f, 0.5f, 0f),
            new Vector3(0f, 0f, 1f)
        });

        void AddFace(int a, int b, int c)
        {
            mesh.Faces.Add(new Face
            {
                FaceIndex = mesh.Faces.Count,
                Indices = new[] { a, b, c }
            });
        }

        AddFace(0, 1, 2);
        AddFace(0, 2, 3);
        AddFace(0, 1, 4);
        AddFace(1, 2, 4);
        AddFace(2, 3, 4);
        AddFace(3, 0, 4);

        return mesh;
    }

#pragma warning disable S2368 // Multidimensional arrays required for efficient image processing
    protected void DrawGroundShadow(Image<Rgba32> img, float[,] depth01, RenderOptions opt)
    {
        int w = img.Width, h = img.Height;
        var frame = img.Frames.RootFrame;

        // 1) Presence mask
        var mask = new float[w, h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                mask[x, y] = depth01[x, y] < float.PositiveInfinity ? 1f : 0f;
            }
        }

        // 2) Drop shadow offset
        var dropped = new float[w, h];
        int ox = opt.GroundShadowOffsetXPx;
        int oy = opt.GroundShadowOffsetYPx;

        for (int y = 0; y < h; y++)
        {
            int sy = y - oy;
            if ((uint)sy >= (uint)h)
            {
                continue;
            }

            for (int x = 0; x < w; x++)
            {
                int sx = x - ox;
                if ((uint)sx >= (uint)w)
                {
                    continue;
                }

                dropped[x, y] = mask[sx, sy];
            }
        }

        // 3) Blur
        int r = Math.Max(1, opt.GroundShadowBlurRadiusPx);
        var blurred = BoxBlurSeparable(dropped, w, h, r);

        // 4) Composite across entire image
        float opacity = Math.Clamp(opt.GroundShadowOpacity, 0f, 1f);

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float a = blurred[x, y] * opacity;
                if (a <= 0.001f)
                {
                    continue;
                }

                var c = frame[x, y];
                frame[x, y] = new Rgba32(
                    (byte)(c.R * (1f - a)),
                    (byte)(c.G * (1f - a)),
                    (byte)(c.B * (1f - a)),
                    255
                );
            }
        }
    }

    protected float[,] BoxBlurSeparable(float[,] src, int w, int h, int r)
#pragma warning restore S2368
    {
        var tmp = new float[w, h];
        var dst = new float[w, h];

        // Horizontal
        for (int y = 0; y < h; y++)
        {
            float sum = 0f;
            int count = 0;

            for (int x = -r; x <= r; x++)
            {
                int ix = Math.Clamp(x, 0, w - 1);
                sum += src[ix, y];
                count++;
            }

            for (int x = 0; x < w; x++)
            {
                tmp[x, y] = sum / count;

                int x0 = x - r;
                int x1 = x + r + 1;

                sum -= src[Math.Clamp(x0, 0, w - 1), y];
                sum += src[Math.Clamp(x1, 0, w - 1), y];
            }
        }

        // Vertical
        for (int x = 0; x < w; x++)
        {
            float sum = 0f;
            int count = 0;

            for (int y = -r; y <= r; y++)
            {
                int iy = Math.Clamp(y, 0, h - 1);
                sum += tmp[x, iy];
                count++;
            }

            for (int y = 0; y < h; y++)
            {
                dst[x, y] = sum / count;

                int y0 = y - r;
                int y1 = y + r + 1;

                sum -= tmp[x, Math.Clamp(y0, 0, h - 1)];
                sum += tmp[x, Math.Clamp(y1, 0, h - 1)];
            }
        }

        return dst;
    }

    protected static float SmoothStep(float edge0, float edge1, float x)
    {
        x = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return x * x * (3f - 2f * x);
    }

    // ---------------------------------------------------------------------
    //  MESH LOADING
    // ---------------------------------------------------------------------
    private Mesh LoadMesh(string inputPath, MeshLoadOptions? opts = null)
    {
        opts ??= new MeshLoadOptions();

        // Use Lib3MF for 3MF files, Assimp for others
        string extension = System.IO.Path.GetExtension(inputPath).ToLowerInvariant();
        if (extension == ".3mf")
        {
            return Load3MFMesh(inputPath);
        }

        var ctx = new AssimpContext();

        var scene = ctx.ImportFile(inputPath,
            PostProcessSteps.Triangulate |
            PostProcessSteps.GenerateNormals |
            PostProcessSteps.JoinIdenticalVertices |
            PostProcessSteps.FlipUVs);

        if (scene == null || !scene.HasMeshes)
        {
            throw new InvalidOperationException("No mesh found in file.");
        }

        var mesh = new Mesh();

        // Rotation for Y-up → Z-up
        Numerics.Matrix4x4 axisFix = Numerics.Matrix4x4.Identity;
        if (!opts.UseZUp)
        {
            // Convert Y-up to Z-up: rotate +90° around X
            axisFix = Numerics.Matrix4x4.CreateRotationX(MathF.PI / 2f);
        }

        int vertexOffset = 0;

        for (int meshIndex = 0; meshIndex < scene.Meshes.Count; meshIndex++)
        {
            var aMesh = scene.Meshes[meshIndex];

            Numerics.Matrix4x4 nodeTransform = Numerics.Matrix4x4.Identity;
            if (scene.RootNode != null)
            {
                var node = FindNodeForMesh(scene.RootNode, meshIndex);
                if (node != null)
                {
                    nodeTransform = ToNumerics(node);
                }
            }

            foreach (var v in aMesh.Vertices)
            {
                Vector3 p = new Vector3(v.X, v.Y, v.Z);

                // Apply node transform
                p = Vector3.Transform(p, nodeTransform);

                // Apply axis fix
                p = Vector3.Transform(p, axisFix);

                mesh.Vertices.Add(p);
            }

            // Faces
            foreach (var f in aMesh.Faces)
            {
                if (f.IndexCount != 3)
                {
                    continue;
                }

                mesh.Faces.Add(new Face
                {
                    FaceIndex = mesh.Faces.Count,
                    Indices = new[]
                    {
                        vertexOffset + f.Indices[0],
                        vertexOffset + f.Indices[1],
                        vertexOffset + f.Indices[2]
                    }
                });
            }

            vertexOffset = mesh.Vertices.Count;

            if (!opts.MergeMeshes)
            {
                break; // only first mesh
            }
        }

        return mesh;
    }

    /// <summary>
    /// Loads a 3MF mesh using Lib3MF library
    /// Falls back to Assimp if Lib3MF fails
    /// </summary>
    private Mesh Load3MFMesh(string inputPath)
    {
        try
        {
            // Use Lib3MF for better 3MF support
            return LoadWith3MFLibrary(inputPath);
        }
        catch (Exception lib3mfEx)
        {
            // Fallback to Assimp if Lib3MF fails
            try
            {
                var ctx = new AssimpContext();
                var scene = ctx.ImportFile(inputPath,
                    PostProcessSteps.Triangulate |
                    PostProcessSteps.GenerateNormals |
                    PostProcessSteps.JoinIdenticalVertices |
                    PostProcessSteps.FlipUVs);

                if (scene == null || !scene.HasMeshes)
                {
                    throw new InvalidOperationException("No mesh found in file.");
                }

                var mesh = new Mesh();
                int vertexOffset = 0;

                foreach (var aMesh in scene.Meshes)
                {
                    foreach (var v in aMesh.Vertices)
                    {
                        mesh.Vertices.Add(new Vector3(v.X, v.Y, v.Z));
                    }

                    foreach (var face in aMesh.Faces)
                    {
                        if (face.IndexCount >= 3)
                        {
                            mesh.Faces.Add(new Face
                            {
                                Indices = new[]
                                {
                                    face.Indices[0] + vertexOffset,
                                    face.Indices[1] + vertexOffset,
                                    face.Indices[2] + vertexOffset
                                }
                            });
                        }
                    }

                    vertexOffset = mesh.Vertices.Count;
                }

                return mesh;
            }
            catch (Exception assimpEx)
            {
                throw new InvalidOperationException(
                    $"Failed to load 3MF file with both Lib3MF and Assimp. " +
                    $"Lib3MF error: {lib3mfEx.Message}. " +
                    $"Assimp error: {assimpEx.Message}", assimpEx);
            }
        }
    }

    /// <summary>
    /// Loads a 3MF file using the official Lib3MF C# wrapper
    /// </summary>
    private Mesh LoadWith3MFLibrary(string inputPath)
    {
        var mesh = new Mesh();
        int vertexOffset = 0;

        try
        {
            // Create a model
            var model = Lib3MF.Wrapper.CreateModel();

            // Get a reader and load the file
            var reader = model.QueryReader("3mf");
            reader.ReadFromFile(inputPath);

            // Get mesh objects from the model using iterator
            var meshObjectIterator = model.GetMeshObjects();

            int meshCount = 0;
            while (meshObjectIterator.MoveNext())
            {
                var meshObject = meshObjectIterator.GetCurrentMeshObject();
                meshCount++;

                // Get vertices
                var vertexCount = meshObject.GetVertexCount();
                for (UInt32 v = 0; v < vertexCount; v++)
                {
                    var vertex = meshObject.GetVertex(v);
                    // sPosition uses Coordinates array [0]=X, [1]=Y, [2]=Z
                    // Apply Z-up conversion to match Assimp's coordinate system
                    mesh.Vertices.Add(new Vector3(
                        vertex.Coordinates[0],
                        vertex.Coordinates[2],
                        -vertex.Coordinates[1]
                    ));
                }

                // Get triangles
                var triangleCount = meshObject.GetTriangleCount();
                for (UInt32 t = 0; t < triangleCount; t++)
                {
                    var triangle = meshObject.GetTriangle(t);
                    // sTriangle uses Indices array [0]=Index1, [1]=Index2, [2]=Index3
                    mesh.Faces.Add(new Face
                    {
                        FaceIndex = mesh.Faces.Count,
                        Indices = new[]
                        {
                            (int)triangle.Indices[0] + vertexOffset,
                            (int)triangle.Indices[1] + vertexOffset,
                            (int)triangle.Indices[2] + vertexOffset
                        }
                    });
                }

                vertexOffset = mesh.Vertices.Count;
            }

            if (meshCount == 0)
            {
                throw new InvalidOperationException("No mesh objects found in 3MF file.");
            }

            return mesh.Vertices.Count == 0 ? throw new InvalidOperationException("Failed to extract vertices from 3MF file.") : mesh;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to load 3MF file with Lib3MF: {ex.Message}", ex);
        }
    }

    private Assimp.Node? FindNodeForMesh(Assimp.Node node, int meshIndex)
    {
        if (node.MeshIndices.Contains(meshIndex))
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            var found = FindNodeForMesh(child, meshIndex);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private Numerics.Matrix4x4 ToNumerics(Assimp.Node node)
    {
        return node.Transform; // already a System.Numerics.Matrix4x4
    }

    // Style hooks
    protected abstract void ApplyStyleDefaults(RenderOptions options);

    /// <summary>
    /// Capture all property values from RenderOptions into a dictionary.
    /// Used to preserve user settings before ApplyStyleDefaults overwrites them.
    /// </summary>
    private static Dictionary<string, object?> CaptureRenderOptions(RenderOptions options)
    {
        var properties = new Dictionary<string, object?>();

        foreach (var prop in typeof(RenderOptions).GetProperties(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (prop.CanRead)
            {
                properties[prop.Name] = prop.GetValue(options);
            }
        }

        return properties;
    }

    /// <summary>
    /// Restore user-provided settings that differ from preset defaults.
    /// Uses reflection to automatically handle all RenderOptions properties,
    /// so new properties don't require code changes.
    /// </summary>
    protected virtual void RestoreUserSettings(
        RenderOptions options,
        Dictionary<string, object?> userProperties,
        RenderOptions defaultOptions)
    {
        var optionsType = typeof(RenderOptions);

        // Only restore properties that differ from the preset defaults
        foreach (var kvp in userProperties)
        {
            var prop = optionsType.GetProperty(kvp.Key,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

            if (prop == null || !prop.CanWrite)
            {
                continue;
            }

            var userValue = kvp.Value;
            var defaultValue = prop.GetValue(defaultOptions);

            // Only restore if values differ
            if (!PropertyValuesEqual(userValue, defaultValue))
            {
                prop.SetValue(options, userValue);
            }
        }
    }

    /// <summary>
    /// Check if two property values are equal, with special handling for float and Vector3.
    /// </summary>
    private static bool PropertyValuesEqual(object? a, object? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a == null || b == null)
        {
            return a == b;
        }

        // Special handling for float values (epsilon comparison)
        if (a is float fa && b is float fb)
        {
            return ApproximatelyEqual(fa, fb);
        }

        // Special handling for Vector3 (epsilon comparison)
        if (a is Vector3 va && b is Vector3 vb)
        {
            return ApproximatelyEqual(va, vb);
        }

        // Default equality
        return a.Equals(b);
    }

    private static bool ApproximatelyEqual(float a, float b, float epsilon = 1e-6f)
    {
        return Math.Abs(a - b) < epsilon;
    }

    private static bool ApproximatelyEqual(Vector3 a, Vector3 b, float epsilon = 1e-6f)
    {
        return ApproximatelyEqual(a.X, b.X, epsilon) &&
               ApproximatelyEqual(a.Y, b.Y, epsilon) &&
               ApproximatelyEqual(a.Z, b.Z, epsilon);
    }

    protected abstract void DrawBackground(Image<Rgba32> img, RenderOptions options);
    protected abstract Rgba32 ShadeTriangle(Vector3 normal, float ao, RenderOptions options);

    // --------------------------------------------------------
    // CAMERA
    // --------------------------------------------------------
    protected (Numerics.Matrix4x4 view, Numerics.Matrix4x4 proj) BuildCameraMatrices(RenderOptions options)
    {
        var view = Numerics.Matrix4x4.CreateLookAt(
            options.CameraPosition,
            options.CameraTarget,
            options.CameraUp
        );

        Numerics.Matrix4x4 proj;
        if (options.UseOrthographic)
        {
            float s = options.OrthoSize;
            proj = Numerics.Matrix4x4.CreateOrthographicOffCenter(
                -s, s, -s, s,
                0.1f,   // TEMP: push near plane forward
                10f
            );
        }
        else
        {
            proj = Numerics.Matrix4x4.CreatePerspectiveFieldOfView(
                MathF.PI / 4f,
                (float)options.Width / options.Height,
                0.1f,
                10f
            );
        }

        return (view, proj);
    }

    // --------------------------------------------------------
    // NORMALIZATION
    // --------------------------------------------------------
    protected NormalizedMesh NormalizeMesh(Mesh mesh)
    {
        var result = new NormalizedMesh();

        // ------------------------------------------------------------
        // 1. Compute bounding box (for scale + Z grounding only)
        // ------------------------------------------------------------
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var v in mesh.Vertices)
        {
            min = Vector3.Min(min, v);
            max = Vector3.Max(max, v);
        }

        float minZ = min.Z;

        // ------------------------------------------------------------
        // 2. Compute TRUE centroid in XY (fixes backward tilt)
        // ------------------------------------------------------------
        Vector2 centroid = Vector2.Zero;
        foreach (var v in mesh.Vertices)
        {
            centroid += new Vector2(v.X, v.Y);
        }

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
            var p = v;

            // Center using centroid (NOT bounding box midpoint)
            p.X -= centroid.X;
            p.Y -= centroid.Y;

            // Ground Z
            p.Z -= minZ;

            // Normalize scale
            p *= scale;

            // Minimal shrink to keep within frame while maximizing fill
            p *= 0.98f;

            result.Vertices.Add(p);
        }

        // ------------------------------------------------------------
        // 5. Copy faces
        // ------------------------------------------------------------
        foreach (var f in mesh.Faces)
        {
            if (f.IndexCount == 3)
            {
                result.Faces.Add(f);
            }
        }

        // ------------------------------------------------------------
        // 6. prepare normals list (one per vertex) 
        // ------------------------------------------------------------
        for (int i = 0; i < result.Vertices.Count; i++)
        {
            result.Normals.Add(Vector3.Zero);
        }

        return result;
    }

    /// <summary>
    /// Computes the bounding box of a normalized mesh and stores it in RenderOptions
    /// for dynamic camera target Z calculation.
    /// </summary>
    protected void UpdateMeshBounds(NormalizedMesh mesh, RenderOptions options)
    {
        if (mesh.Vertices.Count == 0)
        {
            options.ModelBoundsMinZ = 0f;
            options.ModelBoundsMaxZ = 1f;
            return;
        }

        float minZ = mesh.Vertices[0].Z;
        float maxZ = mesh.Vertices[0].Z;

        foreach (var v in mesh.Vertices)
        {
            minZ = Math.Min(minZ, v.Z);
            maxZ = Math.Max(maxZ, v.Z);
        }

        options.ModelBoundsMinZ = minZ;
        options.ModelBoundsMaxZ = maxZ;

        Console.WriteLine($"[BOUNDS] Model Z range: {minZ:F3} to {maxZ:F3}, center: {(minZ + maxZ) / 2f:F3}");

        // Apply pending camera view now that bounds are known
        if (!string.IsNullOrWhiteSpace(options.PendingCameraView))
        {
            var viewName = options.PendingCameraView;
            options.PendingCameraView = null;
            options.SetCameraView(viewName);
        }
    }

    // Computes smooth, area‑weighted normals per vertex (no geometry smoothing, just shading).
    protected void ComputeVertexNormals(NormalizedMesh mesh, RenderOptions meshOptions)
    {
        // Precompute face normals
        var faceNormals = new Vector3[mesh.Faces.Count];
        for (int fi = 0; fi < mesh.Faces.Count; fi++)
        {
            var f = mesh.Faces[fi];
            var p0 = mesh.Vertices[f.Indices[0]];
            var p1 = mesh.Vertices[f.Indices[1]];
            var p2 = mesh.Vertices[f.Indices[2]];

            var fn = Vector3.Cross(p1 - p0, p2 - p0);
            faceNormals[fi] = fn.LengthSquared() < 1e-12f ? Vector3.UnitZ : Vector3.Normalize(fn);
        }

        // Build adjacency list (faces touching each vertex)
        var vertexFaces = new List<int>[mesh.Vertices.Count];
        for (int i = 0; i < vertexFaces.Length; i++)
        {
            vertexFaces[i] = [];
        }

        for (int fi = 0; fi < mesh.Faces.Count; fi++)
        {
            var f = mesh.Faces[fi];
            vertexFaces[f.Indices[0]].Add(fi);
            vertexFaces[f.Indices[1]].Add(fi);
            vertexFaces[f.Indices[2]].Add(fi);
        }

        float cosThresh = MathF.Cos(MathF.PI * meshOptions.NormalSmoothingAngleDeg / 180f);

        for (int v = 0; v < mesh.Vertices.Count; v++)
        {
            var faces = vertexFaces[v];
            if (faces.Count == 0)
            {
                mesh.Normals[v] = Vector3.UnitZ;
                continue;
            }

            Vector3 sum = Vector3.Zero;
            foreach (int fi in faces)
            {
                var fn = faceNormals[fi];

                if (sum == Vector3.Zero || Vector3.Dot(Vector3.Normalize(sum), fn) >= cosThresh)
                {
                    sum += fn;
                }
            }

            if (sum.LengthSquared() < 1e-12f)
            {
                sum = faceNormals[faces[0]];
            }

            mesh.Normals[v] = Vector3.Normalize(sum);
        }
    }

    // --------------------------------------------------------
    // TRIANGLE PIPELINE
    // --------------------------------------------------------

    protected List<Triangle> BuildTriangleList(NormalizedMesh mesh, Numerics.Matrix4x4 view, Numerics.Matrix4x4 proj)
    {
        var tris = new List<Triangle>(mesh.Faces.Count);

        for (int i = 0; i < mesh.Faces.Count; i++)
        {
            var f = mesh.Faces[i];

            int i0 = f.Indices[0];
            int i1 = f.Indices[1];
            int i2 = f.Indices[2];

            var p0 = mesh.Vertices[i0];
            var p1 = mesh.Vertices[i1];
            var p2 = mesh.Vertices[i2];

            // View-space positions
            Vector3 v0 = Vector3.Transform(p0, view);
            Vector3 v1 = Vector3.Transform(p1, view);
            Vector3 v2 = Vector3.Transform(p2, view);

            // View-space vertex normals
            var n0v = Vector3.Normalize(Vector3.TransformNormal(mesh.Normals[i0], view));
            var n1v = Vector3.Normalize(Vector3.TransformNormal(mesh.Normals[i1], view));
            var n2v = Vector3.Normalize(Vector3.TransformNormal(mesh.Normals[i2], view));

            // Face normal (view space)
            var faceNormal = Vector3.Normalize(Vector3.Cross(v1 - v0, v2 - v0));

            // Clip-space (pre-divide)
            Vector4 c0 = Vector4.Transform(new Vector4(v0, 1f), proj);
            Vector4 c1 = Vector4.Transform(new Vector4(v1, 1f), proj);
            Vector4 c2 = Vector4.Transform(new Vector4(v2, 1f), proj);

            // Per-vertex AO
            float ao0 = mesh.Ao.Length > i0 ? mesh.Ao[i0] : 1f;
            float ao1 = mesh.Ao.Length > i1 ? mesh.Ao[i1] : 1f;
            float ao2 = mesh.Ao.Length > i2 ? mesh.Ao[i2] : 1f;

            var clipped = ClipToNearPlane(
                new ClipVertex { C = c0, N = n0v, Ao = ao0 },
                new ClipVertex { C = c1, N = n1v, Ao = ao1 },
                new ClipVertex { C = c2, N = n2v, Ao = ao2 }
            );

            if (clipped.Count < 3)
            {
                continue;
            }

            // Triangulate fan (max 2 triangles)
            for (int k = 1; k + 1 < clipped.Count; k++)
            {
                var vA = clipped[0];
                var vB = clipped[k];
                var vC = clipped[k + 1];

                Vector4 ndcA = vA.C / vA.C.W;
                Vector4 ndcB = vB.C / vB.C.W;
                Vector4 ndcC = vC.C / vC.C.W;

                float d0 = ndcA.Z;
                float d1 = ndcB.Z;
                float d2 = ndcC.Z;

                // Compute face normal in NDC space for screen-space operations
                var ndcFaceNormal = Vector3.Cross(
                    ndcB.XYZ() - ndcA.XYZ(),
                    ndcC.XYZ() - ndcA.XYZ()
                );
                if (ndcFaceNormal.LengthSquared() > 1e-12f)
                {
                    ndcFaceNormal = Vector3.Normalize(ndcFaceNormal);
                }
                else
                {
                    ndcFaceNormal = Vector3.UnitZ;
                }

                tris.Add(new Triangle
                {
                    V0 = ndcA,
                    V1 = ndcB,
                    V2 = ndcC,

                    D0 = d0,
                    D1 = d1,
                    D2 = d2,

                    // Store NDC-space face normal for screen operations
                    FaceNormal = ndcFaceNormal,

                    // Store VIEW-SPACE face normal for silhouette detection
                    // This is the proper space for comparing with view direction
                    ViewSpaceFaceNormal = faceNormal,

                    N0 = vA.N,
                    N1 = vB.N,
                    N2 = vC.N,

                    Ao0 = vA.Ao,
                    Ao1 = vB.Ao,
                    Ao2 = vC.Ao,

                    Cz0 = vA.C.Z,
                    Cw0 = vA.C.W,
                    Cz1 = vB.C.Z,
                    Cw1 = vB.C.W,
                    Cz2 = vC.C.Z,
                    Cw2 = vC.C.W
                });
            }
        }

        return tris;
    }

    private List<ClipVertex> ClipToNearPlane(ClipVertex a, ClipVertex b, ClipVertex c)
    {
        Span<ClipVertex> input = stackalloc ClipVertex[3] { a, b, c };
        var output = new List<ClipVertex>(4);

        static float Lerp(float a, float b, float t)
        { return a + (b - a) * t; }

        for (int i = 0; i < 3; i++)
        {
            var v0 = input[i];
            var v1 = input[(i + 1) % 3];

            // Clip against Near Plane (Z >= 0 in Clip Space [0, W])
            // Previously was clipping against Far Plane (W - Z >= 0) which is wrong for Near Plane clipping
            float s0 = v0.C.Z;
            float s1 = v1.C.Z;

            bool in0 = s0 >= 0f;
            bool in1 = s1 >= 0f;

            if (in0)
            {
                output.Add(v0);
            }

            if (in0 ^ in1)
            {
                float t = s0 / (s0 - s1);

                output.Add(new ClipVertex
                {
                    C = Vector4.Lerp(v0.C, v1.C, t),
                    N = Vector3.Normalize(Vector3.Lerp(v0.N, v1.N, t)),
                    Ao = Lerp(v0.Ao, v1.Ao, t)
                });
            }
        }

        return output;
    }

    // ---------------------------------------------------------------------
    //  NDC → SCREEN
    // ---------------------------------------------------------------------

    protected Vector2 NdcToScreen(Vector4 ndc, int w, int h)
    {
        float x = (ndc.X * 0.5f + 0.5f) * (w - 1);
        float y = (1f - (ndc.Y * 0.5f + 0.5f)) * (h - 1);
        return new Vector2(x, y);
    }

    // ---------------------------------------------------------------------
    //  TRIANGLE RASTERIZATION
    // ---------------------------------------------------------------------

    protected float[,] RasterizeTriangles(Image<Rgba32> img, List<Triangle> tris, RenderOptions options)
    {
        int w = img.Width;
        int h = img.Height;

        var depth01 = new float[w, h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                depth01[x, y] = float.PositiveInfinity;
            }
        }

        static float Edge(Vector2 a, Vector2 b, Vector2 c)
        {
            return (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        }

        var frame = img.Frames.RootFrame;

        foreach (var tri in tris)
        {
            var s0 = NdcToScreen(tri.V0, w, h);
            var s1 = NdcToScreen(tri.V1, w, h);
            var s2 = NdcToScreen(tri.V2, w, h);

            float area = Edge(s0, s1, s2);

            // Backface culling; allow two-sided if configured
            if (Math.Abs(area) < 1e-6f)
            {
                continue;
            }

            float sign = 1f;
            if (area < 0f)
            {
                if (!options.TwoSided)
                {
                    continue;
                }

                sign = -1f;
                area = -area;
            }

            float invArea = 1f / area;

            int minX = (int)MathF.Floor(MathF.Min(s0.X, MathF.Min(s1.X, s2.X)));
            int maxX = (int)MathF.Ceiling(MathF.Max(s0.X, MathF.Max(s1.X, s2.X)));
            int minY = (int)MathF.Floor(MathF.Min(s0.Y, MathF.Min(s1.Y, s2.Y)));
            int maxY = (int)MathF.Ceiling(MathF.Max(s0.Y, MathF.Max(s1.Y, s2.Y)));

            minX = Math.Clamp(minX, 0, w - 1);
            maxX = Math.Clamp(maxX, 0, w - 1);
            minY = Math.Clamp(minY, 0, h - 1);
            maxY = Math.Clamp(maxY, 0, h - 1);

            // Perspective-correct depth terms
            float invW0 = 1f / tri.Cw0;
            float invW1 = 1f / tri.Cw1;
            float invW2 = 1f / tri.Cw2;

            float zOverW0 = tri.Cz0 * invW0;
            float zOverW1 = tri.Cz1 * invW1;
            float zOverW2 = tri.Cz2 * invW2;

            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);

                    float w0 = Edge(s1, s2, p) * sign;
                    float w1 = Edge(s2, s0, p) * sign;
                    float w2 = Edge(s0, s1, p) * sign;

                    if (w0 < 0f || w1 < 0f || w2 < 0f)
                    {
                        continue;
                    }

                    w0 *= invArea;
                    w1 *= invArea;
                    w2 *= invArea;

                    float invW = w0 * invW0 + w1 * invW1 + w2 * invW2;
                    if (invW <= 0f)
                    {
                        continue;
                    }

                    float zOverW = w0 * zOverW0 + w1 * zOverW1 + w2 * zOverW2;
                    float zNdc = zOverW / invW;

                    float d01 = zNdc; // System.Numerics uses [0, 1] depth range
                    if (d01 >= depth01[x, y])
                    {
                        continue;
                    }

                    depth01[x, y] = d01;

                    // Per-pixel normal + AO (screen-space barycentric; good enough for normals)
                    Vector3 n = Vector3.Normalize(w0 * tri.N0 + w1 * tri.N1 + w2 * tri.N2);
                    float ao = w0 * tri.Ao0 + w1 * tri.Ao1 + w2 * tri.Ao2;

                    frame[x, y] = ShadeTriangle(n, ao, options);
                }
            }
        }

        return depth01;
    }

    // ---------------------------------------------------------------------
    //  TRUE SILHOUETTE EDGE DETECTION (A1 subtle)
    // ---------------------------------------------------------------------

#pragma warning disable S2368 // Multidimensional arrays required for efficient image processing
    protected void DrawSilhouetteEdges(Image<Rgba32> img, List<Triangle> tris, float[,] depth01, RenderOptions options)
#pragma warning restore S2368
    {
        int w = img.Width;
        int h = img.Height;

        static float Cross2D(Vector2 a, Vector2 b, Vector2 c)
        {
            return (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        }

        static (PointF, PointF) MakeEdgeKey(PointF a, PointF b)
        {
            if (a.X < b.X || (Math.Abs(a.X - b.X) < 0.0001f && a.Y <= b.Y))
            {
                return (a, b);
            }

            return (b, a);
        }

        // In view space, the camera looks down -Z axis, so view direction is (0, 0, -1)
        // Front-facing triangles have normals pointing toward camera (+Z in view space)
        Vector3 viewSpaceViewDir = new Vector3(0f, 0f, -1f);

        // edge -> list of triangle contributions
        var edgeMap = new Dictionary<(PointF, PointF), List<(Vector3 faceNormal, bool frontFacing, Vector2 s0, Vector2 s1, Vector2 s2, float d0, float d1, float d2)>>();

        float ndcEpsilon = 1e-4f;

        for (int i = 0; i < tris.Count; i++)
        {
            var tri = tris[i];

            var s0 = NdcToScreen(tri.V0, w, h);
            var s1 = NdcToScreen(tri.V1, w, h);
            var s2 = NdcToScreen(tri.V2, w, h);

            if ((s0 - s1).LengthSquared() < ndcEpsilon ||
                (s1 - s2).LengthSquared() < ndcEpsilon ||
                (s2 - s0).LengthSquared() < ndcEpsilon)
            {
                continue;
            }

            float winding = Cross2D(s0, s1, s2);
            if (winding <= 0f)
            {
                continue;
            }

            // Use VIEW-SPACE face normal with VIEW-SPACE view direction for correct comparison
            // Front-facing: normal points toward camera (positive Z), viewDir is -Z, so dot < 0
            float ndotv = Vector3.Dot(tri.ViewSpaceFaceNormal, viewSpaceViewDir);
            bool frontFacing = ndotv < 0f;

            var p0 = new PointF(s0.X, s0.Y);
            var p1 = new PointF(s1.X, s1.Y);
            var p2 = new PointF(s2.X, s2.Y);

            float d0 = tri.D0;
            float d1 = tri.D1;
            float d2 = tri.D2;

            void RegisterEdge((PointF, PointF) edgeKey)
            {
                if (!edgeMap.TryGetValue(edgeKey, out var list))
                {
                    list = new List<(Vector3, bool, Vector2, Vector2, Vector2, float, float, float)>(2);
                    edgeMap[edgeKey] = list;
                }

                // Store VIEW-SPACE face normal for consistent silhouette calculations
                list.Add((tri.ViewSpaceFaceNormal, frontFacing, s0, s1, s2, d0, d1, d2));
            }

            RegisterEdge(MakeEdgeKey(p0, p1));
            RegisterEdge(MakeEdgeKey(p1, p2));
            RegisterEdge(MakeEdgeKey(p2, p0));
        }

        img.Mutate(ctx =>
        {
            var color = options.SilhouetteColor;
            float width = options.SilhouetteEdgeWidth <= 0f ? 1.0f : options.SilhouetteEdgeWidth;

            foreach (var kvp in edgeMap)
            {
                var edgeKey = kvp.Key;
                var list = kvp.Value;

                bool isSilhouette = false;

                if (list.Count == 1)
                {
                    // Boundary edges: only if face is near perpendicular to view (contour-like)
                    // Use view-space normal with view-space view direction
                    var n = Vector3.Normalize(list[0].faceNormal);
                    float ndotvAbs = MathF.Abs(Vector3.Dot(n, viewSpaceViewDir));
                    if (ndotvAbs < 0.25f)
                    {
                        isSilhouette = true;
                    }
                }
                else if (list.Count == 2)
                {
                    bool f0 = list[0].frontFacing;
                    bool f1 = list[1].frontFacing;

                    if (f0 != f1)
                    {
                        var n0 = Vector3.Normalize(list[0].faceNormal);
                        var n1 = Vector3.Normalize(list[1].faceNormal);
                        float ndot = Vector3.Dot(n0, n1);

                        if (ndot < 0.5f)
                        {
                            isSilhouette = true;
                        }
                    }
                }

                if (!isSilhouette)
                {
                    continue;
                }

                // Depth-aware: test midpoint against z-buffer
                var (a, b) = edgeKey;
                var mid = new Vector2((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f);

                int mx = (int)MathF.Floor(mid.X);
                int my = (int)MathF.Floor(mid.Y);
                if ((uint)mx >= (uint)w || (uint)my >= (uint)h)
                {
                    continue;
                }

                // Choose a front-facing entry if possible (more likely to be visible surface)
                var entry = list.Count == 2
                    ? (list[0].frontFacing ? list[0] : list[1])
                    : list[0];

                float zbuf = depth01[mx, my];
                if (zbuf >= 1f)
                {
                    continue; // background, edge not visible
                }

                var pb = new PathBuilder();
                pb.AddLine(a, b);
                ctx.Draw(color, width, pb.Build());
            }
        });
    }

    // ---------------------------------------------------------------------
    //  END OF CLASS
    // ---------------------------------------------------------------------
}
