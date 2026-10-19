using System.Buffers.Binary;
using Source2.Compiler.Maps.NavGen;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The Recast stage against tools/re/capture_navgen.py's dumps (<c>NAVGEN_CAPTURE=&lt;dir&gt;</c>, default
/// %TEMP%/navgen_capture/probe01): Valve's own Recast input in, each intermediate compared.
/// </summary>
public sealed class NavGenRecastTests(ITestOutputHelper output)
{
    private static string? Dir()
    {
        var dir = Environment.GetEnvironmentVariable("NAVGEN_CAPTURE")
                  ?? Path.Combine(Path.GetTempPath(), "navgen_capture", "probe01");
        return File.Exists(Path.Combine(dir, "recast_cfg.bin")) ? dir : null;
    }

    internal static RecastTiles Load(string dir)
    {
        var (polys, verts) = NavGenProbe.Load(Path.Combine(dir, "005_Recast_in.bin"));
        var settings = File.ReadAllBytes(Path.Combine(dir, "recast_params.bin"));
        var hulls = File.ReadAllBytes(Path.Combine(dir, "recast_hulls.bin"));
        var global = File.ReadAllBytes(Path.Combine(dir, "recast_global.bin"));
        var stage = File.ReadAllBytes(Path.Combine(dir, "recast_settings.bin"));
        int hull = stage[0x190];
        var config = RecastConfig.From(settings, hulls.AsSpan(hull * 0x48, 0x48), BinaryPrimitives.ReadSingleLittleEndian(global.AsSpan(0x58)), 0);
        var input = RecastInput.From([.. verts.Select(v => (v.X, v.Y, v.Z))], polys);
        return new RecastTiles(config, input);
    }

    [Fact]
    public void ConfigAndInputMatchValve()
    {
        if (Dir() is not { } dir)
            return;
        var tiles = Load(dir);
        var cfg = File.ReadAllBytes(Path.Combine(dir, "recast_cfg.bin"));
        var words = tiles.Config.TileWords();
        for (var o = 0; o < words.Length; o++)
            Assert.True(words[o] == cfg[0xb8 + o], $"tile word +{0x80 + o:x}: ours {words[o]:x2}, Valve {cfg[0xb8 + o]:x2}");
        for (var k = 0; k < 3; k++)
        {
            Assert.Equal(BinaryPrimitives.ReadSingleLittleEndian(cfg.AsSpan(0x48 + k * 4)), tiles.Input.BMin[k]);
            Assert.Equal(BinaryPrimitives.ReadSingleLittleEndian(cfg.AsSpan(0x54 + k * 4)), tiles.Input.BMax[k]);
        }
        var vb = File.ReadAllBytes(Path.Combine(dir, "recast_input_verts.bin"));
        Assert.Equal(vb.Length / 4, tiles.Input.Verts.Length);
        for (var i = 0; i < tiles.Input.Verts.Length; i++)
            Assert.True(BitConverter.SingleToInt32Bits(tiles.Input.Verts[i]) == BinaryPrimitives.ReadInt32LittleEndian(vb.AsSpan(i * 4)),
                $"vertex float {i}: ours {tiles.Input.Verts[i]:R}, Valve {BinaryPrimitives.ReadSingleLittleEndian(vb.AsSpan(i * 4)):R}");
        var tb = File.ReadAllBytes(Path.Combine(dir, "recast_input_tris.bin"));
        Assert.Equal(tb.Length / 4, tiles.Input.Tris.Length);
        for (var i = 0; i < tiles.Input.Tris.Length; i++)
            Assert.Equal(BinaryPrimitives.ReadInt32LittleEndian(tb.AsSpan(i * 4)), tiles.Input.Tris[i]);
        output.WriteLine($"{tiles.TilesX} x {tiles.TilesZ} tiles, {tiles.Input.TriCount} triangles, {tiles.Chunky.NodeCount} chunky nodes");
    }

