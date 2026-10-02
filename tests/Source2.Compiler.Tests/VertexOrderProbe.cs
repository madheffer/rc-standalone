using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Exploration (<c>VERTEXORDER=&lt;compiled vpk&gt;</c>): whether each world
/// node draw's vertices sit in the order its index list first uses them
/// (meshopt's optimizeVertexFetch), counted by model kind.
/// </summary>
public class VertexOrderProbe(ITestOutputHelper output)
{
    /// <summary>
    /// Exploration (<c>VERTEXLAYOUT=&lt;vpk&gt;</c>, stock pak for materials):
    /// each world node vertex buffer layout tallied by model kind and the
    /// draw's shader.
    /// </summary>
    [Fact]
    public void Layouts()
    {
        if (Environment.GetEnvironmentVariable("VERTEXLAYOUT") is not { } vpk || CS2Fixtures.StockPak() is not { } pak)
            return;
        var game = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pak)!, ".."));
        using var content = new Maps.GameContent(pak, Environment.GetEnvironmentVariable("VERTEXLAYOUT_ADDON") is { } addon ? Path.Combine(game, "csgo_addons", addon) : null);
        using var package = new ValvePak.Package();
        package.Read(vpk);
        var tally = new SortedDictionary<string, int>();
        foreach (var entry in package.Entries!.GetValueOrDefault("vmdl_c") ?? [])
        {
            if (!entry.DirectoryName.Contains("worldnodes", StringComparison.OrdinalIgnoreCase))
                continue;
            package.ReadEntry(entry, out var bytes);
            using var resource = new Resource();
            resource.Read(new MemoryStream(bytes));
            var kind = entry.FileName.Contains("agg_prop") ? "agg_prop" : entry.FileName.Contains("agg_") ? "aggregate" : entry.FileName.Contains("overlay") ? "overlay" : "plain";
            foreach (var (mesh, _, _, _) in ((Model)resource.DataBlock!).GetEmbeddedMeshesAndLoD())
            {
                // Per model: TEXCOORD0's formats across its vertex buffers beside the model's largest value.
                {
                    var formats = new SortedSet<string>();
                    var modelMax = 0f;
                    foreach (var vb in mesh.VBIB.VertexBuffers)
                        foreach (var f in vb.InputLayoutFields.Where(f => f.SemanticName == "TEXCOORD" && f.SemanticIndex == 0))
                        {
                            formats.Add(f.Format.ToString());
                            for (var v = 0; v < vb.ElementCount; v++)
                            {
                                var uv = Maps.NodePropEntries.Texcoord(vb, f, v * (int)vb.ElementSizeInBytes);
                                modelMax = MathF.Max(modelMax, MathF.Max(MathF.Abs(uv.X), MathF.Abs(uv.Y)));
                            }
                        }
                    if (formats.Count > 0)
                    {
                        var mk = $"MODEL {kind,-9} max {(modelMax <= 1f ? "<=1" : modelMax <= 16f ? "<=16" : ">16"),-5} formats {string.Join("+", formats)}";
                        tally[mk] = tally.GetValueOrDefault(mk) + 1;
                    }
                }
                // Texcoord streams per vertex buffer: the format beside the values' reach and how a half or snorm would hold them.
                foreach (var vb in mesh.VBIB.VertexBuffers)
                    foreach (var f in vb.InputLayoutFields.Where(f => f.SemanticName == "TEXCOORD" && (f.Format.ToString().Contains("G16") || f.Format.ToString() == "R32G32_FLOAT")))
                    {
                        float max = 0, halfErr = 0, snormErr = 0;
                        for (var v = 0; v < vb.ElementCount; v++)
                        {
                            var uv = Maps.NodePropEntries.Texcoord(vb, f, v * (int)vb.ElementSizeInBytes);
                            foreach (var x in new[] { uv.X, uv.Y })
                            {
                                max = MathF.Max(max, MathF.Abs(x));
                                halfErr = MathF.Max(halfErr, MathF.Abs((float)(Half)x - x));
                                snormErr = MathF.Max(snormErr, MathF.Abs(MathF.Round(Math.Clamp(x, -1f, 1f) * 32767f) / 32767f - x));
                            }
                        }
                        var bucket = max <= 1f ? "<=1" : max <= 2f ? "<=2" : max <= 16f ? "<=16" : max <= 256f ? "<=256" : ">256";
                        var herr = halfErr == 0 ? "0" : halfErr < 1e-4f ? "<1e-4" : halfErr < 1e-3f ? "<1e-3" : halfErr < 1e-2f ? "<1e-2" : ">=1e-2";
                        var serr = snormErr == 0 ? "0" : snormErr < 2e-5f ? "<2e-5" : snormErr < 1e-4f ? "<1e-4" : ">=1e-4";
                        var tk = $"TC {kind,-9} {f.SemanticIndex} {f.Format,-14} max {bucket,-6} halfErr {herr,-7} snormErr {serr}";
                        tally[tk] = tally.GetValueOrDefault(tk) + 1;
                        if (kind == "agg_prop" && f.Format.ToString() == "R32G32_FLOAT")
                            output.WriteLine($"F32 {entry.FileName} TEXCOORD{f.SemanticIndex}: max {max:R} halfErr {halfErr:R} snormErr {snormErr:R} vertices {vb.ElementCount} layout {string.Join(" ", vb.InputLayoutFields.Select(x => $"{x.SemanticName}{x.SemanticIndex}:{x.Format}"))}");
                    }
                foreach (var so in mesh.Data.GetArray("m_sceneObjects"))
                    foreach (var dc in so.GetArray("m_drawCalls"))
                    {
                        var vb = mesh.VBIB.VertexBuffers[dc.GetArray("m_vertexBuffers")[0].GetInt32Property("m_hBuffer")];
                        var material = dc.GetStringProperty("m_material") ?? "";
                        var shader = Path.GetFileNameWithoutExtension(content.Material(material)?.Shader ?? "?");
                        var layout = string.Join(" ", vb.InputLayoutFields.Select(f => $"{f.SemanticName}{f.SemanticIndex}:{f.Format.ToString().Replace("_FLOAT", "F").Replace("_UNORM", "U").Replace("_SNORM", "S").Replace("_UINT", "I")}"));
                        var key = $"{kind,-9} {shader,-28} {layout}";
                        tally[key] = tally.GetValueOrDefault(key) + 1;
                        if (kind != "agg_prop")
                        {
                            var tc = string.Join(" ", dc.GetArray("m_vertexBuffers").Select(b => mesh.VBIB.VertexBuffers[b.GetInt32Property("m_hBuffer")])
                                .SelectMany(b => b.InputLayoutFields).Where(f => f.SemanticName == "TEXCOORD" && f.SemanticIndex < 2).Select(f => f.Format.ToString()));
                            var origin = material.StartsWith("materials/models/", StringComparison.OrdinalIgnoreCase) ? "model material" : "map material";
                            var wk = $"WORLD {kind,-9} {origin,-14} {shader,-28} {tc}";
                            tally[wk] = tally.GetValueOrDefault(wk) + 1;
                        }
                    }
            }
        }
        foreach (var (k, v) in tally)
            output.WriteLine($"{v,5} {k}");
    }

    [Fact]
    public void FirstUseOrder()
    {
        if (Environment.GetEnvironmentVariable("VERTEXORDER") is not { } vpk)
            return;
        using var package = new ValvePak.Package();
        package.Read(vpk);
        var tally = new SortedDictionary<string, int>();
        var shown = 0;
        foreach (var entry in package.Entries!.GetValueOrDefault("vmdl_c") ?? [])
        {
            if (!entry.DirectoryName.Contains("worldnodes", StringComparison.OrdinalIgnoreCase))
                continue;
            package.ReadEntry(entry, out var bytes);
            using var resource = new Resource();
            resource.Read(new MemoryStream(bytes));
            var kind = entry.FileName.Contains("agg_prop") ? "agg_prop" : entry.FileName.Contains("agg_") ? "aggregate" : "plain";
            foreach (var (mesh, _, _, _) in ((Model)resource.DataBlock!).GetEmbeddedMeshesAndLoD())
                foreach (var so in mesh.Data.GetArray("m_sceneObjects"))
                    foreach (var dc in so.GetArray("m_drawCalls"))
                    {
                        var ib = mesh.VBIB.IndexBuffers[dc.GetSubCollection("m_indexBuffer").GetInt32Property("m_hBuffer")];
                        int I(int i) => ib.ElementSizeInBytes == 2 ? BitConverter.ToUInt16(ib.Data, i * 2) : BitConverter.ToInt32(ib.Data, i * 4);
                        var start = dc.GetInt32Property("m_nStartIndex");
                        var count = dc.GetInt32Property("m_nIndexCount");
                        var seen = new HashSet<int>();
                        var next = int.MinValue;
                        var firstUse = true;
                        var lo = int.MaxValue;
                        for (var i = start; i < start + count; i++)
                            lo = Math.Min(lo, I(i));
                        next = lo;
                        for (var i = start; i < start + count && firstUse; i++)
                        {
                            var v = I(i);
                            if (seen.Add(v))
                            {
                                firstUse = v == next;
                                next++;
                            }
                        }
                        var key = $"{kind} {(firstUse ? "first-use" : "other")}";
                        tally[key] = tally.GetValueOrDefault(key) + 1;
                        if (!firstUse && shown++ < 5)
                            output.WriteLine($"{entry.FileName}: first indices {string.Join(",", Enumerable.Range(start, Math.Min(24, count)).Select(I))}");
                    }
        }
        output.WriteLine($"TALLY {string.Join(", ", tally.Select(kv => $"{kv.Key} {kv.Value}"))}");
    }
}
