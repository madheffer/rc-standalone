using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// The whole-package score: <c>PARITY=&lt;addon&gt;|&lt;map&gt;|&lt;valve.vpk&gt;[|&lt;switches&gt;]</c>
/// runs compile-map (default: no switches, a full build) into an EMPTY package
/// under %TEMP%/parity/&lt;map&gt;, so nothing of Valve's is kept, and scores
/// it against Valve's package: per role, how many of Valve's files we write
/// byte for byte and what share of Valve's bytes they hold. A file we do not
/// write, or write differently, scores nothing. <c>PARITY_KEEP=1</c> leaves
/// our package in place; <c>PARITY_LIST=n</c> lists the n largest misses.
/// </summary>
public class ParityReport(ITestOutputHelper output)
{
    /// <summary>The role of a package file, by its path.</summary>
    public static string Role(string path)
    {
        var p = path.ToLowerInvariant();
        if (p.Contains("/lightmaps/") || p.Contains("lightmap"))
            return "lightmaps";
        if (p.Contains("cubemap") || p.Contains("/envmaps/") || p.EndsWith(".vtex_c", StringComparison.Ordinal) && p.Contains("envmap"))
            return "cubemaps";
        if (p.Contains("steamaudio") || p.EndsWith(".vsnap_c", StringComparison.Ordinal) || p.Contains("/sa_"))
            return "steam audio";
        if (p.EndsWith(".nav", StringComparison.Ordinal))
            return "nav";
        if (p.EndsWith(".vvis_c", StringComparison.Ordinal))
            return "visibility";
        if (p.Contains("world_physics"))
            return "world physics";
        if (p.EndsWith(".vents_c", StringComparison.Ordinal))
            return "entity lumps";
        if (p.Contains("/entities/"))
            return "entity models";
        if (p.Contains("/worldnodes/"))
            return "world nodes";
        if (p.EndsWith(".vmap_c", StringComparison.Ordinal) || p.EndsWith(".vwrld_c", StringComparison.Ordinal)
            || p.EndsWith(".vrman_c", StringComparison.Ordinal))
            return "index";
        return "other";
    }

    /// <summary>Per role: (Valve's files, ours identical, Valve's bytes, bytes in identical files, files we lack).</summary>
    public static SortedDictionary<string, (int Files, int Same, long Bytes, long SameBytes, int Missing)> Score(
        IReadOnlyDictionary<string, byte[]> valve, IReadOnlyDictionary<string, byte[]> ours)
    {
        var table = new SortedDictionary<string, (int Files, int Same, long Bytes, long SameBytes, int Missing)>();
        foreach (var (path, bytes) in valve)
        {
            var role = Role(path);
            var row = table.GetValueOrDefault(role);
            var mine = ours.GetValueOrDefault(path);
            var same = mine is not null && mine.AsSpan().SequenceEqual(bytes);
            table[role] = (row.Files + 1, row.Same + (same ? 1 : 0), row.Bytes + bytes.Length,
                           row.SameBytes + (same ? bytes.Length : 0), row.Missing + (mine is null ? 1 : 0));
        }
        return table;
    }

    private static ValveKeyValue.KVObject? Find(ValveKeyValue.KVObject node, string name)
    {
        foreach (var child in node.Children)
        {
            if (child.Key == name)
                return child.Value;
            if (Find(child.Value, name) is { } hit)
                return hit;
        }
        return null;
    }

