using System.Numerics;

namespace Farm.Web.Api.Services;

#pragma warning disable CA1051
#pragma warning disable CA1815
#pragma warning disable S1104 // Struct fields used for performance in rendering pipeline
public struct Triangle
{
    public Vector4 V0;
    public Vector4 V1;
    public Vector4 V2;

    // Shading
    public Vector3 Normal;        // keep if you want (optional after per-vertex)
    public float Ao;              // keep if you want (optional after per-vertex)

    // Silhouette / facing - stored in VIEW SPACE for correct comparisons
    public Vector3 FaceNormal;
    public Vector3 ViewSpaceFaceNormal;

    // Per-vertex shading inputs (view-space normals + AO)
    public Vector3 N0, N1, N2;
    public float Ao0, Ao1, Ao2;

    // Clip-space (pre-divide), used for perspective-correct depth
    public float Cz0, Cw0;
    public float Cz1, Cw1;
    public float Cz2, Cw2;

    public float D0, D1, D2; // depth in [0,1]
}
