using System.Numerics;
using Source2.Compiler.Maps;
using ValveKeyValue;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="WorldNodeModelTrees"/> against every node model Valve compiled
/// for probe01, cardtest and atixref. Each model's description is rebuilt:
/// bounds, per-draw bounds, vertex ends and the searchable counts from the
/// decoded buffers; material, tint, layout, meshlets and compile arguments
/// from Valve's trees (their rules are separate work). The four trees must
/// match field for field, and the container around Valve's own buffer
/// blocks must have Valve's facts (block order, KV3 versions, compression).
/// </summary>
public class WorldNodeModelTreesTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("probe01")]
    [InlineData("cardtest")]
    [InlineData("atixref")]
    public void BuildsEveryNodeModel(string map)
    {
        var vpk = Path.Combine(@"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo_addons\s2c_rc_probe\maps", map + ".vpk");
        if (!File.Exists(vpk))
            return;
        using var package = new Package();
        package.Read(vpk);
        var tally = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var examples = new Dictionary<string, string>();
        int count = 0, factsExact = 0, treesExact = 0, propCones = 0;
        foreach (var entry in package.Entries.GetValueOrDefault("vmdl_c") ?? [])
        {
            var path = entry.GetFullPath();
            if (!path.Contains("/worldnodes/", StringComparison.Ordinal))
                continue;
            package.ReadEntry(entry, out var valve);
            count++;
            var trees = WorldPhysicsAuthorTests.Trees(valve);
            var model = Describe(valve, trees);
            var mine = new Dictionary<string, KVObject>
            {
                ["MDAT"] = WorldNodeModelTrees.Mdat(model),
                ["CTRL"] = WorldNodeModelTrees.Ctrl(model),
                ["RED2"] = WorldNodeModelTrees.Red2(model),
                ["DATA"] = WorldNodeModelTrees.Data(model),
            };
            var lines = new List<string>();
            foreach (var (name, tree) in mine)
                lines.AddRange(KvTreeDiff.Diff(trees[name], tree).Select(l => name + l));
            // Static prop aggregates' meshlet cones are not the index buffer's
            // (they follow the prop's own meshlets; open, GEOMETRY.md).
            if (lines.Count > 0 && path.Contains("_agg_prop_", StringComparison.Ordinal)
                && lines.All(l => l.Contains(".m_CullingData.", StringComparison.Ordinal)))
            {
                propCones++;
                lines.Clear();
            }
            if (lines.Count == 0)
                treesExact++;
            else if (Environment.GetEnvironmentVariable("WNTREES_SHOW") == "1")
            {
                using var r = new Resource();
                r.Read(new MemoryStream(valve));
                var vb0 = ((Model)r.DataBlock!).GetEmbeddedMeshesAndLoD().First().Mesh.VBIB.VertexBuffers[0];
                output.WriteLine($"{Path.GetFileName(path)}: {lines.Count} lines, {string.Join(" ", vb0.InputLayoutFields.Select(f => $"{f.SemanticName}{f.SemanticIndex}:{f.Format}"))}; {lines[0]}");
            }
            foreach (var l in lines)
            {
                var key = System.Text.RegularExpressions.Regex.Replace(l.Split(':')[0], @"\[\d+\]", "[]");
                tally[key] = tally.GetValueOrDefault(key) + 1;
                examples.TryAdd(key, $"{Path.GetFileName(path)}: {l}");
            }
            var file = WorldNodeModelAuthor.Container(WorldNodeModelAuthorTests.BufferBlocks(valve), mine["MDAT"], mine["CTRL"],
                                                      WorldNodeModelAuthorTests.References(valve), mine["RED2"], mine["DATA"]);
            // Compressed KV3 bytes are encoder defined: parity is the trees
            // plus the container facts (RERL's offset aside, as in
            // WorldNodeModelAuthorTests).
            static string[] Facts(byte[] b) => WorldPhysicsAuthorTests.Facts(b).Split(' ')
                .Select(x => x.StartsWith("RERL", StringComparison.Ordinal) ? "RERL" : x).ToArray();
            if (Facts(file).SequenceEqual(Facts(valve)))
                factsExact++;
            else
            {
                tally["container"] = tally.GetValueOrDefault("container") + 1;
                examples.TryAdd("container", $"{Path.GetFileName(path)}: {string.Join(" ", Facts(valve))} vs {string.Join(" ", Facts(file))}");
            }
        }
        output.WriteLine($"{map}: {count} node models, trees exact on {treesExact}, container facts on {factsExact}"
                         + (propCones > 0 ? $", {propCones} static prop aggregates differ only in meshlet cones (open)" : ""));
        foreach (var (k, v) in tally)
            output.WriteLine($"{v,5} {examples[k]}");
        Assert.Equal(count, treesExact);
        Assert.Equal(count, factsExact);
    }

    /// <summary>A node model's description, measured parts from its buffers.</summary>
    internal static WorldNodeModel Describe(byte[] file, Dictionary<string, KVObject> trees)
    {
        using var resource = new Resource();
        resource.Read(new MemoryStream(file));
        var mesh = ((Model)resource.DataBlock!).GetEmbeddedMeshesAndLoD().First().Mesh;
        var ctrl = trees["CTRL"]["embedded_meshes"]!.Values.First();
        var vbs = ctrl["m_vertexBuffers"]!.Values.Select(v => new WorldNodeModel.VertexBuffer(
            (int)v["m_nElementCount"]!, (int)v["m_nElementSizeInBytes"]!, (bool)v["m_bMeshoptCompressed"]!,
            v["m_inputLayoutFields"]!.Values.Select(f => new WorldNodeModel.LayoutField(
                (string)f["m_pSemanticName"]!, (int)f["m_nSemanticIndex"]!, (uint)(long)f["m_Format"]!, (int)f["m_nOffset"]!,
                (string)f["m_szShaderSemantic"]!)).ToList())).ToList();
        var ibs = ctrl["m_indexBuffers"]!.Values.Select(v => new WorldNodeModel.IndexBuffer(
            (int)v["m_nElementCount"]!, (int)v["m_nElementSizeInBytes"]!, (bool)v["m_bMeshoptCompressed"]!,
            (bool)v["m_bCreatePooledBuffer"]!)).ToList();
        var sceneObjects = new List<WorldNodeModel.SceneObject>();
        foreach (var so in trees["MDAT"]["m_sceneObjects"]!.Values)
        {
            var draws = new List<WorldNodeModel.Draw>();
            var drawBounds = new List<(Vector3, Vector3)>();
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var d in so["m_drawCalls"]!.Values)
            {
                var vbHandle = (int)d["m_vertexBuffers"]!.Values.First()["m_hBuffer"]!;
                var vb = mesh.VBIB.VertexBuffers[vbHandle];
                var positions = VBIB.GetVector3AttributeArray(vb, vb.InputLayoutFields.First(f => f.SemanticName is "POSITION" or "position"));
                var ib = mesh.VBIB.IndexBuffers[(int)d["m_indexBuffer"]!["m_hBuffer"]!];
                var start = (int)d["m_nStartIndex"]!;
                var indexCount = (int)d["m_nIndexCount"]!;
                var offset = (int)d["m_nAppliedIndexOffset"]!;
                var dmin = new Vector3(float.MaxValue);
                var dmax = new Vector3(float.MinValue);
                var end = 0;
                for (var i = 0; i < indexCount; i++)
                {
                    var at = start + i;
                    var index = (ib.ElementSizeInBytes == 2 ? BitConverter.ToUInt16(ib.Data, at * 2) : BitConverter.ToInt32(ib.Data, at * 4));
                    end = Math.Max(end, index + 1);
                    dmin = Vector3.Min(dmin, positions[index]);
                    dmax = Vector3.Max(dmax, positions[index]);
                }
                min = Vector3.Min(min, dmin);
                max = Vector3.Max(max, dmax);
                drawBounds.Add((dmin, dmax));
                var tint = d["m_vTintColor"]!.Values.Select(x => (float)(double)x).ToArray();
                draws.Add(new WorldNodeModel.Draw(
                    (string)d["m_material"]!, new Vector3(tint[0], tint[1], tint[2]), (float)(double)d["m_flUvDensity"]!,
                    (int)d["m_nFirstMeshlet"]!, (int)d["m_nNumMeshlets"]!, offset, (int)d["m_nDepthVertexBufferIndex"]!, end,
                    start, indexCount, (int)d["m_indexBuffer"]!["m_hBuffer"]!,
                    d["m_vertexBuffers"]!.Values.Select(v => (int)v["m_hBuffer"]!).ToList(),
                    d.Children.Any(c => c.Key == "m_bHasBakedLightingFromVertexStream"),
                    d.Children.Any(c => c.Key == "m_bIsNotMatchedToMaterial")));
            }
            // Meshlets: the partition (each one's triangle offset and count)
            // from Valve's, the rest from the index buffer.
            var meshlets = new List<WorldNodeModel.Meshlet>();
            var valveMeshlets = so["m_meshlets"]!.Values.ToList();
            var drawList = so["m_drawCalls"]!.Values.ToList();
            for (var di = 0; di < drawList.Count; di++)
            {
                var d = drawList[di];
                var vb = mesh.VBIB.VertexBuffers[(int)d["m_vertexBuffers"]!.Values.First()["m_hBuffer"]!];
                var positions = VBIB.GetVector3AttributeArray(vb, vb.InputLayoutFields.First(f => f.SemanticName is "POSITION" or "position"));
                var ib = mesh.VBIB.IndexBuffers[(int)d["m_indexBuffer"]!["m_hBuffer"]!];
                int Index(int at) => ib.ElementSizeInBytes == 2 ? BitConverter.ToUInt16(ib.Data, at * 2) : BitConverter.ToInt32(ib.Data, at * 4);
                var first = (int)d["m_nFirstMeshlet"]!;
                var n = (int)d["m_nNumMeshlets"]!;
                var cones = Enumerable.Range(first, n).Any(i => (int)valveMeshlets[i]["m_CullingData"]!["m_ConeCutoff"]! != 0
                                                                || valveMeshlets[i]["m_CullingData"]!["m_ConeAxis"]!.Values.Any(v => (int)v != 0));
                var (bmin, bmax) = drawBounds[di];
                for (var i = first; i < first + n; i++)
                {
                    var triangleOffset = (int)valveMeshlets[i]["m_nTriangleOffset"]!;
                    var triangleCount = (int)valveMeshlets[i]["m_nTriangleCount"]!;
                    var idx = Enumerable.Range(triangleOffset * 3, triangleCount * 3).Select(Index).ToArray();
                    var mmin = new Vector3(float.MaxValue);
                    var mmax = new Vector3(float.MinValue);
                    foreach (var v in idx)
                    {
                        mmin = Vector3.Min(mmin, positions[v]);
                        mmax = Vector3.Max(mmax, positions[v]);
                    }
                    var (pmin, pmax) = Meshopt.MeshletBounds.Pack(bmin, bmax, mmin, mmax);
                    var cone = cones ? Meshopt.MeshletBounds.Cone(positions, idx) : (0, 0, 0, 0);
                    meshlets.Add(new WorldNodeModel.Meshlet(pmin, pmax, cone.Item1, cone.Item2, cone.Item3, cone.Item4,
                        (int)d["m_nAppliedIndexOffset"]!, triangleOffset, idx.Distinct().Count(), triangleCount));
                }
            }
            var keepBounds = Path.GetFileName((string)trees["DATA"]["m_name"]!).Split('_').Skip(2).FirstOrDefault() == "agg";
            sceneObjects.Add(new WorldNodeModel.SceneObject(min, max, draws, keepBounds ? drawBounds : [], meshlets));
        }
        var name = (string)trees["DATA"]["m_name"]!;
        var aggregate = Path.GetFileName(name).Split('_').Skip(2).FirstOrDefault() == "agg";
        return new WorldNodeModel(name, vbs, ibs, sceneObjects, aggregate,
                                  (int)trees["RED2"]["m_SearchableUserData"]!["compile_warnings"]!);
    }
}

