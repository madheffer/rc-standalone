using System.Globalization;
using System.Numerics;
using System.Text;

namespace Source2.Compiler.Maps;

/// <summary>
/// The baked shadow slot assignment of a map compile with baked lighting
/// (CResourceCompilerMap_AssignBakedShadowIndices, resourcecompiler 0923).
/// Stationary lights (directlight 3) that cast shadows share four baked
/// shadow channels: overlapping lights must take different ones. The step
/// writes, per light:
/// <list type="bullet">
/// <item>directlight 2 on a stationary light that casts no shadow;</item>
/// <item>light_path_uniqueid, light_map_uniqueid and bakedshadowindex on each
/// stationary shadowed light, or, when no channel is free, desiredstationary 1,
/// bakedshadowindex -1, directlight 1 and precomputedfieldsvalid 0.</item>
/// </list>
/// The step runs only when baked lighting is available to the compile and
/// the game's LightmapChannels/direct_light_shadows is set (CS2: 1).
/// </summary>
public static class BakedShadowAssignment
{
    /// <summary>
    /// One light as the gather lists it (MapWorld_GatherLights): the world's
    /// "light_*" entities in the world's entity order, hidden ones skipped,
    /// then those of the worlds it references; one entry per placement.
    /// </summary>
    /// <param name="ClassName">The entity class.</param>
    /// <param name="Key">The light's keys, case-blind, null when absent.</param>
    /// <param name="World">The placement's 3x4: the instance step matrix times the node's own.</param>
    /// <param name="IdPath">The node's id path (node +0x310): the inline ids, a plain node's
    /// being its hammer id.</param>
    /// <param name="IdPathHeap">The id path's heap part, hashed first; empty for a plain node.</param>
    public sealed record Light(string ClassName, LightPrecompute.KeyReader Key, float[] World, int[] IdPath,
                               int[]? IdPathHeap = null);

    /// <summary>What the step does to one light.</summary>
    /// <param name="Slot">The shadow channel (0 to 3), -1 when none was free, null when the
    /// light took no part.</param>
    /// <param name="Keys">The keys written, in write order (the lump lists new keys in reverse).</param>
    public sealed record Result(int? Slot, IReadOnlyList<KeyValuePair<string, string>> Keys);

    /// <summary>
    /// Runs the step. <paramref name="mapPath"/> is the map document's
    /// content-relative path with its extension and forward slashes
    /// (<c>maps/probe_classes.vmap</c>). Returns one result per light, in
    /// input order.
    /// </summary>
    public static IReadOnlyList<Result> Assign(IReadOnlyList<Light> lights, string mapPath, bool baked = true,
                                                bool directLightShadows = true)
    {
        var results = new List<(int? Slot, List<KeyValuePair<string, string>> Keys)>();
        foreach (var _ in lights)
            results.Add((null, []));
        if (!baked || !directLightShadows)
            return [.. results.Select(r => new Result(r.Slot, r.Keys))];

        // The rewrite loop of 1800fe7b0: a stationary light that casts no
        // shadow gets directlight 2 (MapNode_SetKeyInt, "%d").
        var modes = new (int Mode, int CastShadows)[lights.Count];
        for (var i = 0; i < lights.Count; i++)
        {
            modes[i] = Mode(lights[i]);
            if (modes[i].Mode == 3 && modes[i].CastShadows == 0)
                results[i].Keys.Add(new("directlight", "2"));
        }

        // FUN_180f45900 per light: stationary and casting shadows.
        var records = new List<Record>();
        for (var i = 0; i < lights.Count; i++)
            if (modes[i].Mode == 3 && modes[i].CastShadows != 0)
                records.Add(Build(lights[i], i));

        Overlaps(records);
        AssignSlots(records);

        var mapBytes = Encoding.UTF8.GetBytes(mapPath);
        var mapId = unchecked((int)Io.ResourceNames.Hash(mapBytes, mapBytes.Length, 0x3501a674));
        foreach (var r in records)
        {
            var keys = results[r.Index].Keys;
            var light = lights[r.Index];
            keys.Add(new("light_path_uniqueid", D(unchecked((int)PathHash(light.IdPath, light.IdPathHeap ?? [])))));
            keys.Add(new("light_map_uniqueid", D(mapId)));
            if (r.Slot == -1)
            {
                keys.Add(new("desiredstationary", "1"));
                keys.Add(new("bakedshadowindex", "-1"));
                keys.Add(new("directlight", "1"));
                keys.Add(new("precomputedfieldsvalid", "0"));
            }
            else
                keys.Add(new("bakedshadowindex", D(r.Slot)));
            results[r.Index] = (r.Slot, keys);
        }
        return [.. results.Select(r => new Result(r.Slot, r.Keys))];
    }

