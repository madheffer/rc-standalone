using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <c>VTXSTREAM=&lt;model.vmdl_c&gt;[;...]|&lt;shader semantic&gt;</c>: the distinct raw values of one vertex attribute
/// (by m_szShaderSemantic, e.g. VertexPaintTintColor) in each embedded mesh's vertex buffers.
/// <c>VTXLAYOUT=&lt;package.vpk&gt;</c>: every vertex buffer layout of every model in a package.
/// </summary>
public sealed class VertexStreamValuesProbe(ITestOutputHelper output)
{
    [Fact]
    public void Layouts()
    {
        if (Environment.GetEnvironmentVariable("VTXLAYOUT") is not { Length: > 0 } vpk)
            return;
        using var pkg = new ValvePak.Package();
        pkg.Read(vpk);
        foreach (var entry in pkg.Entries.Where(e => e.Key == "vmdl_c").SelectMany(e => e.Value).OrderBy(e => e.GetFullPath(), StringComparer.Ordinal))
        {
            pkg.ReadEntry(entry, out var bytes);
            using var res = new ValveResourceFormat.Resource();
            res.Read(new MemoryStream(bytes));
            var model = (ValveResourceFormat.ResourceTypes.Model)res.DataBlock!;
            foreach (var (mesh, _, _, _) in model.GetEmbeddedMeshesAndLoD())
                foreach (var vb in mesh.VBIB.VertexBuffers)
                    output.WriteLine($"{entry.GetFullPath()}: {vb.ElementCount} x {vb.ElementSizeInBytes}: "
                        + string.Join(", ", vb.InputLayoutFields.Select(f => $"{f.SemanticName}{f.SemanticIndex}/{f.ShaderSemantic} {f.Format}@{f.Offset}")));
        }
    }

    [Fact]
    public void DistinctValues()
    {
        if (Environment.GetEnvironmentVariable("VTXSTREAM") is not { Length: > 0 } spec || spec.Split('|') is not [var files, var semantic])
            return;
        foreach (var file in files.Split(';'))
        {
            using var res = new ValveResourceFormat.Resource();
            res.Read(file);
            var model = (ValveResourceFormat.ResourceTypes.Model)res.DataBlock!;
            foreach (var (mesh, _, _, _) in model.GetEmbeddedMeshesAndLoD())
                foreach (var vb in mesh.VBIB.VertexBuffers)
                {
                    var field = vb.InputLayoutFields.FirstOrDefault(f => f.ShaderSemantic == semantic);
                    if (field.ShaderSemantic != semantic)
                    {
                        output.WriteLine($"{Path.GetFileName(file)}: no {semantic} ({vb.ElementCount} vertices)");
                        continue;
                    }
                    var size = (int)vb.ElementSizeInBytes;
                    var values = new Dictionary<string, int>();
                    for (var i = 0; i < vb.ElementCount; i++)
                    {
                        var hex = Convert.ToHexString(vb.Data.AsSpan(i * size + (int)field.Offset, 4));
                        values[hex] = values.GetValueOrDefault(hex) + 1;
                    }
                    output.WriteLine($"{Path.GetFileName(file)}: {semantic} {field.Format} at {field.Offset}, {vb.ElementCount} vertices: "
                        + string.Join(", ", values.OrderByDescending(v => v.Value).Take(8).Select(v => $"{v.Key} x{v.Value}")));
                }
        }
    }
}
