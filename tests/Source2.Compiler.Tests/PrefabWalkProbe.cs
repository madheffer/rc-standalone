using Source2.Compiler.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>Scratch (PREFABWALK=addon|map): the meshes and entities the walk meets inside prefabs.</summary>
public class PrefabWalkProbe(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("PREFABWALK") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        var source = MapFixtures.VmapSource(p[0], p[1])!;
        var doc = DmxBinary.ReadFile(source);
        MapPrefabs.Attach(doc, MapPrefabs.FromContent(Path.GetDirectoryName(Path.GetDirectoryName(source))!));
        var (meshes, entities) = MapMeshes.ReadWithEntities(doc);
        foreach (var m in meshes)
            output.WriteLine($"mesh {string.Join(":", m.Prefabs.Append(m.NodeId))} parent {m.ParentType} hidden {m.Hidden} faces {m.Faces.Length} physicsType {m.Element?.Get<string>("physicsType")}");
        foreach (var e in entities)
            output.WriteLine($"entity {string.Join(":", e.Prefabs.Append(e.Element.GetValue<int>("nodeID") ?? -1))} {e.Element.Get<DmxBinary.Element>("entity_properties")?.Get<string>("classname")} hidden {e.Hidden}");
    }
}
