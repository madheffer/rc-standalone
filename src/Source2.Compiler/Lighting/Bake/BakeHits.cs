using System.Numerics;

namespace Source2.Compiler.Lighting.Bake;

/// <summary>The ray payload of vrad3's shaders (Payload_t with MaterialDataMinimal_t).</summary>
public struct Payload
{
    public Vector3 SmoothPosition, FaceNormal, InterpolatedNormal;
    public float HitT, OpacityRng;
    public int Instance;
    public bool Flipped;
    public Vector3 Normal, Albedo, Emissive;
    public float Metalness, Roughness, Reflectance;
    public uint MaterialTypeAndFlags;
    public Vector4 MaterialBits;

    /// <summary>The payload every traceRayEXT starts from.</summary>
    public static Payload Initial(float opacityRng) => new()
    {
        SmoothPosition = new Vector3(float.MaxValue), HitT = float.MaxValue, OpacityRng = opacityRng, Instance = -1,
        Albedo = new Vector3(1f, 0f, 1f), Roughness = 1f, Reflectance = 0.040008545f,
    };
}

/// <summary>
/// The hit group for every material, rtx_default_hitgroup's closest hit:
/// normals interpolated and taken to world by the inverse transpose, the face
/// normal cross(v1 - v0, v2 - v0), all three flipped for a back-facing hit on
/// a double-sided material; the smooth position (Hanika's shadow terminator
/// fix: the hit pushed along each corner's tangent plane, kept when it rises
/// over the face by more than 0.01, else FLT_MAX); the albedo the colour
/// texture at mip 0 times the material tint times the instance colour.
/// S_TRANSLUCENT's any-hit: the texture alpha against the payload's opacity
/// random number, which it rescales.
/// </summary>
public sealed class BakeHits(BakeScene scene)
{
    private readonly BakeScene _scene = scene;

    /// <summary>The transpiled material hit groups; instances without one keep the default hit group below.</summary>
    public RtxScene? Rtx { get; set; }

    /// <summary>A scale on every hit's emission (1 as vrad3 bakes it; experiments set 0 to measure it).</summary>
    public float Emission { get; set; } = 1f;

    private (BakeScene.Instance Inst, BakeScene.Mesh Mesh, int I0, int I1, int I2) Corners(int triangle)
    {
        var t = _scene.Rays.TriangleAt(triangle);
        var inst = _scene.Instances[t.Instance];
        var m = _scene.Meshes[inst.Mesh];
        return (inst, m, m.Indices[t.Primitive * 3], m.Indices[t.Primitive * 3 + 1], m.Indices[t.Primitive * 3 + 2]);
    }

    public int InstanceOf(int triangle) => _scene.Rays.TriangleAt(triangle).Instance;

    /// <summary>The material's opacity at a candidate hit.</summary>
    public float Opacity(int triangle, float u, float v)
    {
        var (inst, m, i0, i1, i2) = Corners(triangle);
        var mat = inst.Material;
        if (mat.Color == null)
            return mat.Opacity;
        var uv = m.Texcoords[i0] * (1f - u - v) + m.Texcoords[i1] * u + m.Texcoords[i2] * v;
        return mat.Color.Sample(uv).W;
    }

    /// <summary>The S_TRANSLUCENT any-hit: accept with probability opacity, rescaling the random number.</summary>
    public static bool StochasticOpacity(float opacity, ref float rng)
    {
        if (opacity <= 0f)
            return false;
        if (opacity >= 1f)
            return true;
        if (rng < opacity)
        {
            rng = MathF.Min(rng / opacity, 0.99999988f);
            return true;
        }
        rng = MathF.Min((rng - opacity) / (1f - opacity), 0.99999988f);
        return false;
    }

