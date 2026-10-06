using System.Numerics;
using ValvePak;

namespace Source2.Compiler.Maps;

/// <summary>
/// The world step's render half without baked lighting (a <c>-world -vis</c>
/// compile's package): the trace scene's visibility
/// (<c>world_visibility.vvis_c</c>), one world node (<c>n0</c>): its models
/// from CompileNode's objects and aggregates (<see cref="WorldNodeCompile"/>)
/// and the prop aggregates (<see cref="NodePropAggregates"/>), its scene file
/// and manifest, then <c>world.vwrld_c</c> and <c>world.vrman_c</c>.
/// <para>Measured parity: probe01's node models (bar the previous bake's
/// per-vertex lighting), .vwnod and .vwrld are Valve's; the rest is listed in
/// GROUND_TRUTH ledger 54. A node needing the grid render clusters, or an
/// unported list, comes out without those objects and a note.</para>
/// </summary>
public static class WorldNodeFiles
{
    public sealed record Output(string Path, byte[] Bytes);

    /// <param name="volumes">The cubemap and light probe volumes the entity lump author found.</param>
    /// <param name="timestamp">The compile's time, for .vwrld.</param>
    public static List<Output> Build(DmxBinary.Document document, string addon, string map, GameContent content, ShaderLibrary shaders,
                                     FgdSchema schema, string game, EnvVolumes.Set volumes, uint timestamp, List<string> notes)
    {
        map = Io.Tier0Strings.LowerAscii(map);
        var outputs = new List<Output>();
        bool RendersAsWorld(string cls) => schema.IsSolidClass(cls) && schema.HasFlag(cls, "render_as_world_but_physics_as_entity");

        // Visibility from the trace scene.
        var packages = new[] { "csgo", "core" }.Select(d => { var pk = new Package(); pk.Read(Path.Combine(game, d, "pak01_dir.vpk")); return pk; }).ToList();
        VoxelVisibility vxvs;
        List<(Vector3 Min, Vector3 Max)>[] flat;
        try
        {
            var visFlags = new MaterialVisFlags.Source([Path.Combine(game, "csgo_addons", addon)], packages);
            var rte = TraceScene.Environment(TraceScene.Triangles(MapMeshes.Read(document), content.Material, mat => visFlags[mat], RendersAsWorld));
            (vxvs, flat) = VisBuild.RunWithBlocks(rte, VisConfig.FromMap(MapEntities.From(document), schema));
        }
        finally
        {
            foreach (var p in packages)
                p.Dispose();
        }
        outputs.Add(new($"maps/{map}/world_visibility.vvis_c", VisibilityAuthor.Container(vxvs)));
        var mutual = VisOutput.MutualVisibility(vxvs);

        // BuildNode, the mesh lists, CompileNode.
        var world = WorldNodeBuild.WorldEntries(document, content, shaders, RendersAsWorld);
        var props = WorldNodeBuild.PropEntries(document, content, shaders, notes);
        var descriptors = NodeOverlays.FromWorld(document);
        var (built, overlays) = WorldNodeBuild.BuildNode(world, props, descriptors);
        var envMaps = WorldNodeBuild.EnvMaps(built, volumes);
        var lists = WorldNodeBuild.MergerInputs(built, overlays, descriptors, envMaps, content, shaders, kinds: [.. MeshLists.Order.Select(o => o.Kind)])
            .ToDictionary(kv => kv.Key, kv => kv.Value.Select(m => new WorldNodeCompile.Entry(m.Material, m.Stride, m.Vertices, m.Indices, m.Facts)).ToList());
        var result = WorldNodeCompile.Compile(lists, flat, mutual);
        notes.AddRange(result.Unported.Select(u => "world node list not ported: " + u));
        var (nodeMin, nodeMax) = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
        foreach (var b in built)
            for (var k = 0; k + 2 < b.Vertices.Length; k += b.Source.Entry.Stride)
            {
                var p = new Vector3(b.Vertices[k], b.Vertices[k + 1], b.Vertices[k + 2]);
                (nodeMin, nodeMax) = (Vector3.Min(nodeMin, p), Vector3.Max(nodeMax, p));
            }

        // The models.
        var facts = new Dictionary<string, WorldNodeModels.MaterialFacts>(StringComparer.OrdinalIgnoreCase);
        WorldNodeModels.MaterialFacts Material(string path)
        {
            if (!facts.TryGetValue(path, out var f))
                facts[path] = f = content.Read(path + "_c") is { } bytes
                    ? new WorldNodeModels.MaterialFacts(Source2.Compiler.MaterialAuthor.ExtractInputSignature(bytes), Source2.Compiler.MaterialAuthor.ExtractDepthSignature(bytes))
                    : new WorldNodeModels.MaterialFacts([], []);
            return f;
        }
        string ModelPath(string name) => $"maps/{map}/worldnodes/{name}.vmdl";
        void Write(string name, WorldNodeModels.Built b)
        {
            var file = WorldNodeModelAuthor.Container(b.Buffers, WorldNodeModelTrees.Mdat(b.Model), WorldNodeModelTrees.Ctrl(b.Model), b.References,
                                                      WorldNodeModelTrees.Red2(b.Model), WorldNodeModelTrees.Data(b.Model));
            outputs.Add(new(ModelPath(name) + "_c", file));
        }
        static float Boost(WrbMeshEntry f) => f.Field1a3 != 0 && (f.ObjectFlags & 0x100000) != 0 ? f.Field_b8 : 1f;
        var sceneObjects = new List<WorldNodeSceneTrees.SceneObject>();
        foreach (var o in result.Objects)
        {
            Write(o.Name, WorldNodeModels.Build(ModelPath(o.Name), o.Meshes, false, Material));
            var f = o.Meshes[0].Facts;
            sceneObjects.Add(new(ModelPath(o.Name), o.Flags, o.OverlayOrder, f.Cubemap, f.LightProbe, Boost(f)));
        }
        var aggregates = new List<WorldNodeSceneTrees.Aggregate>();
        foreach (var a in result.Aggregates)
        {
            var b = WorldNodeModels.Build(ModelPath(a.Name), a.Meshes, true, Material);
            Write(a.Name, b);
            aggregates.Add(new(ModelPath(a.Name), a.Layer, [.. b.DrawSources.Select((e, i) => new WorldNodeSceneTrees.AggregateMesh(i, e.Facts.ObjectFlags, e.Facts.LightProbe,
                new Vector3(e.Facts.Field28.X, e.Facts.Field28.Y, e.Facts.Field28.Z) * 255f))]));
        }
        // The static prop aggregates, named per material as BuildAggregate names them.
        var attributes = new Dictionary<string, MeshEntryFlags.IAttributes>(StringComparer.OrdinalIgnoreCase);
        MeshEntryFlags.IAttributes Attributes(string material)
        {
            if (!attributes.TryGetValue(material, out var a))
                attributes[material] = a = content.Material(material) is { } info ? MaterialAttributes.Of(info, shaders, content.TextureSize) : MaterialAttributes.Empty;
            return a;
        }
        var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var agg in NodePropAggregates.FromWorld(document, content, Attributes, new Vector2(nodeMin.X, nodeMin.Y), notes))
        {
            var prefix = $"agg_prop_{Path.GetFileNameWithoutExtension(agg.Material.Replace('\\', '/'))}_";
            var n = counters.GetValueOrDefault(prefix);
            counters[prefix] = n + 1;
            var name = $"n0_lr0_{prefix}{n}";
            Write(name, WorldNodeModels.BuildPropAggregate(ModelPath(name), agg, Material));
            aggregates.Add(new(ModelPath(name), 0, [.. agg.Fragments.Select(f => new WorldNodeSceneTrees.AggregateMesh(f.Draw, f.AggregateFlags,
                EnvVolumes.ForEntry(new Vector3(float.MaxValue), "", f.Attributes, () => f.MeshBounds, volumes).Probe,
                new Vector3(f.Tint.X, f.Tint.Y, f.Tint.Z) * 255f) { Transform = f.Matrix, LodMask = f.LodMask, LodSetup = f.LodSetup })])
            {
                LodSetups = [.. agg.LodSetups.Select(l => new WorldNodeSceneTrees.LodSetup(l.Origin, l.MaxScale, l.SwitchDistances))],
            });
        }

