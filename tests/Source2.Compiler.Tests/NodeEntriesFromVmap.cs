using System.Numerics;
using System.Text.Json;
using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>NODEENTRIES=&lt;capture&gt;</c>, <c>NODEENTRIES_VMAP=&lt;vmap&gt;</c>):
/// the node mesh entries BuildNode starts from, as
/// <c>tools/vis/capture_nodeentries.py</c> recorded them, against the pieces
/// we build from the .vmap: which match, and for the rest the first stream
/// that differs.
/// </summary>
public class NodeEntriesFromVmap(ITestOutputHelper output)
{
    internal sealed record Captured(string Stage, int Index, string Material, int Stride, float[] Vertices, int[] Indices, string[] Streams, byte[] Raw)
    {
        /// <summary>Each stream's first float, float count and type, in order.</summary>
        public IReadOnlyList<(string Name, int First, int Count, int Type)> Layout { get; init; } = [];
    }

    /// <summary>The FGD's render_as_world_but_physics_as_entity, for <c>game</c> (the CS2 game folder).</summary>
    internal static Func<string, bool> RendersAsWorld(string game)
    {
        var schema = FgdSchema.Load(Path.Combine(game, "csgo", "csgo.fgd"), [Path.Combine(game, "core"), Path.Combine(game, "csgo")]);
        return c => schema.IsSolidClass(c) && schema.HasFlag(c, "render_as_world_but_physics_as_entity");
    }

    internal static List<Captured> Read(string path)
    {
        var data = File.ReadAllBytes(path);
        var list = new List<Captured>();
        for (var at = 0; at < data.Length;)
        {
            var n = BitConverter.ToInt32(data, at);
            var head = JsonDocument.Parse(data.AsMemory(at + 4, n)).RootElement;
            at += 4 + n;
            var m = BitConverter.ToInt32(data, at);
            var blob = data.AsSpan(at + 4, m).ToArray();
            at += 4 + m;
            if (head.GetProperty("ev").GetString() != "entry")
                continue;
            int nv = head.GetProperty("nv").GetInt32(), stride = head.GetProperty("stride").GetInt32(), ni = head.GetProperty("ni").GetInt32();
            var v = new float[nv * stride];
            Buffer.BlockCopy(blob, 0x238, v, 0, v.Length * 4);
            var idx = new int[ni];
            Buffer.BlockCopy(blob, 0x238 + v.Length * 4, idx, 0, ni * 4);
            list.Add(new Captured(head.GetProperty("stage").GetString()!, head.GetProperty("i").GetInt32(), head.GetProperty("material").GetString()!,
                                  stride, v, idx, [.. head.GetProperty("streams").EnumerateArray().Select(s => s.GetProperty("name").GetString()!)],
                                  blob.AsSpan(0, 0x238).ToArray())
            {
                Layout = [.. head.GetProperty("streams").EnumerateArray().Select(x => (x.GetProperty("name").GetString()!, x.GetProperty("first").GetInt32(),
                                                                                      x.GetProperty("count").GetInt32(), x.GetProperty("type").GetInt32()))],
            });
        }
        return list;
    }