    /// <summary>ENTORDER=addon|map: entity classes in first-appearance order, by the walk and by document element order.</summary>
    [Fact]
    public void EntityClassOrder()
    {
        if (Environment.GetEnvironmentVariable("ENTORDER") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        var document = MapSource.Read(MapFixtures.VmapSource(p[0], p[1])!);
        output.WriteLine("walk: " + string.Join(" ", MapEntities.From(document).Select(e => e.ClassName).Distinct()));
        output.WriteLine("by node id: " + string.Join(" ", MapEntities.From(document).OrderBy(e => e.NodeId).Select(e => e.ClassName).Distinct()));
        output.WriteLine("by node id desc: " + string.Join(" ", MapEntities.From(document).OrderByDescending(e => e.NodeId).Select(e => e.ClassName).Distinct()));
        output.WriteLine("ids: " + string.Join(" ", MapEntities.From(document).Select(e => $"{e.NodeId}:{e.ClassName}")));
        output.WriteLine("elements: " + string.Join(" ", document.Elements.Select(e => e.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname"))
            .OfType<string>().Distinct()));
    }

    /// <summary>ENTUV=addon|map|model substring: an entity model's meshes' texcoords, raw and after the island shift, as float bits.</summary>
    [Fact]
    public void EntityTexcoords()
    {
        if (Environment.GetEnvironmentVariable("ENTUV") is not { Length: > 0 } spec || MapFixtures.GameSchema() is not { } schema)
            return;
        var p = spec.Split('|');
        var source = MapFixtures.VmapSource(p[0], p[1])!;
        var document = DmxBinary.ReadFile(source);
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(CS2Fixtures.StockPak()!)!, ".."));
        using var content = new GameContent(CS2Fixtures.StockPak()!, Path.Combine(game, "csgo_addons", p[0]));
        var models = Physics.EntityPhysicsModels.Build(document, p[1], content.Material, content.CollisionProperty, schema, null, content.SmartProp);
        foreach (var m in models.Where(m => m.Path.Contains(p[2], StringComparison.Ordinal)))
            foreach (var mesh in m.Meshes)
            {
                var raw = MapMeshCorners.Build(mesh, false, null, withTangent: false, allStreams: true);
                var shifted = MapMeshCorners.Build(mesh, true, null, withTangent: false, allStreams: true);
                for (var k = 0; k < raw.Count; k++)
                {
                    var t = raw[k].Streams.First(x => x.Name == "texcoord").First;
                    for (var c = 0; c < raw[k].Vertices.Length / raw[k].Stride; c++)
                    {
                        float ru = raw[k].Vertices[c * raw[k].Stride + t], rv = raw[k].Vertices[c * raw[k].Stride + t + 1];
                        float su = shifted[k].Vertices[c * shifted[k].Stride + t], sv = shifted[k].Vertices[c * shifted[k].Stride + t + 1];
                        output.WriteLine($"{m.Path} piece {k} corner {c}: raw ({ru:R}, {rv:R}) shifted ({su:R} {BitConverter.SingleToInt32Bits(su):X8}, {sv:R} {BitConverter.SingleToInt32Bits(sv):X8})");
                    }
                }
            }
    }

    /// <summary>
    /// UVDCAP=capture.bin (tools/vis/capture_uvdensity.py): each captured call's
    /// density recomputed by <see cref="UvDensity"/> from the inputs Valve's
    /// function saw, with the first vertices' values as float bits.
    /// </summary>
    [Fact]
    public void UvDensityCapture()
    {
        if (Environment.GetEnvironmentVariable("UVDCAP") is not { Length: > 0 } path)
            return;
        var data = File.ReadAllBytes(path);
        for (var at = 0; at + 20 <= data.Length;)
        {
            var count = BitConverter.ToInt32(data, at);
            var stride = BitConverter.ToInt32(data, at + 4);
            var po = BitConverter.ToInt32(data, at + 8);
            var to = BitConverter.ToInt32(data, at + 12);
            var pct = BitConverter.ToInt32(data, at + 16);
            var positions = new List<System.Numerics.Vector3>();
            var uvs = new List<System.Numerics.Vector2>();
            for (var k = 0; k < count; k++)
            {
                var o = at + 20 + k * 20;
                positions.Add(new(BitConverter.ToSingle(data, o), BitConverter.ToSingle(data, o + 4), BitConverter.ToSingle(data, o + 8)));
                uvs.Add(new(BitConverter.ToSingle(data, o + 12), BitConverter.ToSingle(data, o + 16)));
            }
            var density = UvDensity.Compute(positions, uvs, Enumerable.Range(0, count).ToArray(), pct);
            output.WriteLine($"call: {count} indices, stride {stride}, position at {po}, tex at {to}, percentile {pct}: density {density:R}; "
                             + $"first ({positions[0].X:R} {positions[0].Y:R} {positions[0].Z:R}) uv ({uvs[0].X:R} {uvs[0].Y:R})");
            at += 20 + count * 20;
        }
    }

