using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>Diagnosis (CUBEDCKEYS=&lt;package&gt;): each world node model's draw call keys and vertex fields, one line per material.</summary>
public sealed class CubemapDrawCallKeysProbe(ITestOutputHelper output)
{
    [Fact]
    public void ListDrawCalls()
    {
        if (Environment.GetEnvironmentVariable("CUBEDCKEYS") is not { Length: > 0 } vpk)
            return;
        using var package = new ValvePak.Package();
        package.Read(vpk);
        var seen = new HashSet<string>();
        foreach (var entry in package.Entries!.SelectMany(kv => kv.Value).Where(e => e.GetFullPath().EndsWith(".vwnod_c", StringComparison.Ordinal)))
        {
            package.ReadEntry(entry, out var nodeBytes);
            using var nodeResource = new ValveResourceFormat.Resource();
            nodeResource.Read(new MemoryStream(nodeBytes));
            foreach (var o in ((WorldNode)nodeResource.DataBlock!).Data.GetArray("m_aggregateSceneObjects") ?? [])
            {
                var keys = string.Join(" ", o.Children.Where(c => !c.Value.IsArray && c.Value.ValueType != ValveKeyValue.KVValueType.Collection).Select(c => $"{c.Key}={c.Value}"));
                var m0 = o.GetArray("m_aggregateMeshes").FirstOrDefault();
                var mk = m0 == null ? "" : string.Join(" ", m0.Children.Where(c => c.Value.ValueType != ValveKeyValue.KVValueType.Collection).Select(c => $"{c.Key}={(c.Value.IsArray ? string.Join(",", c.Value.Values) : c.Value)}"));
                output.WriteLine($"AGG {o.GetStringProperty("m_renderableModel")}: {keys} | mesh0 {mk}");
            }
        }
        foreach (var entry in package.Entries!.GetValueOrDefault("vmdl_c") ?? [])
        {
            if (!entry.DirectoryName.Contains("worldnodes", StringComparison.Ordinal))
                continue;
            package.ReadEntry(entry, out var bytes);
            using var resource = new ValveResourceFormat.Resource();
            resource.Read(new MemoryStream(bytes));
            foreach (var (mesh, _, _, _) in ((Model)resource.DataBlock!).GetEmbeddedMeshesAndLoD())
                foreach (var dc in mesh.Data.GetArray("m_sceneObjects").SelectMany(so => so.GetArray("m_drawCalls")))
                {
                    var material = dc.GetStringProperty("m_material");
                    if (!seen.Add(material))
                        continue;
                    var keys = string.Join(" ", dc.Children.Where(c => !c.Value.IsArray && c.Value.ValueType != ValveKeyValue.KVValueType.Collection).Select(c => $"{c.Key}={c.Value}"));
                    var fields = string.Join(" ", mesh.VBIB.VertexBuffers[0].InputLayoutFields.Select(f => $"{f.SemanticName}{f.SemanticIndex}:{f.Format}"));
                    output.WriteLine($"{material}: {keys} | {fields} | streams {mesh.VBIB.VertexBuffers.Count}");
                }
        }
    }
}
