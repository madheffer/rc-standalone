using System.Numerics;
using Source2.Compiler.Maps;
using ValvePak;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;
using Xunit;
using Xunit.Abstractions;

namespace Source2.Compiler.Tests;

/// <summary>
/// <see cref="UvDensity"/> against every draw call of the node models Valve
/// compiled for probe01, cardtest and atixref, computed from the draw call's
/// own decoded positions and texcoords. Plain cluster meshes (<c>_mesh_cm</c>)
/// must match bit for bit. Aggregates, overlays, decals, blocklight and
/// no-merge models are reported: some differ in the last bits, which says
/// their density is taken before their buffers' final values (not read yet).
/// Draw calls whose texcoords are stored at half precision are skipped.
/// </summary>
public class UvDensityTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("probe01")]
    [InlineData("cardtest")]
    [InlineData("atixref")]
    public void MatchesEveryDrawCall(string map)
    {
        var vpk = Path.Combine(@"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\csgo_addons\s2c_rc_probe\maps", map + ".vpk");
        if (!File.Exists(vpk))
            return;
        using var package = new Package();
        package.Read(vpk);
        int same = 0, high = 0, other = 0, skipped = 0, reported = 0;
        var misses = new List<string>();
        foreach (var entry in package.Entries.GetValueOrDefault("vmdl_c") ?? [])
        {
            var path = entry.GetFullPath();
            if (!path.Contains("/worldnodes/", StringComparison.Ordinal))
                continue;
            package.ReadEntry(entry, out var bytes);
            using var resource = new Resource();
            resource.Read(new MemoryStream(bytes));
            var mesh = ((Model)resource.DataBlock!).GetEmbeddedMeshesAndLoD().First().Mesh;
            foreach (var so in mesh.Data.GetArray("m_sceneObjects"))
                foreach (var dc in so.GetArray("m_drawCalls"))
                {
                    var vb = mesh.VBIB.VertexBuffers[dc.GetArray("m_vertexBuffers")[0].GetInt32Property("m_hBuffer")];
                    var posField = vb.InputLayoutFields.FirstOrDefault(f => f.SemanticName == "POSITION");
                    var uvField = vb.InputLayoutFields.FirstOrDefault(f => f.SemanticName == "TEXCOORD" && f.SemanticIndex == 0);
                    if (posField.SemanticName == null || uvField.SemanticName == null || uvField.Format != DXGI_FORMAT.R32G32_FLOAT)
                    {
                        skipped++;
                        continue;
                    }
                    var positions = VBIB.GetVector3AttributeArray(vb, posField);
                    var uvs = VBIB.GetVector2AttributeArray(vb, uvField);
                    var ib = mesh.VBIB.IndexBuffers[dc.GetSubCollection("m_indexBuffer").GetInt32Property("m_hBuffer")];
                    var start = dc.GetInt32Property("m_nStartIndex");
                    var count = dc.GetInt32Property("m_nIndexCount");
                    var baseVertex = dc.GetInt32Property("m_nBaseVertex");
                    var idx = new int[count];
                    for (var i = 0; i < count; i++)
                        idx[i] = baseVertex + (ib.ElementSizeInBytes == 2 ? BitConverter.ToUInt16(ib.Data, (start + i) * 2) : BitConverter.ToInt32(ib.Data, (start + i) * 4));
                    var valve = (float)dc.GetDoubleProperty("m_flUvDensity");
                    var ours = UvDensity.Compute(positions, uvs, idx);
                    if (BitConverter.SingleToInt32Bits(ours) == BitConverter.SingleToInt32Bits(valve))
                        same++;
                    else if (UvDensity.Compute(positions, uvs, idx, 95) == valve)
                        high++;
                    else if (path.Contains("_mesh_cm", StringComparison.Ordinal))
                    {
                        other++;
                        misses.Add($"{Path.GetFileName(path)} {dc.GetStringProperty("m_material")}: valve {valve:R} ours {ours:R}");
                    }
                    else
                        reported++;
                }
        }
        output.WriteLine($"{map}: {same} exact at the 20th percentile, {high} at the 95th, {other} cluster meshes differ, {reported} others differ, {skipped} skipped");
        foreach (var m in misses.Take(20))
            output.WriteLine(m);
        Assert.Equal(0, other);
    }
}