    private static string D(int v) => v.ToString(CultureInfo.InvariantCulture);

    private static readonly string[] LegacyClasses =
        ["light_omni", "light_capsule", "light_spot", "light_directional", "light_environment", "light_ortho"];

    /// <summary>
    /// The direct light mode and castshadows byte the step tests. Light_ReadKeys
    /// (180eece60): directlight (default 2) and castshadows (default 0) as
    /// V_atoi truncated to a byte. A legacy light (Light_LegacyDescription,
    /// 180eed570) remaps the mode by baked_light_indexing unless directlight is
    /// 3: mode 1 with indexing 1 becomes 3, mode 3 with indexing not 1 becomes
    /// 1; a light_capsule's castshadows is 0. WorldRenderer/DirectLightBaking
    /// is absent from CS2's gameinfo, so its default 1 leaves the mode alone.
    /// </summary>
    public static (int Mode, int CastShadows) Mode(Light light)
    {
        var mode = (int)(sbyte)Atoi(light.Key, "directlight", 2);
        var cast = (int)(sbyte)Atoi(light.Key, "castshadows", 0);
        var legacy = LegacyClasses.Any(c => c.Equals(light.ClassName, StringComparison.OrdinalIgnoreCase));
        if (legacy)
        {
            if (Atoi(light.Key, "directlight", -1) != 3)
            {
                var indexing = Atoi(light.Key, "baked_light_indexing", -1);
                if (indexing != -1)
                {
                    if (mode == 1 && indexing == 1)
                        mode = 3;
                    else if (mode == 3 && indexing != 1)
                        mode = 1;
                }
            }
            if (light.ClassName.Equals("light_capsule", StringComparison.OrdinalIgnoreCase))
                cast = 0;
            return (mode, cast);
        }
        return light.ClassName is "light_barn" or "light_rect" or "light_omni2" ? (mode, cast) : (0, 0);
    }

    /// <summary>A light's record in the assignment (0x350 bytes in the binary).</summary>
    public sealed class Record
    {
        public int Index;
        public bool Directional;                       // +0x20c
        public Vector3 SphereCentre;                   // +0x228
        public float SphereRadius;                     // +0x234
        public float[] Xform = [];                     // +0x274
        public Vector3[] Hull = [];                    // +0x2a8, in light space
        public LightObb.Box? Precomputed;              // +0x2c8 flag, +0x2d0 box
        public List<Record> Neighbours = [];           // +0x330 count, +0x338 list
        public int AssignedNeighbours;                 // +0x324
        public int Slot = -1;                          // +0x328
    }

