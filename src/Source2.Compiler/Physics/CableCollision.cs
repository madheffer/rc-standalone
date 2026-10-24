using Source2.Compiler.Simulation;
using System.Numerics;

namespace Source2.Compiler.Physics;

/// <summary>
/// A cable's collision tube (CMapCable::vf277, rc 18114d850), which a cable with
/// collisionEnabled ships as a triangle mesh.
///
/// <para>The path's control points (CMapPath_IsLoop, 1810b7ca0: nine floats a
/// node, its position then its in and out tangents, the first repeated at the
/// end of a closed loop) are sampled as one cubic Bezier per node pair
/// (1812802d0), each pair's samples sharing its first with the last of the one
/// before. Every sample takes a radius and height scale interpolated between
/// its two nodes (1810b8270), times the cable's radius times 1.025. A frame
/// rides along the samples (1812820e0, rotation minimising from +z), and a ring
/// of sides + 1 points goes round each sample (1810bbc40); the tube is
/// two triangles a side between neighbouring rings (1810b8ae0). The tube has a
/// side count of its own: the cable's numSides up to 8, 8 below 18, half of it
/// below 32 and 16 from there.</para>
///
/// <para>A physicsSimplificationError above 0 sends the tube through
/// CMapCable_SimplifyPhysics (181156860): welded at 1/32 and simplified with
/// the error squared (rc's CMesh_Simplify 1813cb3d0, the same code as
/// physicsbuilder's, <see cref="MeshSimplifier"/>).</para>
///
/// <para>Every float expression keeps the association the disassembly shows
/// (symsse.py): Bezier points are ((a t t t + b t t) + c t) + P0 with the
/// products left to right, and squared lengths sum z, y, then x.</para>
/// </summary>
internal static class CableCollision
{
    /// <summary>
    /// The settings of a cable vf277 reads: numSides (+0x187c),
    /// tessellationSpacing (+0x1880), radius (+0x1884), flipFaces (+0x1888),
    /// closedLoop (+0x1864) and physicsSimplificationError (+0x18a4).
    /// </summary>
    internal sealed record Settings(int Sides, float Spacing, float Radius, bool Flip, bool ClosedLoop, float Error);

    /// <summary>A CMapCable node's settings, the factory's (rc 18114e350) where the source stores none: 8 sides, spacing 16, radius 16, error 2.</summary>
    internal static Settings SettingsOf(DmxBinary.Element cable) => new(
        cable.GetValue<int>("numSides") ?? 8, cable.GetValue<float>("tessellationSpacing") ?? 16f, cable.GetValue<float>("radius") ?? 16f,
        cable.GetValue<bool>("flipFaces") ?? false, cable.GetValue<bool>("closedLoop") ?? false, cable.GetValue<float>("physicsSimplificationError") ?? 2f);

    /// <summary>
    /// The settings of a cable an instance collapse copied: the clone is a
    /// new node from the factory (18114e350) given CMapCable::CopyFrom
    /// (vf 0x3d8, rc 18114b270), which clamps numSides to 3..1000 and the
    /// spacing and radius to 0.01f..1e5f (maxss, then minss) and copies
    /// flipFaces and collisionEnabled, but not physicsSimplificationError
    /// (+0x18a4): the copy keeps the factory's 2 (s2c_cableinst2). A prefab's
    /// nodes are not cloned and keep their own.
    /// </summary>
    internal static Settings Copied(Settings cable)
    {
        static float Clamp(float x)
        {
            x = x > 0.01f ? x : 0.01f;
            return x < 100000f ? x : 100000f;
        }
        return cable with { Sides = cable.Sides <= 3 ? 3 : cable.Sides < 1000 ? cable.Sides : 1000, Spacing = Clamp(cable.Spacing), Radius = Clamp(cable.Radius), Error = 2f };
    }

    /// <summary>
    /// The tube, in the cable's space, through the deformer that reaches the
    /// cable (PhysMesh_Shapes, rc 181057020: PropDeformer_ApplyArrays with the
    /// cable's node matrix), the whole index list reversed when the lattice is
    /// mirrored. The deformer node is moved by the collapse as the cable is.
    /// Without a deformer the tube is as it was.
    /// </summary>
    internal static (Vector3[] Points, int[] Indices) Deformed((Vector3[] Points, int[] Indices) tube, DmxBinary.Element cable, float[] nodeMatrix,
                                                               IReadOnlyList<DmxBinary.Element> through, IReadOnlyList<DmxBinary.Element> prefabs)
    {
        if (cable.Get<DmxBinary.Element>(Maps.MapDeformers.CableKey) is not { } node)
            return tube;
        var deformer = Maps.LatticeDeformer.FromNode(node, through, prefabs);
        if (!deformer.Enabled)
            return tube;
        var evaluator = deformer.For(nodeMatrix);
        return ([.. tube.Points.Select(evaluator.Deform)], deformer.Mirror ? [.. tube.Indices.Reverse()] : tube.Indices);
    }

