using System.Numerics;
using Source2.Compiler.Maps;
using ValveKeyValue;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler.Cubemaps;

/// <summary>
/// The world node aggregates a cubemap face draws, read from a compiled map package: their fragments, LODs and
/// transforms (<see cref="Read"/>, what <see cref="AggregateCulling"/> and <see cref="CubemapFaceDrawList"/> take),
/// and their layer-selection inputs (<see cref="LayerInputs"/>, <see cref="Memberships"/>: m_objectFlags, the draw
/// call's booleans and the material's attributes through its shader, GROUND_TRUTH 157). Only the package and the
/// game's content are read. Moved here from the capture tests (CUBEMAPS.md C.1), which checked both against cubehost
/// captures (AggregateCullingCaptureTests, CubemapLayerFlagsCaptureTests).
/// </summary>
public static class CubemapPackageAggregates
{
    /// <summary>One aggregate (its node file and model) and its model's first vertex buffer (size in bytes, stride), which tells its draws apart in a capture.</summary>
    public sealed record Aggregate(string Node, string Model, List<AggregateCulling.Fragment> Fragments, bool OwnTransforms, long VertexBytes, uint VertexStride,
        List<string> Flags, List<AggregateCulling.LodSetup> Lods);

    /// <summary>One aggregate's layer-selection inputs (<see cref="CubemapLayerFlags"/>), with the fragments whose 0x20 needs CPVS's box query (not ported).</summary>
    public sealed record Layer(string Model, List<ushort> Fragments, List<int> IndexCounts, uint DrawCall1c, ushort DrawCall14, uint ObjectE0,
        MaterialAttributes? Material, List<bool> VisQuery, uint F78, ulong F80);

