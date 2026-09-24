using System.Buffers.Binary;
using System.Numerics;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration: faces with subdivision levels, gridded at their highest level
/// by candidate constructions, each cell split toward the face centre, every
/// triangle's tracer record looked up in the .rte of the same compile.
/// <c>SUBDIV=&lt;addon&gt;;&lt;map&gt;;&lt;rte&gt;</c>.
/// </summary>
public class SubdivisionReplay(ITestOutputHelper output)
{
    private delegate Vector3[,] Grid(Vector3[] corners, int cells);

    [Fact]
    public void TheSubdividedFaces()
    {
        if (Environment.GetEnvironmentVariable("SUBDIV") is not { Length: > 0 } spec)
            return;
        var parts = spec.Split(';');
        var cs2 = Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive";
        var doc = DmxBinary.ReadFile(Path.Combine(cs2, "content", "csgo_addons", parts[0], "maps", parts[1] + ".vmap"));
        var rte = RayTraceEnvironment.ReadFile(parts[2]);
        var file = new HashSet<string>();
        for (var i = 0; i < rte.TriangleCount; i++)
            file.Add(Key(rte.FileRecord(i)));

        Func<Vector3[], Vector3>[] centres =
        [
            c => (((c[0] + c[1]) + c[2]) + c[3]) * 0.25f,
            c => (((c[0] + c[1]) + c[2]) + c[3]) / 4f,
            c => ((c[0] + c[1]) + (c[2] + c[3])) * 0.25f,
            c => (((c[0] + c[1]) * 0.5f) + ((c[2] + c[3]) * 0.5f)) * 0.5f,
            c => (c[0] + c[2]) * 0.5f,
            c => (((c[0] * 0.25f) + (c[1] * 0.25f)) + (c[2] * 0.25f)) + (c[3] * 0.25f),
        ];
        Func<Vector3, Vector3, Vector3>[] mids = [(a, b) => (a + b) * 0.5f, (a, b) => a + ((b - a) * 0.5f)];
        Func<Vector3, Vector3, float, Vector3>[] lerps = [(a, b, t) => a + ((b - a) * t), (a, b, t) => (a * (1 - t)) + (b * t)];
        var extra = new List<(string Name, Grid Grid)>();
        for (var ci = 0; ci < centres.Length; ci++)
            for (var mi = 0; mi < mids.Length; mi++)
                for (var li = 0; li < lerps.Length; li++)
                    for (var order = 0; order < 2; order++)
                    {
                        var (cf, mf, lf, o) = (centres[ci], mids[mi], lerps[li], order);
                        extra.Add(($"q c{ci} m{mi} l{li} o{o}", (c, n) => QuadrantsWith(c, n, cf, mf, lf, o == 1)));
                    }
        var variants = Environment.GetEnvironmentVariable("SUBDIV_QUAD") is not null ? extra.ToArray() : new (string Name, Grid Grid)[]
        {
            ("face bilinear a+(b-a)t", (c, n) => Bilinear(c, n, (a, b, t) => a + ((b - a) * t))),
            ("face bilinear a(1-t)+bt", (c, n) => Bilinear(c, n, (a, b, t) => (a * (1 - t)) + (b * t))),
            ("quadrants, centre *0.25", (c, n) => Quadrants(c, n, 0.25f)),
            ("recursive midpoints", (c, n) => Recursive(c, n)),
            ("weights ((a+b)+c)+d", (c, n) => Weights(c, n, (a, b, cc, d) => ((a + b) + cc) + d)),
            ("weights (a+b)+(c+d)", (c, n) => Weights(c, n, (a, b, cc, d) => (a + b) + (cc + d))),
            ("weights ((a+b)+d)+c", (c, n) => Weights(c, n, (a, b, cc, d) => ((a + b) + d) + cc)),
            ("weights (a+d)+(b+c)", (c, n) => Weights(c, n, (a, b, cc, d) => (a + d) + (b + cc))),
            ("quadrant weights ((a+b)+c)+d", (c, n) => QuadrantWeights(c, n, (a, b, cc, d) => ((a + b) + cc) + d)),
            ("quadrant weights (a+b)+(c+d)", (c, n) => QuadrantWeights(c, n, (a, b, cc, d) => (a + b) + (cc + d))),
        };
        var faces = new List<(Vector3[] Corners, int Level, bool Displaced, int Node, Vector3[] Local, float[] World)>();
        foreach (var mesh in MapMeshes.Read(doc))
        {
            var data = mesh.Element!.Get<DmxBinary.Element>("meshData")!;
            var sub = data.Get<DmxBinary.Element>("subdivisionData");
            var levels = (sub?.Get<object?[]>("subdivisionLevels") ?? []).Select(x => x is int i ? i : 0).ToArray();
            if (levels.All(l => l == 0))
                continue;
            var displaced = (sub!.GetElements("streams").FirstOrDefault(s => s.Name.StartsWith("displacement", StringComparison.Ordinal))?
                             .Get<object?[]>("data") ?? []).Any(d => d is Vector3 v && v != Vector3.Zero);
            var next = (data.Get<object?[]>("edgeNextIndices") ?? []).Select(x => (int)x!).ToArray();
            var first = (data.Get<object?[]>("faceEdgeIndices") ?? []).Select(x => (int)x!).ToArray();
            var to = (data.Get<object?[]>("edgeVertexIndices") ?? []).Select(x => (int)x!).ToArray();
            var positions = data.Get<DmxBinary.Element>("vertexData")!.GetElements("streams").First(st => st.Name.StartsWith("position", StringComparison.Ordinal)).Get<object?[]>("data")!;
            for (var f = 0; f < first.Length; f++)
            {
                var level = 0;
                var e = first[f];
                do
                {
                    level = Math.Max(level, levels[e]);
                    e = next[e];
                }
                while (e != first[f]);
                var local = new List<Vector3>();
                e = first[f];
                do
                {
                    local.Add((Vector3)positions[to[e]]!);
                    e = next[e];
                }
                while (e != first[f]);
                if (level > 0 && mesh.Faces[f].Corners.Length == 4)
                    faces.Add((mesh.Faces[f].Corners, level, displaced, mesh.NodeId, [.. local], mesh.World));
            }
        }
        output.WriteLine($"{faces.Count} subdivided quads, {faces.Count(f => !f.Displaced)} in undisplaced meshes");

        var c9 = new float[9];
        var r13 = new float[13];
        var all = variants.SelectMany(v => new[] { (v.Name, v.Grid, false), (v.Name + " local", v.Grid, true) })
                          .Where(v => Environment.GetEnvironmentVariable("SUBDIV_QUAD") is null || v.Item3).ToArray();
        foreach (var (name, grid, inLocal) in all)
        {
            int found = 0, total = 0;
            var rotations = new int[3];
            var perFace = new List<string>();
            foreach (var (corners, level, displaced, node, localCorners, world) in faces.Where(f => !f.Displaced))
            {
                var before = found;
                var n = 1 << level;
                var g = grid(inLocal ? localCorners : corners, n);
                if (inLocal)
                    for (var a = 0; a <= n; a++)
                        for (var b = 0; b <= n; b++)
                            g[a, b] = Place(world, g[a, b]);
                var h = n / 2;
                for (var i = 0; i < n; i++)
                {
                    for (var j = 0; j < n; j++)
                    {
                        // Cell (i, j) with corners p00 p10 p11 p01; the diagonal runs toward the face centre.
                        Vector3 p00 = g[i, j], p10 = g[i + 1, j], p11 = g[i + 1, j + 1], p01 = g[i, j + 1];
                        var towardCentre = (i < h) == (j < h);
                        if (Environment.GetEnvironmentVariable("SUBDIV_FLIP") is not null)
                            towardCentre = !towardCentre;
                        Vector3[][] tris = towardCentre ? [[p00, p10, p11], [p00, p11, p01]] : [[p00, p10, p01], [p10, p11, p01]];
                        foreach (var t in tris)
                        {
                            total++;
                            for (var rot = 0; rot < 3; rot++)
                            {
                                for (var k = 0; k < 3; k++)
                                {
                                    var q = t[(k + rot) % 3];
                                    c9[k * 3] = q.X; c9[(k * 3) + 1] = q.Y; c9[(k * 3) + 2] = q.Z;
                                }
                                if (RayTraceEnvironment.RecordFromCorners(c9, r13) && file.Contains(Key(r13)))
                                {
                                    found++;
                                    rotations[rot]++;
                                    break;
                                }
                            }
                        }
                    }
                }
                var cellsPerFace = 2 * (1 << level) * (1 << level);
                if (found - before != cellsPerFace)
                    perFace.Add($"mesh {node} level {level}: {found - before}/{cellsPerFace} corners {string.Join(" ", localCorners)}");
            }
            output.WriteLine($"{name,-36} {found}/{total}  rotations {string.Join(",", rotations)}");
            if (Environment.GetEnvironmentVariable("SUBDIV_PERFACE") == name)
                foreach (var line in perFace)
                    output.WriteLine("    " + line);
        }
    }

