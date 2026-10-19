using System.Numerics;
using Source2.Compiler.Maps;
using Source2.Compiler.Physics;
using Source2.Compiler.Simulation;
using ValveResourceFormat;
using ValveResourceFormat.NavMesh;
using ValveResourceFormat.ResourceTypes;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="BombDamage"/> against Valve's baked_bomb_damage.vdata_c, fed Valve's own nav and
/// world collision from the same package (<c>BOMBPKG=&lt;full .vpk&gt;|&lt;map&gt;</c>, default
/// %TEMP%/vrad3_refs/probe01.vpk).
/// </summary>
public sealed class BombDamageTests(ITestOutputHelper output)
{
    private static (string Package, string Map)? Target()
    {
        var spec = Environment.GetEnvironmentVariable("BOMBPKG") ?? Path.Combine(Path.GetTempPath(), "vrad3_refs", "probe01.vpk") + "|probe01";
        var p = spec.Split('|');
        return File.Exists(p[0]) ? (p[0], p[1]) : null;
    }

    private static byte[] Entry(ValvePak.Package package, string path)
    {
        package.ReadEntry(package.FindEntry(path)!, out var bytes);
        return bytes;
    }

    /// <summary>The blobs of Valve's file: bombsites, positions, damage_values.</summary>
    private static (byte[] Sites, byte[] Positions, byte[] Damage) ValveFile(byte[] vdata)
    {
        using var resource = new Resource();
        resource.Read(new MemoryStream(vdata));
        var data = ((BinaryKV3)resource.DataBlock!).Data.Root;
        var d = data["data"];
        return (d["bombsites"].AsBlob(), d["positions"].AsBlob(), d["damage_values"].AsBlob());
    }

