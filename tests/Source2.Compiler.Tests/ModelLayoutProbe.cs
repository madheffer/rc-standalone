using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// Scratch probe (MODELLAYOUT=a.vpk[,b.vpk]|substring): every model in the
/// packages whose path contains the substring: its vertex buffers' element
/// count and input layout (semantic, index, format), and its draw calls.
/// </summary>
public class ModelLayoutProbe(ITestOutputHelper output)
{
    [Fact]
    public void Layouts()
    {
        if (Environment.GetEnvironmentVariable("MODELLAYOUT") is not { Length: > 0 } spec)
            return;
        var p = spec.Split('|');
        foreach (var vpk in p[0].Split(','))
        {
            using var package = new ValvePak.Package();
            package.Read(vpk);
            foreach (var entry in (package.Entries?.GetValueOrDefault("vmdl_c") ?? []).Where(e => e.GetFullPath().Contains(p[1], StringComparison.Ordinal)).OrderBy(e => e.GetFullPath()))
            {
                package.ReadEntry(entry, out var bytes);
                using var resource = new Resource();
                resource.Read(new MemoryStream(bytes));
                var model = (Model)resource.DataBlock!;
                output.WriteLine($"{Path.GetFileName(vpk)} {entry.GetFullPath()}");
                foreach (var (mesh, _, _, _) in model.GetEmbeddedMeshesAndLoD())
                {
                    foreach (var vb in mesh.VBIB.VertexBuffers)
                        output.WriteLine($"  vb {vb.ElementCount} x {vb.ElementSizeInBytes}: {string.Join(" ", vb.InputLayoutFields.Select(f => $"{f.SemanticName}{f.SemanticIndex}:{f.Format}"))}");
                    foreach (var so in mesh.Data.GetArray("m_sceneObjects"))
                        foreach (var dc in so.GetArray("m_drawCalls"))
                            output.WriteLine($"  draw {dc.GetStringProperty("m_material")} indices {dc.GetInt32Property("m_nIndexCount")} vertices {dc.GetInt32Property("m_nVertexCount")}");
                }
            }
        }
    }
}
