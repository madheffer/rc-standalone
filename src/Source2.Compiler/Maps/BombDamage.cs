using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The CS2 bomb damage bake (maps/&lt;map&gt;/baked_bomb_damage.vdata, CCS2BombDamageBaker in
/// csgo's modtools.dll, run as the map compile modifier CS2BombDamage, 18003ad70). A grid of
/// cells (10 units) is sampled from the nav mesh, grown downward through free space with
/// zero-length box casts, and every bombsite (func_bomb_target with bomb_damage_power above 0)
/// spreads path distances over it. The file lists the sampled cells and, per site and cell, the
/// distance and the direction it comes from.
/// </summary>
public static class BombDamage
{
    /// <summary>The grid spacing (18003b750 sets +0x50 to 10).</summary>
    public const float Cell = 10f;

    /// <summary>A nav area: its polygon's corners in the mesh's order.</summary>
    public sealed record Area(Vector3[] Corners);

    /// <summary>
    /// A bombsite: its world bounds (the entity's vtable +0x1f0), bomb_damage_power (default
    /// 3000) and whether a point lies in its collision (vtable +0xd0 of each collision object).
    /// </summary>
    public sealed record Site(Vector3 Mins, Vector3 Maxs, float Power, Func<Vector3, bool> Contains);

    /// <summary>The physics queries the bake runs (CVPhys2World vtable +0x558, a shape cast).</summary>
    public interface IWorld
    {
        /// <summary>A box of this half size at the point, cast by zero: true when nothing is hit.</summary>
        bool BoxFree(Vector3 centre, float half);
    }

    // The point key's hash (180041470): MurmurHash2's mix over the three floats' bits, seed 0xea711bd8.
    internal static uint Hash(Vector3 p)
    {
        const uint m = 0x5bd1e995;
        static uint Mix(uint k)
        {
            k *= m;
            k ^= k >> 24;
            return k * m;
        }
        var h = 0xea711bd8u ^ Mix(BitConverter.SingleToUInt32Bits(p.X));
        h = h * m ^ Mix(BitConverter.SingleToUInt32Bits(p.Y));
        h = h * m ^ Mix(BitConverter.SingleToUInt32Bits(p.Z));
        h = (h >> 13 ^ h) * m;
        return h >> 15 ^ h;
    }

    /// <summary>A node of the grid (the 0x14-byte entries at +0x30): +0x10 blocked, +0x11 written out.</summary>
    internal struct Node
    {
        public bool Blocked;
        public bool Sampled;
    }

    /// <summary>A site's node (0x20 bytes): the path distance and the node it came from.</summary>
    internal struct SiteNode
    {
        public float Distance;
        public Vector3 From;
    }

    /// <summary>
    /// 18003d030: for each area and each triangle of its polygon's fan, (v0, v[n-1], v[n-2])
    /// down to (v0, v[2], v[1]), every cell of the area's bounds, snapped to the grid (y, then
    /// x, then z, each from the top), whose box of half size 10 meets the triangle (1803f2400,
    /// tolerance 0) is added once. The bounds of every such cell are kept.
    /// </summary>
    internal static Lighting.UtlHashtable<Vector3, byte> Seed(IReadOnlyList<Area> areas, out Vector3 min, out Vector3 max)
    {
        var table = new Lighting.UtlHashtable<Vector3, byte>();
        min = new Vector3(float.MaxValue);
        max = new Vector3(-float.MaxValue);
        const float half = Cell * 0.5f;
        foreach (var area in areas)
        {
            var c = area.Corners;
            if (c.Length < 3)
                throw new NotSupportedException("a nav area with no polygon (18003d030 traces a degenerate triangle) is not ported");
            var lo = c[0];
            var hi = c[0];
            foreach (var v in c)
            {
                lo = Vector3.Min(lo, v);
                hi = Vector3.Max(hi, v);
            }
            float Low(float v) => v - Fmod(v) + 0f - half;
            float High(float v) => v - Fmod(v) + Cell + half;
            float x0 = High(hi.X), y0 = High(hi.Y), z0 = High(hi.Z);
            float x1 = Low(lo.X), y1 = Low(lo.Y), z1 = Low(lo.Z);
            for (var k = c.Length - 1; k >= 2; k--)
            {
                Vector3 a = c[0], b = c[k], d = c[k - 1];
                if (y1 > y0)
                    continue;
                for (var y = y0; y1 <= y; y -= Cell)
                    for (var x = x0; x1 <= x; x -= Cell)
                    {
                        if (z1 > z0)
                            continue;
                        for (var z = z0; z1 <= z; z -= Cell)
                        {
                            var p = new Vector3(x, y, z);
                            if (!TriangleOverlapsBox(p, new Vector3(Cell), a, b, d, 0f))
                                continue;
                            min = new Vector3(x <= min.X ? x : min.X, y <= min.Y ? y : min.Y, z <= min.Z ? z : min.Z);
                            max = new Vector3(max.X <= x ? x : max.X, max.Y <= y ? y : max.Y, max.Z <= z ? z : max.Z);
                            table.FindOrInsert(p, Hash(p), out _);
                        }
                    }
            }
        }
        return table;
    }

    /// <summary>The bake's grid: the nodes (slot order is the file's), each site's own table, the seeds' bounds.</summary>
    internal sealed class Grid
    {
        public readonly Lighting.UtlHashtable<Vector3, Node> Nodes = new();
        public readonly List<Lighting.UtlHashtable<Vector3, SiteNode>> Sites = [];
        public Vector3 Min, Max;
    }

    /// <summary>The height the sampled cells are lifted by: floor(32 / cell) cells (30).</summary>
    internal static float Lift => MathF.Floor(32f / Cell) * Cell;

    /// <summary>
    /// 18003b750, the build. The seed cells (<see cref="Seed"/>) in slot order: a cell with
    /// another seed right above it is skipped; the rest are lifted by <see cref="Lift"/> into
    /// the node table and marked written out (+0x11), and a site whose collision holds the cell
    /// (unlifted) takes the lifted node at distance 0 (the first such site check per site).
    /// Then rounds over the node table in slot order: a node not yet visited is visited; a box
    /// of half the cell there, cast by zero, either hits (the node is marked blocked, +0x10)
    /// or the node proposes its five neighbours one cell along (+-1, 0, -1), (0, +-1, -1) and
    /// (0, 0, -1) that lie in the seeds' bounds grown by the lift and are not visited yet.
    /// After a round the proposals join the node table; the rounds end when none are made or
    /// after 64.
    /// </summary>
    internal static Grid Build(IReadOnlyList<Area> areas, IReadOnlyList<Site> sites, IWorld world)
    {
        var seeds = Seed(areas, out var min, out var max);
        var grid = new Grid { Min = min, Max = max };
        foreach (var _ in sites)
            grid.Sites.Add(new Lighting.UtlHashtable<Vector3, SiteNode>());
        var lift = Lift;
        foreach (var slot in seeds.Slots().ToList())
        {
            var p = seeds.Key(slot);
            var above = new Vector3(Cell * 0f + p.X, Cell * 0f + p.Y, Cell + p.Z);
            if (seeds.Find(above, Hash(above)) >= 0)
                continue;
            var q = new Vector3(lift * 0f + p.X, lift * 0f + p.Y, lift + p.Z);
            var at = grid.Nodes.FindOrInsert(q, Hash(q), out _);
            grid.Nodes.Value(at).Sampled = true;
            for (var s = 0; s < sites.Count; s++)
                if (sites[s].Contains(p))
                {
                    var n = grid.Sites[s].FindOrInsert(q, Hash(q), out _);
                    grid.Sites[s].Value(n).Distance = 0f;
                }
        }
        if (grid.Sites.Any(t => t.Count == 0))
            throw new NotSupportedException("a bombsite no sampled cell lies in (18003b750 fails the bake) is not ported");

        Vector3 lo = new(min.X - lift, min.Y - lift, min.Z - lift), hi = new(max.X + lift, max.Y + lift, max.Z + lift);
        ReadOnlySpan<Vector3> steps = [new(1, 0, -1), new(-1, 0, -1), new(0, 1, -1), new(0, -1, -1), new(0, 0, -1)];
        var visited = new Lighting.UtlHashtable<Vector3, byte>();
        var proposed = new List<Vector3>();
        for (var rounds = 64; ;)
        {
            foreach (var slot in grid.Nodes.Slots().ToList())
            {
                var p = grid.Nodes.Key(slot);
                var h = Hash(p);
                if (visited.Find(p, h) >= 0)
                    continue;
                visited.FindOrInsert(p, h, out _);
                if (!world.BoxFree(p, Cell * 0.5f))
                {
                    grid.Nodes.Value(slot).Blocked = true;
                    continue;
                }
                foreach (var d in steps)
                {
                    var c = new Vector3(Cell * d.X + p.X, Cell * d.Y + p.Y, Cell * d.Z + p.Z);
                    if (lo.X <= c.X && lo.Y <= c.Y && lo.Z <= c.Z && c.X <= hi.X && c.Y <= hi.Y && c.Z <= hi.Z
                        && visited.Find(c, Hash(c)) < 0)
                        proposed.Add(c);
                }
            }
            if (proposed.Count == 0 || --rounds < 1)
                break;
            foreach (var c in proposed)
                grid.Nodes.FindOrInsert(c, Hash(c), out _);
            proposed.Clear();
        }
        return grid;
    }

    /// <summary>
    /// 180041040's positions: the written-out nodes in slot order, lowered by the lift, each
    /// coordinate truncated to a 16-bit integer (x, y, z).
    /// </summary>
    internal static byte[] Positions(Grid grid)
    {
        var lift = Lift;
        var bytes = new List<byte>();
        foreach (var slot in grid.Nodes.Slots())
        {
            if (!grid.Nodes.Value(slot).Sampled)
                continue;
            var p = grid.Nodes.Key(slot);
            foreach (var v in new[] { p.X, p.Y, p.Z - lift })
                bytes.AddRange(BitConverter.GetBytes((short)(int)v));
        }
        return [.. bytes];
    }

    // V_fmodf: the C fmodf, exact; C#'s float remainder is the same operation.
    private static float Fmod(float v) => v % Cell;

    /// <summary>
    /// 1803f2400: a box (centre, half size) against a triangle, the separating axis test of
    /// Akenine-Moller: the box's axes first, then the triangle's normal and the nine edge cross
    /// products, each with the tolerance added to the box's reach.
    /// </summary>
    internal static bool TriangleOverlapsBox(Vector3 centre, Vector3 half, Vector3 a, Vector3 b, Vector3 c, float tolerance)
    {
        // The corners relative to the centre (v0 = a, v1 = b, v2 = c).
        float x0 = a.X - centre.X, x1 = b.X - centre.X, x2 = c.X - centre.X;
        if (!AxisOverlaps(x0, x1, x2, tolerance + half.X))
            return false;
        float y0 = a.Y - centre.Y, y1 = b.Y - centre.Y, y2 = c.Y - centre.Y;
        if (!AxisOverlaps(y0, y1, y2, tolerance + half.Y))
            return false;
        float z0 = a.Z - centre.Z, z1 = b.Z - centre.Z, z2 = c.Z - centre.Z;
        if (!AxisOverlaps(z0, z1, z2, tolerance + half.Z))
            return false;

        // Edge v1 - v0: X x e (v0 and v2), Y x e (v0 and v2), Z x e (v2 and v1).
        var (ex, ey, ez) = Unit(x1 - x0, y1 - y0, z1 - z0);
        var e0 = (ex, ey, ez);
        if (Separated(y0 * ez - z0 * ey, y2 * ez - z2 * ey, half.Y * MathF.Abs(ez) + MathF.Abs(ey) * half.Z + tolerance)
            || Separated(z0 * ex - x0 * ez, z2 * ex - x2 * ez, MathF.Abs(ex) * half.Z + half.X * MathF.Abs(ez) + tolerance)
            || Separated(x2 * ey - y2 * ex, x1 * ey - y1 * ex, half.Y * MathF.Abs(ex) + half.X * MathF.Abs(ey) + tolerance))
            return false;

        // Edge v2 - v1: X x e (v0, v1), Y x e (v0, v1), Z x e (v0, v2).
        (ex, ey, ez) = Unit(x2 - x1, y2 - y1, z2 - z1);
        if (Separated(y0 * ez - z0 * ey, y1 * ez - z1 * ey, half.Y * MathF.Abs(ez) + MathF.Abs(ey) * half.Z + tolerance)
            || Separated(z0 * ex - x0 * ez, z1 * ex - x1 * ez, half.X * MathF.Abs(ez) + MathF.Abs(ex) * half.Z + tolerance)
            || Separated(x0 * ey - y0 * ex, x2 * ey - y2 * ex, half.Y * MathF.Abs(ex) + half.X * MathF.Abs(ey) + tolerance))
            return false;

        // Edge v0 - v2: X x e (v0, v1), Y x e (v0, v1), Z x e (v2, v1).
        (ex, ey, ez) = Unit(x0 - x2, y0 - y2, z0 - z2);
        if (Separated(y0 * ez - z0 * ey, y1 * ez - z1 * ey, half.Y * MathF.Abs(ez) + MathF.Abs(ey) * half.Z + tolerance)
            || Separated(z0 * ex - x0 * ez, z1 * ex - x1 * ez, half.X * MathF.Abs(ez) + MathF.Abs(ex) * half.Z + tolerance)
            || Separated(x2 * ey - y2 * ex, x1 * ey - y1 * ex, half.Y * MathF.Abs(ex) + half.X * MathF.Abs(ey) + tolerance))
            return false;

        // The normal, (v0 - v2) x (v1 - v0) of the unit edges, made unit.
        var (nx, ny, nz) = Unit(ez * e0.ey - ey * e0.ez, ex * e0.ez - ez * e0.ex, ey * e0.ex - ex * e0.ey);
        var d = nz * z0 + ny * y0 + nx * x0;
        var r = MathF.Abs(ny * half.Y) + MathF.Abs(nx * half.X) + MathF.Abs(nz * half.Z) + tolerance;
        return d <= r && -r <= d;
    }

    // The bounds test on one axis: the corners' least against r, their greatest against -r.
    private static bool AxisOverlaps(float v0, float v1, float v2, float r)
    {
        var lo = v0 <= v1 ? v0 : v1;
        lo = lo <= v2 ? lo : v2;
        if (!(lo <= r))
            return false;
        var hi = v1 <= v0 ? v0 : v1;
        hi = v2 <= hi ? hi : v2;
        return -r <= hi;
    }

    // One edge axis as the binary tests it: "second <= first" picks the lesser, which must not
    // pass r, while the greater must not fall short of -r.
    private static bool Separated(float first, float second, float r)
        => second <= first ? r < second || first < -r : r < first || second < -r;

    // Vector normalisation as the decompile has it: length from z^2 + y^2 + x^2, times 1 / length;
    // zero for a zero vector (the 1e-17 / 1e17 rescue, 1803f1c80, is not ported).
    private static (float X, float Y, float Z) Unit(float x, float y, float z)
    {
        var len = MathF.Sqrt(z * z + y * y + x * x);
        if (len == 0f)
            return (0f, 0f, 0f);
        if (len < 1e-17f || 1e17f < len)
            throw new NotSupportedException("1803f1c80 (normalising a tiny or huge vector) is not ported");
        var inv = 1f / len;
        return (x * inv, y * inv, z * inv);
    }
}