    /// <summary>collisionEnabled (+0x18a0, CMapCable vf 0x8a0), on unless the source turns it off; it makes CollisionType 4 (CMapPath vf208, 1810b5e00) and the physics export.</summary>
    internal static bool Collides(DmxBinary.Element cable) => cable.GetValue<bool>("collisionEnabled") ?? true;

    /// <summary>
    /// The cable's node matrix (vtable 0xa0): AngleMatrix of its angles with
    /// its origin, which is its first node's (CMapPath::vf274,
    /// <see cref="MapEntities.ReadNode"/>).
    /// </summary>
    internal static float[] NodeMatrix(MapEntities.Entity cable)
    {
        var m = Maps.MapMeshes.AngleMatrix(cable.Angles);
        (m[3], m[7], m[11]) = (cable.Origin.X, cable.Origin.Y, cable.Origin.Z);
        return m;
    }

    /// <summary>
    /// The tube in the cable's own space, as vf277 hands it on: positions and
    /// triangles, or none when the path has fewer than two nodes or the tube
    /// fewer than three sides.
    /// </summary>
    /// <param name="controls">Nine floats a node, the loop's repeated first
    /// node included (<see cref="EntityLumpAuthor.PathControls"/>).</param>
    /// <param name="scales">Each node's radius and height scale, the same
    /// nodes.</param>
    internal static (Vector3[] Points, int[] Indices) Build(float[] controls, (float Radius, float Height)[] scales, Settings cable)
    {
        var (points, indices) = Tube(controls, scales, cable);
        if (cable.Error <= 0f || points.Length == 0)
            return (points, indices);
        return WorldCollision.Simplify(cable.Error * cable.Error, points, indices);
    }

    /// <summary>The tube before any simplification (1810b8ae0's output).</summary>
    internal static (Vector3[] Points, int[] Indices) Tube(float[] controls, (float Radius, float Height)[] scales, Settings cable)
    {
        var sides = cable.Sides;
        if (sides > 8)
            sides = sides < 18 ? 8 : sides < 32 ? sides / 2 : 16;
        return Tube(controls, scales, cable, sides, cable.Radius * 1.025f);
    }

    /// <summary>
    /// The render tube's positions and triangles (181151b30, vf200 with export
    /// 0 or 2): the same tube with the cable's own side count and its radius
    /// as it is.
    /// </summary>
    internal static (Vector3[] Points, int[] Indices) RenderTube(float[] controls, (float Radius, float Height)[] scales, Settings cable)
        => Tube(controls, scales, cable, cable.Sides, cable.Radius);

    private static (Vector3[] Points, int[] Indices) Tube(float[] controls, (float Radius, float Height)[] scales, Settings cable, int sides, float factor)
    {
        var (samples, starts) = Samples(controls, cable.Spacing);
        if (samples.Length < 2 || sides < 3)
            return ([], []);
        var radii = SampleScales(scales, starts, samples.Length);
        for (var i = 0; i < radii.Length; i++)
            radii[i] = (factor * radii[i].Radius, factor * radii[i].Height);

        var (normals, tangents) = Frames(samples);
        if (cable.ClosedLoop)
        {
            normals[^1] = normals[0];
            tangents[^1] = tangents[0];
        }

        // The ring profile: the angle summed a step at a time, cos then sin.
        var sin = new float[sides];
        var cos = new float[sides];
        var step = 6.2831855f / sides;
        var angle = 0f;
        for (var j = 0; j < sides; j++)
        {
            cos[j] = CrtMath.Cos(angle);
            sin[j] = CrtMath.Sin(angle);
            angle += step;
        }

        var points = new Vector3[samples.Length * (sides + 1)];
        for (var i = 0; i < samples.Length; i++)
            Ring(samples[i], tangents[i], normals[i], radii[i], sin, cos, points.AsSpan(i * (sides + 1), sides + 1));

        var width = sides + 1;
        var indices = new int[(samples.Length - 1) * sides * 6];
        var k = 0;
        for (var i = 0; i < samples.Length - 1; i++)
        {
            var b = width * i;
            for (var j = 0; j < sides; j++)
            {
                if (!cable.Flip)
                {
                    (indices[k], indices[k + 1], indices[k + 2]) = (j + b, width + j + b, j + 1 + b);
                    (indices[k + 3], indices[k + 4], indices[k + 5]) = (j + 1 + b, width + j + b, width + j + 1 + b);
                }
                else
                {
                    (indices[k], indices[k + 1], indices[k + 2]) = (j + 1 + b, width + j + b, j + b);
                    (indices[k + 3], indices[k + 4], indices[k + 5]) = (width + j + 1 + b, width + j + b, j + 1 + b);
                }
                k += 6;
            }
        }
        return (points, indices);
    }