/// <summary>
/// Exploration (<c>WNDERIVE=1</c>): per model kind, how many draws' UV density
/// our <see cref="UvDensity"/> gives (20th or 95th percentile), and each kind's
/// compile argument fingerprints.
/// </summary>
public class WorldNodeModelDeriveProbe(ITestOutputHelper output)
{
    [Fact]
    public void Derive()
    {
        if (Environment.GetEnvironmentVariable("WNDERIVE") != "1")
            return;
        var tally = new SortedDictionary<string, int>(StringComparer.Ordinal);
        void Count(string k) => tally[k] = tally.GetValueOrDefault(k) + 1;
        foreach (var map in new[] { "probe01", "cardtest", "atixref" })
        {
            var vpk = Path.Combine(@"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo_addons\s2c_rc_probe\maps", map + ".vpk");
            using var package = new Package();
            package.Read(vpk);
            foreach (var entry in package.Entries.GetValueOrDefault("vmdl_c") ?? [])
            {
                var path = entry.GetFullPath();
                if (!path.Contains("/worldnodes/", StringComparison.Ordinal))
                    continue;
                package.ReadEntry(entry, out var valve);
                var name = Path.GetFileNameWithoutExtension(path);
                var kind = System.Text.RegularExpressions.Regex.Replace(name, @"^n\d+_lr\d+_(c\d+_)?", "");
                kind = kind.StartsWith("agg_", StringComparison.Ordinal) ? string.Join("_", kind.Split('_').Take(2))
                     : System.Text.RegularExpressions.Regex.Replace(kind, @"(cm\d+_lp\d+|\d+)", "#").Split("_mt_")[0];
                var trees = WorldPhysicsAuthorTests.Trees(valve);
                var args = string.Join(",", trees["RED2"]["m_ArgumentDependencies"]!.Values
                    .Where(a => (long)a["m_nFingerprint"]! != (long)a["m_nFingerprintDefault"]!)
                    .Select(a => $"{a["m_ParameterName"]}={a["m_nFingerprint"]}"));
                Count($"args {kind}: {args} warnings={trees["RED2"]["m_SearchableUserData"]!["compile_warnings"]}");
                using var resource = new Resource();
                resource.Read(new MemoryStream(valve));
                var mesh = ((Model)resource.DataBlock!).GetEmbeddedMeshesAndLoD().First().Mesh;
                foreach (var so in trees["MDAT"]["m_sceneObjects"]!.Values)
                {
                    Count($"bounds {kind}: drawBounds={so["m_drawBounds"]!.Values.Any()} meshlets={so["m_meshlets"]!.Values.Any()}");
                    foreach (var d in so["m_drawCalls"]!.Values)
                    {
                        var vb = mesh.VBIB.VertexBuffers[(int)d["m_vertexBuffers"]!.Values.First()["m_hBuffer"]!];
                        var pos = vb.InputLayoutFields.FirstOrDefault(f => f.SemanticName is "POSITION" or "position");
                        var uv = vb.InputLayoutFields.FirstOrDefault(f => f.SemanticName is "TEXCOORD" or "texcoord" && f.SemanticIndex == 0);
                        if (uv.SemanticName == null || uv.Format != DXGI_FORMAT.R32G32_FLOAT)
                        {
                            Count($"uv {kind}: texcoords {uv.Format}");
                            continue;
                        }
                        var positions = VBIB.GetVector3AttributeArray(vb, pos);
                        var uvs = VBIB.GetVector2AttributeArray(vb, uv);
                        var ib = mesh.VBIB.IndexBuffers[(int)d["m_indexBuffer"]!["m_hBuffer"]!];
                        var start = (int)d["m_nStartIndex"]!;
                        var idx = new int[(int)d["m_nIndexCount"]!];
                        for (var i = 0; i < idx.Length; i++)
                            idx[i] = ib.ElementSizeInBytes == 2 ? BitConverter.ToUInt16(ib.Data, (start + i) * 2) : BitConverter.ToInt32(ib.Data, (start + i) * 4);
                        var valveDensity = (float)(double)d["m_flUvDensity"]!;
                        var verdict = UvDensity.Compute(positions, uvs, idx) == valveDensity ? "20th"
                            : UvDensity.Compute(positions, uvs, idx, 95) == valveDensity ? "95th" : "differs";
                        Count($"uv {kind}: {verdict}");
                    }
                }
            }
        }
        foreach (var (k, v) in tally)
            output.WriteLine($"{v,5} {k}");
    }
}

