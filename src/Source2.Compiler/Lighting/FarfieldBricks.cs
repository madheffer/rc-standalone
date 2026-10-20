using System.Numerics;
using ValveKeyValue;

namespace Source2.Compiler.Lighting;

/// <summary>
/// vrad3's farfield_compute_bricks (1800204d0, vrad3_farfieldprobevolume.cpp): the far field probe
/// volume's sparse brick layout, farfield_&lt;n&gt;_bricks.kv3.
/// <list type="number">
/// <item>The grid (the 1802ecfe0 struct): the box, its cells, and the 0x8c-byte g_Grids record
/// (180032b60): world to cell, cell to world, the world bounds, the counts.</item>
/// <item>Occupancy: rtx_vrad3_mark_occupied (D_MODE 0) marks every cell a scene triangle touches
/// (a separating-axis test against the cell grown to 0.5001), then D_MODE 1 dilates by the 26
/// neighbours, ceil(2 max cell / max cell) = 2 times. Ported on the CPU, see <see cref="Occupancy"/>.</item>
/// <item>Metrics: rtx_vrad3_farfield_metrics, 8 samples a cell, 64 ray queries each of length
/// 2 max cell (the GPU's, <c>RtxBakeHost.FarfieldMetrics</c>): {min t, max t, escaping fraction,
/// flags (1 backface majority, 2 unoccupied)}.</item>
/// <item>Levels (18001fa00), the budget's coarsening (18001fc60), the allocation over six axis
/// orders (18001e4b0, 18001ea40), the sort by level then z, y, x (180026b70), the level 7 root in
/// front, the cell descriptors (18001f590).</item>
/// </list>
/// </summary>
public static class FarfieldBricks
{
    /// <summary>A brick: its origin in cells and its level (it spans 2^level cells a side; 7 is the root).</summary>
    public readonly record struct Brick(int X, int Y, int Z, int Level);

    /// <summary>The script's farfield_box, farfield_local_to_world (3x4, rows) and farfield_cells.</summary>
    public sealed record Grid(Vector3 Mins, Vector3 Maxs, float[] LocalToWorld, int X, int Y, int Z)
    {
        public int Count => X * Y * Z;

        public Vector3 CellSize => new((Maxs.X - Mins.X) / X, (Maxs.Y - Mins.Y) / Y, (Maxs.Z - Mins.Z) / Z);

        /// <summary>The cell index (x fastest) or -1 outside.</summary>
        public int Index(int x, int y, int z) => x < 0 || y < 0 || z < 0 || x >= X || y >= Y || z >= Z ? -1 : (z * Y + y) * X + x;

        /// <summary>180032b60: world to cell, the inverse (1801f4490) with row i scaled by 1 / cell size i less mins i / cell size i.</summary>
        public float[] WorldToCell()
        {
            var m = Inverse(LocalToWorld);
            var c = CellSize;
            float[] cs = [c.X, c.Y, c.Z], mn = [Mins.X, Mins.Y, Mins.Z];
            for (var i = 0; i < 3; i++)
            {
                var r = 1f / cs[i];
                for (var k = 0; k < 4; k++)
                    m[i * 4 + k] = r * m[i * 4 + k];
                m[i * 4 + 3] -= r * mn[i];
            }
            return m;
        }

        /// <summary>Cell to world: local to world after (cell size, mins) (1801f3df0).</summary>
        public float[] CellToWorld()
        {
            var c = CellSize;
            return Concat(LocalToWorld, [c.X, 0f, 0f, Mins.X, 0f, c.Y, 0f, Mins.Y, 0f, 0f, c.Z, Mins.Z]);
        }

        /// <summary>1801f4f70: the box's world bounds, the centre through the transform and the half extents through its absolute.</summary>
        public (Vector3 Min, Vector3 Max) WorldBounds()
        {
            var m = LocalToWorld;
            var c = new Vector3((Maxs.X + Mins.X) * 0.5f, (Maxs.Y + Mins.Y) * 0.5f, (Maxs.Z + Mins.Z) * 0.5f);
            var e = Maxs - c;
            var w = Transform(m, c);
            var r = new Vector3(
                MathF.Abs(e.Y * m[1]) + MathF.Abs(e.X * m[0]) + MathF.Abs(e.Z * m[2]),
                MathF.Abs(e.Y * m[5]) + MathF.Abs(e.X * m[4]) + MathF.Abs(e.Z * m[6]),
                MathF.Abs(e.Y * m[9]) + MathF.Abs(e.X * m[8]) + MathF.Abs(e.Z * m[10]));
            return (w - r, r + w);
        }

