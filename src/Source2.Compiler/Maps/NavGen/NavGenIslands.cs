namespace Source2.Compiler.Maps.NavGen;

/// <summary>
/// What IslandRemoval reads from the RC nav input (CNavGenInput at ctx+8) and the ladder connectivity
/// (ctx+0x88/0x90): the point_nav_walkable world origins (+0x78), the nav links' two endpoints (+0xc0,
/// 0x18 bytes each) and, per ladder, the seven polygon indices its connectivity names (0x1c bytes,
/// +0 to +0x18; -1 for none).
/// WALKABLESEED markups (+0x90) are not ported.
/// </summary>
public sealed record NavIslandInput(
    IReadOnlyList<(float X, float Y, float Z)> Seeds,
    IReadOnlyList<((float X, float Y, float Z) A, (float X, float Y, float Z) B)> Links,
    IReadOnlyList<int[]> Ladders)
{
    public static NavIslandInput FromSeeds(IReadOnlyList<(float X, float Y, float Z)> seeds) => new(seeds, [], []);
}

public sealed partial class NavGenPolys
{
    /// <summary>180129420: half the box IslandRemoval puts around each walkable seed.</summary>
    public const float SeedBoxHalf = 40f;

    // The ladder connectivity's ints in the order IslandRemoval floods them (+0x10, +0x14, +0x18, +0xc, +0, +4, +8).
    private static readonly int[] LadderFloodOrder = [4, 5, 6, 3, 0, 1, 2];

    /// <summary>IslandRemoval's grid cell size (180046720's last argument).</summary>
    public const double IslandGridCell = 32.0;

    /// <summary>
    /// IslandRemoval (180070780): every polygon marked removed, then flooded back from what the input
    /// seeds: polygons near both ends of a nav link (paired, 18006e460), polygons touching a 40-unit box
    /// round each walkable seed (180078490), and the polygons each ladder names. A flood (18006e5f0) runs
    /// breadth first across connections and link pairs; every polygon it reaches past its start is
    /// listed. With allHulls (the stage that runs: nav_gen_island_removal_all_hulls 1, the single-hull
    /// one off), each listed polygon then floods from any removed polygon whose XY outline meets its own
    /// (18006d260, margin 0) within 4 units (1800a3060), both of kind 0, until no new polygon is listed.
    /// The polygons left removed are dropped as 18006cb10 does, but connections keep +0x61 and one to
    /// polygon -1 goes too; ladder indices follow. Skipped (nothing removed) when the settings' +0x58
    /// byte is set. The seed and link boxes are tested with vphysics2's GJK (CVPhysics2Interface vfn
    /// 0x190, 180073970: touching when the distance is at most 0.125), stood in for here by navsystem's
    /// (<see cref="NavGjk"/>).
    /// </summary>
    public void IslandRemoval(NavIslandInput input, bool allHulls, bool skip)
    {
        if (skip)
            return;
        foreach (var p in Polys)
            p.Removed = true;
        var (lo, hi) = ComputeBounds();
        var grid = new IslandGrid(lo.X, lo.Y, hi.X, hi.Y, IslandGridCell);
        for (var i = 0; i < Polys.Count; i++)
        {
            var b = Polys[i].Bounds!;
            grid.Insert(b[0], b[2], b[1], b[3], i);
        }
        var reached = new List<int>();
        var pairs = new List<(int A, int B)>();
        foreach (var (a, b) in input.Links)
            if (FindNear(grid, a, out var pa) && FindNear(grid, b, out var pb))
                pairs.Add((pa, pb));
        foreach (var seed in input.Seeds)
        {
            (float X, float Y, float Z) min = (seed.X - SeedBoxHalf, seed.Y - SeedBoxHalf, seed.Z - SeedBoxHalf);
            (float X, float Y, float Z) max = (seed.X + SeedBoxHalf, seed.Y + SeedBoxHalf, seed.Z + SeedBoxHalf);
            foreach (var i in grid.Query(min.X, min.Y, max.X, max.Y))
                if (Polys[i].Removed && BoxTouches(min, max, Corners(Polys[i])))
                    Flood(i, pairs, reached);
        }
        foreach (var ladder in input.Ladders)
            foreach (var k in LadderFloodOrder)
                if (ladder[k] != -1 && Polys[ladder[k]].Removed)
                    Flood(ladder[k], pairs, reached);
        if (allHulls)
        {
            while (reached.Count != 0)
            {
                var sources = reached.ToArray();
                reached.Clear();
                foreach (var s in sources)
                {
                    var source = Polys[s];
                    var b = source.Bounds!;
                    foreach (var i in grid.Query(b[0], b[2], b[1], b[3]))
                    {
                        var other = Polys[i];
                        if (!other.Removed || source.Kind != 0 || other.Kind != 0 || !OverlapXY(source, other, 0.0))
                            continue;
                        if (NavGjk.Distance(Corners(source), Corners(other)) <= 4f)
                            Flood(i, pairs, reached);
                    }
                }
            }
        }
        var remap = new int[Polys.Count];
        var kept = new List<NavPoly>();
        for (var i = 0; i < Polys.Count; i++)
        {
            if (Polys[i].Removed)
            {
                remap[i] = -1;
                Polys[i].Links = null;
                continue;
            }
            remap[i] = kept.Count;
            kept.Add(Polys[i]);
        }
        Polys = kept;
        foreach (var p in Polys)
        {
            if (p.Links is not { } links)
                continue;
            for (var i = 0; i < links.Count; i++)
            {
                var c = links.Connections[i];
                var target = c.PolyB;
                c.PolyA = remap[c.PolyA];
                c.PolyB = target == -1 ? -1 : remap[target];
                if (c.PolyB == -1)
                {
                    links.RemoveAt(i);
                    i--;
                }
            }
        }
        foreach (var ladder in input.Ladders)
            for (var k = 0; k < ladder.Length; k++)
                ladder[k] = ladder[k] == -1 ? -1 : remap[ladder[k]];
    }

