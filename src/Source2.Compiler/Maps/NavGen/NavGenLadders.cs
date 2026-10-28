using System.Numerics;

namespace Source2.Compiler.Maps.NavGen;

/// <summary>A ladder in the RC nav input (CNavGenInput +0x60, 0x28 bytes, FUN_1818ca470): top, bottom, normal, width.</summary>
public readonly record struct NavLadder(Vector3 Top, Vector3 Bottom, Vector3 Normal, float Width);

/// <summary>The world LadderConnect traces through (CVPhys2World vfn 0x4f8, CRnWorld::CastRay 1801f4ad0).</summary>
public interface INavRayWorld
{
    /// <summary>The fraction of <paramref name="delta"/> a ray from <paramref name="start"/> gets before a hit, 1 when none.</summary>
    float CastRay((float X, float Y, float Z) start, (float X, float Y, float Z) delta);
}

public static partial class NavGenMapInput
{
    /// <summary>The material int attribute that makes a mesh a ladder: "mapbuilder.ladder" (MurmurHash2, seed 0x31415926).</summary>
    public const uint LadderAttribute = 0x1a382654;

    /// <summary>
    /// The RC ladder collector (resourcecompiler 20260923 180fb15e0) into CNavGenInput +0x60. Each mesh in
    /// the walk (180fb2830 recursing through non-mesh nodes) whose material list holds a material with
    /// a non-zero mapbuilder.ladder int (material vfn 0x50) gives, per placement (180fb21f0; one per
    /// <see cref="MapMeshes.Mesh"/> copy, the node's matrix after the instance path's), the face using
    /// the first such material with the largest |top - bottom| times width (180fb1c10; the first face
    /// wins a tie). The walk skips func_ladder entities; each visible func_ladder's direct child
    /// meshes go through the same 180fb21f0 after all the others (180fb1380: the map's flat node array,
    /// the root document's nodes in pre-order, GROUND_TRUTH 107), so they too need a ladder material.
    /// A func_ladder reached through an instance or a prefab (its place in the array and the
    /// sub-maps, 180fc8c80) refuses. Unsettled: the order of several ladders (the walk against
    /// 180f88460's paths), and that hidden meshes are left out as the compile leaves them out.
    /// </summary>
    public static List<NavLadder> Ladders(DmxBinary.Document document, Func<string, SettleWorld.MaterialInfo?> material)
    {
        var ladders = new List<NavLadder>();
        var isLadder = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool IsLadder(string path)
        {
            if (!isLadder.TryGetValue(path, out var yes))
            {
                yes = material(path) is { } info
                      && info.Ints.Any(kv => MeshEntryFlags.Key(kv.Key) == LadderAttribute && (int)kv.Value != 0);
                isLadder[path] = yes;
            }
            return yes;
        }
        var meshes = MapMeshes.Read(document);
        var underLadder = new HashSet<DmxBinary.Element>(ReferenceEqualityComparer.Instance);
        foreach (var e in SettleWorld.AllDocuments(document).SelectMany(d => d.Elements))
            if (e.Type == "CMapEntity"
                && string.Equals(e.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname"), "func_ladder", StringComparison.OrdinalIgnoreCase))
                foreach (var child in e.GetElements("children"))
                    underLadder.Add(child);
        bool InFuncLadder(MapMeshes.Mesh m) => m.Element is { } el && underLadder.Contains(el);
        foreach (var mesh in meshes.Where(m => !InFuncLadder(m)).Concat(meshes.Where(InFuncLadder)))
        {
            if (mesh.Hidden || mesh.Element is not { } element)
                continue;
            if (InFuncLadder(mesh) && (mesh.Instances.Length > 0 || mesh.Prefabs.Length > 0))
                throw new NotSupportedException("a func_ladder inside an instance or prefab (its place in the flat node array, 180fc8c80) is not ported");
            var (corners, faceMaterial, materials) = MapMeshes.LocalFaces(element);
            var index = Array.FindIndex(materials, IsLadder);
            if (index < 0)
                continue;
            NavLadder? best = null;
            var bestScore = 0f;
            for (var f = 0; f < corners.Length; f++)
            {
                if (faceMaterial[f] != index)
                    continue;
                var l = FaceLadder(corners[f], mesh.World);
                var d = l.Top - l.Bottom;
                var score = MathF.Sqrt((d.X * d.X + d.Y * d.Y) + d.Z * d.Z) * l.Width;
                if (best is null || bestScore < score)
                {
                    best = l;
                    bestScore = score;
                }
            }
            if (best is { } found)
                ladders.Add(found);
        }
        return ladders;
    }

    /// <summary>
    /// 180fb1c10: one face as a ladder. The corners through the matrix (18125d1f0, as
    /// <see cref="MapMeshes.Transform"/>) bound it (1810ce420); the face's Newell normal (181384e40)
    /// rotated by the matrix (18125d1b0) is n; u = up x n and v = n x u, each normalised (squares
    /// summed y, z, x for u and z, y, x for v; between 1e-17 and 1e17 times the reciprocal, zero
    /// stays zero); half the height is |v . (max - min)| / 2 and the width |u . (max - min)|, both
    /// summed z, y, x; top and bottom the bounds' centre plus and minus v times the half height.
    /// </summary>
    internal static NavLadder FaceLadder(Vector3[] local, float[] world)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(-float.MaxValue);
        foreach (var p in local)
        {
            var q = MapMeshes.Transform(world, p);
            min = new Vector3(q.X <= min.X ? q.X : min.X, q.Y <= min.Y ? q.Y : min.Y, q.Z <= min.Z ? q.Z : min.Z);
            max = new Vector3(max.X <= q.X ? q.X : max.X, max.Y <= q.Y ? q.Y : max.Y, max.Z <= q.Z ? q.Z : max.Z);
        }
        var c = new Vector3((max.X + min.X) * 0.5f, (max.Y + min.Y) * 0.5f, (max.Z + min.Z) * 0.5f);
        var n = NodeMeshEntries.Rotate(world, PolygonTriangulator.Newell(local));
        var u = Normalise(new Vector3(n.Z * 0f - n.Y, n.X - n.Z * 0f, n.Y * 0f - n.X * 0f), (u => (u.Y * u.Y + u.Z * u.Z) + u.X * u.X));
        var v = Normalise(new Vector3(n.Y * u.Z - n.Z * u.Y, n.Z * u.X - u.Z * n.X, u.Y * n.X - n.Y * u.X), (v => (v.Z * v.Z + v.Y * v.Y) + v.X * v.X));
        float dx = max.X - min.X, dy = max.Y - min.Y, dz = max.Z - min.Z;
        var half = MathF.Abs((v.Z * dz + v.Y * dy) + v.X * dx) * 0.5f;
        var width = MathF.Abs((u.Z * dz + u.Y * dy) + u.X * dx);
        var top = new Vector3(v.X * half + c.X, v.Y * half + c.Y, v.Z * half + c.Z);
        var bottom = new Vector3(c.X - v.X * half, c.Y - v.Y * half, c.Z - v.Z * half);
        return new NavLadder(top, bottom, n, width);
    }

    private static Vector3 Normalise(Vector3 d, Func<Vector3, float> squared)
    {
        var len = MathF.Sqrt(squared(d));
        if (len < 1e-17f || 1e17f < len)
            return len == 0f ? Vector3.Zero : VectorNormalizeSlow.Normalise(d);
        var inv = 1f / len;
        return new Vector3(d.X * inv, d.Y * inv, d.Z * inv);
    }
}

public sealed partial class NavGenPolys
{
    /// <summary>LadderConnect's step along a probe direction (180129594 / 6 is its z shift).</summary>
    public const float LadderStep = 16f;

