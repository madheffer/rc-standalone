using System.Buffers.Binary;
using Source2.Compiler.Maps;

namespace Source2.Compiler.Lighting;

/// <summary>
/// What CWorldRendererBuilderNode::BakeLightMaps (0923 18025db60) leaves in the node of a baked
/// compile, after the T-junction fix and before the closing steps (which change none of them on
/// ze_hold_em_p; tools/vis/capture_nodeentries.py --full, BakeLightMaps:out):
/// <list type="bullet">
/// <item>the entries that do not bake into the lightmap, in node order, unchanged;</item>
/// <item>then the baked ones (<see cref="ChartMeshes.IsBaked"/>), in BakeLighting's sorted order
/// (<see cref="ChartMeshes.Sort"/>), each now its chart mesh (the 1/32 weld of
/// <see cref="ChartMeshPrep"/>, without PerVertexLighting) with a LightmapUV stream of two floats
/// appended (1802171b0).</item>
/// </list>
/// The LightmapUV values are the packed atlas uvs, snapped as BakeLighting does after PackCharts
/// (180217740): R = floor(32767 / side) side 2 (57344 at 4096), u = floorf(u / (1 / R) + 0.5) (1 / R),
/// then stored over the world's uv scale, times R / 65535 (world.vwrld's m_vLightmapUvScale is
/// 65535 / R). The capture's 81,324 values are matched bit for bit. The u axis takes the height's R
/// and v the width's, as 180217740 does; CS2's atlas is square.
/// </summary>
internal static class LightmapNodeBake
{
    public static List<WorldNodeBuild.Built> Apply(IReadOnlyList<WorldNodeBuild.Built> built, IReadOnlyList<System.Numerics.Vector3[]> volumes,
                                                    Func<string, bool> renderBackfaces, LightmapSettings settings, float transitionRegion)
    {
        var entries = built.Select(ToEntry).ToList();
        var charted = new List<PackingAssembly.Mesh>();
        var (geometry, packed) = ChartMeshes.Build(entries, volumes, renderBackfaces, settings, transitionRegion, charted: charted);
        if (!packed)
            throw new NotSupportedException("the lightmap charts did not pack (BakeLighting fails and the compile keeps the unbaked node)");
        var order = ChartMeshes.SortedBaked(entries);
        if (order.Count != charted.Count)
            throw new InvalidOperationException($"{order.Count} baked entries against {charted.Count} chart meshes");

        float R(int side) => LightmapQueryData.UvScale(side);
        var (ru, rv) = (R(settings.Height), R(settings.Width));
        float invU = 1f / ru, invV = 1f / rv;
        float storeU = ru / 65535f, storeV = rv / 65535f;

        var result = new List<WorldNodeBuild.Built>(built.Count);
        var bakedSet = order.ToHashSet();
        for (var i = 0; i < built.Count; i++)
            if (!bakedSet.Contains(i))
                result.Add(built[i]);
        var baseVertex = 0;
        for (var k = 0; k < order.Count; k++)
        {
            var b = built[order[k]];
            var stripped = ChartMeshes.Strip(entries[order[k]]);
            var r = charted[k].Result;
            var stride = stripped.Stride;
            var n = r.VertexCount;
            var vertices = new float[n * (stride + 2)];
            for (var v = 0; v < n; v++)
            {
                for (var f = 0; f < stride; f++)
                    vertices[v * (stride + 2) + f] = BinaryPrimitives.ReadSingleLittleEndian(r.Vertices.AsSpan((v * stride + f) * 4));
                var u = geometry.AtlasUv[(baseVertex + v) * 2];
                var w = geometry.AtlasUv[(baseVertex + v) * 2 + 1];
                vertices[v * (stride + 2) + stride] = MathF.Floor(u / invU + 0.5f) * invU * storeU;
                vertices[v * (stride + 2) + stride + 1] = MathF.Floor(w / invV + 0.5f) * invV * storeV;
            }
            baseVertex += n;
            var streams = new List<Physics.MeshWeld.Stream>(stripped.Streams) { new("LightmapUV", stride, 2, false, 0x2b) };
            var entry = b.Source.Entry with { Stride = stride + 2, Streams = streams, Vertices = vertices, Indices = r.Indices };
            result.Add(new WorldNodeBuild.Built(b.Source with { Entry = entry }, vertices, r.Indices));
        }
        return result;
    }

    /// <summary>A built entry as the charting reads it (ChartMeshesTests.OurInputs: the header's bake fields).</summary>
    public static ChartMeshes.Entry ToEntry(WorldNodeBuild.Built b)
        => new(b.Vertices, b.Source.Entry.Stride, b.Source.Entry.Streams, b.Indices, b.Source.Entry.Material,
               b.Source.Header.Attributes, b.Source.Header.Baked, b.Source.Header.TextureWidth, b.Source.Header.TextureHeight,
               b.Source.Header.Byte1a0 != 0 ? 1f : LightPow.Pow(2f, b.Source.Entry.LightmapBias), b.Source.Header.Field9c,
               b.Source.Header.Byte1a0 != 0 && (b.Source.Header.Fieldc0 & 0x10) != 0);
}