    private static string Text(KVObject o, string key)
        => o.Children.FirstOrDefault(c => c.Key == key) is { } c ? (c.Value.IsArray ? string.Join("|", c.Value.Values.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture))) : c.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)) : "";

    private static IEnumerable<(string Node, KVObject Aggregate, Model Model)> Objects(ValvePak.Package package, bool byPath)
    {
        var entries = package.Entries!.SelectMany(kv => kv.Value).Where(e => e.GetFullPath().EndsWith(".vwnod_c", StringComparison.Ordinal));
        if (byPath)
            entries = entries.OrderBy(e => e.GetFullPath(), StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            package.ReadEntry(entry, out var nodeBytes);
            using var nodeResource = new Resource();
            nodeResource.Read(new MemoryStream(nodeBytes));
            foreach (var o in ((WorldNode)nodeResource.DataBlock!).Data.GetArray("m_aggregateSceneObjects") ?? [])
            {
                var name = o.GetStringProperty("m_renderableModel") ?? "";
                package.ReadEntry(package.FindEntry(Path.ChangeExtension(name, ".vmdl_c"))!, out var bytes);
                using var resource = new Resource();
                resource.Read(new MemoryStream(bytes));
                yield return (entry.FileName, o, (Model)resource.DataBlock!);
            }
        }
    }

    /// <summary>Every world node's aggregates, nodes in path order: fragments, LODs and whether each fragment has its own transform.</summary>
    public static List<Aggregate> Read(ValvePak.Package package)
    {
        var result = new List<Aggregate>();
        foreach (var (node, o, model) in Objects(package, byPath: true))
        {
            var draws = new List<AggregateCulling.DrawCall>();
            long vertexBytes = 0;
            uint vertexStride = 0;
            foreach (var (mesh, _, _, _) in model.GetEmbeddedMeshesAndLoD())
            {
                if (mesh.VBIB.VertexBuffers.Count > 0 && vertexBytes == 0)
                    (vertexBytes, vertexStride) = (mesh.VBIB.VertexBuffers[0].Data.Length, mesh.VBIB.VertexBuffers[0].ElementSizeInBytes);
                foreach (var so in mesh.Data.GetArray("m_sceneObjects"))
                {
                    var bounds = so.GetArray("m_drawBounds");
                    var calls = so.GetArray("m_drawCalls");
                    for (var i = 0; i < calls.Count; i++)
                    {
                        var dc = calls[i];
                        var tint = dc.ContainsKey("m_vTintColor") ? dc.GetSubCollection("m_vTintColor").ToVector3() : Vector3.One;
                        draws.Add(new AggregateCulling.DrawCall(dc.GetInt32Property("m_nStartIndex"), dc.GetInt32Property("m_nIndexCount"),
                            dc.GetInt32Property("m_nBaseVertex"), bounds[i].GetSubCollection("m_vMinBounds").ToVector3(),
                            bounds[i].GetSubCollection("m_vMaxBounds").ToVector3(), new Vector4(tint, dc.GetFloatProperty("m_flAlpha"))));
                    }
                }
            }
            var transforms = o.GetArray("m_fragmentTransforms") ?? [];
            var nextTransform = 0;
            var meshes = new List<AggregateCulling.MeshInfo>();
            var flags = new List<string>();
            foreach (var m in o.GetArray("m_aggregateMeshes"))
            {
                flags.Add(Text(m, "m_objectFlags") + $" lod {m.GetInt32Property("m_nLODSetupIndex")} mask {m.GetInt32Property("m_nLODGroupMask")}");
                float[]? transform = null;
                if (m.GetBooleanProperty("m_bHasTransform"))
                    transform = [.. transforms[nextTransform++].Values.Select(v => (float)v.ToDouble(System.Globalization.CultureInfo.InvariantCulture))];
                var tint = m.GetArray<long>("m_vTintColor") is { } t ? (uint)(t[0] | t[1] << 8 | t[2] << 16) : 0xffffffu;
                meshes.Add(new AggregateCulling.MeshInfo(m.GetInt32Property("m_nDrawCallIndex"), transform, tint, m.GetInt32Property("m_nLODSetupIndex"), m.GetInt32Property("m_nLODGroupMask")));
            }
            var fragments = AggregateCulling.Fragments(meshes, draws);
            var lods = (o.GetArray("m_lodSetups") ?? []).Select(l => new AggregateCulling.LodSetup(l.GetSubCollection("m_vLODOrigin").ToVector3(),
                l.GetFloatProperty("m_fMaxObjectScale"), [.. l.GetArray<double>("m_fSwitchDistances").Select(d => (float)d)])).ToList();
            var slots = fragments.Count == 0 ? 0 : fragments[^1].TransformSlot + 1;
            result.Add(new Aggregate(node, o.GetStringProperty("m_renderableModel") ?? "", fragments, slots == fragments.Count, vertexBytes, vertexStride, flags, lods));
        }
        return result;
    }

    private static uint ObjectFlags(KVObject mesh, string key = "m_objectFlags")
    {
        var text = mesh.Children.First(c => c.Key == key).Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (uint.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n))
            return n;
        return text.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Aggregate(0u, (f, name) => f | WorldNodeSceneTrees.ObjectTypes.First(t => t.Name == name).Value);
    }

    private static bool Flag(KVObject o, string key) => o.ContainsKey(key) && o.GetBooleanProperty(key);

    /// <summary>
    /// Every world node aggregate's layer-selection inputs, in the package's entry order. Materials come from
    /// <paramref name="content"/>, their attributes from every render mode's program in <paramref name="shaders"/>
    /// (<see cref="MaterialAttributes.OfModes"/>; the -vulkan game's are <c>vulkan_50</c>).
    /// </summary>
    public static List<Layer> LayerInputs(ValvePak.Package package, GameContent content, ShaderLibrary shaders)
    {
        var attributes = new Dictionary<string, MaterialAttributes>();
        MaterialAttributes Attributes(string m)
        {
            if (!attributes.TryGetValue(m, out var a))
                attributes[m] = a = content.Material(m) is { } info ? MaterialAttributes.OfModes(info, shaders) : MaterialAttributes.Empty;
            return a;
        }
        var result = new List<Layer>();
        foreach (var (_, o, model) in Objects(package, byPath: false))
        {
            var calls = new List<KVObject>();
            foreach (var (mesh, _, _, _) in model.GetEmbeddedMeshesAndLoD())
                foreach (var so in mesh.Data.GetArray("m_sceneObjects"))
                    calls.AddRange(so.GetArray("m_drawCalls"));
            var flags = calls.Select(c => CubemapLayerFlags.MaterialFlags(c.GetStringProperty("m_material") is { Length: > 0 } m ? Attributes(m) : null)).ToList();
            var fragments = new List<ushort>();
            var counts = new List<int>();
            var query = new List<bool>();
            foreach (var m in o.GetArray("m_aggregateMeshes"))
            {
                var call = m.GetInt32Property("m_nDrawCallIndex");
                var objectFlags = ObjectFlags(m);
                // Fragments whose 0x20 needs CPVS's box query (not ported): computed with no clusters.
                query.Add(CubemapLayerFlags.NeedsVisQuery(objectFlags));
                fragments.Add(CubemapLayerFlags.FragmentFlags(objectFlags, flags[call], m.GetInt32Property("m_nVisClusterMemberCount"), visClusters: 0));
                counts.Add(calls[call].GetInt32Property("m_nIndexCount"));
            }
            var first = calls[0];
            var bools = new CubemapLayerFlags.DrawCallBools(Flag(first, "m_bUseShadowFastPath"), Flag(first, "m_bUseCompressedNormalTangent"),
                Flag(first, "m_bIsNotMatchedToMaterial"), Flag(first, "m_bHasBakedLightingFromVertexStream"), Flag(first, "m_bHasBakedLightingFromLightMap"),
                Flag(first, "m_bCanBatchWithDynamicShaderConstants"), Flag(first, "m_bDrawLast"), Flag(first, "m_bBakedTwoSided"),
                Flag(first, "m_bOnlyShadow"), Flag(first, "m_bNoShadow"));
            var material = first.GetStringProperty("m_material") is { Length: > 0 } fm ? Attributes(fm) : null;
            var (f78, f80) = CubemapLayerFlags.ObjectWords(ObjectFlags(o, "m_allFlags"), ObjectFlags(o, "m_anyFlags"), flags[0]);
            result.Add(new Layer(o.GetStringProperty("m_renderableModel") ?? "", fragments, counts, flags[0], CubemapLayerFlags.DrawCallFlags(bools, material),
                CubemapLayerFlags.ObjectFlags(fragments), material, query, f78, f80));
        }
        return result;
    }

    /// <summary>Each world node aggregate's layer-selection inputs by its model (m_renderableModel); the first aggregate of a model decides.</summary>
    public static Dictionary<string, CubemapFaceDrawList.Membership> Memberships(IEnumerable<Layer> layers)
        => layers.GroupBy(o => o.Model).ToDictionary(g => g.Key,
            g => new CubemapFaceDrawList.Membership(g.First().F78, g.First().F80, g.First().Fragments, g.First().DrawCall1c, g.First().DrawCall14, g.First().ObjectE0));
}