    /// <summary>What differs between two compiled resources: block lists, tree fields by name, raw blocks by type.</summary>
    public static string Why(byte[] valve, byte[] mine)
    {
        try
        {
            var reasons = new SortedSet<string>(StringComparer.Ordinal);
            var (ta, tb) = (ResourceTrees.BlockTable(valve), ResourceTrees.BlockTable(mine));
            if (!ta.Select(b => b.FourCC).SequenceEqual(tb.Select(b => b.FourCC)))
                reasons.Add("block list");
            var (ra, rb) = (RawBlocks(valve), RawBlocks(mine));
            var (ka, kb) = (WorldPhysicsAuthorTests.Trees(valve), WorldPhysicsAuthorTests.Trees(mine));
            foreach (var (type, bytes) in ra)
            {
                if (!rb.TryGetValue(type, out var other) || bytes.AsSpan().SequenceEqual(other))
                    continue;
                if (ka.TryGetValue(type, out var x) && kb.TryGetValue(type, out var y))
                {
                    var lines = KvTreeDiff.Diff(x, y, 400);
                    foreach (var line in lines)
                    {
                        var field = line.Split(':')[0];
                        field = System.Text.RegularExpressions.Regex.Replace(field, @"\[\d+\]", "");
                        reasons.Add($"{type}{field[(field.LastIndexOf('.') is var dot && dot >= 0 ? dot : 0)..]}");
                    }
                    if (lines.Count == 0)
                        reasons.Add($"{type} (bytes only)");
                }
                else
                    reasons.Add($"{type} bytes");
            }
            return reasons.Count == 0 ? "header" : string.Join(", ", reasons);
        }
        catch (Exception e)
        {
            return "undecoded: " + e.GetType().Name;
        }
    }

    private static Dictionary<string, byte[]> RawBlocks(byte[] file)
    {
        var blockOffset = BitConverter.ToUInt32(file, 8);
        var count = BitConverter.ToUInt32(file, 12);
        var at = 8 + (int)blockOffset;
        var blocks = new Dictionary<string, byte[]>();
        for (var i = 0; i < count; i++, at += 12)
        {
            var type = System.Text.Encoding.ASCII.GetString(file, at, 4);
            var offset = at + 4 + (int)BitConverter.ToUInt32(file, at + 4);
            var size = (int)BitConverter.ToUInt32(file, at + 8);
            var name = blocks.ContainsKey(type) ? $"{type}{i}" : type;
            blocks[name] = file.AsSpan(offset, size).ToArray();
        }
        return blocks;
    }

