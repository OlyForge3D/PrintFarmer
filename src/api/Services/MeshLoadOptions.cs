using System.Collections.Generic;
using System.Numerics;
using SixLabors.ImageSharp.PixelFormats;

namespace Farm.Web.Api.Services;

public sealed class MeshLoadOptions
{
    public bool MergeMeshes { get; set; } = true;

    public bool UseZUp { get; set; } = true;   // false = Y-up
}