    [Fact]
    public void BuildNodeInput()
    {
        if (Environment.GetEnvironmentVariable("NODEENTRIES") is not { } path || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap)
            return;
        var valve = Read(path).Where(c => c.Stage == "BuildNode:in").ToList();
        var ours = new List<(string Material, float[] V, int Stride, int NodeId)>();
        foreach (var mesh in MapMeshes.Read(DmxBinary.ReadFile(vmap)))
        {
            if (mesh.Element is null || mesh.Hidden || mesh.ParentType != "CMapWorld")
                continue;
            var names = mesh.Element.Get<DmxBinary.Element>("meshData")?.Get<object?[]>("materials")?.OfType<string>().ToArray() ?? [];
            var world = mesh.World;
            foreach (var piece in MapMeshCorners.Build(mesh.Element, true, p => MapMeshes.Transform(world, p), withTangent: true))
            {
                var v = (float[])piece.Vertices.Clone();
                var s = piece.Stride;
                for (var c = 0; c < v.Length / s; c++)
                {
                    var p = MapMeshes.Transform(world, new Vector3(v[c * s], v[c * s + 1], v[c * s + 2]));
                    (v[c * s], v[c * s + 1], v[c * s + 2]) = (p.X, p.Y, p.Z);
                    foreach (var st in piece.Streams.Where(x => x.Name is "normal" or "tangent"))
                    {
                        var d = Rotate(world, new Vector3(v[c * s + st.First], v[c * s + st.First + 1], v[c * s + st.First + 2]));
                        if (Environment.GetEnvironmentVariable("NODEENTRIES_NORMALISE") == "1")
                        {
                            var len = MathF.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
                            if (len != 0f)
                            {
                                var inv = 1f / len;
                                d = new Vector3(d.X * inv, d.Y * inv, d.Z * inv);
                            }
                        }
                        (v[c * s + st.First], v[c * s + st.First + 1], v[c * s + st.First + 2]) = (d.X, d.Y, d.Z);
                    }
                }
                if (Environment.GetEnvironmentVariable("NODEENTRIES_NODE") == mesh.NodeId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                {
                    output.WriteLine($"  node {mesh.NodeId} angles {mesh.Angles} matrix {string.Join(" ", world.Select(x => x.ToString("R")))}");
                    for (var c = 0; c < 3; c++)
                        output.WriteLine($"    corner {c} normal before {piece.Vertices[c * s + 5]:R},{piece.Vertices[c * s + 6]:R},{piece.Vertices[c * s + 7]:R} after {v[c * s + 5]:R},{v[c * s + 6]:R},{v[c * s + 7]:R}");
                }
                var name = piece.Material < names.Length ? names[piece.Material] : "?";
                ours.Add((name, v, s, mesh.NodeId));
                if (Environment.GetEnvironmentVariable("NODEENTRIES_RAW") is { } rawPath)
                {
                    var raw = (float[])piece.Vertices.Clone();
                    for (var c = 0; c < raw.Length / s; c++)
                        foreach (var st in piece.Streams.Where(x => x.Name is "normal" or "tangent"))
                        {
                            var d = Rotate(world, new Vector3(raw[c * s + st.First], raw[c * s + st.First + 1], raw[c * s + st.First + 2]));
                            (raw[c * s + st.First], raw[c * s + st.First + 1], raw[c * s + st.First + 2]) = (d.X, d.Y, d.Z);
                        }
                    Raw.Add(raw);
                }
            }
        }
        output.WriteLine($"valve {valve.Count} entries, ours {ours.Count} pieces");
        if (Environment.GetEnvironmentVariable("NODEENTRIES_DETAIL") == "1")
        {
            var first = MapMeshes.Read(DmxBinary.ReadFile(vmap)).First(m => m.NodeId == 57).Element!;
            foreach (var st in first.Get<DmxBinary.Element>("meshData")!.Get<DmxBinary.Element>("faceVertexData")!.GetElements("streams"))
                output.WriteLine($"  node 57 faceVertexData stream {st.Name}: {st.Get<object?[]>("data")?.Length} values, first {string.Join(" ", (st.Get<object?[]>("data") ?? []).Take(3).Select(x => x?.ToString()))}");
        }
        var used = new HashSet<int>();
        foreach (var e in valve)
        {
            var key = Positions(e.Vertices, e.Stride);
            var j = ours.FindIndex(o => !used.Contains(ours.IndexOf(o)) && string.Equals(o.Material, e.Material, StringComparison.OrdinalIgnoreCase)
                                        && Positions(o.V, o.Stride).SequenceEqual(key));
            if (j < 0)
            {
                output.WriteLine($"entry {e.Index} {e.Material} {e.Vertices.Length / e.Stride}v: no piece with the same corners");
                continue;
            }
            used.Add(j);
            var o = ours[j];
            var diff = "same";
            if (o.Stride != e.Stride)
                diff = $"stride {o.Stride} vs {e.Stride} [{string.Join(",", e.Streams)}]";
            else if (o.V.Length != e.Vertices.Length)
                diff = $"{o.V.Length / o.Stride} vs {e.Vertices.Length / e.Stride} corners";
            else
                for (var k = 0; k < o.V.Length; k++)
                    if (BitConverter.SingleToInt32Bits(o.V[k]) != BitConverter.SingleToInt32Bits(e.Vertices[k]))
                    {
                        diff = $"float {k % e.Stride} of corner {k / e.Stride}: ours {o.V[k]:R} valve {e.Vertices[k]:R}";
                        break;
                    }
            output.WriteLine($"entry {e.Index} {Path.GetFileName(e.Material)} node {o.NodeId} (piece {j}): {diff}");
            if (Environment.GetEnvironmentVariable("NODEENTRIES_RAW") is { } rp && o.V.Length == e.Vertices.Length)
                using (var w = File.AppendText(rp))
                    for (var c = 0; c < e.Vertices.Length / e.Stride; c++)
                        foreach (var f in new[] { 5, 8 })
                            w.WriteLine(string.Join(" ", Enumerable.Range(0, 3).Select(k => BitConverter.SingleToInt32Bits(Raw[j][c * e.Stride + f + k]).ToString(System.Globalization.CultureInfo.InvariantCulture))
                                .Concat(Enumerable.Range(0, 3).Select(k => BitConverter.SingleToInt32Bits(e.Vertices[c * e.Stride + f + k]).ToString(System.Globalization.CultureInfo.InvariantCulture)))));
            if (Environment.GetEnvironmentVariable("NODEENTRIES_NODE") == o.NodeId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                for (var c = 0; c < 3; c++)
                    output.WriteLine($"    valve corner {c} normal {e.Vertices[c * e.Stride + 5]:R},{e.Vertices[c * e.Stride + 6]:R},{e.Vertices[c * e.Stride + 7]:R}");
            if (o.Stride == e.Stride && o.V.Length == e.Vertices.Length && Environment.GetEnvironmentVariable("NODEENTRIES_DETAIL") == "1")
            {
                var byFloat = Enumerable.Range(0, e.Stride).Select(f => Enumerable.Range(0, e.Vertices.Length / e.Stride)
                    .Count(c => BitConverter.SingleToInt32Bits(o.V[c * e.Stride + f]) != BitConverter.SingleToInt32Bits(e.Vertices[c * e.Stride + f]))).ToArray();
                output.WriteLine($"    differing corners per float: {string.Join(" ", byFloat)}");
                var firstPos = Enumerable.Range(0, e.Vertices.Length / e.Stride).FirstOrDefault(c => Enumerable.Range(0, 3).Any(k =>
                    BitConverter.SingleToInt32Bits(o.V[c * e.Stride + k]) != BitConverter.SingleToInt32Bits(e.Vertices[c * e.Stride + k])), -1);
                if (firstPos >= 0)
                    output.WriteLine($"    positions differ in order from corner {firstPos}: ours {string.Join(" ", Enumerable.Range(firstPos - firstPos % 3, 6).Select(c => $"({o.V[c * e.Stride]},{o.V[c * e.Stride + 1]},{o.V[c * e.Stride + 2]})"))}"
                                     + $" valve {string.Join(" ", Enumerable.Range(firstPos - firstPos % 3, 6).Select(c => $"({e.Vertices[c * e.Stride]},{e.Vertices[c * e.Stride + 1]},{e.Vertices[c * e.Stride + 2]})"))}");
                if (e.Stride > 12)
                    output.WriteLine($"    valve PerVertexLighting of corners 0..3: {string.Join(" | ", Enumerable.Range(0, 4).Select(c => string.Join(",", e.Vertices.Skip(c * e.Stride + 12).Take(4).Select(x => $"{x * 255:0.##}"))))}");
            }
        }
    }

    /// <summary>
    /// <see cref="NodeMeshEntries.FromWorld"/> against the captured BuildNode
    /// input: same entries in the same order, every float of every corner the
    /// same bar PerVertexLighting (baked lighting, an input).
    /// </summary>
    [Fact]
    public void LibraryMatchesBuildNodeInput()
    {
        if (Environment.GetEnvironmentVariable("NODEENTRIES") is not { } path || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap)
            return;
        var valve = Read(path).Where(c => c.Stage == "BuildNode:in").ToList();
        if (CS2Fixtures.StockPak() is not { } pak)
            return;
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var addon = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(vmap)))!;
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", addon));
        var signatures = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyCollection<string> Signature(string material)
        {
            if (!signatures.TryGetValue(material, out var sig))
                signatures[material] = sig = content.Read(material + "_c") is { } b
                    ? [.. Source2.Compiler.MaterialAuthor.ExtractInputSignature(b).Select(x => x.Semantic)] : [];
            return sig;
        }
        var ours = NodeMeshEntries.FromWorld(DmxBinary.ReadFile(vmap), signature: Signature, rendersAsWorld: NodeEntriesFromVmap.RendersAsWorld(game));
        var problems = new List<string>();
        if (ours.Count != valve.Count)
            problems.Add($"{ours.Count} entries, valve {valve.Count}");
        for (var i = 0; i < Math.Min(ours.Count, valve.Count); i++)
        {
            var (o, e) = (ours[i], valve[i]);
            if (!string.Equals(o.Material, e.Material, StringComparison.OrdinalIgnoreCase) || o.Stride != e.Stride || o.Vertices.Length != e.Vertices.Length
                || !o.Indices.SequenceEqual(e.Indices))
            {
                problems.Add($"entry {i}: {o.Material} {o.Vertices.Length / o.Stride}v stride {o.Stride}, valve {e.Material} {e.Vertices.Length / e.Stride}v stride {e.Stride}");
                continue;
            }
            var pvl = o.Streams.Where(x => x.Name == "PerVertexLighting").Select(x => (First: x.First, Count: x.Count)).FirstOrDefault((First: -1, Count: 0));
            var diff = Enumerable.Range(0, o.Vertices.Length).Where(k => k % o.Stride < pvl.First || k % o.Stride >= pvl.First + pvl.Count)
                .Count(k => BitConverter.SingleToInt32Bits(o.Vertices[k]) != BitConverter.SingleToInt32Bits(e.Vertices[k]));
            if (diff > 0)
                problems.Add($"entry {i} {Path.GetFileName(o.Material)} node {o.NodeId}: {diff} floats differ");
        }
        // Aligned by material and corner positions, for maps where some entries are missing.
        var byKey = ours.Select((o, i) => (o, i)).GroupBy(x => (x.o.Material.ToLowerInvariant(), string.Join(";", Positions(x.o.Vertices, x.o.Stride))))
            .ToDictionary(g => g.Key, g => g.Select(x => x.i).ToList());
        int aligned = 0, alignedExact = 0, unmatched = 0;
        var floatDiffs = new SortedDictionary<string, int>();
        foreach (var e in valve)
        {
            if (!byKey.TryGetValue((e.Material.ToLowerInvariant(), string.Join(";", Positions(e.Vertices, e.Stride))), out var idx) || idx.Count == 0)
            {
                unmatched++;
                continue;
            }
            var o = ours[idx[0]];
            idx.RemoveAt(0);
            aligned++;
            if (o.Stride != e.Stride)
            {
                floatDiffs["stride"] = floatDiffs.GetValueOrDefault("stride") + 1;
                continue;
            }
            var pvl2 = o.Streams.Where(x => x.Name == "PerVertexLighting").Select(x => (First: x.First, Count: x.Count)).FirstOrDefault((First: -1, Count: 0));
            var bad = Enumerable.Range(0, o.Vertices.Length).Where(k => (k % o.Stride < pvl2.First || k % o.Stride >= pvl2.First + pvl2.Count)
                && BitConverter.SingleToInt32Bits(o.Vertices[k]) != BitConverter.SingleToInt32Bits(e.Vertices[k])).ToList();
            if (bad.Count == 0 && o.Indices.SequenceEqual(e.Indices))
                alignedExact++;
            else
            {
                var stream = bad.Count == 0 ? "indices" : o.Streams.Last(x => x.First <= bad[0] % o.Stride).Name;
                if (bad.Count > 0 && Environment.GetEnvironmentVariable("NODEENTRIES_ALLDIFFS") == "1")
                    foreach (var k in bad)
                    {
                        var st = o.Streams.Last(x => x.First <= k % o.Stride);
                        var c = k / o.Stride;
                        string Z(float x) => BitConverter.SingleToInt32Bits(x) == int.MinValue ? "-0" : x.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                        output.WriteLine($"  diff {Path.GetFileName(e.Material)} node {o.NodeId} float {k % o.Stride}: ours {Z(o.Vertices[k])} valve {Z(e.Vertices[k])}; stored {st.Name} {string.Join(",", o.Stored.Skip(c * o.Stride + st.First).Take(3).Select(Z))}; turn {string.Join(" ", o.Turn.Select(Z))}");
                    }
                if (bad.Count > 0 && floatDiffs.GetValueOrDefault(stream) < 2)
                {
                    var c = bad[0] / o.Stride;
                    var st = o.Streams.Last(x => x.First <= bad[0] % o.Stride);
                    output.WriteLine($"  {Path.GetFileName(e.Material)} node {o.NodeId} corner {c} {st.Name}: ours {string.Join(",", o.Vertices.Skip(c * o.Stride + st.First).Take(st.Count).Select(x => x.ToString("R")))} valve {string.Join(",", e.Vertices.Skip(c * e.Stride + st.First).Take(st.Count).Select(x => x.ToString("R")))}");
                }
                floatDiffs[stream] = floatDiffs.GetValueOrDefault(stream) + 1;
            }
        }
        output.WriteLine($"aligned {aligned} of {valve.Count} valve entries ({unmatched} without a piece with the same corners), {alignedExact} exact; first differing stream: {string.Join(", ", floatDiffs.Select(kv => $"{kv.Key} {kv.Value}"))}");
        output.WriteLine($"{ours.Count} entries, {problems.Count} problems");
        foreach (var p in problems.Take(int.Parse(Environment.GetEnvironmentVariable("NODEENTRIES_PROBLEMS") ?? "20")))
            output.WriteLine("  " + p);
        Assert.Empty(problems);
    }

    /// <summary>Exploration (<c>NODEENTRIES_STREAMS=1</c>): each world mesh's faceVertexData stream list, tallied.</summary>
    [Fact]
    public void VmapStreamLists()
    {
        if (Environment.GetEnvironmentVariable("NODEENTRIES_STREAMS") != "1" || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap)
            return;
        var tally = new SortedDictionary<string, int>();
        foreach (var mesh in MapMeshes.Read(DmxBinary.ReadFile(vmap)).Where(m => m.Element != null && !m.Hidden && m.ParentType == "CMapWorld"))
        {
            var key = string.Join(" ", mesh.Element!.Get<DmxBinary.Element>("meshData")!.Get<DmxBinary.Element>("faceVertexData")!.GetElements("streams").Select(s => s.Name));
            var sub = mesh.Element.Get<DmxBinary.Element>("meshData")!.Get<DmxBinary.Element>("subdivisionData") is { } sd && (sd.GetElements("streams").Any()) ? " +subdiv" : "";
            tally[key + sub] = tally.GetValueOrDefault(key + sub) + 1;
        }
        foreach (var (k, v) in tally)
            output.WriteLine($"{v,5} {k}");
    }

    /// <summary>
    /// Exploration (<c>NODEENTRIES_INSG=1</c> with <c>NODEENTRIES</c>): each
    /// captured entry's stream layout beside its material's INSG and the
    /// .vmap mesh's own streams, tallied.
    /// </summary>
    [Fact]
    public void LayoutAgainstInputSignature()
    {
        if (Environment.GetEnvironmentVariable("NODEENTRIES_INSG") != "1" || Environment.GetEnvironmentVariable("NODEENTRIES") is not { } path
            || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap || CS2Fixtures.StockPak() is not { } pak)
            return;
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        var addon = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(vmap)))!;
        using var content = new GameContent(pak, Path.Combine(game, "csgo_addons", addon));
        var valve = Read(path).Where(c => c.Stage == "BuildNode:in").ToList();
        var ours = NodeMeshEntries.FromWorld(DmxBinary.ReadFile(vmap));
        var tally = new SortedDictionary<string, int>();
        for (var i = 0; i < valve.Count; i++)
        {
            var e = valve[i];
            var bytes = content.Read(e.Material + "_c");
            var insg = bytes == null ? "(no vmat_c)" : string.Join(",", Source2.Compiler.MaterialAuthor.ExtractInputSignature(bytes)
                .Select(x => x.Semantic).Where(x => x is not ("PosXyz" or "Normal" or "Tangent" or "LowPrecisionUv")).Distinct());
            var match = ours.FirstOrDefault(o => string.Equals(o.Material, e.Material, StringComparison.OrdinalIgnoreCase)
                                                 && o.Vertices.Length / o.Stride == e.Vertices.Length / e.Stride);
            var vmapStreams = match is null ? "?" : string.Join(" ", match.Streams.Skip(1).Select(x => x.Name));
            var key = $"valve [{string.Join(" ", e.Streams.Skip(1))}] | vmap [{vmapStreams}] | insg [{insg}]";
            tally[key] = tally.GetValueOrDefault(key) + 1;
        }
        foreach (var (k, v) in tally)
            output.WriteLine($"{v,4} {k}");

        // Per mesh: the union of its materials' signatures against the layout
        // Valve gave its pieces.
        string[] Insg(string material) => content.Read(material + "_c") is { } b
            ? [.. Source2.Compiler.MaterialAuthor.ExtractInputSignature(b).Select(x => x.Semantic)] : [];
        var byNode = ours.GroupBy(o => o.NodeId).ToList();
        int agree = 0, disagree = 0;
        foreach (var g in byNode)
        {
            var union = g.SelectMany(o => Insg(o.Material)).ToHashSet();
            var predicted = new List<string> { "texcoord" };
            if (union.Contains("LowPrecisionUv1")) predicted.Add("texcoord");
            var layouts = g.Select(o => valve.FirstOrDefault(e => string.Equals(e.Material, o.Material, StringComparison.OrdinalIgnoreCase)
                                                              && e.Vertices.Length / e.Stride == o.Vertices.Length / o.Stride))
                          .Where(e => e != null).Select(e => string.Join(" ", e!.Streams.Skip(1))).Distinct().ToList();
            if (layouts.Count == 0)
                continue;
            var v = layouts[0];
            var ok = v.Contains("texcoord texcoord") == union.Contains("LowPrecisionUv1")
                     && v.Contains("VertexPaintTintColor") == union.Contains("VertexPaintTintColor")
                     && v.Contains("VertexPaintBlendParams") == union.Contains("VertexPaintBlendParams");
            if (ok) agree++;
            else
            {
                disagree++;
                if (disagree <= 8)
                    output.WriteLine($"node {g.Key}: valve [{string.Join(" | ", layouts)}], union has uv1 {union.Contains("LowPrecisionUv1")} tint {union.Contains("VertexPaintTintColor")} blend {union.Contains("VertexPaintBlendParams")}; materials {string.Join(",", g.Select(o => Path.GetFileNameWithoutExtension(o.Material)).Distinct())}");
            }
            if (layouts.Count > 1)
                output.WriteLine($"node {g.Key}: pieces with different layouts {string.Join(" | ", layouts)}");
        }
        output.WriteLine($"per mesh: {agree} agree with the union rule, {disagree} do not");
        var byShader = new SortedDictionary<string, int>();
        foreach (var e in valve)
        {
            var shader = content.Material(e.Material)?.Shader ?? "?";
            var k = $"{Path.GetFileName(shader)}: {string.Join(" ", e.Streams.Skip(4))}";
            byShader[k] = byShader.GetValueOrDefault(k) + 1;
        }
        foreach (var (k, v) in byShader)
            output.WriteLine($"  shader {v,4} {k}");

        // Matched by exact corner positions: the mesh's own .vmap streams.
        var doc = DmxBinary.ReadFile(vmap);
        var meshes = MapMeshes.Read(doc).Where(m => m.Element != null && !m.Hidden && m.ParentType == "CMapWorld").ToDictionary(m => m.NodeId);
        var byPos = ours.GroupBy(o => (o.Material.ToLowerInvariant(), string.Join(";", Positions(o.Vertices, o.Stride)))).ToDictionary(g => g.Key, g => g.First());
        var rule = new SortedDictionary<string, int>();
        foreach (var e in valve)
        {
            var key = (e.Material.ToLowerInvariant(), string.Join(";", Positions(e.Vertices, e.Stride)));
            if (!byPos.TryGetValue(key, out var o))
            {
                rule["(no positional match)"] = rule.GetValueOrDefault("(no positional match)") + 1;
                continue;
            }
            var own = string.Join(" ", meshes[o.NodeId].Element!.Get<DmxBinary.Element>("meshData")!.Get<DmxBinary.Element>("faceVertexData")!.GetElements("streams").Select(x => x.Name.Split(':')[0] + x.Name.Split(':')[1]));
            var sig = Insg(e.Material);
            var info = content.Material(e.Material);
            var k = $"valve [{string.Join(" ", e.Streams.Skip(1))}] <- vmap [{own}] | {Path.GetFileName(info?.Shader ?? "?")} uv1 {sig.Contains("LowPrecisionUv1")} blend {sig.Contains("VertexPaintBlendParams")} tint {sig.Contains("VertexPaintTintColor")}";
            rule[k] = rule.GetValueOrDefault(k) + 1;
        }
        foreach (var (k, v) in rule)
            output.WriteLine($"  pos {v,4} {k}");

        // The candidate rule, per mesh.
        int ruleOk = 0, ruleBad = 0;
        foreach (var e in valve)
        {
            var key = (e.Material.ToLowerInvariant(), string.Join(";", Positions(e.Vertices, e.Stride)));
            if (!byPos.TryGetValue(key, out var o))
                continue;
            var own = meshes[o.NodeId].Element!.Get<DmxBinary.Element>("meshData")!.Get<DmxBinary.Element>("faceVertexData")!.GetElements("streams")
                .Select(x => x.Name.Split(':')[0]).ToList();
            var mats = ours.Where(x => x.NodeId == o.NodeId).Select(x => x.Material).Distinct().ToList();
            var needs = mats.SelectMany(Insg).ToHashSet();
            var blend = own.Contains("VertexPaintBlendParams") || needs.Contains("LowPrecisionUv1") || needs.Contains("VertexPaintBlendParams");
            var predicted = new List<string>(own);
            if (blend && own.Count(x => x == "texcoord") < 2)
                predicted.Insert(predicted.IndexOf("texcoord") + 1, "texcoord");
            if (blend && !predicted.Contains("VertexPaintBlendParams"))
                predicted.Add("VertexPaintBlendParams");
            if (string.Join(" ", predicted) == string.Join(" ", e.Streams.Skip(1)))
                ruleOk++;
            else if (ruleBad++ < 6)
                output.WriteLine($"  rule miss node {o.NodeId} {Path.GetFileName(e.Material)}: predicted [{string.Join(" ", predicted)}] valve [{string.Join(" ", e.Streams.Skip(1))}]; mesh materials {string.Join(",", mats.Select(Path.GetFileNameWithoutExtension))}");
        }
        output.WriteLine($"  rule: {ruleOk} entries right, {ruleBad} wrong");
    }

