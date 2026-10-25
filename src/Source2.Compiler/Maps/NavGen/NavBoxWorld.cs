using System.Numerics;
using Source2.Compiler.Physics;
using Source2.Compiler.Simulation;

namespace Source2.Compiler.Maps.NavGen;

/// <summary>
/// The nav world's box casts (<see cref="INavBoxWorld"/>) over the settle world's bodies, as vphysics2's
/// CastShape (1801f5080) answers navsystem's query: the box (mins, maxs) about the start becomes its centre
/// ((mins + maxs) / 2 added to the start) and half extents ((maxs - mins) / 2); every static body's shapes
/// that pass the query (interacts with 0xc3021, group 4) are cast with the best fraction so far as their
/// limit, and a hit counts when it is strictly below it. The fraction is the least hit, 1 when none.
/// The shape's transform is built per shape in the broadphase walk (1801d64a0) from the body's
/// orientation, scale and mass centres (<see cref="Transform"/>). Meshes take the unscaled cast
/// (180245a10) when the shape has no scale flag (+0xd1) and the transform's diagonal is 1 with no
/// translation, else the transformed one (180244dc0); hulls take 180251800 about their centroid; spheres
/// and capsules the GJK cast against their centre or segment (<see cref="HullCast.CastBoxRound"/>). Rays
/// (<see cref="CastRay"/>) go through the same bodies (meshes and hulls; a round shape refuses).
/// </summary>
public sealed class NavBoxWorld : INavBoxWorld, INavRayWorld
{
    private readonly record struct Cast(int Type, RnMesh? Mesh, Vector3 MeshScale, bool ScaleFlag, RnHull? Hull, float HullScale,
                                        float[] T, Vector3 Min, Vector3 Max, Vector3[]? Centres = null, float Radius = 0f);

    private readonly List<Cast> _shapes = [];
    private readonly List<Cast> _rayShapes = [];
    private readonly List<SettleWorld.BodyBuild> _bodyOf = [];

    /// <summary>The body a shape index (as <see cref="CastBox(ValueTuple{float, float, float}, ValueTuple{float, float, float}, ValueTuple{float, float, float}, ValueTuple{float, float, float}, Action{int, int, float, float[]}?)"/> reports it) belongs to.</summary>
    public SettleWorld.BodyBuild BodyOf(int shape) => _bodyOf[shape];

    /// <summary>The world from <paramref name="bodies"/> (all static), those <paramref name="inNavWorld"/> keeps.</summary>
    public NavBoxWorld(IReadOnlyList<SettleWorld.BodyBuild> bodies, Func<SettleWorld.BodyBuild, bool>? inNavWorld = null)
    {
        var table = CollisionGroupTable.Build();
        var kept = bodies.Where(b => inNavWorld?.Invoke(b) != false).ToList();
        var world = SettleWorld.CreateWorld(kept, []);
        for (var i = 0; i < kept.Count; i++)
        {
            var state = world.Bodies[i + 1].State;
            var t = Transform(state);
            foreach (var shape in kept[i].Shapes)
            {
                var boxes = NavGenInput.ShapePasses(shape.Attributes, state.BodyType, 0xc3021ul, 1, 4, table);
                // LadderConnect's rays (18006e170): layers 0x3021, nothing excluded or queried as.
                var rays = NavGenInput.ShapePasses(shape.Attributes, state.BodyType, 0x3021ul, 1, 4, table, 0, 0);
                if (!boxes && !rays)
                    continue;
                Cast cast;
                if (shape.Type == BroadphaseShape.HullType && shape.Hull is { } hull)
                {
                    var (min, max) = Bounds(hull.VertexPositions.Select(p => p * shape.HullScale), t);
                    cast = new Cast(2, null, default, false, hull, shape.HullScale, t, min, max);
                }
                else if (shape.Mesh is { } mesh)
                {
                    var corners = new List<Vector3>();
                    for (var c = 0; c < 8; c++)
                        corners.Add(new Vector3((c & 1) != 0 ? mesh.Max.X : mesh.Min.X, (c & 2) != 0 ? mesh.Max.Y : mesh.Min.Y,
                                                (c & 4) != 0 ? mesh.Max.Z : mesh.Min.Z) * shape.MeshScale);
                    var (min, max) = Bounds(corners, t);
                    cast = new Cast(3, mesh, shape.MeshScale, shape.MeshScale != Vector3.One, null, 0f, t, min, max);
                }
                else if (shape.Type == BroadphaseShape.SphereType && shape.Sphere is { } sphere)
                {
                    var k = shape.HullScale;
                    var centre = World(sphere.Center * k, t);
                    var r = sphere.Radius * k;
                    cast = new Cast(0, null, default, false, null, 0f, t, centre - new Vector3(r + 1f), centre + new Vector3(r + 1f), [centre], r);
                }
                else if (shape.Type == BroadphaseShape.CapsuleType && shape.Capsule is { } capsule)
                {
                    var k = shape.HullScale;
                    Vector3 a = World(capsule.A * k, t), b = World(capsule.B * k, t);
                    var r = capsule.Radius * k;
                    cast = new Cast(1, null, default, false, null, 0f, t, Vector3.Min(a, b) - new Vector3(r + 1f), Vector3.Max(a, b) + new Vector3(r + 1f), [a, b], r);
                }
                else
                    throw new NotSupportedException($"a nav box cast against a type {shape.Type} shape is not ported");
                if (boxes)
                {
                    _bodyOf.Add(kept[i]);
                    _shapes.Add(cast);
                }
                if (rays)
                    _rayShapes.Add(cast);
            }
        }
    }