    /// <summary>The world's collision as vphysics2 holds it: meshes and hulls of world_physics.vmdl_c.</summary>
    private sealed class World(List<RnMesh> meshes, List<RnHull> hulls) : BombDamage.IWorld
    {
        public bool BoxFree(Vector3 centre, float half)
        {
            var hits = new List<int>();
            var adjust = float.TryParse(Environment.GetEnvironmentVariable("BOMB_HALF_ADJUST"), System.Globalization.CultureInfo.InvariantCulture, out var a) ? a : 0f;
            var h = new Vec3(half + adjust, half + adjust, half + adjust);
            foreach (var m in meshes)
            {
                MeshSweep.Query(m, null, new Vec3(centre.X, centre.Y, centre.Z), new Vec3(0f, 0f, 0f), h, false, hits);
                if (hits.Count > 0)
                    return false;
            }
            foreach (var hull in hulls)
            {
                // A stand-in until the hull cast (180251800) is ported: the box against the hull's planes.
                if (centre.X + half < hull.BoundsMin.X || hull.BoundsMax.X < centre.X - half
                    || centre.Y + half < hull.BoundsMin.Y || hull.BoundsMax.Y < centre.Y - half
                    || centre.Z + half < hull.BoundsMin.Z || hull.BoundsMax.Z < centre.Z - half)
                    continue;
                var inside = true;
                foreach (var (n, offset) in hull.Planes)
                {
                    var r = half * (MathF.Abs(n.X) + MathF.Abs(n.Y) + MathF.Abs(n.Z));
                    if (n.X * centre.X + n.Y * centre.Y + n.Z * centre.Z - offset > r)
                    {
                        inside = false;
                        break;
                    }
                }
                if (inside)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// The mesh ray (1802406c0's triangle test): the three edge tests of the ray against each
        /// edge (from the edge's midpoint relative to the origin; any negative misses), then the
        /// front face only (n = e01 x e02, n . d below 0), t = (v0 - o) . n / (n . d) above 0,
        /// at most 1; a hit when the least t is below 1.
        /// </summary>
        public bool RayHits(Vector3 o, Vector3 d)
        {
            if (hulls.Count > 0)
                throw new NotSupportedException("the hull ray (1802505c0) is not ported");
            var lo = Vector3.Min(o, o + d);
            var hi = Vector3.Max(o, o + d);
            foreach (var m in meshes)
                foreach (var (ia, ib, ic) in m.Triangles)
                {
                    Vector3 v0 = m.Vertices[ia], v1 = m.Vertices[ib], v2 = m.Vertices[ic];
                    if (Math.Max(v0.X, Math.Max(v1.X, v2.X)) < lo.X || hi.X < Math.Min(v0.X, Math.Min(v1.X, v2.X))
                        || Math.Max(v0.Y, Math.Max(v1.Y, v2.Y)) < lo.Y || hi.Y < Math.Min(v0.Y, Math.Min(v1.Y, v2.Y))
                        || Math.Max(v0.Z, Math.Max(v1.Z, v2.Z)) < lo.Z || hi.Z < Math.Min(v0.Z, Math.Min(v1.Z, v2.Z)))
                        continue;
                    if (TriangleT(o, d, v0, v1, v2) < 1f)
                        return true;
                }
            return false;
        }

        private static float TriangleT(Vector3 o, Vector3 d, Vector3 v0, Vector3 v1, Vector3 v2)
        {
            float ex = v1.X - v0.X, ey = v1.Y - v0.Y, ez = v1.Z - v0.Z;
            // Edge midpoints relative to the origin.
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
                return 1f;
            float hx = v2.X - v0.X, hy = v2.Y - v0.Y, hz = v2.Z - v0.Z;
            float nx = hz * ey - hy * ez, ny = hx * ez - hz * ex, nz = hy * ex - hx * ey;
            var denom = nx * d.X + ny * d.Y + nz * d.Z;
            if (0f <= denom)
                return 1f;
            var t = ((v0.X - o.X) * nx + (v0.Y - o.Y) * ny + (v0.Z - o.Z) * nz) / denom;
            if (t <= 0f)
                return 1f;
            return Math.Min(t, 1f);
        }
    }

    /// <summary>Exploration (BOMB_MARGIN=1): the boxes a 0.0005 margin blocks that the exact test leaves free, with the triangles they meet.</summary>
    [Fact]
    public void MarginBoxes()
    {
        if (Environment.GetEnvironmentVariable("BOMB_MARGIN") != "1" || Target() is not { } t)
            return;
        using var package = new ValvePak.Package();
        package.Read(t.Package);
        using var physics = new Resource();
        physics.Read(new MemoryStream(Entry(package, $"maps/{t.Map}/world_physics.vmdl_c")));
        var phys = ((Model)physics.DataBlock!).GetEmbeddedPhys()!;
        var meshes = phys.Parts.SelectMany(p => p.Shape.Meshes).Select(m => SettleWorld.Mesh(m.Shape)).ToList();
        var nav = new NavMeshFile();
        nav.Read(new MemoryStream(Entry(package, $"maps/{t.Map}.nav")));
        var areas = nav.Areas.Values.Select(a => new BombDamage.Area(a.Corners)).ToList();
        var (sitesBlob, _, _) = ValveFile(Entry(package, $"maps/{t.Map}/baked_bomb_damage.vdata_c"));
        var sites = new List<BombDamage.Site> { new(Vector3.Zero, Vector3.Zero, 0, _ => true) };
        var world = new World(meshes, []);
        var grid = BombDamage.Build(areas, sites, world);
        var hits = new List<int>();
        foreach (var slot in grid.Nodes.Slots())
        {
            var c = grid.Nodes.Key(slot);
            if (grid.Nodes.Value(slot).Blocked)
                continue;
            for (var mi = 0; mi < meshes.Count; mi++)
            {
                hits.Clear();
                MeshSweep.Query(meshes[mi], new Vec3(1f, 1f, 1f), new Vec3(c.X, c.Y, c.Z), new Vec3(0f, 0f, 0f), new Vec3(5.0005f, 5.0005f, 5.0005f), false, hits);
                foreach (var tri in hits)
                {
                    var (a, b, d) = meshes[mi].Triangles[tri];
                    var va = meshes[mi].Vertices[a]; var vb = meshes[mi].Vertices[b]; var vd = meshes[mi].Vertices[d];
                    output.WriteLine($"box {c} mesh {mi} tri {tri}: ({va.X:R},{va.Y:R},{va.Z:R}) ({vb.X:R},{vb.Y:R},{vb.Z:R}) ({vd.X:R},{vd.Y:R},{vd.Z:R})");
                }
            }
        }
    }

    [Fact]
    public void PositionsMatchValve()
    {
        if (Target() is not { } t)
            return;
        using var package = new ValvePak.Package();
        package.Read(t.Package);
        var nav = new NavMeshFile();
        nav.Read(new MemoryStream(Entry(package, $"maps/{t.Map}.nav")));
        var areas = nav.Areas.Values.Select(a => new BombDamage.Area(a.Corners)).ToList();
        var (sitesBlob, valvePositions, _) = ValveFile(Entry(package, $"maps/{t.Map}/baked_bomb_damage.vdata_c"));
        var sites = new List<BombDamage.Site>();
        for (var i = 0; i + 28 <= sitesBlob.Length; i += 28)
        {
            var f = Enumerable.Range(0, 7).Select(k => BitConverter.ToSingle(sitesBlob, i + k * 4)).ToArray();
            Vector3 lo = new(f[0], f[1], f[2]), hi = new(f[3], f[4], f[5]);
            sites.Add(new BombDamage.Site(lo, hi, f[6], p => lo.X <= p.X + 5f && p.X - 5f <= hi.X && lo.Y <= p.Y + 5f && p.Y - 5f <= hi.Y && lo.Z <= p.Z + 5f && p.Z - 5f <= hi.Z));
        }
        using var physics = new Resource();
        physics.Read(new MemoryStream(Entry(package, $"maps/{t.Map}/world_physics.vmdl_c")));
        var phys = ((Model)physics.DataBlock!).GetEmbeddedPhys()!;
        var meshes = phys.Parts.SelectMany(p => p.Shape.Meshes).Select(m => SettleWorld.Mesh(m.Shape)).ToList();
        var hulls = phys.Parts.SelectMany(p => p.Shape.Hulls).Select(h => SettleWorld.Hull(h.Shape)).ToList();
        output.WriteLine($"{areas.Count} areas, {sites.Count} sites, {meshes.Count} meshes, {hulls.Count} hulls");

        var grid = BombDamage.Build(areas, sites, new World(meshes, hulls));
        var ours = BombDamage.Positions(grid);
        output.WriteLine($"nodes {grid.Nodes.Count} (slots {grid.Nodes.Buckets}), blocked {grid.Nodes.Slots().Count(s => grid.Nodes.Value(s).Blocked)}, sites [{string.Join(", ", grid.Sites.Select(s => s.Count))}]");
        output.WriteLine($"positions: ours {ours.Length / 6}, Valve's {valvePositions.Length / 6}");
        if (Environment.GetEnvironmentVariable("BOMB_OUT") is { } outDir)
        {
            File.WriteAllBytes(Path.Combine(outDir, "ours_positions.bin"), ours);
            File.WriteAllBytes(Path.Combine(outDir, "valve_positions.bin"), valvePositions);
        }
        var first = Enumerable.Range(0, Math.Min(ours.Length, valvePositions.Length)).FirstOrDefault(i => ours[i] != valvePositions[i], -1);
        output.WriteLine($"first differing byte {first}, positions in place {Enumerable.Range(0, Math.Min(ours.Length, valvePositions.Length) / 6).Count(i => ours.AsSpan(i * 6, 6).SequenceEqual(valvePositions.AsSpan(i * 6, 6)))}");
        Assert.Equal(valvePositions, ours);
    }

    /// <summary>The .vdata text the modifier writes, from Valve's own blobs: its CRC is the one the .vdata_c records.</summary>
    [Fact]
    public void VdataTextMatchesValvesCrc()
    {
        if (Target() is not { } t)
            return;
        using var package = new ValvePak.Package();
        package.Read(t.Package);
        var vdata = Entry(package, $"maps/{t.Map}/baked_bomb_damage.vdata_c");
        var (sites, positions, damage) = ValveFile(vdata);
        using var resource = new Resource();
        resource.Read(new MemoryStream(vdata));
        var expected = ((ValveResourceFormat.Blocks.ResourceEditInfo2)resource.GetBlockByType(BlockType.RED2)!).AdditionalInputDependencies[0].FileCRC;
        var text = BombDamage.VdataText(sites, positions, damage);
        var crlf = System.IO.Hashing.Crc32.HashToUInt32(System.Text.Encoding.UTF8.GetBytes(text));
        var lf = System.IO.Hashing.Crc32.HashToUInt32(System.Text.Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n")));
        output.WriteLine($"Valve's CRC {expected}, ours with CRLF {crlf}, with LF {lf}, {text.Length} chars");
        Assert.True(expected == crlf || expected == lf);

        // The whole compiled file from the same blobs.
        var ours = BombDamage.VdataC(sites, positions, damage, "s2c_rc_probe", t.Map);
        var first = Enumerable.Range(0, Math.Min(ours.Length, vdata.Length)).FirstOrDefault(i => ours[i] != vdata[i], -1);
        output.WriteLine($"vdata_c: ours {ours.Length} bytes, Valve's {vdata.Length}, first difference {first}");
        if (Environment.GetEnvironmentVariable("BOMB_OUT") is { } outDir)
            File.WriteAllBytes(Path.Combine(outDir, "ours.vdata_c"), ours);
        Assert.Equal(vdata, ours);
    }

    [Fact]
    public void DamageValuesMatchValve()
    {
        if (Target() is not { } t)
            return;
        using var package = new ValvePak.Package();
        package.Read(t.Package);
        var nav = new NavMeshFile();
        nav.Read(new MemoryStream(Entry(package, $"maps/{t.Map}.nav")));
        var areas = nav.Areas.Values.Select(a => new BombDamage.Area(a.Corners)).ToList();
        var (sitesBlob, _, valveDamage) = ValveFile(Entry(package, $"maps/{t.Map}/baked_bomb_damage.vdata_c"));
        var sites = new List<BombDamage.Site>();
        for (var i = 0; i + 28 <= sitesBlob.Length; i += 28)
        {
            var f = Enumerable.Range(0, 7).Select(k => BitConverter.ToSingle(sitesBlob, i + k * 4)).ToArray();
            Vector3 lo = new(f[0], f[1], f[2]), hi = new(f[3], f[4], f[5]);
            sites.Add(new BombDamage.Site(lo, hi, f[6], p => lo.X <= p.X + 5f && p.X - 5f <= hi.X && lo.Y <= p.Y + 5f && p.Y - 5f <= hi.Y && lo.Z <= p.Z + 5f && p.Z - 5f <= hi.Z));
        }
        using var physics = new Resource();
        physics.Read(new MemoryStream(Entry(package, $"maps/{t.Map}/world_physics.vmdl_c")));
        var phys = ((Model)physics.DataBlock!).GetEmbeddedPhys()!;
        var meshes = phys.Parts.SelectMany(p => p.Shape.Meshes).Select(m => SettleWorld.Mesh(m.Shape)).ToList();
        var world = new World(meshes, []);
        var grid = BombDamage.Build(areas, sites, world);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var s = 0; s < sites.Count; s++)
            Assert.True(BombDamage.Distances(grid, s, world));
        output.WriteLine($"distances {sw.Elapsed.TotalSeconds:F1} s");
        var ours = BombDamage.DamageValues(grid, sites);
        output.WriteLine($"damage bytes: ours {ours.Length}, Valve's {valveDamage.Length}");
        int words = Math.Min(ours.Length, valveDamage.Length) / 4, distSame = 0, yawSame = 0, pitchSame = 0;
        var firstDist = -1;
        for (var i = 0; i < words; i++)
        {
            if (ours[i * 4] == valveDamage[i * 4] && ours[i * 4 + 1] == valveDamage[i * 4 + 1])
                distSame++;
            else if (firstDist < 0)
                firstDist = i;
            yawSame += ours[i * 4 + 2] == valveDamage[i * 4 + 2] ? 1 : 0;
            pitchSame += ours[i * 4 + 3] == valveDamage[i * 4 + 3] ? 1 : 0;
        }
        output.WriteLine($"words {words}: distance same {distSame}, yaw byte same {yawSame}, pitch byte same {pitchSame}");
        var sampled = grid.Nodes.Slots().Where(sl => grid.Nodes.Value(sl).Sampled).Select(sl => grid.Nodes.Key(sl)).ToList();
        var zero = grid.Sites[0].Slots().Where(sl => grid.Sites[0].Value(sl).Distance == 0f).Select(sl => grid.Sites[0].Key(sl)).ToList();
        for (var i = 0; i < 8; i++)
        {
            var q = sampled[i];
            var nearest = zero.Min(z => Vector3.Distance(z, q));
            var rec = grid.Sites[0].Value(grid.Sites[0].Find(q, BombDamage.Hash(q)));
            output.WriteLine($"  node {q}: straight to the site {nearest:F2}, ours {rec.Distance:F2} via {rec.From}, valve {BitConverter.ToUInt16(valveDamage, i * 4)}");
        }
        if (firstDist >= 0)
            for (var i = firstDist; i < Math.Min(words, firstDist + 8); i++)
                output.WriteLine($"  word {i}: ours {BitConverter.ToUInt16(ours, i * 4)} {ours[i * 4 + 2]} {ours[i * 4 + 3]}  valve {BitConverter.ToUInt16(valveDamage, i * 4)} {valveDamage[i * 4 + 2]} {valveDamage[i * 4 + 3]}");
        Assert.Equal(valveDamage, ours);
    }
}
