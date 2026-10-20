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
/// translation, else the transformed one (180244dc0); hulls take 180251800 about their centroid. Spheres
/// and capsules are not ported (refused).
/// </summary>
public sealed class NavBoxWorld : INavBoxWorld
{
    private readonly record struct Cast(int Type, RnMesh? Mesh, Vector3 MeshScale, bool ScaleFlag, RnHull? Hull, float HullScale,
                                        float[] T, Vector3 Min, Vector3 Max);

    private readonly List<Cast> _shapes = [];
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
                if (!NavGenInput.ShapePasses(shape.Attributes, state.BodyType, 0xc3021ul, 1, 4, table))
                    continue;
                if (shape.Type == BroadphaseShape.HullType && shape.Hull is { } hull)
                {
                    var (min, max) = Bounds(hull.VertexPositions.Select(p => p * shape.HullScale), t);
                    _bodyOf.Add(kept[i]);
                    _shapes.Add(new Cast(2, null, default, false, hull, shape.HullScale, t, min, max));
                }
                else if (shape.Mesh is { } mesh)
                {
                    var corners = new List<Vector3>();
                    for (var c = 0; c < 8; c++)
                        corners.Add(new Vector3((c & 1) != 0 ? mesh.Max.X : mesh.Min.X, (c & 2) != 0 ? mesh.Max.Y : mesh.Min.Y,
                                                (c & 4) != 0 ? mesh.Max.Z : mesh.Min.Z) * shape.MeshScale);
                    var (min, max) = Bounds(corners, t);
                    _bodyOf.Add(kept[i]);
                    _shapes.Add(new Cast(3, mesh, shape.MeshScale, shape.MeshScale != Vector3.One, null, 0f, t, min, max));
                }
                else
                    throw new NotSupportedException($"a nav box cast against a type {shape.Type} shape is not ported");
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
            else
            {
                var hit = HullCast.Cast(s.Hull!.Centroid, s.Hull.VertexPositions, s.HullScale, s.T, centre, d, half);
                (found, fraction) = (hit.Found, hit.Fraction);
            }
            if (found)
                hits?.Invoke(i, s.Type, fraction, s.T);
            if (found && fraction < best)
                best = fraction;
        }
        return best;
    }
}
