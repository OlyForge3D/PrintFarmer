using System.Collections.Generic;
using System.Numerics;
using SixLabors.ImageSharp.PixelFormats;

namespace Farm.Web.Api.Services;

public static class VectorExtensions
{
    public static Vector3 XYZ(this Vector4 v)
    {
        return new Vector3(v.X, v.Y, v.Z);
    }
}