    /// <summary>
    /// FUN_180f45900 for a light that takes part. A legacy directional light
    /// (light_environment, light_directional) is flagged and needs no shape; a
    /// barn whose shape key is above 0.05 gets its frustum hull (180f461c0).
    /// Every other kind is read but not ported yet.
    /// </summary>
    public static Record Build(Light light, int index)
    {
        var r = new Record { Index = index, Xform = light.World };
        var name = light.ClassName.ToLowerInvariant();
        if (name is "light_environment" or "light_directional")
        {
            r.Directional = true;
            return r;
        }
        if (name != "light_barn")
            throw new NotSupportedException($"{light.ClassName}'s shadow slot shape (180f48c50, 180f47430, 180f48520) is not ported");

        var record = LightPrecompute.Records("light_barn", light.Key, [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0], split: false)[0];
        if (record[0xa8] <= 0.05f)
            throw new NotSupportedException("a barn with shape at or below 0.05 (180f48c50 / 180f48520) is not ported");
        r.Hull = FrustumHull(record);
        (r.SphereCentre, r.SphereRadius) = BoundingSphere(r.Hull, r.Xform);
        if (Atoi(light.Key, "precomputedfieldsvalid", 0) != 0)
        {
            var angles = Vector3Key(light.Key, "precomputedobbangles", Vector3.Zero);
            r.Precomputed = new LightObb.Box(CTransform.AngleQuaternion(angles),
                                             Vector3Key(light.Key, "precomputedobborigin", Vector3.Zero),
                                             Vector3Key(light.Key, "precomputedobbextent", Vector3.Zero));
        }
        return r;
    }

    /// <summary>
    /// 180f461c0: the barn's hull. Twelve points of the superellipse
    /// |x|^(2/e) + |y|^(2/e) = 1.01 (e the record's shape at 0xa8), taken at
    /// angles i/12 of a turn as (sign s |s|^e, sign c |c|^e) * 1.01, with their
    /// tangents by central difference (180f43360, +-0.01 rad, times 50). The
    /// tangent lines of neighbours meet in twelve corners, which the record's
    /// inverse (0x40) takes back from the unit volume at depth 0 and 1: 24
    /// points, depth 0 first.
    /// </summary>
    public static Vector3[] FrustumHull(LightShape record)
    {
        var e = record[0xa8];
        var p = new Vector2[12];
        var d = new Vector2[12];
        for (var i = 0; i < 12; i++)
        {
            var angle = (float)i / 12f * 6.2831855f + 0f;
            p[i] = new Vector2(SignedPow(Sin(angle), e) * 1.01f, SignedPow(Cos(angle), e) * 1.01f);
            var t = Tangent(angle, e);
            var len2 = t.X * t.X + t.Y * t.Y;
            var len = MathF.Sqrt(len2);
            if (len != 0f)
            {
                var inv = 1f / len;
                t = new Vector2(t.X * inv, t.Y * inv);
            }
            else
                t = Vector2.Zero;
            d[i] = t;
        }
        var corners = new Vector2[12];
        for (int k = 0, prev = 11; k < 12; prev = k, k++)
        {
            var lp = (p[prev].X + d[prev].X) * p[prev].Y - (p[prev].Y + d[prev].Y) * p[prev].X;
            var lc = (p[k].X + d[k].X) * p[k].Y - (p[k].Y + d[k].Y) * p[k].X;
            var inv = 1f / (d[prev].X * d[k].Y - d[k].X * d[prev].Y);
            corners[k] = new Vector2(inv * (lp * d[k].X - lc * d[prev].X), inv * (lp * d[k].Y - lc * d[prev].Y));
        }
        var m = record.F;
        var hull = new Vector3[24];
        for (var z = 0; z < 2; z++)
            for (var k = 0; k < 12; k++)
            {
                float x = corners[k].X, y = corners[k].Y, fz = z;
                var w = 1f / (x * m[28] + y * m[29] + fz * m[30] + m[31]);
                var px = (y * m[17] + x * m[16] + fz * m[18] + m[19]) * w;
                var pz = (x * m[24] + y * m[25] + fz * m[26] + m[27]) * w;
                var py = (x * m[20] + y * m[21] + fz * m[22] + m[23]) * w;
                hull[z * 12 + k] = new Vector3(px, py, pz);
            }
        return hull;
    }

    private static float Sin(float x)
    {
        LightCrt.SinCos(x, out var s, out _);
        return s;
    }

    private static float Cos(float x)
    {
        LightCrt.SinCos(x, out _, out var c);
        return c;
    }

    private static float SignedPow(float v, float e) => LightPow.Pow(MathF.Abs(v), e) * (v < 0f ? -1f : 1f);

