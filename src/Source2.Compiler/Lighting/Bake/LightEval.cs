using System.Numerics;

namespace Source2.Compiler.Lighting.Bake;

/// <summary>
/// The light functions rtx_vrad3_lightmap inlines at every call site (one
/// evaluation of PathTraceBarnLightGPU_t at a point, one luminaire sample,
/// the BRDF), kept in the shader's operation order.
/// </summary>
internal static class LightEval
{
    private const float InvPi = 0.31830987f, Pi = 3.1415927f, TwoPi = 6.2831855f;

    /// <summary>
    /// The light's intensity at <paramref name="p"/>: zero outside its clip
    /// volume ([-1, 1] x [-1, 1] x [0, 1]) or its illumination box; the bounce
    /// or direct colour, a luminaire's radiance factor over pi or a point
    /// light's falloff, the distance fades, the angle fade (punctual, sphere and
    /// tube), the shape mask without a cookie. Cookies are not sampled.
    /// </summary>
    public static Vector3 Intensity(Light l, Vector3 p, bool bounce)
    {
        var clip = Light.Apply(l.ToClipSpace, p);
        var c = new Vector3(clip.X / clip.W, clip.Y / clip.W, clip.Z / clip.W);
        if (!(c.X >= -1f && c.Y >= -1f && c.Z >= 0f && c.X <= 1f && c.Y <= 1f && c.Z <= 1f))
            return Vector3.Zero;
        if (!l.InsideIllumination(p))
            return Vector3.Zero;
        var col = bounce ? new Vector3(l.BounceColor.X, l.BounceColor.Y, l.BounceColor.Z) : new Vector3(l.Color.X, l.Color.Y, l.Color.Z);
        if (l.LuminaireType != 0)
            col = col * l.Luminaire[0].W * InvPi;
        else if (l.Position.W != 0f)
        {
            var d = new Vector3(l.Position.X, l.Position.Y, l.Position.Z) - p;
            col *= l.Position.W / MathF.Max(Vector3.Dot(d, d), l.Position.W);
        }
        var dfs = l.DistanceFadeSkirt;
        var fade = dfs.Z > 0f ? SmoothStep(0f, 1f, c.Z * dfs.Z) : 1f;
        if (dfs.W > 0f)
            fade *= SmoothStep(0f, 1f, (1f - c.Z) * dfs.W);
        float f;
        if (l.Position.W != 0f)
        {
            var d = new Vector3(l.Position.X, l.Position.Y, l.Position.Z) - p;
            var len = d.Length();
            var dir = d / len;
            var f2 = fade * SmoothStep(0f, 1f, dfs.X + dfs.Y * len);
            if (l.LuminaireType != 2 && l.LuminaireType != 1)
            {
                var q = l.Orientation;
                var axis = new Vector3(2f * q.X * q.Y - 2f * q.W * q.Z, (1f - 2f * q.X * q.X) - 2f * q.Z * q.Z, 2f * q.Y * q.Z + 2f * q.W * q.X) * l.AngleFade.Z;
                var a = dir - axis;
                var b = dir + axis;
                var ab = b - a;
                var t = Vector3.Dot(-a, ab);
                Vector3 v;
                if (t <= 0f)
                    v = a;
                else
                {
                    var abab = Vector3.Dot(ab, ab);
                    v = t > abab ? b : a + ab * (t / abab);
                }
                var nv = v != Vector3.Zero ? Vector3.Normalize(v) : v;
                var fwd = new Vector3((1f - 2f * q.Y * q.Y) - 2f * q.Z * q.Z, 2f * q.X * q.Y + 2f * q.W * q.Z, 2f * q.X * q.Z - 2f * q.W * q.Y);
                f = f2 * Saturate(l.AngleFade.X + l.AngleFade.Y * Vector3.Dot(fwd, nv));
            }
            else
                f = f2;
        }
        else
            f = fade;
        if (l.Cookie == -1)
        {
            var ax = MathF.Abs(c.X);
            var ay = MathF.Abs(c.Y);
            var cp = l.CookieParameters;
            if (cp.Z == 0f)
                f *= SmoothStep(1f, cp.X, ax) * SmoothStep(1f, cp.Y, ay);
            else
            {
                var e = -0.5f * cp.Z;
                var outer = cp.X * cp.Y * MathF.Pow(MathF.Max(MathF.Pow(cp.Y * ax, 2f / cp.Z) + MathF.Pow(cp.X * ay, 2f / cp.Z), 1.1754944e-38f), e);
                var inner = MathF.Pow(MathF.Max(MathF.Pow(ax, 2f / cp.Z) + MathF.Pow(ay, 2f / cp.Z), 1.1754944e-38f), e);
                f *= outer < inner ? SmoothStep(inner, outer, 1f) : inner > 1f ? 1f : 0f;
            }
        }
        return col * f;
    }