        // The node's scene and manifest, the world and its manifest.
        var vwnod = $"maps/{map}/worldnodes/n0.vwnod";
        outputs.Add(new(vwnod + "_c", Source2ContainerAuthor.AuthorKv3Tree(WorldNodeSceneTrees.Data(sceneObjects, aggregates),
                                                                         ValveResourceFormat.Serialization.KeyValues.KV3IDLookup.Get("generic"), ".vwnod")));
        outputs.Add(new($"maps/{map}/worldnodes/n0.vrman_c", ResourceManifestAuthor.AuthorSaved([vwnod], $"maps/{map}/worldnodes/n0.vrman", addon)));
        var vwrld = WorldTrees.Data(1024f, [new WorldTrees.Node(nodeMin, nodeMax, $"maps\\{map.Replace('/', '\\')}\\worldnodes\\n0")],
                                    [$"maps/{map}/entities/default_ents.vents"], timestamp, (ulong)Random.Shared.NextInt64());
        outputs.Add(new($"maps/{map}/world.vwrld_c", Source2ContainerAuthor.AuthorKv3Tree(vwrld, ValveResourceFormat.Serialization.KeyValues.KV3IDLookup.Get("generic"), ".vwrld")));
        outputs.Add(new($"maps/{map}/world.vrman_c", ResourceManifestAuthor.Author(
            [$"maps/{map}/world.vwrld", $"maps/{map}/world_physics.vrman", $"maps/{map}/world_visibility.vvis"])));
        return outputs;
    }
}
