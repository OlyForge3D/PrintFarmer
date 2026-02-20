using System.Collections.Generic;
using System.Numerics;
using SixLabors.ImageSharp.PixelFormats;

namespace Farm.Slicer.Module.Services.Rendering;

public sealed class Mesh
{
    public List<Vector3> Vertices { get; } = new();

    public List<Face> Faces { get; } = new();
}