    /// <summary>
    /// A sample on the luminaire seen from <paramref name="p"/>: the direction
    /// (xyz) and distance (w, infinite for a directional light) and the factor
    /// the intensity is divided by (1 / the solid-angle pdf up to the radiance
    /// convention): punctual 1; quad a spherical rectangle; disc concentric,
    /// d^2 / (cos area); sphere a cone, 1 / (2 pi (1 - cos max)); tube a side or cap.
    /// </summary>
    public static (Vector4 Dir, float Factor) Sample(Light l, Vector3 p, Vector2 u)
    {
        Vector4 Towards(Vector3 point)
        {
            if (l.Position.W == 0f)
                return new Vector4(Vector3.Normalize(point), float.PositiveInfinity);
            var d = point - p;
            var len = d.Length();
            return new Vector4(d / len, len);
        }
        var L = l.Luminaire;
        switch (l.LuminaireType)
        {
            case 0:
            {
                if (l.Position.W == 0f)
                    return (new Vector4(Vector3.Normalize(new Vector3(l.Position.X, l.Position.Y, l.Position.Z)), float.PositiveInfinity), 1f);
                var d = new Vector3(l.Position.X, l.Position.Y, l.Position.Z) - p;
                var len = d.Length();
                return (new Vector4(d / len, len), 1f);
            }
            case 1:
            {
                var e0 = V3(L[1]) - V3(L[0]);
                var e1 = V3(L[2]) - V3(L[0]);
                var l0 = e0.Length();
                var l1 = e1.Length();
                var x = e0 / l0;
                var y = e1 / l1;
                var z = Vector3.Cross(x, y);
                var o = V3(L[0]) - p;
                var z0 = Vector3.Dot(o, z);
                if (z0 > 0f)
                {
                    z0 *= -1f;
                    z *= -1f;
                }
                var x0 = Vector3.Dot(o, x);
                var y0 = Vector3.Dot(o, y);
                var x1 = x0 + l0;
                var y1 = y0 + l1;
                var v00 = new Vector3(x0, y0, z0);
                var v01 = new Vector3(x0, y1, z0);
                var v10 = new Vector3(x1, y0, z0);
                var v11 = new Vector3(x1, y1, z0);
                var n0 = Vector3.Normalize(Vector3.Cross(v00, v10));
                var n1 = Vector3.Normalize(Vector3.Cross(v10, v11));
                var n2 = Vector3.Normalize(Vector3.Cross(v11, v01));
                var n3 = Vector3.Normalize(Vector3.Cross(v01, v00));
                var b0 = n0.Z;
                var k = (TwoPi - MathF.Acos(Math.Clamp(-Vector3.Dot(n2, n3), -1f, 1f))) - MathF.Acos(Math.Clamp(-Vector3.Dot(n3, n0), -1f, 1f));
                var s = (MathF.Acos(Math.Clamp(-Vector3.Dot(n0, n1), -1f, 1f)) + MathF.Acos(Math.Clamp(-Vector3.Dot(n1, n2), -1f, 1f))) - k;
                if (float.IsNaN(s))
                    s = 0f;
                var au = u.X * s + k;
                var fu = (MathF.Cos(au) * b0 - n2.Z) / MathF.Sin(au);
                var sign = fu > 0f ? 1f : -1f;
                var cu = Math.Clamp(1f / MathF.Sqrt(fu * fu + b0 * b0) * sign, -1f, 1f);
                var xu = Math.Clamp(-(cu * z0) / MathF.Sqrt(1f - cu * cu), x0, x1);
                var d = MathF.Sqrt(xu * xu + z0 * z0);
                var d2 = d * d;
                var h0 = y0 / MathF.Sqrt(d2 + y0 * y0);
                var hv = h0 + u.Y * (y1 / MathF.Sqrt(d2 + y1 * y1) - h0);
                var hv2 = hv * hv;
                var yv = hv2 < 0.99999988f ? hv * d / MathF.Sqrt(1f - hv2) : y1;
                var point = ((p + x * xu) + y * yv) + z * z0;
                return (Towards(point), 1f / s);
            }
            case 2:
            {
                var c = Concentric(u);
                var point = (V3(L[0]) + V3(L[1]) * c.X) + V3(L[2]) * c.Y;
                var dir = Towards(point);
                var dd = float.IsInfinity(dir.W) || float.IsNaN(dir.W) ? 1f : dir.W * dir.W;
                return (dir, dd / (MathF.Max(0f, Vector3.Dot(V3(L[3]), -new Vector3(dir.X, dir.Y, dir.Z))) * L[1].W));
            }
            case 3:
            {
                var o = V3(L[0]) - p;
                var dd = Vector3.Dot(o, o);
                var dist = MathF.Sqrt(dd);
                var w = o / dist;
                var (t, b) = Basis(w);
                var r2 = L[1].X * L[1].X;
                var cosMax = MathF.Sqrt(MathF.Max(0f, 1f - r2 / dd));
                var cosT = (1f - u.X) + u.X * cosMax;
                var sinT = MathF.Sqrt(MathF.Max(0f, 1f - cosT * cosT));
                var phi = u.Y * TwoPi;
                var dist2 = dist * dist;
                var ds = dist * cosT - MathF.Sqrt(MathF.Max(0f, r2 - dist2 * sinT * sinT));
                var cosA = ((dist2 + r2) - ds * ds) / (2f * dist * L[1].X);
                var sinA = MathF.Sqrt(MathF.Max(0f, 1f - cosA * cosA));
                var local = new Vector3(MathF.Cos(phi) * sinA, MathF.Sin(phi) * sinA, cosA);
                var point = V3(L[0]) + (-t * local.X - b * local.Y - w * local.Z) * L[1].X;
                return (Towards(point), 0.15915494f / (1f - cosMax));
            }
            case 4 or 5:
            {
                var uu = u;
                Vector3 normal, point;
                if (u.X < L[4].Y)
                {
                    uu.X = Math.Clamp(uu.X / L[4].Y, 0f, 0.99999988f);
                    var axis = Vector3.Normalize(V3(L[0]) - V3(L[1]));
                    if (uu.X >= 0.5f)
                    {
                        uu.X = Math.Clamp(2f * uu.X - 1f, 0f, 0.99999988f);
                        normal = -axis;
                        point = V3(L[1]);
                    }
                    else
                    {
                        uu.X = Math.Clamp(2f * uu.X, 0f, 0.99999988f);
                        normal = axis;
                        point = V3(L[0]);
                    }
                    var cc = Concentric(uu);
                    point += V3(L[2]) * cc.X + V3(L[3]) * cc.Y;
                }
                else
                {
                    uu.X = Math.Clamp((uu.X - L[4].Y) / (1f - L[4].Y), 0f, 0.99999988f);
                    var side = V3(L[2]) * MathF.Cos(uu.X * TwoPi) + V3(L[3]) * MathF.Sin(uu.X * TwoPi);
                    normal = Vector3.Normalize(side);
                    point = Vector3.Lerp(V3(L[0]), V3(L[1]), uu.Y) + side;
                }
                var dir = Towards(point);
                var dd = float.IsInfinity(dir.W) || float.IsNaN(dir.W) ? 1f : dir.W * dir.W;
                return (dir, dd / (MathF.Max(0f, Vector3.Dot(normal, -new Vector3(dir.X, dir.Y, dir.Z))) * L[1].W));
            }
            default:
                return (new Vector4(float.NaN), 0f);
        }
    }

