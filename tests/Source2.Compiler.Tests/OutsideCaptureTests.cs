using System.Text.Json;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Regions and outside verdicts against Valve's (tools/vis/capture_outside.py):
/// OUTSIDECAP=&lt;map&gt; with %TEMP%/vis_capture/&lt;map&gt;.outside.bin and the
/// .outside.rte/.viscfg that compile traced. The voxelizer and region builder
/// run as VisBuild runs them; the verdicts are taken twice, tracing the
/// file's kd tree and the loader's rebuilt one (GROUND_TRUTH 30).
/// </summary>
public class OutsideCaptureTests(ITestOutputHelper output)
{
    [Fact]
    public void RegionsAndVerdicts()
    {
        if (Environment.GetEnvironmentVariable("OUTSIDECAP") is not { Length: > 0 } map)
            return;
        var stem = Path.Combine(Path.GetTempPath(), "vis_capture", map + (Environment.GetEnvironmentVariable("OUTSIDECAP_SEED") == "1" ? ".seed.outside" : ".outside"));
        var records = Read(stem + ".bin");
        var entry = records.First(r => r.Head.GetProperty("ev").GetString() == "regions" && r.Head.GetProperty("when").GetString() == "enter").Blob;
        var status = records.First(r => r.Head.GetProperty("ev").GetString() == "status").Blob;
        // Records with flag bit 1 are skipped by the detection (their status
        // byte is never written); the rest are the regions VisRegions builds.
        var kept = Enumerable.Range(0, entry.Length / 16).Where(i => (BitConverter.ToUInt32(entry, i * 16 + 4) & 2) == 0).ToArray();
        var valveRegions = kept
            .Select(i => (Leaf: (int)(BitConverter.ToUInt32(entry, i * 16 + 4) >> 2), Open: BitConverter.ToUInt64(entry, i * 16 + 8))).ToArray();
        status = [.. kept.Select(i => status[i])];
        output.WriteLine($"Valve's records {entry.Length / 16:n0}, {kept.Length:n0} without flag bit 1");

        // OUTSIDECAP_SEED=1: also capture_outside.py --seed's gathers (<map>.seed.outside.bin).
        List<(System.Numerics.Vector3 Mins, System.Numerics.Vector3 Maxs, int[] T, bool InSeed, int Verdict)>? seeds = null;
        if (Environment.GetEnvironmentVariable("OUTSIDECAP_SEED") == "1")
        {
            var stemSeed = Path.Combine(Path.GetTempPath(), "vis_capture", map + ".seed.outside");
            seeds = [];
            foreach (var (head, blob) in Read(stemSeed + ".bin").Where(r => r.Head.GetProperty("ev").GetString() == "gathers"))
                for (var at = 0; at + 72 <= blob.Length; at += 72)
                {
                    float F(int k) => BitConverter.ToSingle(blob, at + k * 4);
                    seeds.Add((new(F(0), F(1), F(2)), new(F(3), F(4), F(5)),
                               [.. Enumerable.Range(0, 7).Select(k => BitConverter.ToInt32(blob, at + 28 + k * 4))],
                               BitConverter.ToInt32(blob, at + 56) != 0, BitConverter.ToInt32(blob, at + 60)));
                }
            stem = stemSeed;
        }
        var rte = RayTraceEnvironment.ReadFile(stem + ".rte");
        var config = VisConfig.Read(stem + ".viscfg");
        const float voxel = 8f;
        var (mins, maxs) = rte.TracedBounds;
        var (min, max) = VisVoxelizer.RootCube(mins, maxs, voxel);
        var hints = VisVoxelizer.VoxelHints(config.Hints, mins, maxs, min, max, voxel);
        var tree = VisVoxelizer.Build(rte, min, max, voxel, hints);
        var side = VisVoxelizer.VoxelsPerRoot(min, max, voxel) / VisVoxelizer.VoxelsPerLeaf;
        var regions = VisRegions.Build(tree, side);

        // Valve numbers every octree leaf, ours only the listed ones: the
        // regions agree when the masks do and our leaf maps to one of Valve's
        // consistently, in order.
        var leafMap = new Dictionary<int, int>();
        var sameRegions = regions.Regions.Count == valveRegions.Length
            && regions.Regions.Select((r, i) => r.Open == valveRegions[i].Open
                && (leafMap.TryGetValue(r.Leaf, out var v) ? v == valveRegions[i].Leaf : leafMap.TryAdd(r.Leaf, valveRegions[i].Leaf))).All(x => x);
        if (sameRegions)
        {
            var ordered = leafMap.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToArray();
            sameRegions = ordered.Zip(ordered.Skip(1)).All(p => p.First < p.Second);
        }
        output.WriteLine($"regions: ours {regions.Regions.Count:n0}, Valve's {valveRegions.Length:n0}, same {sameRegions}");
        if (!sameRegions)
        {
            var first = Enumerable.Range(0, Math.Min(regions.Regions.Count, valveRegions.Length))
                .FirstOrDefault(i => regions.Regions[i].Open != valveRegions[i].Open, -1);
            output.WriteLine($"  first differing region {first}: ours {(first >= 0 ? regions.Regions[first] : null)}, Valve's {(first >= 0 ? valveRegions[first] : default)}");
            for (var i = Math.Max(0, first - 6); i < Math.Min(first + 30, valveRegions.Length); i++)
                output.WriteLine($"    {i}: ours leaf {regions.Regions[i].Leaf} {regions.Leaves[regions.Regions[i].Leaf]} open {regions.Regions[i].Open:x16} | Valve's leaf {valveRegions[i].Leaf} open {valveRegions[i].Open:x16}");
            Assert.Fail("regions differ");
        }

        foreach (var (name, scene) in new[] { ("file tree", rte), ("rebuilt tree", rte.WithTracerTree()) })
        {
            var inside = VisOutside.Detect(tree, regions, scene, voxel);
            var differ = Enumerable.Range(0, status.Length).Where(i => (byte)inside.Regions[i] != status[i]).ToList();
            output.WriteLine($"{name}: verdicts differing {differ.Count:n0} of {status.Length:n0} (inside ours {inside.Inside:n0}, Valve's {status.Count(s => s == 1):n0})");
            foreach (var i in differ.Take(15))
                output.WriteLine($"  region {i} (leaf {regions.Regions[i].Leaf}): ours {inside.Regions[i]} (seed {inside.Seeded?[i]}), Valve's {(VisOutside.Status)status[i]}");
            if (name == "file tree" && seeds != null)
            {
                // The seed's tallies side by side, for the regions that differ.
                var boxOf = VisOutside.RegionBoxes(tree, regions, voxel);
                foreach (var i in differ.Take(15))
                {
                    var (lo, hi) = boxOf(i);
                    var c = VisSeed.Gather(scene, lo, hi);
                    var theirs = seeds.Where(g => g.Mins == lo && g.Maxs == hi).ToList();
                    output.WriteLine($"  region {i} box {lo}-{hi}: ours facing {c.Facing} behind {c.Behind} insubstantial {c.Insubstantial} escaped {c.Escaped} -> {VisSeed.Decide(c)}; "
                        + (theirs.Count == 0 ? "no Valve gather with this box" : string.Join(" | ", theirs.Select(g => $"Valve's facing {g.T[1]} behind {g.T[2]} insubstantial {g.T[3]} escaped {g.T[4]} rays {g.T[6]} seed {g.InSeed} -> {g.Verdict}"))));
                }
                output.WriteLine($"  traced bounds {scene.TracedBounds.Mins} - {scene.TracedBounds.Maxs}; file {scene.Mins} - {scene.Maxs}");
                var judged = new Dictionary<int, VisOutside.Judged>();
                VisOutside.Detect(tree, regions, scene, voxel, watch: j => { lock (judged) judged[j.Region] = j; });
                foreach (var i in differ.Take(6))
                    if (judged.TryGetValue(i, out var j))
                        output.WriteLine($"  region {i}: marched {j.Marched}, inside votes {j.Inside}, outside votes {j.Outside}, answer {j.Answer}");
                var seedDiffer = 0;
                var seedTotal = 0;
                var byBox = seeds.Where(g => g.InSeed).GroupBy(g => (g.Mins, g.Maxs)).ToDictionary(g => g.Key, g => g.First());
                for (var i = 0; i < regions.Regions.Count; i++)
                {
                    if (!byBox.TryGetValue(boxOf(i), out var g))
                        continue;
                    seedTotal++;
                    if ((int)inside.Seeded![i] != g.Verdict)
                        seedDiffer++;
                }
                output.WriteLine($"  seed verdicts compared {seedTotal:n0}, differing {seedDiffer:n0}");
            }
        }
    }

    private static List<(JsonElement Head, byte[] Blob)> Read(string path)
    {
        var found = new List<(JsonElement, byte[])>();
        using var reader = new BinaryReader(File.OpenRead(path));
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var head = JsonDocument.Parse(reader.ReadBytes(reader.ReadInt32())).RootElement.Clone();
            found.Add((head, reader.ReadBytes(reader.ReadInt32())));
        }
        return found;
    }
}