    // 180f43360: the scaled superellipse's tangent at an angle.
    private static Vector2 Tangent(float angle, float e)
    {
        LightCrt.SinCos(angle + 0.01f, out var s1, out var c1);
        LightCrt.SinCos(angle - 0.01f, out var s2, out var c2);
        return new Vector2((1.01f * SignedPow(s1, e) - 1.01f * SignedPow(s2, e)) * 50f,
                           (SignedPow(c1, e) * 1.01f - SignedPow(c2, e) * 1.01f) * 50f);
    }

    /// <summary>
    /// 180f43680: the hull's points moved by the light's 3x4, their mean
    /// (summed in order, times 1 / n) and the largest distance from it.
    /// </summary>
    public static (Vector3 Centre, float Radius) BoundingSphere(Vector3[] hull, float[] xform)
    {
        float sx = 0f, sy = 0f, sz = 0f;
        foreach (var q in hull)
        {
            var w = LightMath.Transform34(xform, q);
            sx += w.X;
            sy += w.Y;
            sz += w.Z;
        }
        var inv = 1f / hull.Length;
        var c = new Vector3(sx * inv, sy * inv, sz * inv);
        var radius = 0f;
        foreach (var q in hull)
        {
            var w = LightMath.Transform34(xform, q);
            var dist = MathF.Sqrt((c.Z - w.Z) * (c.Z - w.Z) + (c.Y - w.Y) * (c.Y - w.Y) + (c.X - w.X) * (c.X - w.X));
            if (radius <= dist)
                radius = dist;
        }
        return (c, radius);
    }

    /// <summary>
    /// 180f44db0: each pair once, i before j. A pair with a directional light
    /// always overlaps. Otherwise the bounding spheres must touch; then, with a
    /// precomputed box on either light, the two boxes must overlap
    /// (181270590) or the box must reach the other's sphere (181271420); then
    /// the hulls, moved by their 3x4s, must come within 1 unit (181266fc0).
    /// </summary>
    public static void Overlaps(IReadOnlyList<Record> records)
    {
        foreach (var r in records)
            r.Neighbours.Clear();
        for (var i = 0; i < records.Count; i++)
            for (var j = i + 1; j < records.Count; j++)
            {
                Record a = records[i], b = records[j];
                if (a.Directional || b.Directional || Touch(a, b))
                {
                    a.Neighbours.Add(b);
                    b.Neighbours.Add(a);
                }
            }
    }

    private static bool Touch(Record a, Record b)
    {
        var dx = a.SphereCentre.X - b.SphereCentre.X;
        var dz = a.SphereCentre.Z - b.SphereCentre.Z;
        var dy = a.SphereCentre.Y - b.SphereCentre.Y;
        var reach = b.SphereRadius + a.SphereRadius;
        if (!(dy * dy + dx * dx + dz * dz <= reach * reach))
            return false;
        if (a.Precomputed is { } boxA && b.Precomputed is { } boxB)
        {
            if (!BoxesOverlap(boxA, boxB))
                return false;
        }
        else if (a.Precomputed is { } onlyA)
        {
            if (!BoxReachesSphere(onlyA, b.SphereCentre, b.SphereRadius))
                return false;
        }
        else if (b.Precomputed is { } onlyB)
        {
            if (!BoxReachesSphere(onlyB, a.SphereCentre, a.SphereRadius))
                return false;
        }
        return HullDistance(a.Hull, a.Xform, b.Hull, b.Xform) <= 1f;
    }

    /// <summary>181271420 through 181270250: the box's point nearest the centre, within the radius.</summary>
    public static bool BoxReachesSphere(LightObb.Box box, Vector3 centre, float radius)
    {
        var d = centre - box.Origin;
        var ax = Axis(box.Rotation, 0);
        var ay = Axis(box.Rotation, 1);
        var az = Axis(box.Rotation, 2);
        var x = Math.Clamp(Vector3.Dot(d, ax), -box.Extent.X, box.Extent.X);
        var y = Math.Clamp(Vector3.Dot(d, ay), -box.Extent.Y, box.Extent.Y);
        var z = Math.Clamp(Vector3.Dot(d, az), -box.Extent.Z, box.Extent.Z);
        var p = box.Origin + ax * x + ay * y + az * z;
        var q = p - centre;
        return q.Y * q.Y + q.X * q.X + q.Z * q.Z <= radius * radius;
    }