    /// <summary>
    /// The BRDF times nothing (the caller multiplies by pi and the cosine):
    /// from behind, the transmissive colour (or 1) over pi; from the front,
    /// Lambert (1 - metalness) albedo / pi plus GGX with Smith's height-correlated
    /// visibility and Schlick's Fresnel from mix(reflectance, albedo, metalness).
    /// </summary>
    public static Vector3 Brdf(bool backside, in Payload m, Vector3 wi, Vector3 wo, float roughness)
    {
        if (backside)
        {
            var t = (m.MaterialTypeAndFlags & 0xffff) == 1 && (m.MaterialBits.X > 0f || m.MaterialBits.Y > 0f || m.MaterialBits.Z > 0f)
                ? new Vector3(m.MaterialBits.X, m.MaterialBits.Y, m.MaterialBits.Z) : Vector3.One;
            return t * InvPi;
        }
        var f0 = Vector3.Lerp(new Vector3(m.Reflectance), m.Albedo, m.Metalness);
        var spec = Ggx(wi, wo, f0, roughness);
        return m.Albedo * (1f - m.Metalness) * InvPi + spec;
    }

    public static Vector3 Ggx(Vector3 wi, Vector3 wo, Vector3 f0, float roughness)
    {
        if (wi.Z <= 0f || wo.Z <= 0f)
            return Vector3.Zero;
        var h = wi + wo;
        if (h == Vector3.Zero)
            return Vector3.Zero;
        h = Vector3.Normalize(h);
        var hz = h.Z;
        var r2 = roughness * roughness;
        var a2 = r2 * r2;
        var d = (hz * a2 - hz) * hz + 1f;
        var fresnel = f0 + (Vector3.One - f0) * MathF.Pow(MathF.Max(0f, 1f - Vector3.Dot(wi, h)), 5f);
        var vis = 0.5f / MathF.Max(9.9999997e-06f, wi.Z * MathF.Sqrt((wo.Z - a2 * wo.Z) * wo.Z + a2) + wo.Z * MathF.Sqrt((wi.Z - a2 * wi.Z) * wi.Z + a2));
        return fresnel * (a2 / MathF.Max(9.9999999e-09f, d * d) * vis * InvPi);
    }

