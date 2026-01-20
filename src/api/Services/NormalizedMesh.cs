using System.Collections.Generic;
using System.Numerics;
using SixLabors.ImageSharp.PixelFormats;

namespace Farm.Web.Api.Services;

public sealed class NormalizedMesh
{
    public List<Vector3> Vertices { get; } = new();

    public List<Face> Faces { get; } = new();

    public float[] Ao { get; set; } = [];

    public List<Vector3> Normals { get; } = new();
}