    /// <summary>
    /// 1812802d0 in mode 0: each node pair's Bezier sampled at
    /// max(4, ceil(length / spacing)) evenly stepped parameters, the pairs
    /// after the first dropping their first sample (it is the last one before).
    /// Also each node's first sample (the last node's is the last sample).
    /// </summary>
    internal static (Vector3[] Samples, int[] Starts) Samples(float[] controls, float spacing)
    {
        var nodes = controls.Length / 9;
        if (nodes < 2)
            return ([], []);
        var samples = new List<Vector3>();
        var starts = new int[nodes];
        for (var k = 0; k < nodes - 1; k++)
        {
            if (k != 0)
                samples.RemoveAt(samples.Count - 1);
            starts[k] = samples.Count;
            var bezier = Segment(controls, k);
            var count = (int)MathF.Ceiling(Length(bezier, 1f) / spacing);
            if (count < 4)
                count = 4;
            Sample(bezier, count, samples);
        }
        starts[^1] = samples.Count - 1;
        return ([.. samples], starts);
    }

    /// <summary>A node pair's control points: P0 the node, P1 it plus its out
    /// tangent, P2 the next node plus its in tangent, P3 the next node.</summary>
    private static Vector3[] Segment(float[] c, int k)
    {
        var a = k * 9;
        var b = a + 9;
        return
        [
            new(c[a], c[a + 1], c[a + 2]),
            new(c[a + 6] + c[a], c[a + 7] + c[a + 1], c[a + 8] + c[a + 2]),
            new(c[b + 3] + c[b], c[b + 4] + c[b + 1], c[b + 5] + c[b + 2]),
            new(c[b], c[b + 1], c[b + 2]),
        ];
    }

    /// <summary>The cubic's coefficients a, b and c (18110fc80, 1810bb3f0).</summary>
    internal static (Vector3 A, Vector3 B, Vector3 C) Coefficients(Vector3[] p)
    {
        static float Ca(float p0, float p1, float p2, float p3) => ((p1 * 3f - p0 * 1f) - p2 * 3f) + p3;
        static float Cb(float p0, float p1, float p2) => (p0 * 3f - p1 * 6f) + p2 * 3f;
        static float Cc(float p0, float p1) => p1 * 3f - p0 * 3f;
        return (new(Ca(p[0].X, p[1].X, p[2].X, p[3].X), Ca(p[0].Y, p[1].Y, p[2].Y, p[3].Y), Ca(p[0].Z, p[1].Z, p[2].Z, p[3].Z)),
                new(Cb(p[0].X, p[1].X, p[2].X), Cb(p[0].Y, p[1].Y, p[2].Y), Cb(p[0].Z, p[1].Z, p[2].Z)),
                new(Cc(p[0].X, p[1].X), Cc(p[0].Y, p[1].Y), Cc(p[0].Z, p[1].Z)));
    }

    private static float Poly(float a, float b, float c, float p0, float t) => ((a * t * t * t + b * t * t) + c * t) + p0;

    internal static Vector3 Point((Vector3 A, Vector3 B, Vector3 C) k, Vector3 p0, float t)
        => new(Poly(k.A.X, k.B.X, k.C.X, p0.X, t), Poly(k.A.Y, k.B.Y, k.C.Y, p0.Y, t), Poly(k.A.Z, k.B.Z, k.C.Z, p0.Z, t));

