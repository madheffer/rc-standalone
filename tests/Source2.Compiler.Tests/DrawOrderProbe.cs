using System.Numerics;
using System.Text.Json;
using Source2.Compiler.Maps;
using Source2.Compiler.Meshopt;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>DRAWORDER=&lt;merger capture&gt;|&lt;compiled map vpk&gt;</c>):
/// each worldnodes model draw beside the merger output entry holding its
/// triangles, that entry through first-use renumbering, the vertex cache
/// and overdraw passes (<see cref="MeshoptOptimizers"/>), triangle order
/// compared by position triples (a triangle's corners may be rotated by the
/// index codec).
/// </summary>
public class DrawOrderProbe(ITestOutputHelper output)
{
    [Fact]
    public void DrawsFromMergerOutput()
    {
        if (Environment.GetEnvironmentVariable("DRAWORDER") is not { } spec)
            return;
        var p = spec.Split('|');
        var data = File.ReadAllBytes(p[0]);
        var outs = new List<VisibilityMeshMerger.Mesh>();
        var outStreams = new List<List<Physics.MeshWeld.Stream>>();
        for (var at = 0; at < data.Length;)
        {
            var n = BitConverter.ToInt32(data, at);
            var head = JsonDocument.Parse(data.AsMemory(at + 4, n)).RootElement;
            at += 4 + n;
            var m = BitConverter.ToInt32(data, at);
            var blob = data.AsSpan(at + 4, m).ToArray();
            at += 4 + m;
            var ev = head.GetProperty("ev").GetString();
            if (ev is "out" or "nov")
            {
                outs.Add(VisibilityMeshMergerReplay.Mesh(head, blob).Item1);
                outStreams.Add([.. head.GetProperty("streams").EnumerateArray().Select(x => new Physics.MeshWeld.Stream(x.GetProperty("name").GetString() ?? "",
                    x.GetProperty("first").GetInt32(), x.GetProperty("count").GetInt32(), false, x.GetProperty("type").GetInt32()))]);
            }
        }
        // Each output entry's triangles keyed by their sorted position triple.
        static string Key(Vector3 a, Vector3 b, Vector3 c)
        {
            var s = new[] { a, b, c }.Select(v => $"{BitConverter.SingleToInt32Bits(v.X):x8}{BitConverter.SingleToInt32Bits(v.Y):x8}{BitConverter.SingleToInt32Bits(v.Z):x8}").Order();
            return string.Join("|", s);
        }
        static Vector3 Pos(VisibilityMeshMerger.Mesh mesh, int v) => new(mesh.Vertices[(v * mesh.Stride) + mesh.PositionOffset], mesh.Vertices[(v * mesh.Stride) + mesh.PositionOffset + 1], mesh.Vertices[(v * mesh.Stride) + mesh.PositionOffset + 2]);
        var owner = new Dictionary<string, int>();
        for (var e = 0; e < outs.Count; e++)
            for (var t = 0; t < outs[e].Indices.Count / 3; t++)
                owner.TryAdd(Key(Pos(outs[e], outs[e].Indices[t * 3]), Pos(outs[e], outs[e].Indices[(t * 3) + 1]), Pos(outs[e], outs[e].Indices[(t * 3) + 2])), e);
        using var package = new ValvePak.Package();
        package.Read(p[1]);
        int draws = 0, exact = 0, unmatched = 0, shownPlain = 0;
        var tally = new SortedDictionary<string, int>();
        foreach (var entry in package.Entries!.GetValueOrDefault("vmdl_c") ?? [])
        {
            if (!entry.DirectoryName.Contains("worldnodes", StringComparison.OrdinalIgnoreCase))
                continue;
            package.ReadEntry(entry, out var bytes);
            using var resource = new Resource();
            resource.Read(new MemoryStream(bytes));
            var model = (Model)resource.DataBlock!;
            foreach (var (mesh, _, _, _) in model.GetEmbeddedMeshesAndLoD())
            {
                var vbib = mesh.VBIB;
                foreach (var so in mesh.Data.GetArray("m_sceneObjects"))
                    foreach (var dc in so.GetArray("m_drawCalls"))
                    {
                        draws++;
                        var vb = vbib.VertexBuffers[dc.GetArray("m_vertexBuffers")[0].GetInt32Property("m_hBuffer")];
                        var ib = vbib.IndexBuffers[dc.GetSubCollection("m_indexBuffer").GetInt32Property("m_hBuffer")];
                        int Index(int i) => ib.ElementSizeInBytes == 2 ? BitConverter.ToUInt16(ib.Data, i * 2) : BitConverter.ToInt32(ib.Data, i * 4);
                        var bas = dc.GetInt32Property("m_nBaseVertex");
                        Vector3 V(int i)
                        {
                            var at = (int)((i + bas) * vb.ElementSizeInBytes);
                            return new Vector3(BitConverter.ToSingle(vb.Data, at), BitConverter.ToSingle(vb.Data, at + 4), BitConverter.ToSingle(vb.Data, at + 8));
                        }
                        var start = dc.GetInt32Property("m_nStartIndex");
                        var count = dc.GetInt32Property("m_nIndexCount");
                        var shipped = Enumerable.Range(0, count / 3).Select(t => Key(V(Index(start + (t * 3))), V(Index(start + (t * 3) + 1)), V(Index(start + (t * 3) + 2)))).ToList();
                        var sources = shipped.Select(k => owner.GetValueOrDefault(k, -1)).Distinct().ToList();
                        if (sources.Count != 1 || sources[0] < 0)
                        {
                            var kindU = entry.FileName.Contains("agg_", StringComparison.Ordinal) ? "aggregate" : "plain";
                            tally[$"{kindU} from {(sources.Contains(-1) ? "unknown" : "several")} entries"] = tally.GetValueOrDefault($"{kindU} from {(sources.Contains(-1) ? "unknown" : "several")} entries") + 1;
                            unmatched++;
                            if (unmatched <= 5)
                                output.WriteLine($"{entry.FileName} draw {dc.GetStringProperty("m_material")}: {count / 3} triangles from entries [{string.Join(",", sources)}]");
                            continue;
                        }
                        var src = outs[sources[0]];
                        var (renumbered, remap) = MeshoptOptimizers.RenumberByFirstUse(src.Indices);
                        var positions = remap.SelectMany(v => { var q = Pos(src, v); return new[] { q.X, q.Y, q.Z }; }).ToList();
                        var cached = MeshoptOptimizers.OptimizeVertexCache(renumbered, remap.Length);
                        var ordered = MeshoptOptimizers.OptimizeOverdraw(cached, positions, remap.Length, 3, 1.03f);
                        Vector3 R(int v) => new(positions[v * 3], positions[(v * 3) + 1], positions[(v * 3) + 2]);
                        var ours = Enumerable.Range(0, ordered.Length / 3).Select(t => Key(R(ordered[t * 3]), R(ordered[(t * 3) + 1]), R(ordered[(t * 3) + 2]))).ToList();
                        var same = ours.Count == shipped.Count ? ours.Zip(shipped).Count(z => z.First == z.Second) : -1;
                        int InPlace(int[] idx) => Enumerable.Range(0, idx.Length / 3).Count(t => t < shipped.Count && Key(R(idx[t * 3]), R(idx[(t * 3) + 1]), R(idx[(t * 3) + 2])) == shipped[t]);
                        var setSame = ours.Order().SequenceEqual(shipped.Order());
                        if (Environment.GetEnvironmentVariable("DRAWORDER_PERM") == "1")
                        {
                            var srcKeys = Enumerable.Range(0, src.Indices.Count / 3).Select(t => Key(Pos(src, src.Indices[t * 3]), Pos(src, src.Indices[(t * 3) + 1]), Pos(src, src.Indices[(t * 3) + 2]))).ToList();
                            var oursKeys = ours;
                            output.WriteLine($"   {entry.FileName}: shipped as entry triangles [{string.Join(",", shipped.Select(k => srcKeys.IndexOf(k)))}]");
                            output.WriteLine($"   ours vcache+overdraw [{string.Join(",", oursKeys.Select(k => srcKeys.IndexOf(k)))}]");
                        }
                        {
                            // Exact-duplicate vertices (every float) merged first, by first occurrence.
                            var firstOf = new Dictionary<string, int>();
                            var dedup = src.Indices.Select(v =>
                            {
                                var keyV = string.Join(",", Enumerable.Range(0, src.Stride).Select(k => BitConverter.SingleToInt32Bits(src.Vertices[(v * src.Stride) + k])));
                                if (!firstOf.TryGetValue(keyV, out var f))
                                    firstOf[keyV] = f = v;
                                return f;
                            }).ToList();
                            var (rn, rm) = MeshoptOptimizers.RenumberByFirstUse(dedup);
                            var pos2 = rm.SelectMany(v => { var q = Pos(src, v); return new[] { q.X, q.Y, q.Z }; }).ToList();
                            var od2 = MeshoptOptimizers.OptimizeOverdraw(MeshoptOptimizers.OptimizeVertexCache(rn, rm.Length), pos2, rm.Length, 3, 1.03f);
                            var k2 = Enumerable.Range(0, od2.Length / 3).Select(t => Key(new Vector3(pos2[od2[t * 3] * 3], pos2[(od2[t * 3] * 3) + 1], pos2[(od2[t * 3] * 3) + 2]),
                                new Vector3(pos2[od2[(t * 3) + 1] * 3], pos2[(od2[(t * 3) + 1] * 3) + 1], pos2[(od2[(t * 3) + 1] * 3) + 2]),
                                new Vector3(pos2[od2[(t * 3) + 2] * 3], pos2[(od2[(t * 3) + 2] * 3) + 1], pos2[(od2[(t * 3) + 2] * 3) + 2]))).ToList();
                            var weldedSame = k2.Zip(shipped).Count(z => z.First == z.Second);
                            if (Environment.GetEnvironmentVariable("DRAWORDER_TOL") is { } tolText)
                            {
                                var tol = float.Parse(tolText, System.Globalization.CultureInfo.InvariantCulture);
                                var (wv, wi) = Physics.MeshWeld.Weld([.. src.Vertices], src.Stride, [.. src.Indices], outStreams[sources[0]], tol, true);
                                var nvw = wv.Length / src.Stride;
                                var wpos = Enumerable.Range(0, nvw).SelectMany(v => new[] { wv[(v * src.Stride) + src.PositionOffset], wv[(v * src.Stride) + src.PositionOffset + 1], wv[(v * src.Stride) + src.PositionOffset + 2] }).ToList();
                                var wo = MeshoptOptimizers.OptimizeOverdraw(MeshoptOptimizers.OptimizeVertexCache(wi, nvw), wpos, nvw, 3, 1.03f);
                                Vector3 P4(int v) => new(wpos[v * 3], wpos[(v * 3) + 1], wpos[(v * 3) + 2]);
                                weldedSame = Enumerable.Range(0, wo.Length / 3).Count(t => t < shipped.Count && Key(P4(wo[t * 3]), P4(wo[(t * 3) + 1]), P4(wo[(t * 3) + 2])) == shipped[t]);
                            }
                            if (entry.FileName.Contains("overlay", StringComparison.Ordinal) && weldedSame != shipped.Count)
                            {
                                Vector3 P2(int v) => new(pos2[v * 3], pos2[(v * 3) + 1], pos2[(v * 3) + 2]);
                                int InPlace2(int[] idx) => Enumerable.Range(0, idx.Length / 3).Count(t => t < shipped.Count && Key(P2(idx[t * 3]), P2(idx[(t * 3) + 1]), P2(idx[(t * 3) + 2])) == shipped[t]);
                                var vc2 = MeshoptOptimizers.OptimizeVertexCache(rn, rm.Length);
                                foreach (var tol in new[] { 1f / 32f, 1f / 1024f, 1e-5f })
                                {
                                    var (wv, wi) = Physics.MeshWeld.Weld([.. src.Vertices], src.Stride, [.. src.Indices], outStreams[sources[0]], tol, true);
                                    var wpos = Enumerable.Range(0, wv.Length / src.Stride).SelectMany(v => new[] { wv[(v * src.Stride) + src.PositionOffset], wv[(v * src.Stride) + src.PositionOffset + 1], wv[(v * src.Stride) + src.PositionOffset + 2] }).ToList();
                                    var nvw = wv.Length / src.Stride;
                                    var wo = MeshoptOptimizers.OptimizeOverdraw(MeshoptOptimizers.OptimizeVertexCache(wi, nvw), wpos, nvw, 3, 1.03f);
                                    Vector3 P3(int v) => new(wpos[v * 3], wpos[(v * 3) + 1], wpos[(v * 3) + 2]);
                                    output.WriteLine($"   OVERLAY weld {tol}: {nvw} vertices, {wi.Length / 3} triangles, {Enumerable.Range(0, wo.Length / 3).Count(t => t < shipped.Count && Key(P3(wo[t * 3]), P3(wo[(t * 3) + 1]), P3(wo[(t * 3) + 2])) == shipped[t])} in place");
                                }
                                output.WriteLine($"   OVERLAY {entry.FileName}: none {InPlace2(rn)}, vcache {InPlace2(vc2)}, overdraw only {InPlace2(MeshoptOptimizers.OptimizeOverdraw(rn, pos2, rm.Length, 3, 1.03f))}, both {weldedSame} of {shipped.Count}");
                            }
                            var kind = entry.FileName.Contains("agg_", StringComparison.Ordinal) ? "aggregate" : "plain";
                            tally[$"{kind} {(weldedSame == shipped.Count ? "exact" : "differs")}"] = tally.GetValueOrDefault($"{kind} {(weldedSame == shipped.Count ? "exact" : "differs")}") + 1;
                            if (kind == "plain" && weldedSame != shipped.Count && shownPlain++ < 6)
                                output.WriteLine($"   PLAIN {entry.FileName} {Path.GetFileName(dc.GetStringProperty("m_material"))}: {weldedSame}/{shipped.Count}");
                            output.WriteLine($"   welded exact duplicates ({src.VertexCount} -> {firstOf.Count} vertices): {weldedSame}/{shipped.Count} in place");
                        }
                        output.WriteLine($"   variants: none {InPlace(renumbered)}, vcache {InPlace(cached)}, vcache+overdraw {InPlace(ordered)}, overdraw only {InPlace(MeshoptOptimizers.OptimizeOverdraw(renumbered, positions, remap.Length, 3, 1.03f))}; same set {setSame}");
                        if (same == shipped.Count)
                            exact++;
                        else if (draws - exact - unmatched <= 12)
                            output.WriteLine($"{entry.FileName} draw {Path.GetFileName(dc.GetStringProperty("m_material"))}: {same}/{shipped.Count} triangles in place (entry {sources[0]}, {src.Indices.Count / 3} triangles)");
                    }
            }
        }
        output.WriteLine($"{draws} draws, {exact} exact, {unmatched} not from one entry");
        output.WriteLine($"TALLY {string.Join(", ", tally.Select(kv => $"{kv.Key} {kv.Value}"))}");
    }
}