        /// <summary>The g_Grids record (0x8c bytes): world to cell, cell to world, world min (w 0), world max (w 0), the counts.</summary>
        public byte[] Record()
        {
            var b = new byte[0x8c];
            var w2c = WorldToCell();
            var c2w = CellToWorld();
            var (lo, hi) = WorldBounds();
            for (var i = 0; i < 12; i++)
            {
                BitConverter.TryWriteBytes(b.AsSpan(i * 4), w2c[i]);
                BitConverter.TryWriteBytes(b.AsSpan(0x30 + i * 4), c2w[i]);
            }
            BitConverter.TryWriteBytes(b.AsSpan(0x60), lo.X);
            BitConverter.TryWriteBytes(b.AsSpan(0x64), lo.Y);
            BitConverter.TryWriteBytes(b.AsSpan(0x68), lo.Z);
            BitConverter.TryWriteBytes(b.AsSpan(0x70), hi.X);
            BitConverter.TryWriteBytes(b.AsSpan(0x74), hi.Y);
            BitConverter.TryWriteBytes(b.AsSpan(0x78), hi.Z);
            BitConverter.TryWriteBytes(b.AsSpan(0x80), X);
            BitConverter.TryWriteBytes(b.AsSpan(0x84), Y);
            BitConverter.TryWriteBytes(b.AsSpan(0x88), Z);
            return b;
        }

        /// <summary>1800204d0: the metrics' ray length (and the dilation's reach), max cell size * 32 * 0.03125 * 2.</summary>
        public float RayLength()
        {
            var c = CellSize;
            var m = MathF.Max(MathF.Max(MathF.Abs(c.X), MathF.Abs(c.Y)), MathF.Abs(c.Z));
            var a = m * 32f * 0.03125f;
            return a + a;
        }

        /// <summary>1800204d0: the dilation passes, (int)ceil(ray length / max cell size).</summary>
        public int DilationPasses()
        {
            var c = CellSize;
            var m = MathF.Max(MathF.Max(MathF.Abs(c.X), MathF.Abs(c.Y)), MathF.Abs(c.Z));
            return (int)MathF.Ceiling(RayLength() / m);
        }
    }

    /// <summary>One metrics sample (the shader's 16 bytes): min t, max t, the escaping fraction, flags.</summary>
    public readonly record struct Sample(float MinT, float MaxT, float Escape, uint Flags);

    /// <summary>The bricks, root first, and the cell descriptors.</summary>
    public sealed record Result(List<Brick> Bricks, uint[] Descriptors, float Coarsen);

