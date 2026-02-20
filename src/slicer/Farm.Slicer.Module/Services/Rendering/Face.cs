using System.Collections.Generic;
using System.Numerics;
using SixLabors.ImageSharp.PixelFormats;

namespace Farm.Slicer.Module.Services.Rendering;

public sealed class Face
{
    public int[] Indices { get; set; } = [];

    public int FaceIndex { get; set; }

    public int IndexCount => Indices?.Length ?? 0;
}