    /// <summary>
    /// 18110fc80: the Bezier's length as a polyline of 2, 4, 8 ... 128 points,
    /// doubled until a step changes it by less than tolerance / 100 of itself.
    /// Each point's parameter is i / (n - 1); the polyline starts at P0.
    /// </summary>
    internal static float Length(Vector3[] p, float tolerance)
    {
        var k = Coefficients(p);
        var n = 2;
        var previous = 0f;
        float length;
        while (true)
        {
            length = 0f;
            var delta = 1f / (n - 1);
            var last = p[0];
            for (var i = 1; i < n; i++)
            {
                var here = Point(k, p[0], i * delta);
                float dx = here.X - last.X, dy = here.Y - last.Y, dz = here.Z - last.Z;
                length += MathF.Sqrt(dz * dz + dy * dy + dx * dx);
                last = here;
            }
            if (!(tolerance / 100f <= MathF.Abs(length - previous) / length))
                break;
            n *= 2;
            previous = length;
            if (n >= 129)
                break;
        }
        return length;
    }

    /// <summary>1810bb3f0: <paramref name="count"/> points, the parameter
    /// stepped by 1 / (count - 1) and summed.</summary>
    private static void Sample(Vector3[] p, int count, List<Vector3> into)
    {
        var k = Coefficients(p);
        var delta = 1f / (count - 1);
        var t = 0f;
        for (var i = 0; i < count; i++)
        {
            into.Add(Point(k, p[0], t));
            t += delta;
        }
    }

    /// <summary>
    /// 1810b8270: each sample's radius and height scale, linear between the
    /// node pair it lies on by its place among the pair's samples; a shared
    /// sample takes the later pair's value.
    /// </summary>
    internal static (float Radius, float Height)[] SampleScales((float Radius, float Height)[] scales, int[] starts, int count)
    {
        var result = new (float Radius, float Height)[count];
        for (var k = 0; k < starts.Length - 1; k++)
        {
            var (r0, h0) = scales[k];
            var (r1, h1) = scales[k + 1];
            var span = starts[k + 1] - starts[k];
            for (var i = 0; i <= span; i++)
            {
                var f = (float)i / span;
                result[starts[k] + i] = (r0 + (r1 - r0) * f, h0 + (h1 - h0) * f);
            }
        }
        return result;
    }

    /// <summary>
    /// 1812820e0: each sample's tangent (towards the next) and normal. The
    /// first normal is +z with the tangent projected out (VectorPerpendicular
    /// when that leaves under 2^-23); each later one is the previous normal
    /// turned by the angle between the two tangents about their cross product
    /// (skipped when the cosine is past 0.999 either way), then squared up
    /// against the tangent. The last sample copies the one before.
    /// </summary>
    internal static (Vector3[] Normals, Vector3[] Tangents) Frames(Vector3[] p)
    {
        var n = p.Length;
        var normals = new Vector3[n];
        var tangents = new Vector3[n];
        for (var i = 0; i < n - 1; i++)
        {
            var t = Normalise(p[i + 1].X - p[i].X, p[i + 1].Y - p[i].Y, p[i + 1].Z - p[i].Z);
            tangents[i] = t;
            if (i == 0)
            {
                var up = Normalise(0f, 0f, 1f);
                var d = up.Z * t.Z + up.Y * t.Y + up.X * t.X;
                float vx = up.X - d * t.X, vy = up.Y - d * t.Y, vz = up.Z - d * t.Z;
                var length = MathF.Sqrt(vy * vy + vz * vz + vx * vx);
                if (length >= 1.1920929e-07f)
                {
                    var inverse = 1f / length;
                    normals[0] = new Vector3(vx * inverse, vy * inverse, vz * inverse);
                }
                else
                    normals[0] = Perpendicular(t);
                continue;
            }

            var before = Normalise(p[i].X - p[i - 1].X, p[i].Y - p[i - 1].Y, p[i].Z - p[i - 1].Z);
            var normal = normals[i - 1];
            var cosine = before.Z * t.Z + before.Y * t.Y + before.X * t.X;
            var c = -1f;
            if (-1f <= cosine)
                c = cosine <= 1f ? cosine : 1f;
            if (MathF.Abs(c) <= 0.999f)
            {
                var axis = Normalise(before.Y * t.Z - before.Z * t.Y, before.Z * t.X - t.Z * before.X, t.Y * before.X - before.Y * t.X);
                var degrees = (float)(Math.Acos(c) * 57.29577951308232);
                var q = AxisAngle(axis, degrees);
                normal = Rotate(q, normal);
            }
            var side = Normalise(normal.Y * t.Z - normal.Z * t.Y, normal.Z * t.X - normal.X * t.Z, normal.X * t.Y - normal.Y * t.X);
            normals[i] = Normalise(side.Z * t.Y - side.Y * t.Z, side.X * t.Z - side.Z * t.X, side.Y * t.X - side.X * t.Y);
        }
        normals[n - 1] = normals[n - 2];
        tangents[n - 1] = tangents[n - 2];
        return (normals, tangents);
    }