    /// <summary>
    /// 1801d64a0's shape transform from the body: the 3x3 of its orientation q (+0x120: x, y, z, w) row
    /// by row, then the world mass centre (+0xfc) less q times the scaled local mass centre (+0x88,
    /// +0xc8), every sum taken left to right and each doubled product added to itself.
    /// </summary>
    public static float[] Transform(in RnBodyState b)
    {
        float x = b.Orientation.X, y = b.Orientation.Y, z = b.Orientation.Z, w = b.Orientation.W;
        float D(float v) => v + v;
        var t = new float[12];
        t[0] = 1f - D(z * z + y * y);
        t[1] = D(w * z + y * x);
        t[2] = D(z * x - w * y);
        t[3] = D(y * x - w * z);
        t[4] = 1f - D(z * z + x * x);
        t[5] = D(z * y + w * x);
        t[6] = D(w * y + z * x);
        t[7] = D(z * y - w * x);
        t[8] = 1f - D(y * y + x * x);
        var s = b.Scale;
        float cx = s * b.LocalMassCenter.X, cy = s * b.LocalMassCenter.Y, cz = s * b.LocalMassCenter.Z;
        var kx = D(cz * y - cy * z);
        var ky = D(cx * z - cz * x);
        var kz = D(cy * x - cx * y);
        t[9] = b.Position.X - (((kz * y - ky * z) + cx) + w * kx);
        t[10] = b.Position.Y - (((kx * z - kz * x) + cy) + w * ky);
        t[11] = b.Position.Z - (((ky * x - kx * y) + cz) + w * kz);
        return t;
    }

    /// <summary>
    /// A round shape's centre in the world as 18024bc00 / 18024e6b0 place it: ((x t0 + y t3) + z t6) + t9
    /// per axis.
    /// </summary>
    internal static Vector3 World(Vector3 p, float[] t)
        => new(((p.X * t[0] + p.Y * t[3]) + p.Z * t[6]) + t[9], ((p.X * t[1] + p.Y * t[4]) + p.Z * t[7]) + t[10],
               ((p.X * t[2] + p.Y * t[5]) + p.Z * t[8]) + t[11]);