    /// <summary>
    /// The occupied mask (a bit a cell, 32 to a word) of world-space triangles: mark_occupied's D_MODE 0
    /// test then its D_MODE 1 dilation. The OR makes the dispatch order immaterial.
    /// </summary>
    public static uint[] Occupancy(Grid grid, IEnumerable<(Vector3 A, Vector3 B, Vector3 C)> triangles)
    {
        var mask = new uint[(grid.Count + 31) >> 5];
        var w2c = grid.WorldToCell();
        var (lo, hi) = grid.WorldBounds();
        foreach (var (a, b, c) in triangles)
        {
            var mn = Vector3.Min(a, Vector3.Min(b, c));
            var mx = Vector3.Max(a, Vector3.Max(b, c));
            if (mn.X > hi.X || mn.Y > hi.Y || mn.Z > hi.Z || mx.X < lo.X || mx.Y < lo.Y || mx.Z < lo.Z)
                continue;
            var p0 = Transform(w2c, a);
            var p1 = Transform(w2c, b);
            var p2 = Transform(w2c, c);
            var cmin = Vector3.Min(p0, Vector3.Min(p1, p2));
            var cmax = Vector3.Max(p0, Vector3.Max(p1, p2));
            int x0 = Math.Clamp((int)MathF.Floor(cmin.X), 0, grid.X - 1), x1 = Math.Clamp((int)MathF.Floor(cmax.X), 0, grid.X - 1);
            int y0 = Math.Clamp((int)MathF.Floor(cmin.Y), 0, grid.Y - 1), y1 = Math.Clamp((int)MathF.Floor(cmax.Y), 0, grid.Y - 1);
            int z0 = Math.Clamp((int)MathF.Floor(cmin.Z), 0, grid.Z - 1), z1 = Math.Clamp((int)MathF.Floor(cmax.Z), 0, grid.Z - 1);
            var e0 = p1 - p0;
            var e1 = p2 - p1;
            var e2 = p0 - p2;
            var n = Vector3.Cross(e0, e1);
            for (var z = z0; z <= z1; z++)
                for (var y = y0; y <= y1; y++)
                    for (var x = x0; x <= x1; x++)
                    {
                        var centre = new Vector3(x, y, z) + new Vector3(0.5f);
                        if (Overlaps(p0 - centre, p1 - centre, p2 - centre, n, e0, e1, e2))
                        {
                            var i = (z * grid.Y + y) * grid.X + x;
                            mask[i >> 5] |= 1u << (i & 31);
                        }
                    }
        }
        for (var pass = grid.DilationPasses(); pass > 0; pass--)
            mask = Dilate(grid, mask);
        return mask;
    }

    // mark_occupied D_MODE 0's test: the triangle against the cell (half size 0.5001) on the three
    // box axes, the normal and the nine edge crosses.
    private static bool Overlaps(Vector3 v0, Vector3 v1, Vector3 v2, Vector3 n, Vector3 e0, Vector3 e1, Vector3 e2)
    {
        if (Separated(v0.X, v1.X, v2.X, 0.5001f) || Separated(v0.Y, v1.Y, v2.Y, 0.5001f) || Separated(v0.Z, v1.Z, v2.Z, 0.5001f))
            return false;
        if (Axis(n, v0, v1, v2))
            return false;
        foreach (var e in (ReadOnlySpan<Vector3>)[e0, e1, e2])
        {
            if (Axis(new Vector3(0f, -e.Z, e.Y), v0, v1, v2) || Axis(new Vector3(e.Z, 0f, -e.X), v0, v1, v2) || Axis(new Vector3(-e.Y, e.X, 0f), v0, v1, v2))
                return false;
        }
        return true;
    }

    private static bool Axis(Vector3 a, Vector3 v0, Vector3 v1, Vector3 v2)
    {
        var r = 0.5001f * MathF.Abs(a.X) + 0.5001f * MathF.Abs(a.Y) + 0.5001f * MathF.Abs(a.Z);
        return Separated(Vector3.Dot(a, v0), Vector3.Dot(a, v1), Vector3.Dot(a, v2), r);
    }

    private static bool Separated(float a, float b, float c, float r) => MathF.Min(a, MathF.Min(b, c)) > r || MathF.Max(a, MathF.Max(b, c)) < -r;

    // D_MODE 1: an unoccupied cell with an occupied neighbour (clamped to the grid) joins.
    private static uint[] Dilate(Grid grid, uint[] input)
    {
        var output = (uint[])input.Clone();
        bool Has(int i) => (input[i >> 5] >> (i & 31) & 1) != 0;
        for (var z = 0; z < grid.Z; z++)
            for (var y = 0; y < grid.Y; y++)
                for (var x = 0; x < grid.X; x++)
                {
                    var i = (z * grid.Y + y) * grid.X + x;
                    if (Has(i))
                        continue;
                    var hit = false;
                    for (var dz = -1; dz <= 1 && !hit; dz++)
                        for (var dy = -1; dy <= 1 && !hit; dy++)
                            for (var dx = -1; dx <= 1 && !hit; dx++)
                            {
                                var nx = Math.Min(Math.Max(x + dx, 0), grid.X - 1);
                                var ny = Math.Min(Math.Max(y + dy, 0), grid.Y - 1);
                                var nz = Math.Min(Math.Max(z + dz, 0), grid.Z - 1);
                                hit = Has((nz * grid.Y + ny) * grid.X + nx);
                            }
                    if (hit)
                        output[i >> 5] |= 1u << (i & 31);
                }
        return output;
    }

