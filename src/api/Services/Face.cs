using System.Collections.Generic;
using System.Numerics;
using SixLabors.ImageSharp.PixelFormats;

namespace Farm.Web.Api.Services;

public sealed class Face
{
    public int[] Indices { get; set; } = [];

    public int FaceIndex { get; set; }

    public int IndexCount => Indices?.Length ?? 0;
}