    // A box around points moved by t, one unit wide of rounding: only a cull, never an answer.
    private static (Vector3 Min, Vector3 Max) Bounds(IEnumerable<Vector3> points, float[] t)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in points)
        {
            var q = new Vector3(p.X * t[0] + p.Y * t[3] + p.Z * t[6] + t[9], p.X * t[1] + p.Y * t[4] + p.Z * t[7] + t[10],
                                p.X * t[2] + p.Y * t[5] + p.Z * t[8] + t[11]);
            min = Vector3.Min(min, q);
            max = Vector3.Max(max, q);
        }
        return (min - Vector3.One, max + Vector3.One);
    }

    public float CastBox((float X, float Y, float Z) mins, (float X, float Y, float Z) maxs, (float X, float Y, float Z) start,
                         (float X, float Y, float Z) delta)
        => CastBox(mins, maxs, start, delta, null);

    /// <summary>The cast, telling <paramref name="hits"/> of every shape that hit (its index, type, fraction and transform).</summary>
    public float CastBox((float X, float Y, float Z) mins, (float X, float Y, float Z) maxs, (float X, float Y, float Z) start,
                         (float X, float Y, float Z) delta, Action<int, int, float, float[]>? hits)
    {
        var half = new Vector3((maxs.X - mins.X) * 0.5f, (maxs.Y - mins.Y) * 0.5f, (maxs.Z - mins.Z) * 0.5f);
        var centre = new Vector3((mins.X + maxs.X) * 0.5f + start.X, (mins.Y + maxs.Y) * 0.5f + start.Y, (mins.Z + maxs.Z) * 0.5f + start.Z);
        var d = new Vector3(delta.X, delta.Y, delta.Z);
        var end = centre + d;
        var lo = Vector3.Min(centre, end) - half;
        var hi = Vector3.Max(centre, end) + half;
        var best = 1f;
        for (var i = 0; i < _shapes.Count; i++)
        {
            var s = _shapes[i];
            if (s.Max.X < lo.X || s.Min.X > hi.X || s.Max.Y < lo.Y || s.Min.Y > hi.Y || s.Max.Z < lo.Z || s.Min.Z > hi.Z)
                continue;
            bool found;
            float fraction;
            if (s.Type == 3)
            {
                var t = s.T;
                var hit = !s.ScaleFlag && t[0] == 1f && t[4] == 1f && t[8] == 1f && t[9] == 0f && t[10] == 0f && t[11] == 0f
                    ? BoxCast.MeshUnscaled(s.Mesh!, centre, d, half, best)
                    : BoxCast.MeshTransformed(s.Mesh!, s.MeshScale, t, centre, d, half, best);
                (found, fraction) = (hit.Found, hit.Fraction);
            }
            else if (s.Type == 2)
            {
                var hit = HullCast.Cast(s.Hull!.Centroid, s.Hull.VertexPositions, s.HullScale, s.T, centre, d, half);
                (found, fraction) = (hit.Found, hit.Fraction);
            }
            else
            {
                var hit = HullCast.CastBoxRound(s.Centres!, s.Radius, centre, d, half, best);
                (found, fraction) = (hit.Found, hit.Fraction);
            }
            if (found)
                hits?.Invoke(i, s.Type, fraction, s.T);
            if (found && fraction < best)
                best = fraction;
        }
        return best;
    }

    /// <summary>
    /// CRnWorld::CastRay (1801f4ad0) as LadderConnect asks it: the static tree's shapes (1801d8580) that
    /// pass the query, each with the best fraction so far as its limit, a hit kept when strictly below
    /// it. The ray goes into the shape's frame through the transform's rows (start less the translation;
    /// each component y, then x, then z). Meshes (CRnMeshShape vfn 0x10, 1802406c0) take it divided by
    /// their scale and are hit on their front only; hulls (1802505c0, 1802544a0) clip it by their planes.
    /// </summary>
    public float CastRay((float X, float Y, float Z) start, (float X, float Y, float Z) delta)
    {
        var p = new Vector3(start.X, start.Y, start.Z);
        var d = new Vector3(delta.X, delta.Y, delta.Z);
        var lo = Vector3.Min(p, p + d);
        var hi = Vector3.Max(p, p + d);
        // A hit below the limit becomes the result; only a fraction above 0 lowers the limit (a hull
        // entered from within its 1/32 margin gives a negative one), and 0 ends the walk.
        var limit = 1f;
        var result = 1f;
        foreach (var s in _rayShapes)
        {
            if (s.Max.X < lo.X || s.Min.X > hi.X || s.Max.Y < lo.Y || s.Min.Y > hi.Y || s.Max.Z < lo.Z || s.Min.Z > hi.Z)
                continue;
            var t = s.T;
            float px = p.X - t[9], py = p.Y - t[10], pz = p.Z - t[11];
            var o = new Vector3((py * t[1] + t[0] * px) + pz * t[2], (py * t[4] + t[3] * px) + pz * t[5], (py * t[7] + t[6] * px) + pz * t[8]);
            var dir = new Vector3((d.Y * t[1] + d.X * t[0]) + d.Z * t[2], (d.Y * t[4] + d.X * t[3]) + d.Z * t[5], (d.Y * t[7] + d.X * t[6]) + d.Z * t[8]);
            if (s.Type < 2)
                throw new NotSupportedException("a nav ray against a sphere or capsule (LadderConnect's casts) is not ported");
            var fraction = s.Type == 3 ? MeshRay(s.Mesh!, s.MeshScale, o, dir, limit) : HullRay(s.Hull!, s.HullScale, o, dir);
            if (!(fraction < limit))
                continue;
            result = fraction;
            if (fraction == 0f)
                break;
            if (0f < fraction)
                limit = fraction;
        }
        return result;
    }

    /// <summary>
    /// 1802406c0's triangle test on the ray divided by the mesh's scale: crossed when no edge crossing
    /// (from the edge midpoints) is negative, the normal (v2 - v0) x (v1 - v0) faces the ray (d . n
    /// below 0), at t = ((v0 - o) . n) / (d . n) above 0, clamped to 1; the least t below the limit.
    /// </summary>
    internal static float MeshRay(RnMesh mesh, Vector3 scale, Vector3 start, Vector3 delta, float limit)
    {
        var o = start / scale;
        var d = delta / scale;
        var best = limit;
        foreach (var (ia, ib, ic) in mesh.Triangles)
        {
            Vector3 v0 = mesh.Vertices[ia], v1 = mesh.Vertices[ib], v2 = mesh.Vertices[ic];
            float ex = v1.X - v0.X, ey = v1.Y - v0.Y, ez = v1.Z - v0.Z;
            float m12x = (v2.X + v1.X) * 0.5f - o.X, m12y = (v2.Y + v1.Y) * 0.5f - o.Y, m12z = (v2.Z + v1.Z) * 0.5f - o.Z;
            float m02x = 0.5f * (v2.X + v0.X) - o.X, m02y = 0.5f * (v2.Y + v0.Y) - o.Y, m02z = 0.5f * (v2.Z + v0.Z) - o.Z;
            float m01x = 0.5f * (v1.X + v0.X) - o.X, m01y = 0.5f * (v1.Y + v0.Y) - o.Y, m01z = 0.5f * (v1.Z + v0.Z) - o.Z;
            float cx = m01z * ey - m01y * ez, cy = m01x * ez - m01z * ex, cz = m01y * ex - m01x * ey;
            var lane2 = d.Z * cz + d.Y * cy + d.X * cx;
            float fx = v0.X - v2.X, fy = v0.Y - v2.Y, fz = v0.Z - v2.Z;
            var lane1 = d.Z * (m02y * fx - m02x * fy) + d.Y * (m02x * fz - m02z * fx) + d.X * (m02z * fy - m02y * fz);
            float gx = v2.X - v1.X, gy = v2.Y - v1.Y, gz = v2.Z - v1.Z;
            var lane0 = d.Z * (m12y * gx - m12x * gy) + d.Y * (m12x * gz - m12z * gx) + d.X * (m12z * gy - m12y * gz);
            if (lane0 < 0f || lane1 < 0f || lane2 < 0f)
                continue;
            float hx = v2.X - v0.X, hy = v2.Y - v0.Y, hz = v2.Z - v0.Z;
            float nx = hz * ey - hy * ez, ny = hx * ez - hz * ex, nz = hy * ex - hx * ey;
            var denom = nx * d.X + ny * d.Y + nz * d.Z;
            if (0f <= denom)
                continue;
            var t = ((v0.X - o.X) * nx + (v0.Y - o.Y) * ny + (v0.Z - o.Z) * nz) / denom;
            if (t <= 0f)
                continue;
            t = Math.Min(t, 1f);
            if (t < best)
                best = t;
        }
        return best;
    }

    /// <summary>
    /// 1802544a0 through 180251dc0: each plane's distance at the start (n . o less the scale times the
    /// offset) and at the end; both outside misses; planes the ray enters raise the entry to (d0 - 1/32)
    /// / (d0 - d1), the others lower the exit to (d0 + 1/32) / (d0 - d1); an entry not below the exit
    /// misses. A start inside every plane hits at 0. 1 when missed.
    /// </summary>
    internal static float HullRay(RnHull hull, float scale, Vector3 o, Vector3 d)
    {
        var enter = -float.MaxValue;
        var exit = float.MaxValue;
        var inside = true;
        foreach (var (n, offset) in hull.Planes)
        {
            var d0 = (n.Z * o.Z + o.Y * n.Y + n.X * o.X) - scale * offset;
            var d1 = (n.Z * d.Z + d.Y * n.Y + n.X * d.X) + d0;
            if (0f < d0 && 0f < d1)
                return 1f;
            if (!(0f < d0) && !(0f < d1))
                continue;
            if (0f < d0)
                inside = false;
            if (d0 <= d1)
            {
                var t = (d0 + 0.03125f) / (d0 - d1);
                if (t < exit)
                    exit = t;
            }
            else
            {
                var t = (d0 - 0.03125f) / (d0 - d1);
                if (enter < t)
                    enter = t;
            }
        }
        if (hull.Planes.Length != 0 && exit <= enter)
            return 1f;
        return inside ? 0f : enter;
    }
}