    /// <summary>Exploration (<c>NODEENTRIES_ZERO=node:corner,...</c>): a corner's stored tangent and normal, the node's angles and matrices.</summary>
    [Fact]
    public void SignedZeros()
    {
        if (Environment.GetEnvironmentVariable("NODEENTRIES_ZERO") is not { } spec || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap)
            return;
        var meshes = MapMeshes.Read(DmxBinary.ReadFile(vmap)).Where(m => m.Element != null).GroupBy(m => m.NodeId).ToDictionary(g => g.Key, g => g.First());
        string F(IEnumerable<float> a) => string.Join(" ", a.Select(x => BitConverter.SingleToInt32Bits(x) == int.MinValue ? "-0" : x.ToString("G4", System.Globalization.CultureInfo.InvariantCulture)));
        foreach (var item in spec.Split(','))
        {
            var parts = item.Split(':');
            var node = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
            var corner = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            if (!meshes.TryGetValue(node, out var m))
            {
                output.WriteLine($"node {node}: not a mesh");
                continue;
            }
            output.WriteLine($"node {node} angles {m.Angles} origin {m.Origin} instances {m.Instances.Length} parent {m.ParentType}");
            output.WriteLine($"  local {F(MapMeshes.Local(m.Element!))}");
            output.WriteLine($"  world {F(m.World)}");
            foreach (var piece in MapMeshCorners.Build(m.Element!, true, null, withTangent: true))
            {
                if (corner * piece.Stride >= piece.Vertices.Length)
                    continue;
                var stored = piece.Vertices.Skip(corner * piece.Stride).Take(piece.Stride).ToArray();
                output.WriteLine($"  piece material {piece.Material} corner {corner}: normal {F(stored.Skip(5).Take(3))} tangent {F(stored.Skip(8).Take(4))}");
            }
        }
    }