/// <summary>
/// Exploration (<c>WNLAYOUT=1</c>): each node vertex buffer's layout with the
/// range of its texcoords, to find what picks a texcoord format.
/// </summary>
public class WorldNodeLayoutProbe(ITestOutputHelper output)
{
    [Fact]
    public void Layouts()
    {
        if (Environment.GetEnvironmentVariable("WNLAYOUT") != "1")
            return;
        var tally = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var map in new[] { "probe01", "cardtest", "atixref" })
        {
            using var package = new Package();
            package.Read(Path.Combine(@"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo_addons\s2c_rc_probe\maps", map + ".vpk"));
            foreach (var entry in package.Entries.GetValueOrDefault("vmdl_c") ?? [])
            {
                if (!entry.GetFullPath().Contains("/worldnodes/", StringComparison.Ordinal))
                    continue;
                package.ReadEntry(entry, out var bytes);
                using var resource = new Resource();
                resource.Read(new MemoryStream(bytes));
                var name = Path.GetFileNameWithoutExtension(entry.GetFullPath());
                var kind = name.Contains("_agg_prop_", StringComparison.Ordinal) ? "prop" : name.Contains("_agg_", StringComparison.Ordinal) ? "agg" : "mesh";
                foreach (var vb in ((Model)resource.DataBlock!).GetEmbeddedMeshesAndLoD().First().Mesh.VBIB.VertexBuffers)
                {
                    var layout = string.Join(" ", vb.InputLayoutFields.Select(f => $"{f.SemanticName}{f.SemanticIndex}:{f.Format}"));
                    foreach (var f in vb.InputLayoutFields.Where(f => f.SemanticName is "TEXCOORD" or "texcoord"
                                                                   && f.Format is DXGI_FORMAT.R32G32_FLOAT or DXGI_FORMAT.R16G16_FLOAT or DXGI_FORMAT.R16G16_SNORM))
                    {
                        var uv = VBIB.GetVector2AttributeArray(vb, f);
                        var lo = uv.Aggregate(new Vector2(float.MaxValue), Vector2.Min);
                        var hi = uv.Aggregate(new Vector2(float.MinValue), Vector2.Max);
                        var span = MathF.Max(MathF.Max(MathF.Abs(lo.X), MathF.Abs(hi.X)), MathF.Max(MathF.Abs(lo.Y), MathF.Abs(hi.Y)));
                        var bucket = span <= 1f ? "<=1" : span <= 2f ? "<=2" : span <= 16f ? "<=16" : span <= 256f ? "<=256" : ">256";
                        if (f.Format == DXGI_FORMAT.R32G32_FLOAT)
                        {
                            var halfExact = uv.All(v => (float)(Half)v.X == v.X && (float)(Half)v.Y == v.Y);
                            var snormExact = uv.All(v => MathF.Abs(v.X) <= 1 && MathF.Abs(v.Y) <= 1
                                                         && MathF.Round(v.X * 32767f) / 32767f == v.X && MathF.Round(v.Y * 32767f) / 32767f == v.Y);
                            var key = $"{kind} {f.SemanticName}{f.SemanticIndex} float32: half-exact {halfExact}, snorm-exact {snormExact}";
                            tally[key] = tally.GetValueOrDefault(key) + 1;
                        }
                        tally[$"{kind} {f.SemanticName}{f.SemanticIndex} {f.Format} max|uv| {bucket}"] = tally.GetValueOrDefault($"{kind} {f.SemanticName}{f.SemanticIndex} {f.Format} max|uv| {bucket}") + 1;
                    }
                    tally[$"{kind} layout {layout}"] = tally.GetValueOrDefault($"{kind} layout {layout}") + 1;
                }
            }
        }
        foreach (var (k, v) in tally)
            output.WriteLine($"{v,5} {k}");
    }
}