    /// <summary>180129594: the height the bottom probes start above the ladder's bottom, and six times the z shift.</summary>
    public const float LadderLift = 25f;

    /// <summary>
    /// LadderConnect (028, 1800732d0): per ladder, the seven polygons IslandRemoval floods from (0x1c
    /// bytes at ctx+0x90): the tops' four probes (+0 to +0xc) and the bottoms' three (+0x10 to +0x18),
    /// -1 for none. The polygons are put in a 32-unit grid as IslandRemoval does (their bounds written,
    /// 180076d40). The probe height is half the ladder's height, at most 120. The bottoms probe from
    /// the bottom moved 16 along n and lifted 25, along n, (-ny, -nx, -0) and (ny, nx, 0), skipping
    /// polygons of kind 1 and 2 and stepping down on a miss; the tops from the top moved 16 along n,
    /// along -n, (-ny, -nx, -0), (ny, nx, 0) and n, every kind, stepping up; a top probe that finds the
    /// polygon its bottom probe (by index: -n and n against n) found gives -1. Then a bottom polygon
    /// whose lowest corner is more than 71 below the ladder's bottom is dropped. The polygons are not
    /// changed.
    /// </summary>
    public List<int[]> LadderConnect(IReadOnlyList<NavLadder> ladders, INavRayWorld world)
    {
        var (lo, hi) = ComputeBounds();
        var grid = new IslandGrid(lo.X, lo.Y, hi.X, hi.Y, IslandGridCell);
        for (var i = 0; i < Polys.Count; i++)
        {
            var b = Polys[i].Bounds!;
            grid.Insert(b[0], b[2], b[1], b[3], i);
        }
        var result = new List<int[]>();
        foreach (var l in ladders)
        {
            var h = (l.Top.Z - l.Bottom.Z) * 0.5f;
            var height = 120f < h ? 120f : h;
            var n = l.Normal;
            (float X, float Y, float Z) low = ((l.Bottom.X + 0f) + n.X * LadderStep, (l.Bottom.Y + 0f) + n.Y * LadderStep, (l.Bottom.Z + LadderLift) + n.Z * LadderStep);
            (float X, float Y, float Z) side1 = (-n.Y, -n.X, -0f), side2 = (n.Y, n.X, 0f);
            var b0 = LadderProbe(grid, world, low, (n.X, n.Y, n.Z), height, up: false, skipCrouch: true);
            var b1 = LadderProbe(grid, world, low, side1, height, up: false, skipCrouch: true);
            var b2 = LadderProbe(grid, world, low, side2, height, up: false, skipCrouch: true);
            (float X, float Y, float Z) high = (n.X * LadderStep + l.Top.X, n.Y * LadderStep + l.Top.Y, n.Z * LadderStep + l.Top.Z);
            var t0 = LadderProbe(grid, world, high, (-n.X, -n.Y, -n.Z), height, up: true, skipCrouch: false);
            if (t0 == b0)
                t0 = -1;
            var t1 = LadderProbe(grid, world, high, side1, height, up: true, skipCrouch: false);
            if (t1 == b1)
                t1 = -1;
            var t2 = LadderProbe(grid, world, high, side2, height, up: true, skipCrouch: false);
            if (t2 == b2)
                t2 = -1;
            var t3 = LadderProbe(grid, world, high, (n.X, n.Y, n.Z), height, up: true, skipCrouch: false);
            if (t3 == b0)
                t3 = -1;
            int Low(int p)
            {
                if (p == -1)
                    return p;
                var m = float.MaxValue;
                foreach (var v in Polys[p].Indices)
                {
                    var z = (float)Pool[v].Z;
                    if (z <= m)
                        m = z;
                }
                return 71f < l.Bottom.Z - m ? -1 : p;
            }
            result.Add([t0, t1, t2, t3, Low(b0), Low(b1), Low(b2)]);
        }
        return result;
    }

