using System.Runtime.InteropServices;
using Source2.Compiler.Physics;

namespace Source2.Compiler.Simulation;

/// <summary>
/// A candidate triangle's narrowphase cache (0x3c bytes, CRnMeshContact +0xe0):
/// the sphere and capsule paths' GJK cache, which a hull leaves zeroed, and the
/// hull's SAT cache at +0x2c.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x3c)]
public struct MeshTriangleCache
{
    [FieldOffset(0x2c)] public SatCache Sat;
}

/// <summary>
/// What a hull-vs-mesh contact (CRnMeshContact) keeps between steps: the fat
/// box the candidate triangles were gathered in (mesh space, +0xa8), those
/// triangles (+0xc0/+0xc8) with a cache each (+0xd8/+0xe0), and the manifold
/// block (+0x80), empty when the pair is apart.
/// </summary>
public sealed class MeshContactState
{
    /// <summary>The cached query box; the constructor's empty box (DAT_18055e910) forces the first query.</summary>
    public Vec3 BoxMin = new(float.MaxValue, float.MaxValue, float.MaxValue);
    public Vec3 BoxMax = new(-float.MaxValue, -float.MaxValue, -float.MaxValue);
    public List<int> Triangles = [];
    public List<MeshTriangleCache> Caches = [];
    public List<CachedManifold> Manifolds = [];

    /// <summary>The solver size estimate (contact +0x98, FUN_1801d18c0).</summary>
    public int SizeEstimate = 0x18;
}

/// <summary>
/// Rubikon's hull against triangle mesh contact update (CRnMeshContact slot 2,
/// FUN_180305460): candidate triangles from the mesh BVH with a fat box kept
/// for hysteresis, the hull-hull narrowphase against each triangle as a
/// two-faced hull, the per-triangle manifolds clustered by normal and merged
/// to one reduced manifold per cluster, warm started from the last step's.
/// Every float operation is in vphysics2's order; MeshCollisionOracleTests
/// checks it against the DLL.
/// </summary>
/// <remarks>
/// Only a standalone hull shape against a standalone mesh shape, the pair a
/// settle meets; compound children and the overlap-only (sensor) path
/// FUN_180305860 are not ported. FUN_1802ffd00 runs only when the flag at
/// *(DAT_180559388) + 0x58 is set; it reads 0 in a plain vphysics2 process
/// and is not ported.
/// </remarks>
public static partial class MeshCollision
{
    /// <summary>Hull AABB and bounding sphere inflation (FUN_180250100, FUN_1802515a0).</summary>
    private const float ShapeMargin = 0.0625f;

    /// <summary>Containment slack of the cached box (0x1803da38c).</summary>
    private const float BoxSlack = 0.0625f;

    /// <summary>How far the cached box reaches past the hull's (0x1803cfec0).</summary>
    private const float BoxReach = 4f;

    /// <summary>Slack on the triangle plane cull (0x1803e3100).</summary>
    private const float PlaneSlack = 0.09375f;

    /// <summary>Normal agreement for clusters and the warm start (0x1803e41ac).</summary>
    private const float SameNormal = 0.995f;

    /// <summary>
    /// One step of the contact (FUN_180305460): refreshes the candidates,
    /// rebuilds <see cref="MeshContactState.Manifolds"/> and the size estimate.
    /// </summary>
    public static void Update(MeshContactState state, in RnTransform xfHull, HullRef hull,
                              in RnTransform xfMesh, RnMesh mesh, Vec3 meshScale)
    {
        UpdateCandidates(state, xfHull, hull, xfMesh, mesh, meshScale);
        var perTriangle = CollideTriangles(state, xfHull, hull, xfMesh, mesh, meshScale);
        var clusters = Cluster(perTriangle);
        var merged = Merge(xfHull, xfMesh, clusters, perTriangle);
        WarmStart(merged, state.Manifolds);
        state.Manifolds = merged;
        state.SizeEstimate = SizeEstimate(merged);
    }

    /// <summary>0x18 + 200 + 0x5c per point, per manifold (FUN_1801d18c0).</summary>
    public static int SizeEstimate(List<CachedManifold> manifolds)
    {
        var size = 0x18;
        foreach (var m in manifolds)
            size = size + 200 + m.PointCount * 0x5c;
        return size;
    }