    private static Dictionary<(int, int), string> ByBmin(string dir, string prefix, int bminOffset)
    {
        var map = new Dictionary<(int, int), string>();
        foreach (var f in Directory.GetFiles(dir, prefix + "*.bin"))
        {
            var b = File.ReadAllBytes(f);
            map[(BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(bminOffset)), BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(bminOffset + 8)))] = f;
        }
        return map;
    }

    private static (int, int) Key(RecastTile t) => (BitConverter.SingleToInt32Bits(t.BMin[0]), BitConverter.SingleToInt32Bits(t.BMin[2]));

    [Fact]
    public void CompactHeightfieldsMatchValve()
    {
        if (Dir() is not { } dir)
            return;
        var tiles = Load(dir);
        var dumps = ByBmin(dir, "chf_", 0x24);
        int tilesChecked = 0, bad = 0;
        var firstDiff = new Dictionary<string, int>();
        foreach (var tile in tiles.Tiles())
        {
            tile.Rasterize();
            tile.BuildRegions();
            if (!dumps.TryGetValue(Key(tile), out var path))
            {
                Assert.Null(tile.Chf);
                continue;
            }
            var b = File.ReadAllBytes(path);
            var c = tile.Chf!;
            int I(int o) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o));
            var n = I(8);
            var diffs = new List<string>();
            void Check(string what, long ours, long valve)
            {
                if (ours != valve)
                    diffs.Add($"{what}: ours {ours}, Valve {valve}");
            }
            Check("width", c.Width, I(0));
            Check("spanCount", c.SpanCount, n);
            Check("wh", c.WalkableHeight, I(0xc));
            Check("wh2", c.WalkableHeight2, I(0x10));
            Check("wh3", c.WalkableHeight3, I(0x14));
            Check("climb", c.WalkableClimb, I(0x18));
            Check("border", c.BorderSize, I(0x1c));
            Check("maxDistance", c.MaxDistance, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(0x20)));
            Check("maxRegions", c.MaxRegions, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(0x22)));
            Check("bmax.y", BitConverter.SingleToInt32Bits(c.BMax[1]), I(0x34));
            if (diffs.Count == 0)
            {
                var at = 0x70;
                for (var i = 0; i < c.Width * c.Height; i++, at += 4)
                    if (c.Cells[i] != BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at)))
                    {
                        diffs.Add($"cell {i}: ours {c.Cells[i]:x}, Valve {BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at)):x}");
                        break;
                    }
                var spans = at;
                var areas = spans + n * 8;
                var dist = areas + n;
                var kinds = new[] { "y", "con", "h", "area", "dist", "reg" };
                for (var i = 0; i < n && diffs.Count < 6; i++)
                {
                    var s = spans + i * 8;
                    var con = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(s + 4));
                    long[] ours = [c.Y[i], c.Con[i] & 0xffffff, c.H(i), c.Areas[i], c.Dist[i], c.Reg[i]];
                    long[] valve = [BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(s)), con & 0xffffff, con >> 24, b[areas + i],
                        BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(dist + i * 2)), BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(s + 2))];
                    for (var k = 0; k < kinds.Length; k++)
                        if (ours[k] != valve[k])
                        {
                            diffs.Add($"span {i} {kinds[k]}: ours {ours[k]}, Valve {valve[k]}");
                            break;
                        }
                }
            }
            tilesChecked++;
            if (diffs.Count > 0)
            {
                bad++;
                var kind = diffs[0].Split(':')[0].Split(' ')[^1];
                firstDiff[kind] = firstDiff.GetValueOrDefault(kind) + 1;
                if (bad <= 4)
                    output.WriteLine($"tile {tile.TileX},{tile.TileZ}:\n  " + string.Join("\n  ", diffs));
            }
        }
        output.WriteLine($"{tilesChecked} tiles checked, {bad} differ; first difference by kind: {string.Join(", ", firstDiff.Select(kv => $"{kv.Key} {kv.Value}"))}");
        Assert.Equal(0, bad);
    }

    /// <summary>A collect_*.bin dump: the tile, the poly mesh (if any) and the detail mesh (if any).</summary>
    internal sealed class CollectDump
    {
        public byte[] Tile = [];
        public int NVerts, NPolys, Nvp;
        public ushort[] Verts = [], Polys = [], Regs = [];
        public byte[] Areas = [];
        public byte[] PolyHeader = [];
        public int NMeshes, NDVerts, NTris;
        public uint[] Meshes = [];
        public float[] DVerts = [];
        public byte[] Tris = [];
        public bool HasPoly, HasDetail;

        public static CollectDump Load(string path)
        {
            var b = File.ReadAllBytes(path);
            var d = new CollectDump { Tile = b[..0x128] };
            var at = 0x128;
            int I(int o) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o));
            if (at < b.Length && b.Length - at != 12 && (b.Length - at) >= 0x60)
            {
                d.PolyHeader = b[at..(at + 0x60)];
                d.NVerts = I(at + 0x28);
                d.NPolys = I(at + 0x2c);
                d.Nvp = I(at + 0x34);
                var ok = d.NVerts >= 0 && d.NPolys >= 0 && d.Nvp is > 2 and <= 8;
                var size = 0x60 + d.NVerts * 6 + d.NPolys * d.Nvp * 4 + d.NPolys * 2 * 2 + d.NPolys;
                if (ok && at + size <= b.Length)
                {
                    d.HasPoly = true;
                    at += 0x60;
                    d.Verts = new ushort[d.NVerts * 3];
                    for (var i = 0; i < d.Verts.Length; i++, at += 2)
                        d.Verts[i] = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
                    d.Polys = new ushort[d.NPolys * d.Nvp * 2];
                    for (var i = 0; i < d.Polys.Length; i++, at += 2)
                        d.Polys[i] = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
                    d.Regs = new ushort[d.NPolys];
                    for (var i = 0; i < d.NPolys; i++, at += 2)
                        d.Regs[i] = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
                    at += d.NPolys * 2;
                    d.Areas = b[at..(at + d.NPolys)];
                    at += d.NPolys;
                }
            }
            if (at + 12 <= b.Length)
            {
                d.HasDetail = true;
                d.NMeshes = I(at);
                d.NDVerts = I(at + 4);
                d.NTris = I(at + 8);
                at += 12;
                d.Meshes = new uint[d.NMeshes * 4];
                for (var i = 0; i < d.Meshes.Length; i++, at += 4)
                    d.Meshes[i] = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
                d.DVerts = new float[d.NDVerts * 3];
                for (var i = 0; i < d.DVerts.Length; i++, at += 4)
                    d.DVerts[i] = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at));
                d.Tris = b[at..(at + d.NTris * 4)];
            }
            return d;
        }
    }

    [Fact]
    public void PolyMeshesMatchValve()
    {
        if (Dir() is not { } dir)
            return;
        var tiles = Load(dir);
        var files = Directory.GetFiles(dir, "collect_*.bin").OrderBy(f => f, StringComparer.Ordinal).ToList();
        int index = 0, bad = 0, withPoly = 0;
        foreach (var tile in tiles.Tiles())
        {
            var dump = CollectDump.Load(files[index++]);
            Assert.Equal(BitConverter.SingleToInt32Bits(tile.BMin[0]), BinaryPrimitives.ReadInt32LittleEndian(dump.Tile.AsSpan(0x98)));
            tile.Rasterize();
            tile.BuildRegions();
            tile.BuildPolyMesh();
            var pm = tile.PolyMesh;
            var diffs = new List<string>();
            if (pm is null != !dump.HasPoly)
                diffs.Add($"poly mesh: ours {(pm is null ? "none" : pm.NPolys + " polys")}, Valve {(dump.HasPoly ? dump.NPolys + " polys" : "none")}");
            else if (pm is not null)
            {
                withPoly++;
                if (pm.NVerts != dump.NVerts || pm.NPolys != dump.NPolys)
                    diffs.Add($"counts: ours {pm.NVerts} verts {pm.NPolys} polys, Valve {dump.NVerts} verts {dump.NPolys} polys");
                else
                {
                    for (var i = 0; i < pm.NVerts * 3 && diffs.Count < 3; i++)
                        if (pm.Verts[i] != dump.Verts[i])
                            diffs.Add($"vert {i / 3}.{i % 3}: ours {pm.Verts[i]}, Valve {dump.Verts[i]}");
                    for (var i = 0; i < pm.NPolys * pm.Nvp * 2 && diffs.Count < 3; i++)
                        if (pm.Polys[i] != dump.Polys[i])
                            diffs.Add($"poly {i / (pm.Nvp * 2)} word {i % (pm.Nvp * 2)}: ours {pm.Polys[i]:x}, Valve {dump.Polys[i]:x}");
                    for (var i = 0; i < pm.NPolys && diffs.Count < 3; i++)
                        if (pm.Regs[i] != dump.Regs[i] || pm.Areas[i] != dump.Areas[i])
                            diffs.Add($"poly {i} reg/area: ours {pm.Regs[i]}/{pm.Areas[i]}, Valve {dump.Regs[i]}/{dump.Areas[i]}");
                }
            }
            if (diffs.Count > 0)
            {
                bad++;
                if (bad <= 4)
                    output.WriteLine($"tile {tile.TileX},{tile.TileZ}:\n  " + string.Join("\n  ", diffs));
            }
        }
        output.WriteLine($"{index} tiles, {withPoly} with a poly mesh, {bad} differ");
        Assert.Equal(0, bad);
    }

    [Fact]
    public void SolidHeightfieldsMatchValve()
    {
        if (Dir() is not { } dir)
            return;
        var tiles = Load(dir);
        var dumps = ByBmin(dir, "tile_", 8);
        int tilesChecked = 0, bad = 0;
        foreach (var tile in tiles.Tiles())
        {
            tile.Rasterize();
            if (!dumps.TryGetValue(Key(tile), out var path))
            {
                Assert.Null(tile.Solid);
                continue;
            }
            var b = File.ReadAllBytes(path);
            var hf = tile.Solid!;
            Assert.Equal(BinaryPrimitives.ReadInt32LittleEndian(b), hf.Width);
            for (var k = 0; k < 3; k++)
                Assert.Equal(BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(0x14 + k * 4)), k switch { 0 => hf.BMaxX, 1 => hf.BMaxY, _ => hf.BMaxZ });
            var at = 0x28;
            var diffs = new List<string>();
            for (var i = 0; i < hf.Width * hf.Height; i++)
            {
                var n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at));
                at += 4;
                var valve = new List<(int, int, uint)>();
                for (var k = 0; k < n; k++, at += 12)
                    valve.Add((BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at)), BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at + 4)), BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at + 8)) & 0x3f));
                var ours = new List<(int, int, uint)>();
                for (var s = hf.Spans[i]; s is not null; s = s.Next)
                    ours.Add((s.SMin, s.SMax, s.Flags & 0x3f));
                if (!valve.SequenceEqual(ours) && diffs.Count < 5)
                    diffs.Add($"column {i % hf.Width},{i / hf.Width}: ours [{string.Join(" ", ours)}] Valve [{string.Join(" ", valve)}]");
            }
            tilesChecked++;
            if (diffs.Count > 0)
            {
                bad++;
                if (bad <= 3)
                    output.WriteLine($"tile {tile.TileX},{tile.TileZ}:\n  " + string.Join("\n  ", diffs));
            }
        }
        output.WriteLine($"{tilesChecked} tiles checked, {bad} differ");
        Assert.Equal(0, bad);
    }
}