    /// <summary>
    /// 181270590: the separating axis test of two boxes, the fifteen axes in
    /// the binary's order (B's, A's, then the nine cross products); an axis
    /// separates only when the gap is above 0 (the thresholds at 18376f8d8 are
    /// zero-filled data the binary never writes).
    /// </summary>
    public static bool BoxesOverlap(LightObb.Box a, LightObb.Box b)
    {
        Span<Vector3> ua = [Axis(a.Rotation, 0), Axis(a.Rotation, 1), Axis(a.Rotation, 2)];
        Span<Vector3> ub = [Axis(b.Rotation, 0), Axis(b.Rotation, 1), Axis(b.Rotation, 2)];
        Span<float> ea = [a.Extent.X, a.Extent.Y, a.Extent.Z];
        Span<float> eb = [b.Extent.X, b.Extent.Y, b.Extent.Z];
        var d = a.Origin - b.Origin;
        var r = new float[3, 3];
        var ar = new float[3, 3];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
            {
                r[i, j] = Vector3.Dot(ub[i], ua[j]);
                ar[i, j] = MathF.Abs(r[i, j]);
            }
        Span<float> t = [Vector3.Dot(d, ub[0]), Vector3.Dot(d, ub[1]), Vector3.Dot(d, ub[2])];
        for (var i = 0; i < 3; i++)
            if (MathF.Abs(t[i]) - eb[i] - (ea[0] * ar[i, 0] + ea[1] * ar[i, 1] + ea[2] * ar[i, 2]) > 0f)
                return false;
        for (var j = 0; j < 3; j++)
        {
            var s = t[0] * r[0, j] + t[1] * r[1, j] + t[2] * r[2, j];
            if (MathF.Abs(s) - ea[j] - (eb[0] * ar[0, j] + eb[1] * ar[1, j] + eb[2] * ar[2, j]) > 0f)
                return false;
        }
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
            {
                int i1 = (i + 1) % 3, i2 = (i + 2) % 3, j1 = (j + 1) % 3, j2 = (j + 2) % 3;
                var s = MathF.Abs(t[i2] * r[i1, j] - t[i1] * r[i2, j]);
                var rb = eb[i1] * ar[i2, j] + eb[i2] * ar[i1, j];
                var ra = ea[j1] * ar[i, j2] + ea[j2] * ar[i, j1];
                if (s - rb - ra > 0f)
                    return false;
            }
        return true;
    }

    private static Vector3 Axis(Quaternion q, int i) => Vector3.Transform(i == 0 ? Vector3.UnitX : i == 1 ? Vector3.UnitY : Vector3.UnitZ, q);

    /// <summary>
    /// The distance between two point hulls moved by their 3x4s, by GJK in
    /// double precision. 181266fc0 is a float GJK with its own simplex
    /// solvers (181268550, 181268c20); this is not a line for line port and
    /// can differ from it only for pairs within rounding of 1 unit.
    /// </summary>
    public static double HullDistance(Vector3[] a, float[] xa, Vector3[] b, float[] xb)
    {
        var pa = a.Select(p => (Vector3D)LightMath.Transform34(xa, p)).ToArray();
        var pb = b.Select(p => (Vector3D)LightMath.Transform34(xb, p)).ToArray();
        Vector3D Support(Vector3D dir) => Max(pa, dir) - Max(pb, -dir);
        var simplex = new List<Vector3D> { Support(new Vector3D(1, 0, 0)) };
        var v = simplex[0];
        for (var iteration = 0; iteration < 64; iteration++)
        {
            var vv = v.Dot(v);
            if (vv < 1e-24)
                return 0;
            var w = Support(-v);
            if (vv - v.Dot(w) <= 1e-12 * vv)
                break;
            simplex.Add(w);
            v = Closest(simplex);
        }
        return Math.Sqrt(v.Dot(v));
    }