    /// <summary>
    /// 1812532c0: the quaternion turning <paramref name="degrees"/> about
    /// <paramref name="axis"/>, sin and cos of half the angle in radians
    /// (V_sincosf); identity for a zero axis or angle.
    /// </summary>
    internal static Quaternion AxisAngle(Vector3 axis, float degrees)
    {
        var radians = degrees * 0.017453292f;
        if (axis == Vector3.Zero || radians == 0f)
            return Quaternion.Identity;
        var half = radians * 0.5f;
        var s = CrtMath.Sin(half);
        var c = CrtMath.Cos(half);
        return new Quaternion(axis.X * s, axis.Y * s, axis.Z * s, c);
    }

    /// <summary>The vector turned by the quaternion: t = 2 (q x v), then
    /// (q x t) + (w t + v), packed (1812825e8; Ghidra prints the sum as
    /// ((q x t) + w t) + v, which it is not).</summary>
    internal static Vector3 Rotate(Quaternion q, Vector3 v)
    {
        var tx = v.Z * q.Y - v.Y * q.Z;
        var ty = v.X * q.Z - v.Z * q.X;
        var tz = v.Y * q.X - v.X * q.Y;
        tx += tx;
        ty += ty;
        tz += tz;
        return new Vector3(
            (tz * q.Y - ty * q.Z) + (q.W * tx + v.X),
            (tx * q.Z - tz * q.X) + (q.W * ty + v.Y),
            (ty * q.X - tx * q.Y) + (q.W * tz + v.Z));
    }

    /// <summary>
    /// 1810bbc40: sides + 1 points round a sample, the last on the first;
    /// point j is ((sin s) r + (cos N) h) + centre with s = normalise(N x T).
    /// </summary>
    private static void Ring(Vector3 centre, Vector3 t, Vector3 n, (float Radius, float Height) scale, float[] sin, float[] cos, Span<Vector3> into)
    {
        var s = Normalise(t.Z * n.Y - n.Z * t.Y, n.Z * t.X - t.Z * n.X, n.X * t.Y - t.X * n.Y);
        var sides = sin.Length;
        for (var j = 0; j <= sides; j++)
        {
            float a = sin[j % sides], b = cos[j % sides];
            into[j] = new Vector3(
                (a * s.X * scale.Radius + b * n.X * scale.Height) + centre.X,
                (a * s.Y * scale.Radius + b * n.Y * scale.Height) + centre.Y,
                (a * s.Z * scale.Radius + b * n.Z * scale.Height) + centre.Z);
        }
    }

    /// <summary>
    /// 18125d0d0: (1 - v.z) (v.y^2 - 0) + v.z, 0, -v.x normalised, the part
    /// along v taken out and normalised again (VectorNormalize, 18013e340).
    /// </summary>
    internal static Vector3 Perpendicular(Vector3 v)
    {
        var p = NormaliseYzx((1f - v.Z) * (v.Y * v.Y - 0f) + v.Z, 0f, -v.X);
        var d = p.Z * v.Z + v.Y * p.Y + p.X * v.X;
        return NormaliseYzx(p.X - d * v.X, p.Y - v.Y * d, p.Z - v.Z * d);
    }

    /// <summary>VectorNormalize (18013e340): the squared length summed y, z, x.</summary>
    private static Vector3 NormaliseYzx(float x, float y, float z)
    {
        var length = MathF.Sqrt(y * y + z * z + x * x);
        return Scale(x, y, z, length);
    }

    /// <summary>The inline normalise: the squared length summed z, y, x; 1 /
    /// length times each component.</summary>
    internal static Vector3 Normalise(float x, float y, float z)
        => Scale(x, y, z, MathF.Sqrt(z * z + y * y + x * x));

    private static Vector3 Scale(float x, float y, float z, float length)
    {
        if (length >= 1e-17f && length <= 1e17f)
        {
            var inverse = 1f / length;
            return new Vector3(inverse * x, inverse * y, inverse * z);
        }
        if (length == 0f)
            return Vector3.Zero;
        return Maps.VectorNormalizeSlow.Normalise(new Vector3(x, y, z));
    }
}
