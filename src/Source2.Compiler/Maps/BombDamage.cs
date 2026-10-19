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

        /// <summary>A ray from start by delta (CVPhys2World vtable +0x4f8): true when it hits before its end.</summary>
        bool RayHits(Vector3 start, Vector3 delta);
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

    private static readonly Vector3 Far = new(float.MaxValue);

    private static readonly SiteNode Unreached = new() { Distance = float.MaxValue, From = Far };

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
                    // 1800417e0 makes the entry at FLT_MAX with no parent; the lift sets the distance only.
                    var n = grid.Sites[s].FindOrInsert(q, Hash(q), out var added);
                    if (added)
                        grid.Sites[s].Value(n) = Unreached;
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

    /// <summary>The 26 neighbour steps of the distance pass (180cd9170), in table order.</summary>
    private static readonly Vector3[] Neighbours =
    [
        new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1),
        new(1, 1, 0), new(-1, 1, 0), new(1, -1, 0), new(-1, -1, 0), new(0, 1, 1), new(0, -1, 1),
        new(0, 1, -1), new(0, -1, -1), new(1, 0, 1), new(-1, 0, 1), new(1, 0, -1), new(-1, 0, -1),
        new(1, 1, 1), new(-1, 1, 1), new(1, -1, 1), new(1, 1, -1), new(-1, -1, 1), new(1, -1, -1),
        new(-1, 1, -1), new(-1, -1, -1),
    ];

    /// <summary>One neighbour's outcome (the 0x28-byte records 18003eb60 fills): a new record for a slot, and a node to visit next.</summary>
    private struct Relax
    {
        public SiteNode Record;
        public int Slot;
        public Vector3? Visit;
    }

    /// <summary>
    /// 18003ddf0, PopulateNodeDistancesFromBombsite, for one site. The frontier starts as the
    /// site's distance-0 nodes (in its table's slot order); every grid node then takes an entry
    /// (FLT_MAX, no parent). Each frontier node in slot order runs its 26 neighbour jobs on the
    /// current records (in parallel in the binary, each writing only its own result), then the
    /// results are applied in neighbour order: the neighbour's record replaced, and a neighbour
    /// whose grid node is not blocked added to the next frontier. Frontiers run until empty.
    /// False when the site's table does not cover every grid node.
    /// </summary>
    internal static bool Distances(Grid grid, int site, IWorld world)
    {
        var table = grid.Sites[site];
        var frontier = new Lighting.UtlHashtable<Vector3, byte>();
        foreach (var slot in table.Slots().ToList())
        {
            var k = table.Key(slot);
            frontier.FindOrInsert(k, Hash(k), out _);
        }
        foreach (var slot in grid.Nodes.Slots().ToList())
        {
            var k = grid.Nodes.Key(slot);
            var at = table.FindOrInsert(k, Hash(k), out var added);
            if (added)
                table.Value(at) = Unreached;
        }
        if (table.Count != grid.Nodes.Count)
            return false;
        var results = new Relax[Neighbours.Length];
        // The two frontiers swap; the finished one is emptied in place (its slots kept) to be the next.
        var next = new Lighting.UtlHashtable<Vector3, byte>();
        while (frontier.Count > 0)
        {
            foreach (var slot in frontier.Slots().ToList())
            {
                var p = frontier.Key(slot);
                var own = table.Find(p, Hash(p));
                if (own < 0)
                    continue;
                for (var d = 0; d < Neighbours.Length; d++)
                    results[d] = Neighbour(grid, table, world, p, own, Neighbours[d]);
                foreach (var r in results)
                {
                    if (r.Slot < 0)
                        continue;
                    table.Value(r.Slot) = r.Record;
                    if (r.Visit is { } v)
                        next.FindOrInsert(v, Hash(v), out _);
                }
            }
            frontier.Clear();
            (frontier, next) = (next, frontier);
        }
        return true;
    }

    // 18003eb60: one neighbour job.
    private static Relax Neighbour(Grid grid, Lighting.UtlHashtable<Vector3, SiteNode> table, IWorld world, Vector3 p, int own, Vector3 step)
    {
        var none = new Relax { Slot = -1, Record = Unreached };
        var n = new Vector3(Cell * step.X + p.X, Cell * step.Y + p.Y, Cell * step.Z + p.Z);
        var slot = table.Find(n, Hash(n));
        if (slot < 0)
            return none;
        var current = table.Value(own);
        var neighbour = table.Value(slot);
        if (!(current.Distance <= neighbour.Distance))
            return none;
        var candidate = neighbour;
        candidate.From = LineBlocked(world, n, current.From) ? p : current.From;
        if (candidate.From == n)
            return none;
        var path = PathTo(n, table, slot, candidate);
        float distance;
        if (path is null)
            distance = -1f;
        else
        {
            var length = PathLength(path);
            var turns = Turns(path);
            var t = (length - 500f) / 1000f;
            if (t <= 0f)
                t = 0f;
            if (1f <= t)
                t = 1f;
            distance = (t * 20f + 0f) * turns + length;
        }
        candidate.Distance = distance;
        if (distance == -1f || !(distance < neighbour.Distance))
            return none;
        var node = grid.Nodes.Find(n, Hash(n));
        return new Relax { Slot = slot, Record = candidate, Visit = node >= 0 && !grid.Nodes.Value(node).Blocked ? n : null };
    }

    /// <summary>
    /// 18003db50: whether the line between two points is blocked: a far point (FLT_MAX) always
    /// is, the same point never is; else a ray each way (meshes take rays on their front only).
    /// </summary>
    private static bool LineBlocked(IWorld world, Vector3 a, Vector3 b)
    {
        if (a == Far || b == Far)
            return true;
        if (b.X == a.X && b.Y == a.Y && b.Z == a.Z)
            return false;
        if (world.RayHits(a, new Vector3(b.X - a.X, b.Y - a.Y, b.Z - a.Z)))
            return true;
        return world.RayHits(b, new Vector3(a.X - b.X, a.Y - b.Y, a.Z - b.Z));
    }

    /// <summary>
    /// 18003f260: the path from a node back along the parents (the candidate standing in for
    /// the neighbour's own record), to a node at distance 0 or its own parent, or to a point the
    /// table lacks. Null when the walk comes back to a point (a cycle).
    /// </summary>
    private static List<Vector3>? PathTo(Vector3 start, Lighting.UtlHashtable<Vector3, SiteNode> table, int candidateSlot, SiteNode candidate)
    {
        var visited = new Lighting.UtlHashtable<Vector3, byte>();
        var path = new List<Vector3>();
        var pt = start;
        var record = candidate;
        while (true)
        {
            var h = Hash(pt);
            if (visited.Find(pt, h) >= 0)
                return null;
            visited.FindOrInsert(pt, h, out _);
            path.Add(pt);
            if (record.Distance <= 0f || record.From == pt)
                return path;
            pt = record.From;
            var slot = table.Find(pt, Hash(pt));
            if (slot < 0)
                return path;
            record = slot == candidateSlot ? candidate : table.Value(slot);
        }
    }

    /// <summary>18003fa60: the path's length, the segments' lengths (from z^2 + y^2 + x^2) summed four at a time.</summary>
    private static float PathLength(List<Vector3> p)
    {
        float Segment(int k)
        {
            float dx = p[k].X - p[k + 1].X, dy = p[k].Y - p[k + 1].Y, dz = p[k].Z - p[k + 1].Z;
            return MathF.Sqrt(dz * dz + dy * dy + dx * dx);
        }
        var n = p.Count - 1;
        var sum = 0f;
        var k = 0;
        if (n >= 4)
            for (; k + 4 <= ((n - 4) >> 2) * 4 + 4; k += 4)
                sum = Segment(k) + sum + Segment(k + 1) + Segment(k + 2) + Segment(k + 3);
        for (; k < n; k++)
            sum += Segment(k);
        return sum;
    }

    /// <summary>
    /// 18003fd80: the turning along the path in degrees: at each inner point the angle between
    /// the segments in and out (acosf of their dot over their lengths, clamped to [-1, 1]), none
    /// where the two differ by under 0.01 on every axis.
    /// </summary>
    private static float Turns(List<Vector3> p)
    {
        var sum = 0f;
        for (var i = 0; i + 2 < p.Count; i++)
        {
            float ax = p[i + 1].X - p[i].X, ay = p[i + 1].Y - p[i].Y, az = p[i + 1].Z - p[i].Z;
            float bx = p[i + 2].X - p[i + 1].X, by = p[i + 2].Y - p[i + 1].Y, bz = p[i + 2].Z - p[i + 1].Z;
            var angle = 0f;
            if (!(MathF.Abs(ax - bx) < 0.01f && MathF.Abs(ay - by) < 0.01f && MathF.Abs(az - bz) < 0.01f))
            {
                var la = MathF.Sqrt(ax * ax + ay * ay + az * az);
                var lb = MathF.Sqrt(bx * bx + by * by + bz * bz);
                var dot = ay * by + ax * bx + az * bz;
                var c = dot * (1f / lb * (1f / la));
                c = MathF.Max(c, -1f);
                c = MathF.Min(c, 1f);
                angle = Simulation.CrtMath.Acos(c);
            }
            sum += MathF.Abs(angle * BitConverter.Int32BitsToSingle(0x42652ee0));
        }
        return sum;
    }

    /// <summary>
    /// 18003ffa0 and 180041040's damage_values: per site, per written-out node in slot order, a
    /// 32-bit word: the distance clamped to [0, 65535] (16 bits), then bytes 2 and 3 the yaw and
    /// pitch / 360 * 255 of the direction from the parent to the node; within 1600 units the
    /// direction leans toward the site (its bounds' centre in x and y, its floor in z) by
    /// 1 - clamp((distance - 800) / 800).
    /// </summary>
    internal static byte[] DamageValues(Grid grid, IReadOnlyList<Site> sites)
    {
        var bytes = new List<byte>();
        for (var s = 0; s < sites.Count; s++)
        {
            var table = grid.Sites[s];
            foreach (var slot in grid.Nodes.Slots())
            {
                if (!grid.Nodes.Value(slot).Sampled)
                    continue;
                var p = grid.Nodes.Key(slot);
                var record = table.Value(table.Find(p, Hash(p)));
                var d = record.Distance;
                var clamped = 0f <= d ? (d <= 65535f ? d : 65535f) : 0f;
                var word = new byte[4];
                BitConverter.TryWriteBytes(word, (ushort)(short)(int)clamped);
                var v1 = Unit3(p.X - record.From.X, p.Y - record.From.Y, p.Z - record.From.Z);
                var (pitch, yaw) = Angles(v1);
                word[3] = AngleByte(pitch);
                word[2] = AngleByte(yaw);
                var w = (clamped - 800f) / 800f;
                if (w <= 0f)
                    w = 0f;
                if (1f <= w)
                    w = 1f;
                w = 1f - w;
                if (0f < w)
                {
                    var site = sites[s];
                    var v2 = Unit3(p.X - (site.Maxs.X + site.Mins.X) * 0.5f, p.Y - (site.Maxs.Y + site.Mins.Y) * 0.5f, p.Z - site.Mins.Z);
                    var blend = Unit3((v2.X - v1.X) * w + v1.X, (v2.Y - v1.Y) * w + v1.Y, (v2.Z - v1.Z) * w + v1.Z);
                    (pitch, yaw) = Angles(blend);
                    word[3] = AngleByte(pitch);
                    word[2] = AngleByte(yaw);
                }
                bytes.AddRange(word);
            }
        }
        return [.. bytes];
    }

    private static (float Pitch, float Yaw) Angles(Vector3 v)
    {
        var a = Lighting.SceneLights.VectorAngles(v);
        return (Wrap(a.X), Wrap(a.Y));
    }

    // The angle normalised to [-180, 180] when outside it (floor(a / 360 + 0.5) turns off), then made positive.
    private static float Wrap(float a)
    {
        if (a < -180f || 180f < a)
        {
            a -= MathF.Floor(a * 0.0027777778f + 0.5f) * 360f;
            if (a <= -180f)
                a = -180f;
            if (180f <= a)
                a = 180f;
        }
        if (a < 0f)
            a += 360f;
        return a;
    }

    private static byte AngleByte(float a) => (byte)(sbyte)(int)(a / 360f * 255f);

    // The vector normalised as the bake's helpers do: length from x, y, z as each call orders
    // them is the caller's concern; here (y^2 + z^2) + x^2 for the parent direction and the blend.
    private static Vector3 Unit3(float x, float y, float z)
    {
        var len = MathF.Sqrt(y * y + z * z + x * x);
        if (len < 1e-17f || 1e17f < len)
        {
            if (len == 0f)
                return Vector3.Zero;
            var (dx, dy, dz) = Wide(x, y, z);
            return new Vector3(dx, dy, dz);
        }
        var inv = 1f / len;
        return new Vector3(x * inv, y * inv, z * inv);
    }

    // 1803f1c80: a tiny or huge vector normalised in double: (y^2 + x^2) + z^2, each component times 1 / length.
    private static (float X, float Y, float Z) Wide(float x, float y, float z)
    {
        double dx = x, dy = y, dz = z;
        var inv = 1.0 / Math.Sqrt(dy * dy + dx * dx + dz * dz);
        return ((float)(inv * dx), (float)(inv * dy), (float)(inv * dz));
    }

    /// <summary>180040c40's bombsites blob: per site its bounds' mins and maxs and its power (7 floats).</summary>
    internal static byte[] Bombsites(IReadOnlyList<Site> sites)
    {
        var bytes = new List<byte>();
        foreach (var s in sites)
            foreach (var f in new[] { s.Mins.X, s.Mins.Y, s.Mins.Z, s.Maxs.X, s.Maxs.Y, s.Maxs.Z, s.Power })
                bytes.AddRange(BitConverter.GetBytes(f));
        return [.. bytes];
    }

    /// <summary>
    /// The .vdata the modifier writes into the map's content folder (SaveKV3, generic format):
    /// generic_data_type "CS2_BOMB_DAMAGE_DATA", header { version = 2 }, and data with the
    /// bombsites, positions and damage_values blobs.
    /// </summary>
    public static string VdataText(byte[] bombsites, byte[] positions, byte[] damage)
    {
        var root = ValveKeyValue.KVObject.Collection();
        root.Add("generic_data_type", new ValveKeyValue.KVObject("CS2_BOMB_DAMAGE_DATA"));
        var header = ValveKeyValue.KVObject.Collection();
        header.Add("version", new ValveKeyValue.KVObject(2));
        root.Add("header", header);
        var data = ValveKeyValue.KVObject.Collection();
        data.Add("bombsites", ValveKeyValue.KVObject.Blob(bombsites));
        data.Add("positions", ValveKeyValue.KVObject.Blob(positions));
        data.Add("damage_values", ValveKeyValue.KVObject.Blob(damage));
        root.Add("data", data);
        return Kv3TextWriter.Write(root, new Guid("7412167c-06e9-4698-aff2-e63eb59037e7"));
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
            return Wide(x, y, z);
        var inv = 1f / len;
        return (x * inv, y * inv, z * inv);
    }
}