    private static Vector3D Max(Vector3D[] points, Vector3D dir)
    {
        var best = points[0];
        var bestDot = best.Dot(dir);
        for (var i = 1; i < points.Length; i++)
        {
            var dot = points[i].Dot(dir);
            if (dot > bestDot)
                (best, bestDot) = (points[i], dot);
        }
        return best;
    }

    // The point of the simplex nearest the origin; the simplex is cut to the
    // smallest face that holds it.
    private static Vector3D Closest(List<Vector3D> s)
    {
        var best = default(Vector3D);
        var bestDist = double.MaxValue;
        List<Vector3D>? bestSet = null;
        var n = s.Count;
        for (var mask = 1; mask < 1 << n; mask++)
        {
            var set = new List<Vector3D>();
            for (var i = 0; i < n; i++)
                if ((mask & (1 << i)) != 0)
                    set.Add(s[i]);
            if (Affine(set) is not { } p)
                continue;
            var dist = p.Dot(p);
            if (dist < bestDist - 1e-18 || (Math.Abs(dist - bestDist) <= 1e-18 && set.Count < bestSet!.Count))
                (best, bestDist, bestSet) = (p, dist, set);
        }
        s.Clear();
        s.AddRange(bestSet!);
        return best;
    }

    // The nearest point to the origin on the set's affine hull, when it lies
    // inside the set's convex hull (all barycentric weights at least 0).
    private static Vector3D? Affine(List<Vector3D> set)
    {
        var k = set.Count;
        if (k == 1)
            return set[0];
        // Solve for weights l with sum 1 minimising |sum l_i p_i|^2.
        var m = new double[k, k + 1];
        for (var i = 0; i < k - 1; i++)
        {
            for (var j = 0; j < k - 1; j++)
                m[i, j] = (set[i + 1] - set[0]).Dot(set[j + 1] - set[0]);
            m[i, k - 1] = -(set[i + 1] - set[0]).Dot(set[0]);
        }
        var x = new double[k - 1];
        if (!Solve(m, k - 1, x))
            return null;
        var l0 = 1 - x.Sum();
        if (l0 < -1e-12 || x.Any(v => v < -1e-12))
            return null;
        var p = set[0];
        for (var i = 0; i < k - 1; i++)
            p += (set[i + 1] - set[0]) * x[i];
        return p;
    }

    private static bool Solve(double[,] m, int n, double[] x)
    {
        for (var c = 0; c < n; c++)
        {
            var pivot = c;
            for (var r = c + 1; r < n; r++)
                if (Math.Abs(m[r, c]) > Math.Abs(m[pivot, c]))
                    pivot = r;
            if (Math.Abs(m[pivot, c]) < 1e-18)
                return false;
            for (var j = 0; j <= n; j++)
                (m[c, j], m[pivot, j]) = (m[pivot, j], m[c, j]);
            for (var r = 0; r < n; r++)
            {
                if (r == c)
                    continue;
                var f = m[r, c] / m[c, c];
                for (var j = c; j <= n; j++)
                    m[r, j] -= f * m[c, j];
            }
        }
        for (var i = 0; i < n; i++)
            x[i] = m[i, n] / m[i, i];
        return true;
    }

