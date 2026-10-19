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
            sites.Add(new BombDamage.Site(lo, hi, f[6], p => lo.X <= p.X && p.X <= hi.X && lo.Y <= p.Y && p.Y <= hi.Y && lo.Z <= p.Z && p.Z <= hi.Z));
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
}