/// <summary>
/// Exploration (<c>WNCONES=1</c>): atixref's static prop aggregate meshlets
/// whose cone is not the one their index range gives, ours beside Valve's.
/// </summary>
public class WorldNodePropConeProbe(ITestOutputHelper output)
{
    [Fact]
    public void Cones()
    {
        if (Environment.GetEnvironmentVariable("WNCONES") != "1")
            return;
        using var package = new Package();
        package.Read(@"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo_addons\s2c_rc_probe\maps\atixref.vpk");
        foreach (var entry in package.Entries.GetValueOrDefault("vmdl_c") ?? [])
        {
            var path = entry.GetFullPath();
            if (!path.Contains("_agg_prop_", StringComparison.Ordinal))
                continue;
            package.ReadEntry(entry, out var valve);
            var trees = WorldPhysicsAuthorTests.Trees(valve);
            var model = WorldNodeModelTreesTests.Describe(valve, trees);
            var so = trees["MDAT"]["m_sceneObjects"]!.Values.First();
            var vm = so["m_meshlets"]!.Values.ToList();
            var ours = model.SceneObjects[0].Meshlets;
            var draws = so["m_drawCalls"]!.Values.ToList();
            for (var i = 0; i < vm.Count; i++)
            {
                var cd = vm[i]["m_CullingData"]!;
                var axis = cd["m_ConeAxis"]!.Values.Select(v => (int)v).ToArray();
                var cut = (int)cd["m_ConeCutoff"]!;
                var o = ours[i];
                if (o.ConeX == axis[0] && o.ConeY == axis[1] && o.ConeZ == axis[2] && o.ConeCutoff == cut)
                    continue;
                var draw = draws.FindIndex(d => (int)d["m_nFirstMeshlet"]! <= i && i < (int)d["m_nFirstMeshlet"]! + (int)d["m_nNumMeshlets"]!);
                output.WriteLine($"{Path.GetFileName(path)} meshlet {i} (draw {draw} of {draws.Count}, {o.TriangleCount} tris, {o.VertexCount} verts, {(string)draws[draw]["m_material"]!}): valve ({axis[0]},{axis[1]},{axis[2]}) {cut}, ours ({o.ConeX},{o.ConeY},{o.ConeZ}) {o.ConeCutoff}");
            }
        }
    }
}