    /// <summary>The closest-hit shader's payload for an accepted hit.</summary>
    public void Shade(ref Payload p, in RayScene.Hit hit)
    {
        if (Rtx != null)
        {
            var tri = _scene.Rays.TriangleAt(hit.Triangle);
            if (Rtx.Shade(ref p, hit, tri.Instance, tri.Primitive, default, default))
            {
                if (Emission != 1f)
                    p.Emissive *= Emission;
                return;
            }
        }
        var (inst, m, i0, i1, i2) = Corners(hit.Triangle);
        float b0 = 1f - hit.U - hit.V, b1 = hit.U, b2 = hit.V;
        var w2o = inst.WorldToObject;
        Vector3 ToWorldNormal(Vector3 n)
        {
            // transpose(gl_WorldToObjectEXT) * n: the inverse transpose of object to world.
            var r = new Vector3(w2o.M11 * n.X + w2o.M12 * n.Y + w2o.M13 * n.Z, w2o.M21 * n.X + w2o.M22 * n.Y + w2o.M23 * n.Z, w2o.M31 * n.X + w2o.M32 * n.Y + w2o.M33 * n.Z);
            return r;
        }
        var n0 = m.Normals[i0];
        var n1 = m.Normals[i1];
        var n2 = m.Normals[i2];
        var interp = n0 * b0 + n1 * b1 + n2 * b2;
        var normal = interp == Vector3.Zero ? Vector3.Zero : Vector3.Normalize(ToWorldNormal(interp));
        var p0 = m.Positions[i0];
        var p1 = m.Positions[i1];
        var p2 = m.Positions[i2];
        var faceObject = Vector3.Cross(p1 - p0, p2 - p0);
        var face = Vector3.Normalize(ToWorldNormal(faceObject));
        p.HitT = hit.T;
        p.Normal = normal;
        p.InterpolatedNormal = normal;
        p.FaceNormal = face;
        p.Flipped = false;
        if ((inst.MaterialFlags & 1) != 0 && !hit.FrontFacing)
        {
            p.FaceNormal = -p.FaceNormal;
            p.InterpolatedNormal = -p.InterpolatedNormal;
            p.Normal = -p.Normal;
            p.Flipped = true;
        }
        p.SmoothPosition = SmoothPosition(p0, p1, p2, n0, n1, n2, b0, b1, b2, inst.ObjectToWorld);
        var albedo = inst.Material.ColorTint;
        if (inst.Material.Color != null)
        {
            var uv = m.Texcoords[i0] * b0 + m.Texcoords[i1] * b1 + m.Texcoords[i2] * b2;
            var s = inst.Material.Color.Sample(uv);
            albedo = new Vector3(s.X, s.Y, s.Z) * inst.Material.ColorTint;
        }
        p.Albedo = albedo * inst.Tint;
        p.Instance = _scene.Rays.TriangleAt(hit.Triangle).Instance;
    }

    // The closest hit's smooth position, in object space then to world.
    private static Vector3 SmoothPosition(Vector3 v0, Vector3 v1, Vector3 v2, Vector3 n0, Vector3 n1, Vector3 n2, float b0, float b1, float b2, Matrix4x4 o2w)
    {
        var face = Vector3.Normalize(Vector3.Cross(v1 - v0, v2 - v0));
        Span<Vector3> v = [v0, v1, v2];
        Span<Vector3> n = [Safe(n0), Safe(n1), Safe(n2)];
        var hit = v0 * b0 + v1 * b1 + v2 * b2;
        Span<Vector3> proj = stackalloc Vector3[3];
        for (var i = 0; i < 3; i++)
            proj[i] = hit - n[i] * Vector3.Dot(hit - v[i], n[i]);
        var smooth = proj[0] * b0 + proj[1] * b1 + proj[2] * b2;
        Span<Vector3> edge = [Vector3.Cross(v0 - v1, face), Vector3.Cross(v1 - v2, face), Vector3.Cross(v2 - v0, face)];
        var rise = Vector3.Dot(smooth - hit, face);
        for (var e = 0; e < 3; e++)
        {
            if (rise > 0.01f)
                break;
            if (Vector3.Dot(edge[e], edge[e]) == 0f)
                continue;
            edge[e] = Vector3.Normalize(edge[e]);
            for (var i = 0; i < 3; i++)
            {
                var t = n[i] - edge[e] * Vector3.Dot(edge[e], n[i]);
                proj[i] = hit - t * Vector3.Dot(hit - v[i], t);
            }
            smooth = proj[0] * b0 + proj[1] * b1 + proj[2] * b2;
            rise = Vector3.Dot(smooth - hit, face);
        }
        if (!(rise > 0.01f))
            return new Vector3(float.MaxValue);
        return Vector3.Transform(smooth, o2w);
    }

    private static Vector3 Safe(Vector3 n) => n;
}
