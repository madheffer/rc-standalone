using System.Numerics;
using Source2.Compiler.Physics;
using Source2.Compiler.Simulation;

namespace Source2.Compiler.Maps.NavGen;

/// <summary>
/// The CS nav analysis's traces (server 1814eb500 -> 1814eb630 -> 1814ea9a0): a sphere of radius 0.1
/// cast through vphysics2's world vfn 0x560 (180065370 -> 1801f4e40 -> 1801f5360) with the query
/// interacts with 0x3021, no excludes, every body type (0xf), group 3, flags 0x49 (captured on probe01).
/// <list type="bullet">
/// <item>1801f5360 clips the ray to the world's coordinate limit (1801c4370) when the delta is longer
/// than it, and maps each hit's fraction back; the analysis's rays are far shorter, so the clip is the
/// identity and is not modelled (refused past <see cref="MaxDelta"/>).</item>
/// <item>The broadphase walk (1801da640) moves the start and delta into each shape's frame:
/// R^T (start - origin) with the products summed left to right, and the delta as
/// (dy r1 + dx r0) + dz r2, with the transform of <see cref="NavBoxWorld.Transform"/>; then the
/// shape's vfn 0x20 with the best fraction so far as its limit.</item>
/// <item>A static body's hit replaces the one hit kept when nearer and shrinks the limit; any other
/// body's hit is added and leaves the limit alone.</item>
/// </list>
/// </summary>
public sealed class NavSphereWorld
{
    public const float MaxDelta = 16384f;

    private readonly record struct Cast(RnMesh? Mesh, Vector3 Scale, Vector3[]? Hull, bool Static, float[] T);

    private readonly List<Cast> _shapes = [];

    /// <summary>
    /// The world from <paramref name="bodies"/>: the nav PhysDoc's world, the bodies
    /// <paramref name="inNavWorld"/> keeps (<see cref="NavGenInput.NavWorldFilter"/>), as navsystem's.
    /// </summary>
    public NavSphereWorld(IReadOnlyList<SettleWorld.BodyBuild> bodies, Func<SettleWorld.BodyBuild, bool>? inNavWorld = null)
    {
        var table = CollisionGroupTable.Build();
        var kept = bodies.Where(b => inNavWorld?.Invoke(b) != false).ToList();
        var world = SettleWorld.CreateWorld(kept, []);
        for (var i = 0; i < kept.Count; i++)
        {
            var state = world.Bodies[i + 1].State;
            var t = NavBoxWorld.Transform(state);
            foreach (var shape in kept[i].Shapes)
            {
                if (!Passes(shape.Attributes, state.BodyType, table))
                    continue;
                if (shape.Type == BroadphaseShape.HullType && shape.Hull is { } hull)
                {
                    // 180250b20: a hull with a region SVM (+0xa8) runs the SVM pass 180257420 first; a scaled one the scaled support.
                    if (hull.RegionSvm != null)
                        throw new NotSupportedException("a nav analysis sphere cast against a hull with a region SVM (180257420) is not ported");
                    if (shape.HullScale != 1f)
                        throw new NotSupportedException("a nav analysis sphere cast against a scaled hull (1802e2bd0) is not ported");
                    _shapes.Add(new Cast(null, Vector3.One, hull.VertexPositions, state.BodyType == 0, t));
                    continue;
                }
                if (shape.Mesh is not { } mesh)
                    throw new NotSupportedException($"a nav analysis sphere cast against a type {shape.Type} shape is not ported");
                _shapes.Add(new Cast(mesh, shape.MeshScale, null, state.BodyType == 0, t));
            }
        }
    }

    /// <summary>1802facf0 with the analysis query; NavGenInput.ShapePasses with no exclude or query-as layers.</summary>
    private static bool Passes(in CollisionAttributes s, int bodyType, ushort[] groupTable)
    {
        const ulong interactsWith = 0x3021;
        const byte queryFlags = 0x49, queryGroup = 3;
        _ = bodyType; // type mask 0xf: every body type
        if ((s.Flags & 2) == 0 || ((0xffff >> (s.FunctionIndex & 0x1f)) & 1) == 0)
            return false;
        var mask = s.MaskIsDirect == 1 ? s.FunctionMask : (ushort)~s.FunctionMask;
        if ((mask & 1) == 0)
            return false;
        if (s.Group == 0)
            return true;
        if ((s.InteractsAs & interactsWith) == 0)
            return false;
        var pair = groupTable[queryGroup * 64 + s.Group];
        var collide = (pair & 1) != 0;
        if ((queryFlags & 2) != 0 && (s.Flags & 1) == 0 && collide)
        {
            collide = false;
            pair = 0;
        }
        if ((queryFlags & 1) == 0 || !collide)
            return !collide && (pair & 0x1c) != 0 && (queryFlags & 4) != 0;
        return (((interactsWith & 0xff) >> 1) & 1) == 0 || (s.Flags & 0x10) == 0;
    }

    /// <summary>The cast: how many hits the world reports and the least fraction (1 with none).</summary>
    public (int Hits, float Fraction) CastSphere(Vector3 start, Vector3 delta, float radius = 0.1f)
    {
        if (delta.X * delta.X + delta.Y * delta.Y + delta.Z * delta.Z > MaxDelta * MaxDelta)
            throw new NotSupportedException("a ray past the world's coordinate limit (1801c4370's clip) is not modelled");
        var best = 1f;
        var kept = -1f;
        var hits = 0;
        var least = 1f;
        foreach (var s in _shapes)
        {
            var t = s.T;
            float ox = start.X - t[9], oy = start.Y - t[10], oz = start.Z - t[11];
            var ls = new Vector3(t[0] * ox + t[1] * oy + t[2] * oz, t[3] * ox + t[4] * oy + t[5] * oz, t[6] * ox + t[7] * oy + t[8] * oz);
            var ld = new Vector3(delta.Y * t[1] + delta.X * t[0] + delta.Z * t[2], delta.Y * t[4] + delta.X * t[3] + delta.Z * t[5],
                                 delta.Y * t[7] + delta.X * t[6] + delta.Z * t[8]);
            var (found, fraction) = s.Hull is { } points
                ? HullCast.CastSphere(points, ls, ld, radius, best) is var h ? (h.Found, h.Fraction) : default
                : SphereCast.Mesh(s.Mesh!, s.Scale, ls, ld, radius, best) is var m ? (m.Found, m.Fraction) : default;
            if (!found)
                continue;
            var hit = (Found: found, Fraction: fraction);
            if (s.Static)
            {
                if (kept < 0f)
                    hits++;
                if (kept < 0f || hit.Fraction < kept)
                    kept = hit.Fraction;
                if (0f < hit.Fraction && hit.Fraction < best)
                    best = hit.Fraction;
            }
            else
                hits++;
            least = MathF.Min(least, hit.Fraction);
            if (s.Static && hit.Fraction == 0f)
                break;
        }
        return (hits, least);
    }
}