    /// <summary>The shaders' orthonormal basis about a unit normal (t, b; n is the third axis).</summary>
    public static (Vector3 T, Vector3 B) Basis(Vector3 n)
    {
        var s = n.Z >= 0f ? 1f : -1f;
        var a = -1f / (s + n.Z);
        var b = n.X * n.Y * a;
        return (new Vector3(1f + s * n.X * n.X * a, s * b, -s * n.X), new Vector3(b, s + n.Y * n.Y * a, -n.Y));
    }

    /// <summary>mat3(t, b, n) * v.</summary>
    public static Vector3 ToWorld(Vector3 t, Vector3 b, Vector3 n, Vector3 v) => t * v.X + b * v.Y + n * v.Z;

    /// <summary>transpose(mat3(t, b, n)) * v.</summary>
    public static Vector3 ToLocal(Vector3 t, Vector3 b, Vector3 n, Vector3 v) => new(Vector3.Dot(t, v), Vector3.Dot(b, v), Vector3.Dot(n, v));

    public static Vector3 CosineSample(Vector2 u)
    {
        var r = MathF.Sqrt(u.Y);
        var phi = TwoPi * u.X;
        return new Vector3(MathF.Cos(phi) * r, MathF.Sin(phi) * r, MathF.Sqrt(MathF.Max(0f, 1f - u.Y)));
    }

    public static Vector2 Concentric(Vector2 u)
    {
        var o = u * 2f - Vector2.One;
        if (o == Vector2.Zero)
            return Vector2.Zero;
        float r, theta;
        if (MathF.Abs(o.X) > MathF.Abs(o.Y))
        {
            r = o.X;
            theta = 0.7853982f * (o.Y / o.X);
        }
        else
        {
            r = o.Y;
            theta = 1.5707964f - 0.7853982f * (o.X / o.Y);
        }
        return new Vector2(MathF.Cos(theta), MathF.Sin(theta)) * r;
    }

    /// <summary>The shaders' reservoir coin: true with probability p, the random number rescaled to stay uniform.</summary>
    public static bool Coin(float p, ref float u)
    {
        if (p <= 0f)
            return false;
        if (p >= 1f)
            return true;
        if (u < p)
        {
            u = MathF.Min(u / p, 0.99999988f);
            return true;
        }
        u = MathF.Min((u - p) / (1f - p), 0.99999988f);
        return false;
    }

    public static float Luminance(Vector3 c) => Vector3.Dot(c, new Vector3(0.2125f, 0.7154f, 0.0721f));

    public static float SmoothStep(float e0, float e1, float x)
    {
        var t = Saturate((x - e0) / (e1 - e0));
        return t * t * (3f - 2f * t);
    }

    public static float Saturate(float x) => x < 0f ? 0f : x > 1f ? 1f : x;

    public static Vector3 V3(Vector4 v) => new(v.X, v.Y, v.Z);

    /// <summary>The shaders' self-intersection offset: p + n (|p| + max|p|) 1e-4, componentwise.</summary>
    public static Vector3 Offset(Vector3 p, Vector3 n)
    {
        var a = Vector3.Abs(p);
        var m = MathF.Max(a.X, MathF.Max(a.Y, a.Z));
        return p + n * ((a + new Vector3(MathF.Abs(m))) * 9.9999997e-05f);
    }
}