    /// <summary>
    /// 18001fa00: a cell's level from its 8 samples. All unoccupied (flag 2) or none usable: 7, no
    /// brick; all backface (flag 1): 0xff, solid; otherwise the smallest usable escaping fraction f,
    /// remapped by the coarsening c (threshold 0.4 - 0.4 clamp(c)), to round(max(0, c - 1) + 5 f)
    /// clamped to 0..5.
    /// </summary>
    public static byte[] Levels(Grid grid, Sample[] samples, float coarsen)
    {
        var levels = new byte[grid.Count];
        for (var i = 0; i < levels.Length; i++)
        {
            var solid = true;
            var usable = false;
            var f = 1f;
            for (var k = 0; k < 8; k++)
            {
                var s = samples[i * 8 + k];
                if ((s.Flags & 2) != 0)
                    solid = false;
                else if ((s.Flags & 1) == 0)
                {
                    solid = false;
                    usable = true;
                    if (s.Escape <= f)
                        f = s.Escape;
                }
            }
            if (solid)
            {
                levels[i] = 0xff;
                continue;
            }
            if (!usable)
            {
                levels[i] = 7;
                continue;
            }
            var c = coarsen <= 0f ? 0f : coarsen;
            if (1f <= c)
                c = 1f;
            var t = c * -0.4f + 0.4f;
            if (t != 1f)
            {
                f = (f - t) / (1f - t);
                f = f <= 0f ? 0f : f;
                f = 1f <= f ? 1f : f;
                f += 0f;
            }
            else
                f = f - 1f < 0f ? 0f : 1f;
            var extra = 0f <= coarsen - 1f ? coarsen - 1f : 0f;
            var level = (int)MathF.Round(extra + f * 5f, MidpointRounding.ToEven);
            levels[i] = (byte)(level < 1 ? 0 : level > 4 ? 5 : level);
        }
        return levels;
    }