    // 18004fa40: the polygon's corners as floats.
    private (float X, float Y, float Z)[] Corners(NavPoly p)
    {
        var corners = new (float X, float Y, float Z)[p.Indices.Count];
        for (var k = 0; k < corners.Length; k++)
        {
            var c = Pool[p.Indices[k]];
            corners[k] = ((float)c.X, (float)c.Y, (float)c.Z);
        }
        return corners;
    }

    // 180078490: the box's eight corners (x fastest, then y, then z) against the polygon.
    private static bool BoxTouches((float X, float Y, float Z) min, (float X, float Y, float Z) max, (float X, float Y, float Z)[] polygon)
    {
        (float X, float Y, float Z)[] box =
        [
            (min.X - 0f, min.Y - 0f, min.Z - 0f), (max.X + 0f, min.Y - 0f, min.Z - 0f),
            (min.X - 0f, max.Y + 0f, min.Z - 0f), (max.X + 0f, max.Y + 0f, min.Z - 0f),
            (min.X - 0f, min.Y - 0f, max.Z + 0f), (max.X + 0f, min.Y - 0f, max.Z + 0f),
            (min.X - 0f, max.Y + 0f, max.Z + 0f), (max.X + 0f, max.Y + 0f, max.Z + 0f),
        ];
        var d = NavGjk.Distance(box, polygon);
        if (d <= 0.125f)
            d = 0f;
        return d <= 0f;
    }

    // 18006e460: the first polygon in the grid's order that a 16-unit box round the point touches.
    private bool FindNear(IslandGrid grid, (float X, float Y, float Z) point, out int found)
    {
        (float X, float Y, float Z) min = (point.X - 16f, point.Y - 16f, point.Z + -16f);
        (float X, float Y, float Z) max = (point.X + 16f, point.Y + 16f, point.Z + 16f);
        foreach (var i in grid.Query(min.X, min.Y, max.X, max.Y))
            if (BoxTouches(min, max, Corners(Polys[i])))
            {
                found = i;
                return true;
            }
        found = -1;
        return false;
    }

    // 18006e5f0: breadth first from start (cleared), across connections then link pairs to removed
    // polygons; each one reached is cleared and listed (the std::function at 1800ff790 appends it).
    private void Flood(int start, List<(int A, int B)> pairs, List<int> reached)
    {
        var queue = new List<int> { start };
        Polys[start].Removed = false;
        for (var head = 0; head < queue.Count; head++)
        {
            var at = queue[head];
            if (Polys[at].Links is { } links)
                for (var k = 0; k < links.Count; k++)
                {
                    var next = links.Connections[k].PolyB;
                    if (!Polys[next].Removed)
                        continue;
                    queue.Add(next);
                    Polys[next].Removed = false;
                    reached.Add(next);
                }
            foreach (var (a, b) in pairs)
            {
                int next;
                if (a == at)
                    next = b;
                else if (b == at)
                    next = a;
                else
                    continue;
                if (!Polys[next].Removed)
                    continue;
                queue.Add(next);
                Polys[next].Removed = false;
                reached.Add(next);
            }
        }
    }
}

/// <summary>
/// The 2D grid IslandRemoval queries (180046720 builds it over the polygons' bounds, 180046c80 adds
/// one, 1800475e0/180046a40/180046a80 walk a box): square cells from the bounds' XY minimum, a cell
/// index the truncated offset clamped to the grid; a query visits rows, then columns, then each cell's
/// entries in insertion order, every entry once.
/// </summary>
internal sealed class IslandGrid
{
    private readonly double _cell, _ox, _oy;
    private readonly int _nx, _ny;
    private readonly List<int>[] _cells;
    private readonly List<int> _items = [];

    public IslandGrid(double minX, double minY, double maxX, double maxY, double cell)
    {
        if (maxX - minX <= 0.0 || maxY - minY <= 0.0)
        {
            _cells = [];
            return;
        }
        _cell = cell;
        _ox = minX;
        _oy = minY;
        _ny = (int)((maxY - minY) / cell) + 1;
        _nx = (int)((maxX - minX) / cell) + 1;
        _cells = new List<int>[_nx * _ny];
        for (var i = 0; i < _cells.Length; i++)
            _cells[i] = [];
    }

    private (int X0, int Y0, int X1, int Y1) Range(double minX, double minY, double maxX, double maxY)
    {
        static int Clamp(int v, int n) => Math.Min(v < 1 ? 0 : v, n - 1);
        return (Clamp((int)((minX - _ox) / _cell), _nx), Clamp((int)((minY - _oy) / _cell), _ny),
                Clamp((int)((maxX - _ox) / _cell), _nx), Clamp((int)((maxY - _oy) / _cell), _ny));
    }

    public void Insert(double minX, double minY, double maxX, double maxY, int item)
    {
        if (_cells.Length == 0)
            return;
        var (x0, y0, x1, y1) = Range(minX, minY, maxX, maxY);
        var at = _items.Count;
        _items.Add(item);
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
                _cells[y * _nx + x].Add(at);
    }

    public IEnumerable<int> Query(double minX, double minY, double maxX, double maxY)
    {
        if (_cells.Length == 0)
            yield break;
        var (x0, y0, x1, y1) = Range(minX, minY, maxX, maxY);
        var seen = new bool[_items.Count];
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
                foreach (var at in _cells[y * _nx + x])
                {
                    if (seen[at])
                        continue;
                    seen[at] = true;
                    yield return _items[at];
                }
    }
}