    // 18006e170: from start, up to three steps of 16 along dir, each traced from start through the world
    // (layers 0x3021); a blocked trace ends the try, else the polygon at the step's end (18006de40)
    // answers. Seven tries, each with the start's z moved by 25 / 6 (down for the bottoms).
    private int LadderProbe(IslandGrid grid, INavRayWorld world, (float X, float Y, float Z) start, (float X, float Y, float Z) dir,
                            float height, bool up, bool skipCrouch)
    {
        var shift = LadderLift / 6f;
        float bx = start.X, by = start.Y, bz = start.Z;
        for (var attempt = 0; ;)
        {
            float ex = bx, ey = by, ez = bz;
            for (var k = 1; k <= 3; k++)
            {
                ex += dir.X * LadderStep;
                ey += dir.Y * LadderStep;
                ez += dir.Z * LadderStep;
                if (world.CastRay((bx, by, bz), (ex - bx, ey - by, ez - bz)) < 1f)
                    break;
                if (LadderPoly(grid, (ex, ey, ez), height, skipCrouch) is var found and not -1)
                    return found;
            }
            attempt++;
            bx += 0f;
            by += 0f;
            bz += up ? shift : -shift;
            if (attempt > 6)
                return -1;
        }
    }

    // 18006de40: the grid's polygons in a +-0.45 box round the point (doubles), kinds 1 and 2 skipped when
    // asked, every corner within 18 above and the height below the point; the nearest by 18004cc30
    // (squared distance to the fan from corner 0, (v0, v[n-1], v[n-2]) down to (v0, v2, v1)), a later one
    // only when strictly nearer.
    private int LadderPoly(IslandGrid grid, (float X, float Y, float Z) p, float height, bool skipCrouch)
    {
        var best = double.MaxValue;
        var found = -1;
        var q = ((double)p.X, (double)p.Y, (double)p.Z);
        foreach (var i in grid.Query((double)p.X - 0.45, (double)p.Y - 0.45, (double)p.X + 0.45, (double)p.Y + 0.45))
        {
            var poly = Polys[i];
            if (skipCrouch && (byte)(poly.Kind - 1) <= 1)
                continue;
            var inRange = true;
            foreach (var v in poly.Indices)
            {
                var z = (float)Pool[v].Z;
                if (p.Z + 18f < z || z < p.Z - height)
                {
                    inRange = false;
                    break;
                }
            }
            if (!inRange)
                continue;
            var d = FanDistance(poly, q);
            if (d < best)
            {
                best = d;
                found = i;
            }
        }
        return found;
    }

    // 18004cc30: the least squared distance (dy^2 + dx^2 + dz^2) from q to the triangles of the fan.
    private double FanDistance(NavPoly poly, (double X, double Y, double Z) q)
    {
        var n = poly.Indices.Count;
        var best = double.MaxValue;
        for (int a = n - 1, b = n - 2; a > 1; a--, b--)
        {
            var c = ClosestOnTriangle(q, Pool[poly.Indices[0]], Pool[poly.Indices[a % n]], Pool[poly.Indices[b % n]]);
            var d = (c.Y - q.Y) * (c.Y - q.Y) + (c.X - q.X) * (c.X - q.X) + (c.Z - q.Z) * (c.Z - q.Z);
            if (!(best <= d))
                best = d;
        }
        return best;
    }
}