    /// <summary>
    /// Displaced faces: each half-edge with level L owns a (2^(L-1)+1)^2 patch
    /// of samples, stored in half-edge order. Scores which face corner a patch
    /// belongs to and how it is laid out, by adding the displacement to the
    /// local quadrant grid before placing it.
    /// </summary>
    [Fact]
    public void DisplacedFaces()
    {
        if (Environment.GetEnvironmentVariable("SUBDIV") is not { Length: > 0 } spec
            || Environment.GetEnvironmentVariable("SUBDIV_DISP") is null)
            return;
        var parts = spec.Split(';');
        var cs2 = Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive";
        var doc = DmxBinary.ReadFile(Path.Combine(cs2, "content", "csgo_addons", parts[0], "maps", parts[1] + ".vmap"));
        var rte = RayTraceEnvironment.ReadFile(parts[2]);
        var file = new HashSet<Int128>();
        for (var i = 0; i < rte.TriangleCount; i++)
            file.Add(Fast(rte.FileRecord(i)));
        var c9 = new float[9];
        var r13 = new float[13];
        foreach (var owner in new[] { "to", "from" })
        foreach (var swap in new[] { false, true })
        {
            int found = 0, total = 0, skipped = 0;
            foreach (var mesh in MapMeshes.Read(doc))
            {
                var data = mesh.Element!.Get<DmxBinary.Element>("meshData")!;
                var sub = data.Get<DmxBinary.Element>("subdivisionData");
                var levels = (sub?.Get<object?[]>("subdivisionLevels") ?? []).Select(x => x is int i ? i : 0).ToArray();
                if (levels.All(l => l == 0))
                    continue;
                var disp = (sub!.GetElements("streams").First(st => st.Name.StartsWith("displacement", StringComparison.Ordinal)).Get<object?[]>("data") ?? [])
                           .Select(d => (Vector3)d!).ToArray();
                if (disp.All(d => d == Vector3.Zero))
                    continue;
                var offsets = new int[levels.Length];
                for (int e = 0, at = 0; e < levels.Length; e++)
                {
                    offsets[e] = at;
                    if (levels[e] > 0)
                        at += ((1 << (levels[e] - 1)) + 1) * ((1 << (levels[e] - 1)) + 1);
                }
                var next = (data.Get<object?[]>("edgeNextIndices") ?? []).Select(x => (int)x!).ToArray();
                var first = (data.Get<object?[]>("faceEdgeIndices") ?? []).Select(x => (int)x!).ToArray();
                var to = (data.Get<object?[]>("edgeVertexIndices") ?? []).Select(x => (int)x!).ToArray();
                var positions = data.Get<DmxBinary.Element>("vertexData")!.GetElements("streams").First(st => st.Name.StartsWith("position", StringComparison.Ordinal)).Get<object?[]>("data")!;
                for (var f = 0; f < first.Length; f++)
                {
                    var loop = new List<int>();
                    var e = first[f];
                    do { loop.Add(e); e = next[e]; } while (e != first[f]);
                    var level = loop.Max(x => levels[x]);
                    if (level == 0)
                        continue;
                    if (loop.Count != 4) { skipped++; continue; }
                    var local = loop.Select(x => (Vector3)positions[to[x]]!).ToArray();
                    var n = 1 << level;
                    var h = n / 2;
                    var g = QuadrantsWith(local, n, q => (((q[0] + q[1]) * 0.5f) + ((q[2] + q[3]) * 0.5f)) * 0.5f, (a, b) => (a + b) * 0.5f, (a, b, t) => a + ((b - a) * t), false);
                    // Quadrant k sits at corner k; its local axes run toward corner k+1 (a) and corner k-1 (b).
                    for (var k = 0; k < 4; k++)
                    {
                        var he = owner == "to" ? loop[k] : loop[(k + 1) % 4];
                        if (levels[he] == 0)
                            continue;
                        var size = (1 << (levels[he] - 1)) + 1;
                        if (size != h + 1) { skipped++; continue; }
                        for (var a = 0; a <= h; a++)
                        {
                            for (var b = 0; b <= h; b++)
                            {
                                var sample = disp[offsets[he] + (swap ? (b * size) + a : (a * size) + b)];
                                (int, int) cell = k switch
                                {
                                    0 => (a, b),
                                    1 => (n - b, a),
                                    2 => (n - a, n - b),
                                    _ => (b, n - a),
                                };
                                g[cell.Item1, cell.Item2] += sample;
                            }
                        }
                    }
                    for (var i = 0; i < n; i++)
                    {
                        for (var j = 0; j < n; j++)
                        {
                            var towardCentre = (i < h) == (j < h);
                            Vector3 p00 = Place(mesh.World, g[i, j]), p10 = Place(mesh.World, g[i + 1, j]), p11 = Place(mesh.World, g[i + 1, j + 1]), p01 = Place(mesh.World, g[i, j + 1]);
                            Vector3[][] tris = towardCentre ? [[p00, p10, p11], [p00, p11, p01]] : [[p00, p10, p01], [p10, p11, p01]];
                            foreach (var t in tris)
                            {
                                total++;
                                for (var rot = 0; rot < 3; rot++)
                                {
                                    for (var kk = 0; kk < 3; kk++)
                                    {
                                        var q = t[(kk + rot) % 3];
                                        c9[kk * 3] = q.X; c9[(kk * 3) + 1] = q.Y; c9[(kk * 3) + 2] = q.Z;
                                    }
                                    if (RayTraceEnvironment.RecordFromCorners(c9, r13) && file.Contains(Fast((ReadOnlySpan<float>)r13)))
                                    {
                                        found++;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            output.WriteLine($"patch owner {owner}, swap {swap}: {found}/{total}, skipped {skipped}");
        }
    }

    /// <summary>
    /// For axis-aligned rectangular faces: per grid line, the ulp offsets from
    /// the face-bilinear value that the compile's triangles agree with.
    /// </summary>
    [Fact]
    public void GridLineOffsets()
    {
        if (Environment.GetEnvironmentVariable("SUBDIV") is not { Length: > 0 } spec
            || Environment.GetEnvironmentVariable("SUBDIV_FACE") is not { } which)
            return;
        var parts = spec.Split(';');
        var cs2 = Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive";
        var doc = DmxBinary.ReadFile(Path.Combine(cs2, "content", "csgo_addons", parts[0], "maps", parts[1] + ".vmap"));
        var rte = RayTraceEnvironment.ReadFile(parts[2]);
        var file = new HashSet<string>();
        for (var i = 0; i < rte.TriangleCount; i++)
            file.Add(Key(rte.FileRecord(i)));
        var w = which.Split(',');
        var mesh = MapMeshes.Read(doc).First(m => m.NodeId.ToString() == w[0]);
        var c = mesh.Faces[int.Parse(w[1])].Corners;
        var n = int.Parse(w[2]);
        output.WriteLine($"corners {string.Join(" ", c.Select(v => $"({v.X:R},{v.Y:R},{v.Z:R})"))}");
        var g = Bilinear(c, n, (a, b, t) => a + ((b - a) * t));
        var h = n / 2;
        var c9 = new float[9];
        var r13 = new float[13];
        static float Ulp(float v, int k) => BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(v) + (v >= 0 ? k : -k));
        // offsets[axis][line] -> set of ulp offsets consistent with every cell touching the line
        var xs = new HashSet<int>?[n + 1];
        var ys = new HashSet<int>?[n + 1];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                var towardCentre = (i < h) == (j < h);
                var ok = new List<(int, int, int, int)>();
                for (var a = -4; a <= 4; a++)
                for (var b = -4; b <= 4; b++)
                for (var d = -4; d <= 4; d++)
                for (var e = -4; e <= 4; e++)
                {
                    float x0 = Ulp(g[i, j].X, a), x1 = Ulp(g[i + 1, j].X, b), y0 = Ulp(g[i, j].Y, d), y1 = Ulp(g[i, j + 1].Y, e);
                    Vector3 p00 = new(x0, y0, c[0].Z), p10 = new(x1, y0, c[0].Z), p11 = new(x1, y1, c[0].Z), p01 = new(x0, y1, c[0].Z);
                    Vector3[][] tris = towardCentre ? [[p00, p10, p11], [p00, p11, p01]] : [[p00, p10, p01], [p10, p11, p01]];
                    var all = true;
                    foreach (var t in tris)
                    {
                        var any = false;
                        for (var rot = 0; rot < 3 && !any; rot++)
                        {
                            for (var k = 0; k < 3; k++)
                            {
                                var q = t[(k + rot) % 3];
                                c9[k * 3] = q.X; c9[(k * 3) + 1] = q.Y; c9[(k * 3) + 2] = q.Z;
                            }
                            any = RayTraceEnvironment.RecordFromCorners(c9, r13) && file.Contains(Key(r13));
                        }
                        all &= any;
                    }
                    if (all)
                        ok.Add((a, b, d, e));
                }
                void Keep(ref HashSet<int>? set, IEnumerable<int> values)
                {
                    var v = values.ToHashSet();
                    if (set is null) set = v; else set.IntersectWith(v);
                }
                Keep(ref xs[i], ok.Select(o => o.Item1));
                Keep(ref xs[i + 1], ok.Select(o => o.Item2));
                Keep(ref ys[j], ok.Select(o => o.Item3));
                Keep(ref ys[j + 1], ok.Select(o => o.Item4));
            }
        }
        for (var i = 0; i <= n; i++)
            output.WriteLine($"x line {i}: base {g[i, 0].X:R} offsets {string.Join(",", xs[i]!.Order())}");
        for (var j = 0; j <= n; j++)
            output.WriteLine($"y line {j}: base {g[0, j].Y:R} offsets {string.Join(",", ys[j]!.Order())}");
    }

    /// <summary>
    /// Per grid vertex of one face (<c>SUBDIV_VERTS=node,face,cells</c>), the x
    /// and y ulp offsets from the face-bilinear value that every incident
    /// triangle agrees with.
    /// </summary>
    [Fact]
    public void VertexOffsets()
    {
        if (Environment.GetEnvironmentVariable("SUBDIV") is not { Length: > 0 } spec
            || Environment.GetEnvironmentVariable("SUBDIV_VERTS") is not { } which)
            return;
        var parts = spec.Split(';');
        var cs2 = Environment.GetEnvironmentVariable("CS2_DIR") ?? @"D:\Steam\steamapps\common\Counter-Strike Global Offensive";
        var doc = DmxBinary.ReadFile(Path.Combine(cs2, "content", "csgo_addons", parts[0], "maps", parts[1] + ".vmap"));
        var rte = RayTraceEnvironment.ReadFile(parts[2]);
        var file = new HashSet<Int128>();
        Span<float> rec = stackalloc float[13];
        for (var i = 0; i < rte.TriangleCount; i++)
            file.Add(Fast(rte.FileRecord(i)));
        var w = which.Split(',');
        var mesh = MapMeshes.Read(doc).First(m => m.NodeId.ToString() == w[0]);
        var c = mesh.Faces[int.Parse(w[1])].Corners;
        var n = int.Parse(w[2]);
        var g = Bilinear(c, n, (a, b, t) => a + ((b - a) * t));
        if (Environment.GetEnvironmentVariable("SUBDIV_LOCALQ") is not null)
        {
            var data = mesh.Element!.Get<DmxBinary.Element>("meshData")!;
            var next = (data.Get<object?[]>("edgeNextIndices") ?? []).Select(x => (int)x!).ToArray();
            var first = (data.Get<object?[]>("faceEdgeIndices") ?? []).Select(x => (int)x!).ToArray();
            var to = (data.Get<object?[]>("edgeVertexIndices") ?? []).Select(x => (int)x!).ToArray();
            var positions = data.Get<DmxBinary.Element>("vertexData")!.GetElements("streams").First(st => st.Name.StartsWith("position", StringComparison.Ordinal)).Get<object?[]>("data")!;
            var local = new List<Vector3>();
            var e0 = first[int.Parse(w[1])];
            var e = e0;
            do { local.Add((Vector3)positions[to[e]]!); e = next[e]; } while (e != e0);
            g = QuadrantsWith([.. local], n, q => (((q[0] + q[1]) * 0.5f) + ((q[2] + q[3]) * 0.5f)) * 0.5f, (a, b) => (a + b) * 0.5f, (a, b, t) => a + ((b - a) * t), false);
            for (var a = 0; a <= n; a++)
                for (var b = 0; b <= n; b++)
                    g[a, b] = Place(mesh.World, g[a, b]);
        }
        var h = n / 2;
        static float Ulp(float v, int k) => BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(v) + (v >= 0 ? k : -k));
        var cand = new Dictionary<(int, int), HashSet<(int, int)>>();
        var c9 = new float[9];
        var r13 = new float[13];
        const int R = 2;
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                var towardCentre = (i < h) == (j < h);
                (int, int)[][] tris = towardCentre
                    ? [[(i, j), (i + 1, j), (i + 1, j + 1)], [(i, j), (i + 1, j + 1), (i, j + 1)]]
                    : [[(i, j), (i + 1, j), (i, j + 1)], [(i + 1, j), (i + 1, j + 1), (i, j + 1)]];
                foreach (var t in tris)
                {
                    var local = new HashSet<(int, int)>[3];
                    for (var k = 0; k < 3; k++)
                        local[k] = [];
                    var span = (2 * R) + 1;
                    var combos = (int)Math.Pow(span, 6);
                    for (var code = 0; code < combos; code++)
                    {
                        var x = code;
                        var off = new int[6];
                        for (var k = 0; k < 6; k++, x /= span)
                            off[k] = (x % span) - R;
                        var any = false;
                        for (var rot = 0; rot < 3 && !any; rot++)
                        {
                            for (var k = 0; k < 3; k++)
                            {
                                var vi = (k + rot) % 3;
                                var p = g[t[vi].Item1, t[vi].Item2];
                                c9[k * 3] = Ulp(p.X, off[vi * 2]); c9[(k * 3) + 1] = Ulp(p.Y, off[(vi * 2) + 1]); c9[(k * 3) + 2] = p.Z;
                            }
                            any = RayTraceEnvironment.RecordFromCorners(c9, r13) && file.Contains(Fast((ReadOnlySpan<float>)r13));
                        }
                        if (any)
                            for (var k = 0; k < 3; k++)
                                local[k].Add((off[k * 2], off[(k * 2) + 1]));
                    }
                    for (var k = 0; k < 3; k++)
                    {
                        if (cand.TryGetValue(t[k], out var set))
                            set.IntersectWith(local[k]);
                        else
                            cand[t[k]] = local[k];
                    }
                }
            }
        }
        if (Environment.GetEnvironmentVariable("SUBDIV_TRUTH") is { } truth)
        {
            using var writer = File.AppendText(truth);
            foreach (var ((i, j), set) in cand)
            {
                if (set.Count != 1)
                    continue;
                var (ox, oy) = set.First();
                writer.WriteLine(FormattableString.Invariant($"{w[0]} {w[1]} {n} {i} {j} {Ulp(g[i, j].X, ox):R} {Ulp(g[i, j].Y, oy):R} {g[i, j].Z:R}"));
            }
        }
        for (var j = n; j >= 0; j--)
            output.WriteLine(string.Join(" ", Enumerable.Range(0, n + 1).Select(i => cand.TryGetValue((i, j), out var s0) ? (s0.Count == 1 ? $"{s0.First().Item1,2},{s0.First().Item2,2}" : s0.Count == 0 ? "  none " : $" ({s0.Count})  ") : "   ?   ")));
        for (var i = 0; i <= n; i++)
            output.WriteLine($"base x[{i}] {g[i, 0].X:R}  y[{i}] {g[0, i].Y:R}");
    }

    private static Vector3 Place(float[] m, Vector3 v)
        => new(((m[0] * v.X) + (m[2] * v.Z)) + ((m[1] * v.Y) + m[3]),
               ((m[4] * v.X) + (m[6] * v.Z)) + ((m[5] * v.Y) + m[7]),
               ((m[8] * v.X) + (m[10] * v.Z)) + ((m[9] * v.Y) + m[11]));

    private static Int128 Fast(ReadOnlySpan<byte> r)
    {
        Span<float> f = stackalloc float[13];
        for (var k = 0; k < 11; k++)
            f[k] = BinaryPrimitives.ReadSingleLittleEndian(r[(k * 4)..]);
        f[11] = r[0x2c];
        f[12] = r[0x2d];
        return Fast((ReadOnlySpan<float>)f);
    }

    private static Int128 Fast(ReadOnlySpan<float> f)
    {
        UInt128 hash = 0;
        foreach (var k in new[] { 0, 1, 2, 3, 5, 6, 7, 8, 9, 10, 11, 12 })
            hash = (hash * 1000003) ^ (uint)BitConverter.SingleToInt32Bits(f[k]);
        return (Int128)hash;
    }

    // Corner k of the grid: (0,0) is corners[0], i runs toward corners[1], j toward corners[3].
    private static Vector3[,] Bilinear(Vector3[] c, int n, Func<Vector3, Vector3, float, Vector3> lerp)
    {
        var g = new Vector3[n + 1, n + 1];
        for (var i = 0; i <= n; i++)
        {
            var u = i / (float)n;
            var a = lerp(c[0], c[1], u);
            var b = lerp(c[3], c[2], u);
            for (var j = 0; j <= n; j++)
                g[i, j] = lerp(a, b, j / (float)n);
        }
        return g;
    }

    private static Vector3[,] Quadrants(Vector3[] c, int n, float quarter)
    {
        var centre = (((c[0] + c[1]) + c[2]) + c[3]) * quarter;
        var m01 = (c[0] + c[1]) * 0.5f; var m12 = (c[1] + c[2]) * 0.5f; var m23 = (c[2] + c[3]) * 0.5f; var m30 = (c[3] + c[0]) * 0.5f;
        var g = new Vector3[n + 1, n + 1];
        var h = n / 2;
        void Fill(Vector3 p00, Vector3 p10, Vector3 p11, Vector3 p01, int i0, int j0)
        {
            for (var i = 0; i <= h; i++)
            {
                var u = i / (float)h;
                var a = p00 + ((p10 - p00) * u);
                var b = p01 + ((p11 - p01) * u);
                for (var j = 0; j <= h; j++)
                    g[i0 + i, j0 + j] = a + ((b - a) * (j / (float)h));
            }
        }
        Fill(c[0], m01, centre, m30, 0, 0);
        Fill(m01, c[1], m12, centre, h, 0);
        Fill(centre, m12, c[2], m23, h, h);
        Fill(m30, centre, m23, c[3], 0, h);
        return g;
    }

    // Bilinear weights on the four corners: a (1-u)(1-v), b u(1-v), c uv, d (1-u)v.
    private static Vector3[,] Weights(Vector3[] c, int n, Func<Vector3, Vector3, Vector3, Vector3, Vector3> sum)
    {
        var g = new Vector3[n + 1, n + 1];
        for (var i = 0; i <= n; i++)
        {
            var u = i / (float)n;
            for (var j = 0; j <= n; j++)
            {
                var v = j / (float)n;
                g[i, j] = sum(c[0] * ((1 - u) * (1 - v)), c[1] * (u * (1 - v)), c[2] * (u * v), c[3] * ((1 - u) * v));
            }
        }
        return g;
    }

    private static Vector3[,] QuadrantWeights(Vector3[] c, int n, Func<Vector3, Vector3, Vector3, Vector3, Vector3> sum)
    {
        var centre = (((c[0] + c[1]) + c[2]) + c[3]) * 0.25f;
        var m01 = (c[0] + c[1]) * 0.5f; var m12 = (c[1] + c[2]) * 0.5f; var m23 = (c[2] + c[3]) * 0.5f; var m30 = (c[3] + c[0]) * 0.5f;
        var g = new Vector3[n + 1, n + 1];
        var h = n / 2;
        void Fill(Vector3 p00, Vector3 p10, Vector3 p11, Vector3 p01, int i0, int j0)
        {
            for (var i = 0; i <= h; i++)
            {
                var u = i / (float)h;
                for (var j = 0; j <= h; j++)
                {
                    var v = j / (float)h;
                    g[i0 + i, j0 + j] = sum(p00 * ((1 - u) * (1 - v)), p10 * (u * (1 - v)), p11 * (u * v), p01 * ((1 - u) * v));
                }
            }
        }
        Fill(c[0], m01, centre, m30, 0, 0);
        Fill(m01, c[1], m12, centre, h, 0);
        Fill(centre, m12, c[2], m23, h, h);
        Fill(m30, centre, m23, c[3], 0, h);
        return g;
    }

    private static Vector3[,] QuadrantsWith(Vector3[] c, int n, Func<Vector3[], Vector3> centreOf, Func<Vector3, Vector3, Vector3> mid,
                                            Func<Vector3, Vector3, float, Vector3> lerp, bool columnsFirst)
    {
        var centre = centreOf(c);
        Vector3 m01 = mid(c[0], c[1]), m12 = mid(c[1], c[2]), m23 = mid(c[2], c[3]), m30 = mid(c[3], c[0]);
        var g = new Vector3[n + 1, n + 1];
        var h = n / 2;
        void Fill(Vector3 p00, Vector3 p10, Vector3 p11, Vector3 p01, int i0, int j0)
        {
            for (var i = 0; i <= h; i++)
            {
                var u = i / (float)h;
                for (var j = 0; j <= h; j++)
                {
                    var v = j / (float)h;
                    g[i0 + i, j0 + j] = columnsFirst
                        ? lerp(lerp(p00, p01, v), lerp(p10, p11, v), u)
                        : lerp(lerp(p00, p10, u), lerp(p01, p11, u), v);
                }
            }
        }
        Fill(c[0], m01, centre, m30, 0, 0);
        Fill(m01, c[1], m12, centre, h, 0);
        Fill(centre, m12, c[2], m23, h, h);
        Fill(m30, centre, m23, c[3], 0, h);
        return g;
    }

    private static Vector3[,] Recursive(Vector3[] c, int n)
    {
        var g = new Vector3[n + 1, n + 1];
        g[0, 0] = c[0]; g[n, 0] = c[1]; g[n, n] = c[2]; g[0, n] = c[3];
        for (var step = n; step > 1; step /= 2)
        {
            var half = step / 2;
            for (var i = 0; i < n; i += step)
            {
                for (var j = 0; j < n; j += step)
                {
                    Vector3 a = g[i, j], b = g[i + step, j], cc = g[i + step, j + step], d = g[i, j + step];
                    g[i + half, j] = (a + b) * 0.5f;
                    g[i + step, j + half] = (b + cc) * 0.5f;
                    g[i + half, j + step] = (d + cc) * 0.5f;
                    g[i, j + half] = (a + d) * 0.5f;
                    g[i + half, j + half] = (((a + b) + cc) + d) * 0.25f;
                }
            }
        }
        return g;
    }

    private static string Key(ReadOnlySpan<byte> r)
    {
        Span<float> f = stackalloc float[13];
        for (var k = 0; k < 11; k++)
            f[k] = BinaryPrimitives.ReadSingleLittleEndian(r[(k * 4)..]);
        f[11] = r[0x2c];
        f[12] = r[0x2d];
        return Key(f);
    }

    private static string Key(ReadOnlySpan<float> f)
    {
        var parts = new List<string>(12);
        foreach (var k in new[] { 0, 1, 2, 3, 5, 6, 7, 8, 9, 10, 11, 12 })
            parts.Add(BitConverter.SingleToInt32Bits(f[k]).ToString("x8"));
        return string.Join(",", parts);
    }
}