    private readonly record struct Vector3D(double X, double Y, double Z)
    {
        public static implicit operator Vector3D(Vector3 v) => new(v.X, v.Y, v.Z);
        public static Vector3D operator -(Vector3D a, Vector3D b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vector3D operator +(Vector3D a, Vector3D b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector3D operator -(Vector3D a) => new(-a.X, -a.Y, -a.Z);
        public static Vector3D operator *(Vector3D a, double s) => new(a.X * s, a.Y * s, a.Z * s);
        public double Dot(Vector3D b) => X * b.X + Y * b.Y + Z * b.Z;
    }

    /// <summary>
    /// CComputeBakedShadowAssignment_AssignSlots (180f45210). The first three
    /// directional lights take slots 0, 1, 2 in list order; every other light
    /// waits in a list. Until the list is empty: each light counts its
    /// neighbours that hold a slot (+0x324), the list is sorted by that count
    /// then by the neighbour count, both descending (std::sort, 180f3f2c0),
    /// its first light is taken (the last moves into its place), and it gets
    /// the lowest of slots 0 to 3 no neighbour holds, else -1.
    /// </summary>
    public static void AssignSlots(IReadOnlyList<Record> records)
    {
        var waiting = new List<Record>();
        var directional = 0;
        foreach (var r in records)
        {
            if (!r.Directional || directional > 2)
            {
                r.AssignedNeighbours = 0;
                r.Slot = -1;
                waiting.Add(r);
            }
            else
                r.Slot = directional++;
        }
        while (waiting.Count > 0)
        {
            foreach (var r in records)
                r.AssignedNeighbours = r.Neighbours.Count(n => n.Slot != -1);
            var items = waiting.ToArray();
            MsvcSort.Sort(items, (x, y) => x.AssignedNeighbours > y.AssignedNeighbours
                                           || (x.AssignedNeighbours == y.AssignedNeighbours && x.Neighbours.Count > y.Neighbours.Count));
            waiting.Clear();
            waiting.AddRange(items);
            var pick = waiting[0];
            var last = waiting.Count - 1;
            if (last != 0)
                waiting[0] = waiting[last];
            waiting.RemoveAt(last);
            Span<bool> free = [true, true, true, true];
            foreach (var n in pick.Neighbours)
                if (n.Slot != -1)
                    free[n.Slot] = false;
            for (var s = 0; s < 4; s++)
                if (free[s])
                {
                    pick.Slot = s;
                    break;
                }
        }
    }

    /// <summary>
    /// NodeIdPath_Hash (180105f50): MurmurHash2 of the heap ids' bytes (seed
    /// 0x3501a674), which then seeds MurmurHash2 of the inline ids' bytes; with
    /// no heap part the inline ids take the seed directly. No case folding.
    /// </summary>
    public static uint PathHash(int[] inline, int[] heap)
    {
        var seed = 0x3501a674u;
        if (heap.Length > 0)
            seed = Murmur2(heap, seed);
        return Murmur2(inline, seed);
    }

    private static uint Murmur2(int[] ids, uint seed)
    {
        const uint M = 0x5bd1e995;
        var data = new byte[ids.Length * 4];
        Buffer.BlockCopy(ids, 0, data, 0, data.Length);
        var h = (uint)data.Length ^ seed;
        var i = 0;
        for (; data.Length - i >= 4; i += 4)
        {
            var k = BitConverter.ToUInt32(data, i) * M;
            k ^= k >> 24;
            h = h * M ^ k * M;
        }
        var rest = data.Length - i;
        if (rest == 3)
            h ^= (uint)data[i + 2] << 16;
        if (rest >= 2)
            h ^= (uint)data[i + 1] << 8;
        if (rest >= 1)
            h = (h ^ data[i]) * M;
        h = (h ^ (h >> 13)) * M;
        return h ^ (h >> 15);
    }

    // FUN_180f32130 (V_atoi over the key, the default when absent).
    private static int Atoi(LightPrecompute.KeyReader key, string name, int fallback)
    {
        if (key(name) is not { } s)
            return fallback;
        s = s.TrimStart();
        var end = 0;
        if (end < s.Length && (s[end] == '-' || s[end] == '+'))
            end++;
        while (end < s.Length && char.IsAsciiDigit(s[end]))
            end++;
        return int.TryParse(s.AsSpan(0, end), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    // FUN_180f32390: sscanf "%f %f %f" over the default.
    private static Vector3 Vector3Key(LightPrecompute.KeyReader key, string name, Vector3 fallback)
    {
        if (key(name) is not { } s)
            return fallback;
        var parts = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        Span<float> v = [fallback.X, fallback.Y, fallback.Z];
        for (var i = 0; i < 3 && i < parts.Length; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                break;
            v[i] = f;
        }
        return new Vector3(v[0], v[1], v[2]);
    }
}
