using System.Numerics;

namespace Farm.Web.Api.Services;

#pragma warning disable S1104 // Struct fields used for performance in rendering pipeline
#pragma warning disable CA1815 // Override equals and operator equals on value types
#pragma warning disable CA1051
public struct ClipVertex
{
    public Vector4 C;   // clip-space position
    public Vector3 N;   // view-space normal
    public float Ao;
}