    // 18001e4b0's axis orders, DAT_1802a0760.
    private static readonly int[][] Orders = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];

    /// <summary>18001e4b0: the allocation over the six axis orders with the fewest bricks (the first on a tie).</summary>
    public static List<Brick> Allocate(Grid grid, byte[] levels)
    {
        List<Brick>? best = null;
        foreach (var order in Orders)
        {
            var b = Allocate(grid, levels, order);
            if (best is null || b.Count < best.Count)
                best = b;
        }
        return best!;
    }

    /// <summary>
    /// 18001ea40: level by level 0 to 5, the cells in the order's loop nest (order[0] outermost); a
    /// cell at that level not yet covered gets a brick: at level 0 the cell, else the one of the 8
    /// placements with the cell in a corner covering the most uncovered cells under level 6 (the
    /// first on a tie); the brick's cells in the grid are covered.
    /// </summary>
    private static List<Brick> Allocate(Grid grid, byte[] levels, int[] order)
    {
        var bricks = new List<Brick>();
        var covered = new bool[grid.Count];
        int[] dims = [grid.X, grid.Y, grid.Z];
        var f = new int[3];
        for (var level = 0; level < 6; level++)
        {
            var s = 1 << level;
            for (var a = 0; a < dims[order[0]]; a++)
                for (var b = 0; b < dims[order[1]]; b++)
                    for (var c = 0; c < dims[order[2]]; c++)
                    {
                        f[order[0]] = a;
                        f[order[1]] = b;
                        f[order[2]] = c;
                        var i = grid.Index(f[0], f[1], f[2]);
                        if (levels[i] != level || covered[i])
                            continue;
                        if (level == 0)
                        {
                            bricks.Add(new Brick(f[0], f[1], f[2], 0));
                            covered[i] = true;
                            continue;
                        }
                        var most = -1;
                        (int X, int Y, int Z) at = default;
                        for (var corner = 0; corner < 8; corner++)
                        {
                            var ox = (corner & 1) != 0 ? f[0] - s + 1 : f[0];
                            var oy = (corner & 2) != 0 ? f[1] - s + 1 : f[1];
                            var oz = (corner & 4) != 0 ? f[2] - s + 1 : f[2];
                            var n = 0;
                            for (var z = Math.Max(oz, 0); z < Math.Min(oz + s, grid.Z); z++)
                                for (var y = Math.Max(oy, 0); y < Math.Min(oy + s, grid.Y); y++)
                                    for (var x = Math.Max(ox, 0); x < Math.Min(ox + s, grid.X); x++)
                                    {
                                        var j = grid.Index(x, y, z);
                                        if (levels[j] < 6 && !covered[j])
                                            n++;
                                    }
                            if (most < n)
                            {
                                most = n;
                                at = (ox, oy, oz);
                            }
                        }
                        bricks.Add(new Brick(at.X, at.Y, at.Z, level));
                        for (var z = Math.Max(at.Z, 0); z < Math.Min(at.Z + s, grid.Z); z++)
                            for (var y = Math.Max(at.Y, 0); y < Math.Min(at.Y + s, grid.Y); y++)
                                for (var x = Math.Max(at.X, 0); x < Math.Min(at.X + s, grid.X); x++)
                                    covered[grid.Index(x, y, z)] = true;
                    }
        }
        return bricks;
    }

    /// <summary>
    /// 18001fc60's budget in bricks: (int)(max MB * 1 MiB / 143748), capped at the atlas's 0x7c0, less
    /// one, at least 1. farfield_max_memory sets the MB; DAT_1802ecdc8 defaults to 271.99 (0x4387fdf8).
    /// </summary>
    public static int Budget(float maxMegabytes)
    {
        var n = (int)((double)maxMegabytes * 1024.0 * 1024.0 / 143748.0);
        if (0x7c0 < n)
            n = 0x7c0;
        n--;
        return n < 2 ? 1 : n;
    }

    public const float DefaultMaxMegabytes = 271.99207f;

    /// <summary>The whole of farfield_compute_bricks after the metrics.</summary>
    public static Result Compute(Grid grid, Sample[] samples, float maxMegabytes = DefaultMaxMegabytes, Action<string>? log = null)
    {
        // 18001fc60: over budget at no coarsening, bisect [0, 6] twelve times, then step by 0.05.
        var budget = Budget(maxMegabytes);
        var coarsen = 0f;
        var first = Allocate(grid, Levels(grid, samples, 0f)).Count;
        if (budget < first)
        {
            float hi = 6f, lo = 0f;
            for (var k = 0; k < 12; k++)
            {
                var mid = (hi + lo) * 0.5f;
                if (Allocate(grid, Levels(grid, samples, mid)).Count <= budget)
                    hi = mid;
                else
                    lo = mid;
            }
            coarsen = hi;
            log?.Invoke($"  farfield: {first} bricks ({first * 0x23184 * 9.5367431640625e-07:F1} MB) over budget, coarsening by {coarsen:F3}");
        }
        var levels = Levels(grid, samples, coarsen);
        var bricks = Allocate(grid, levels);
        while (budget < bricks.Count && coarsen < 6f)
        {
            coarsen += 0.05f;
            if (6f <= coarsen)
                coarsen = 6f;
            levels = Levels(grid, samples, coarsen);
            bricks = Allocate(grid, levels);
        }
        log?.Invoke($"  farfield allocation: {bricks.Count} bricks, coarsen={coarsen:F6}");

        // 180026b70: level, then z, y, x, ascending; the root (level 7 at 0) in front.
        bricks.Sort((p, q) => p.Level != q.Level ? ((uint)p.Level).CompareTo((uint)q.Level)
            : p.Z != q.Z ? p.Z.CompareTo(q.Z) : p.Y != q.Y ? p.Y.CompareTo(q.Y) : p.X.CompareTo(q.X));
        Validate(grid, bricks, levels, log);
        bricks.Insert(0, new Brick(0, 0, 0, 7));
        return new Result(bricks, Descriptors(grid, bricks), coarsen);
    }

    // 18001f210: a warning for a cell under level 6 no brick covers, or whose finest brick is coarser.
    private static void Validate(Grid grid, List<Brick> bricks, byte[] levels, Action<string>? log)
    {
        var finest = new byte[grid.Count];
        Array.Fill(finest, (byte)0xff);
        foreach (var b in bricks)
        {
            var s = 1 << b.Level;
            for (var z = Math.Max(b.Z, 0); z < Math.Min(b.Z + s, grid.Z); z++)
                for (var y = Math.Max(b.Y, 0); y < Math.Min(b.Y + s, grid.Y); y++)
                    for (var x = Math.Max(b.X, 0); x < Math.Min(b.X + s, grid.X); x++)
                    {
                        var i = grid.Index(x, y, z);
                        finest[i] = Math.Min(finest[i], (byte)b.Level);
                    }
        }
        int uncovered = 0, under = 0;
        for (var i = 0; i < levels.Length; i++)
            if (levels[i] < 6)
            {
                if (finest[i] == 0xff)
                    uncovered++;
                else if (finest[i] > levels[i])
                    under++;
            }
        if (uncovered != 0 || under != 0)
            log?.Invoke($"farfield allocation FAILED validation: {uncovered} uncovered, {under} under-served cells");
    }

    /// <summary>
    /// 18001f590: per cell, the finest brick over it (the first on a tie; the root skipped):
    /// index | level &lt;&lt; 12 | (x - bx) &lt;&lt; 15 | (y - by) &lt;&lt; 20 | (z - bz) &lt;&lt; 25, offsets 5 bits;
    /// a cell no brick covers is 0x7000 (the root, level 7).
    /// </summary>
    public static uint[] Descriptors(Grid grid, List<Brick> bricks)
    {
        var owner = new int[grid.Count];
        Array.Fill(owner, -1);
        for (var n = 0; n < bricks.Count; n++)
        {
            var b = bricks[n];
            if (b.Level == 7)
                continue;
            var s = 1 << b.Level;
            for (var z = Math.Max(b.Z, 0); z < Math.Min(b.Z + s, grid.Z); z++)
                for (var y = Math.Max(b.Y, 0); y < Math.Min(b.Y + s, grid.Y); y++)
                    for (var x = Math.Max(b.X, 0); x < Math.Min(b.X + s, grid.X); x++)
                    {
                        var i = grid.Index(x, y, z);
                        if (owner[i] < 0 || (uint)b.Level < (uint)bricks[owner[i]].Level)
                            owner[i] = n;
                    }
        }
        var d = new uint[grid.Count];
        Array.Fill(d, 0x7000u);
        for (var z = 0; z < grid.Z; z++)
            for (var y = 0; y < grid.Y; y++)
                for (var x = 0; x < grid.X; x++)
                {
                    var i = grid.Index(x, y, z);
                    if (owner[i] < 0)
                        continue;
                    var b = bricks[owner[i]];
                    d[i] = (uint)(y - b.Y & 0x1f) << 20 | (uint)(z - b.Z & 0x1f) << 25 | (uint)(x - b.X & 0x1f) << 15 | (uint)(b.Level & 7) << 12 | (uint)owner[i] & 0xfff;
                }
        return d;
    }

    /// <summary>
    /// farfield_&lt;n&gt;_bricks.kv3 (1800204d0): bricks [{level, origin (cells), min, max (box space;
    /// the root the box)}], cell_count, cells_x/y/z, cell_descriptors.
    /// </summary>
    public static string Kv3(Grid grid, Result result)
    {
        var root = KVObject.Collection();
        var list = KVObject.Array();
        var cx = (grid.Maxs.X - grid.Mins.X) / grid.X;
        var cy = (grid.Maxs.Y - grid.Mins.Y) / grid.Y;
        var cz = (grid.Maxs.Z - grid.Mins.Z) / grid.Z;
        foreach (var b in result.Bricks)
        {
            Vector3 lo, hi;
            if (b.Level == 7)
                (lo, hi) = (grid.Mins, grid.Maxs);
            else
            {
                lo = new Vector3((float)b.X * cx + grid.Mins.X, cy * (float)b.Y + grid.Mins.Y, cz * (float)b.Z + grid.Mins.Z);
                var s = (float)(1 << b.Level);
                hi = new Vector3(s * cx + lo.X, cy * s + lo.Y, cz * s + lo.Z);
            }
            var o = KVObject.Collection();
            o.Add("level", new KVObject(b.Level));
            o.Add("origin", Vector((float)b.X, (float)b.Y, (float)b.Z));
            o.Add("min", Vector(lo.X, lo.Y, lo.Z));
            o.Add("max", Vector(hi.X, hi.Y, hi.Z));
            list.Add(o);
        }
        root.Add("bricks", list);
        root.Add("cell_count", new KVObject(grid.Count));
        root.Add("cells_x", new KVObject(grid.X));
        root.Add("cells_y", new KVObject(grid.Y));
        root.Add("cells_z", new KVObject(grid.Z));
        var d = KVObject.Array();
        foreach (var v in result.Descriptors)
            d.Add(new KVObject((int)v));
        root.Add("cell_descriptors", d);
        return Kv3TextWriter.Write(root, new Guid("7412167c-06e9-4698-aff2-e63eb59037e7"));
    }

    private static KVObject Vector(float x, float y, float z)
    {
        var a = KVObject.Array();
        a.Add(new KVObject((double)x));
        a.Add(new KVObject((double)y));
        a.Add(new KVObject((double)z));
        return a;
    }

    /// <summary>A point through a 3x4 (rows): x m0 + y m1 + z m2 + m3 per row.</summary>
    public static Vector3 Transform(float[] m, Vector3 p) => new(
        p.X * m[0] + p.Y * m[1] + p.Z * m[2] + m[3],
        p.X * m[4] + p.Y * m[5] + p.Z * m[6] + m[7],
        p.X * m[8] + p.Y * m[9] + p.Z * m[10] + m[11]);

    /// <summary>1801f4490: the 3x4 inverse (identity when the determinant is under 1.17549435e-35).</summary>
    public static float[] Inverse(float[] p)
    {
        float m0 = p[0], m1 = p[1], m2 = p[2], m4 = p[4], m5 = p[5], m6 = p[6], m8 = p[8], m9 = p[9], m10 = p[10];
        var det = (m6 * m1 - m5 * m2) * m8 + (m9 * m2 - m10 * m1) * m4 + (m5 * m10 - m6 * m9) * m0;
        if (!(1.17549435e-35f <= MathF.Abs(det)))
            return [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0];
        var r = 1f / det;
        var o = new float[12];
        o[0] = (m5 * m10 - m9 * m6) * r;
        o[1] = (m9 * m2 - m1 * m10) * r;
        o[2] = (m1 * m6 - m5 * m2) * r;
        o[4] = (m6 * m8 - m10 * m4) * r;
        o[5] = (m10 * m0 - m2 * m8) * r;
        o[6] = (m2 * m4 - m6 * m0) * r;
        o[8] = (m9 * m4 - m5 * m8) * r;
        o[9] = (m1 * m8 - m9 * m0) * r;
        o[10] = (m5 * m0 - m1 * m4) * r;
        float tx = -p[3], ty = -p[7], tz = -p[11];
        o[3] = tz * o[2] + ty * o[1] + tx * o[0];
        o[7] = tz * o[6] + ty * o[5] + tx * o[4];
        o[11] = tz * o[10] + ty * o[9] + tx * o[8];
        return o;
    }

    /// <summary>1801f3df0: a after b (3x4 rows).</summary>
    public static float[] Concat(float[] a, float[] b)
    {
        var o = new float[12];
        for (var i = 0; i < 3; i++)
        {
            float x = a[i * 4], y = a[i * 4 + 1], z = a[i * 4 + 2];
            o[i * 4] = z * b[8] + y * b[4] + x * b[0] + 0f;
            o[i * 4 + 1] = z * b[9] + y * b[5] + x * b[1] + 0f;
            o[i * 4 + 2] = z * b[10] + y * b[6] + x * b[2] + 0f;
            o[i * 4 + 3] = z * b[11] + y * b[7] + x * b[3] + a[i * 4 + 3];
        }
        return o;
    }
}