    static readonly List<float[]> Raw = [];

    static IEnumerable<(float, float, float)> Positions(float[] v, int stride)
        => Enumerable.Range(0, v.Length / stride).Select(c => (v[c * stride], v[c * stride + 1], v[c * stride + 2])).Order();

    static Vector3 Rotate(float[] m, Vector3 d)
        => new(m[0] * d.X + m[1] * d.Y + m[2] * d.Z, m[4] * d.X + m[5] * d.Y + m[6] * d.Z, m[8] * d.X + m[9] * d.Y + m[10] * d.Z);

    /// <summary>
    /// Exploration (<c>NODEENTRIES_SUBDIV=1</c> with <c>NODEENTRIES</c>):
    /// Valve's BuildNode input for each subdivided world mesh beside the bake
    /// (<see cref="SubdivisionBake"/>): corner counts, positions, and the
    /// first corners' streams.
    /// </summary>
    [Fact]
    public void SubdividedEntries()
    {
        if (Environment.GetEnvironmentVariable("NODEENTRIES_SUBDIV") != "1" || Environment.GetEnvironmentVariable("NODEENTRIES") is not { } path
            || Environment.GetEnvironmentVariable("NODEENTRIES_VMAP") is not { } vmap)
            return;
        var valve = Read(path).Where(c => c.Stage == "BuildNode:in").ToList();
        string F(float x) => BitConverter.SingleToInt32Bits(x) == int.MinValue ? "-0" : x.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var shown = 0;
        foreach (var mesh in MapMeshes.Read(DmxBinary.ReadFile(vmap)).Where(m => m.Element != null && !m.Hidden && m.ParentType == "CMapWorld"))
        {
            var data = mesh.Element!.Get<DmxBinary.Element>("meshData")!;
            var a = new FaceArrays(data);
            if (!a.First.Any(h => a.Level(h) > 0))
                continue;
            var world = mesh.World;
            var fvdFull = data.Get<DmxBinary.Element>("faceVertexData")!.GetElements("streams").Select(x => (Full: x.Name, Name: x.Name.Split(':')[0], Data: x.Get<object?[]>("data") ?? [])).ToList();
            var fvd = fvdFull.Select(x => (x.Name, x.Data)).ToList();
            static float[] Floats(object? x) => FaceArrays.Floats(x);
            var layout = fvdFull.Select(x => (x.Full, x.Data.Length > 0 ? Floats(x.Data[0]).Length : 0)).ToList();
            var bake = SubdivisionBake.Bake(data, p => MapMeshes.Transform(world, p), d => [.. fvd.SelectMany(x => d >= 0 && d < x.Data.Length ? Floats(x.Data[d]) : [])], layout: layout, smoothingAngle: mesh.Element.GetValue<float>("smoothingAngle") ?? 40f);
            var mine = valve.Where(e => BitConverter.ToInt32(e.Raw, 0x40) == mesh.NodeId).ToList();
            var levels = string.Join(",", a.First.Select(h => a.Level(h)).Distinct());
            output.WriteLine($"node {mesh.NodeId}: {a.First.Length} faces, levels {levels}; bake {bake.Indices.Count} corners; valve {mine.Count} entries {string.Join(" ", mine.Select(e => $"{Path.GetFileName(e.Material)}:{e.Vertices.Length / e.Stride}c[{string.Join(",", e.Streams)}]"))}");
            if (shown++ >= 4 || mine.Count == 0)
                continue;
            var e0 = mine[0];
            var bakePos = bake.Indices.Select(k => MapMeshes.Transform(world, bake.Positions[k])).ToList();
            var valvePos = Enumerable.Range(0, e0.Vertices.Length / e0.Stride).Select(c => new System.Numerics.Vector3(e0.Vertices[c * e0.Stride], e0.Vertices[c * e0.Stride + 1], e0.Vertices[c * e0.Stride + 2])).ToList();
            var same = valvePos.Count(v => bakePos.Contains(v));
            output.WriteLine($"  {same} of {valvePos.Count} valve corner positions are bake positions, {Enumerable.Range(0, Math.Min(valvePos.Count, bakePos.Count)).Count(c => valvePos[c] == bakePos[c])} in order");
            var flatBake = SubdivisionBake.Bake(data, p => MapMeshes.Transform(world, p), displace: false);
            var flat = flatBake.Indices.Select(k => MapMeshes.Transform(world, flatBake.Positions[k])).ToList();
            if (Environment.GetEnvironmentVariable("NODEENTRIES_SUBDIV_DUMP") is { } dump)
                File.WriteAllLines(Path.Combine(dump, $"node{mesh.NodeId}.csv"), Enumerable.Range(0, valvePos.Count).Select(c =>
                    $"{bake.Faces[c / 3]},{string.Join(",", e0.Vertices.Skip(c * e0.Stride).Take(e0.Stride).Select(F))},{F(flat[c].X)},{F(flat[c].Y)},{F(flat[c].Z)}"));
            // Tangents: the carried tangent orthogonalised against the
            // renormalised normal, two ways, against Valve's.
            {
                int nAt = 0, tAt = 0, off = 0;
                foreach (var (name, values) in fvd)
                {
                    var w = values.Length > 0 ? Floats(values[0]).Length : 0;
                    if (name == "normal") nAt = off;
                    if (name == "tangent") tAt = off;
                    off += w;
                }
                var vt = Array.IndexOf(e0.Streams, "tangent");
                if (vt >= 0)
                {
                    int gs = 0, cr = 0, raw = 0, cnt = Math.Min(valvePos.Count, bake.CornerData!.Count);
                    string firstBad = "";
                    for (var c = 0; c < cnt; c++)
                    {
                        var d = bake.CornerData[c];
                        var n = NodeMeshEntries.Normalise(new System.Numerics.Vector3(d[nAt], d[nAt + 1], d[nAt + 2]));
                        var t = new System.Numerics.Vector3(d[tAt], d[tAt + 1], d[tAt + 2]);
                        var dot = (n.X * t.X) + (n.Y * t.Y) + (n.Z * t.Z);
                        var g1 = NodeMeshEntries.Normalise(new System.Numerics.Vector3(t.X - (n.X * dot), t.Y - (n.Y * dot), t.Z - (n.Z * dot)));
                        var v = new System.Numerics.Vector3((n.Y * t.Z) - (n.Z * t.Y), (n.Z * t.X) - (n.X * t.Z), (n.X * t.Y) - (n.Y * t.X));
                        var g2 = NodeMeshEntries.Normalise(new System.Numerics.Vector3((v.Y * n.Z) - (v.Z * n.Y), (v.Z * n.X) - (v.X * n.Z), (v.X * n.Y) - (v.Y * n.X)));
                        var want = new System.Numerics.Vector3(e0.Vertices[c * e0.Stride + e0.Layout[vt].First], e0.Vertices[c * e0.Stride + e0.Layout[vt].First + 1], e0.Vertices[c * e0.Stride + e0.Layout[vt].First + 2]);
                        if (g1 == want) gs++;
                        if (g2 == want) cr++;
                        if (NodeMeshEntries.Normalise(t) == want) raw++;
                        else if (firstBad.Length == 0 && g1 != want && g2 != want)
                            firstBad = $" first corner {c}: carried {t} gs {g1} cross {g2} valve {want}";
                    }
                    output.WriteLine($"  tangent candidates: carried {raw}, gram-schmidt {gs}, cross {cr} of {cnt}{firstBad}");
                }
            }
            // Each carried stream against Valve's stream of that name (first occurrence).
            var at = 0;
            foreach (var (name, values) in fvd)
            {
                var width = values.Length > 0 ? Floats(values[0]).Length : 0;
                var vi = Array.IndexOf(e0.Streams, name);
                if (vi >= 0 && e0.Layout.Count > vi)
                {
                    var vf = e0.Layout[vi].First;
                    int eq = 0, shifted = 0, n = Math.Min(valvePos.Count, bake.CornerData!.Count);
                    string first = "";
                    for (var c = 0; c < n; c++)
                    {
                        var ours = bake.CornerData[c].Skip(at).Take(width).ToArray();
                        var theirs = e0.Vertices.Skip(c * e0.Stride + vf).Take(width).ToArray();
                        if (ours.SequenceEqual(theirs))
                            eq++;
                        else if (ours.Length == theirs.Length && ours.Zip(theirs).All(z => (z.First - z.Second) == MathF.Round(z.First - z.Second)))
                            shifted++;
                        else if (first.Length == 0)
                            first = $" first corner {c}: ours {string.Join(",", ours.Select(F))} valve {string.Join(",", theirs.Select(F))}";
                    }
                    var maxDiff = Enumerable.Range(0, n).Max(c => bake.CornerData[c].Skip(at).Take(width).Zip(e0.Vertices.Skip(c * e0.Stride + vf).Take(width)).Select(z => MathF.Abs(z.First - z.Second)).DefaultIfEmpty(0f).Max());
                    output.WriteLine($"  {name}: {eq} of {n} equal, {shifted} by whole numbers, max diff {maxDiff}{first}");
                }
                at += width;
            }
            for (var c = 0; c < Math.Min(6, valvePos.Count); c++)
                output.WriteLine($"  corner {c}: {string.Join(" ", e0.Vertices.Skip(c * e0.Stride).Take(e0.Stride).Select(F))}");
        }
    }
}