    [Fact]
    public void AgainstValve()
    {
        if (Environment.GetEnvironmentVariable("PARITY") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        var (addon, map, valvePath) = (p[0], p[1], p[2]);
        var switches = p.Length > 3 ? p[3] : "";
        var source = MapFixtures.VmapSource(addon, map) ?? throw new FileNotFoundException($"{addon}/{map}.vmap");
        var outRoot = Path.Combine(Path.GetTempPath(), "parity", map);
        var package = Path.Combine(outRoot, "csgo_addons", addon, "maps", map + ".vpk");

        // The .vwrld's compile time and fingerprint are Valve's (both change with
        // every compile, Valve's own included), so the score sees the rest.
        var valve = Io.VpkWriter.ReadAll(valvePath);
        if (valve.FirstOrDefault(kv => kv.Key.EndsWith("world.vwrld_c", StringComparison.OrdinalIgnoreCase)).Value is { } vwrld
            && WorldPhysicsAuthorTests.Trees(vwrld).Values.Select(t => (Find(t, "m_nCompileTimestamp"), Find(t, "m_nCompileFingerprint")))
                   .FirstOrDefault(x => x.Item1 is not null) is ({ } stamp, { } print))
            Environment.SetEnvironmentVariable("S2C_WORLD_STAMP", $"{(uint)stamp},{(ulong)print}");
        // PARITY_OURS=<vpk>: score a package already built (no compile).
        if (Environment.GetEnvironmentVariable("PARITY_OURS") is { Length: > 0 } built)
            package = built;
        else
        {
            var log = new StringWriter();
            var args = MapCompileArgs.Parse(["-i", source, "-outroot", outRoot, .. switches.Split(' ', StringSplitOptions.RemoveEmptyEntries)]);
            Directory.CreateDirectory(Path.GetDirectoryName(package)!);
            File.WriteAllBytes(package, Io.VpkWriter.Write(new Dictionary<string, byte[]>()));
            var started = DateTime.UtcNow;
            var code = MapCompile.Run(args, log, acceptGaps: true, skipUnported: true);
            output.WriteLine(log.ToString().TrimEnd());
            output.WriteLine($"compile-map {switches}: exit {code}, {(DateTime.UtcNow - started).TotalSeconds:F0} s");
        }
        Environment.SetEnvironmentVariable("S2C_WORLD_STAMP", null);
        var ours = Io.VpkWriter.ReadAll(package);
        var table = Score(valve, ours);
        output.WriteLine($"{map} against {Path.GetFileName(valvePath)}: role, files same/total (missing), bytes same/total");
        foreach (var (role, r) in table)
            output.WriteLine($"  {role,-14} {r.Same,5}/{r.Files,-5} ({r.Missing,4} missing)  {r.SameBytes,13:n0}/{r.Bytes,-13:n0} {100.0 * r.SameBytes / Math.Max(1, r.Bytes),6:F2}%");
        int files = table.Values.Sum(r => r.Files), same = table.Values.Sum(r => r.Same);
        long bytes = table.Values.Sum(r => r.Bytes), sameBytes = table.Values.Sum(r => r.SameBytes);
        var unlit = table.Where(kv => kv.Key is not ("lightmaps" or "cubemaps")).Select(kv => kv.Value).ToList();
        // Valve's own .nav differs between two compiles of the same map (probe01,
        // 2026-10-12: 9,580 against 9,590 bytes, apart from byte 5,421 on, the AI
        // data), so it is scored apart.
        var stable = table.Where(kv => kv.Key != "nav").Select(kv => kv.Value).ToList();
        var stableUnlit = table.Where(kv => kv.Key is not ("nav" or "lightmaps" or "cubemaps")).Select(kv => kv.Value).ToList();
        output.WriteLine($"  TOTAL          {same,5}/{files,-5}                 {sameBytes,13:n0}/{bytes,-13:n0} {100.0 * sameBytes / Math.Max(1, bytes),6:F2}%");
        output.WriteLine($"  without lighting outputs: files {unlit.Sum(r => r.Same)}/{unlit.Sum(r => r.Files)}, bytes "
                         + $"{100.0 * unlit.Sum(r => r.SameBytes) / Math.Max(1, unlit.Sum(r => r.Bytes)):F2}%");
        output.WriteLine($"  without nav (Valve's varies run to run): files {stable.Sum(r => r.Same)}/{stable.Sum(r => r.Files)}, bytes "
                         + $"{100.0 * stable.Sum(r => r.SameBytes) / Math.Max(1, stable.Sum(r => r.Bytes)):F2}%; also without lighting outputs: "
                         + $"{100.0 * stableUnlit.Sum(r => r.SameBytes) / Math.Max(1, stableUnlit.Sum(r => r.Bytes)):F2}%");
        var extra = ours.Keys.Except(valve.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        if (extra.Count > 0)
            output.WriteLine($"  ours only: {extra.Count} ({string.Join(", ", extra.Take(5))})");
        var list = int.TryParse(Environment.GetEnvironmentVariable("PARITY_LIST"), out var n) ? n : 15;
        foreach (var (path, bytesOf) in valve.Where(kv => !(ours.GetValueOrDefault(kv.Key)?.AsSpan().SequenceEqual(kv.Value) ?? false))
                                             .OrderByDescending(kv => kv.Value.Length).Take(list))
            output.WriteLine($"  miss {bytesOf.Length,12:n0}  {path}{(ours.ContainsKey(path) ? $" (ours {ours[path].Length:n0})" : " (not written)")}");
        // PARITY_WHY=1: each file we write that differs, by what differs in it
        // (tree fields by name, raw blocks by type), grouped with their bytes.
        if (Environment.GetEnvironmentVariable("PARITY_WHY") == "1")
        {
            var groups = new Dictionary<string, (int Files, long Bytes)>();
            foreach (var (path, theirs) in valve)
            {
                if (ours.GetValueOrDefault(path) is not { } mine || mine.AsSpan().SequenceEqual(theirs))
                    continue;
                var why = Why(theirs, mine);
                var key = $"{Role(path)}: {why}";
                if (Environment.GetEnvironmentVariable("PARITY_WHYFILES") is { Length: > 0 } pattern && why.Contains(pattern, StringComparison.Ordinal))
                    output.WriteLine($"  file {theirs.Length,10:n0} {path}: {why[..Math.Min(why.Length, 160)]}");
                var g = groups.GetValueOrDefault(key);
                groups[key] = (g.Files + 1, g.Bytes + theirs.Length);
            }
            foreach (var (k, g) in groups.OrderByDescending(kv => kv.Value.Bytes))
                output.WriteLine($"  why {g.Bytes,12:n0} in {g.Files,4} files: {k}");
        }
        if (Environment.GetEnvironmentVariable("PARITY_KEEP") != "1" && Environment.GetEnvironmentVariable("PARITY_OURS") is null)
            Directory.Delete(outRoot, true);
    }
}