    /// <summary>
    /// The hull's frame in mesh space, xfMesh⁻¹ xfHull (FUN_1803049b0's
    /// prologue): column j of the rotation is Rmeshᵀ a_j.
    /// </summary>
    internal static RnTransform Relative(in RnTransform a, in RnTransform b)
    {
        ref readonly var ra = ref a.R;
        ref readonly var rb = ref b.R;
        var m = new Mat3();
        for (var j = 0; j < 3; j++)
            for (var i = 0; i < 3; i++)
                m[3 * j + i] = (rb[3 * i] * ra[3 * j] + rb[3 * i + 1] * ra[3 * j + 1]) + rb[3 * i + 2] * ra[3 * j + 2];
        var dx = a.T.X - b.T.X;
        var dy = a.T.Y - b.T.Y;
        var dz = a.T.Z - b.T.Z;
        return new RnTransform
        {
            R = m,
            T = new Vec3((rb.M0 * dx + rb.M1 * dy) + rb.M2 * dz,
                         (rb.M3 * dx + rb.M4 * dy) + rb.M5 * dz,
                         (rb.M6 * dx + rb.M7 * dy) + rb.M8 * dz),
        };
    }

    /// <summary>
    /// The hull's box in a frame, grown by 1/16 (FUN_180250100, the hull
    /// shape's vtable +0x80): the scaled local box's centre moved and its
    /// half extents through |R|.
    /// </summary>
    internal static (Vec3 Min, Vec3 Max) HullBounds(HullRef hull, in RnTransform xf)
    {
        var s = hull.Scale;
        var lo = hull.Hull.BoundsMin;
        var hi = hull.Hull.BoundsMax;
        float minX = lo.X * s, minY = lo.Y * s, minZ = lo.Z * s;
        float maxX = hi.X * s, maxY = hi.Y * s, maxZ = hi.Z * s;
        var cx = (maxX + minX) * 0.5f;
        var cy = (minY + maxY) * 0.5f;
        var cz = (minZ + maxZ) * 0.5f;
        var c = HullCollision.ToWorld(xf, cx, cy, cz);
        var ex = (maxX - minX) * 0.5f;
        var ey = (maxY - minY) * 0.5f;
        var ez = (maxZ - minZ) * 0.5f;
        ref readonly var r = ref xf.R;
        var rx = (MathF.Abs(r.M0) * ex + MathF.Abs(r.M3) * ey) + MathF.Abs(r.M6) * ez;
        var ry = (MathF.Abs(r.M1) * ex + MathF.Abs(r.M4) * ey) + MathF.Abs(r.M7) * ez;
        var rz = (MathF.Abs(r.M2) * ex + MathF.Abs(r.M5) * ey) + MathF.Abs(r.M8) * ez;
        return (new Vec3((c.X - rx) - ShapeMargin, (c.Y - ry) - ShapeMargin, (c.Z - rz) - ShapeMargin),
                new Vec3((c.X + rx) + ShapeMargin, (c.Y + ry) + ShapeMargin, (c.Z + rz) + ShapeMargin));
    }

    /// <summary>
    /// Refreshes the candidate triangles (FUN_1803049b0). While the hull's
    /// box, grown by 1/16, stays inside the cached box nothing changes;
    /// otherwise the cached box becomes it grown by 4 more, the BVH is queried
    /// with it, and each new candidate keeps its old cache if it was a
    /// candidate before. The carry-over walks both lists as if ascending.
    /// </summary>
    internal static void UpdateCandidates(MeshContactState state, in RnTransform xfHull, HullRef hull,
                                          in RnTransform xfMesh, RnMesh mesh, Vec3 scale)
    {
        var (min, max) = HullBounds(hull, Relative(xfHull, xfMesh));
        float loX = min.X - BoxSlack, loY = min.Y - BoxSlack, loZ = min.Z - BoxSlack;
        float hiX = max.X + BoxSlack, hiY = max.Y + BoxSlack, hiZ = max.Z + BoxSlack;
        var box = (state.BoxMin, state.BoxMax);
        if (!(box.BoxMin.X > loX) && !(hiX > box.BoxMax.X)
            && !(box.BoxMin.Y > loY) && !(hiY > box.BoxMax.Y)
            && !(box.BoxMin.Z > loZ) && !(hiZ > box.BoxMax.Z))
            return;
        state.BoxMin = new Vec3(loX - BoxReach, loY - BoxReach, loZ - BoxReach);
        state.BoxMax = new Vec3(hiX + BoxReach, hiY + BoxReach, hiZ + BoxReach);

        var found = new List<int>();
        QueryTriangles(mesh, scale, state.BoxMin, state.BoxMax, found);
        var caches = new List<MeshTriangleCache>(found.Count);
        var j = 0;
        for (var i = 0; i < found.Count; i++)
        {
            var entry = default(MeshTriangleCache);
            for (; j < state.Triangles.Count; j++)
            {
                if ((uint)found[i] <= (uint)state.Triangles[j])
                {
                    if (state.Triangles[j] == found[i])
                        entry = state.Caches[j];
                    break;
                }
            }
            caches.Add(entry);
        }
        state.Triangles = found;
        state.Caches = caches;
    }
}
