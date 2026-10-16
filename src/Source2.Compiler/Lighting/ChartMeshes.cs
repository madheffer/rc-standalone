using System.Runtime.InteropServices;
using Source2.Compiler.Maps;

namespace Source2.Compiler.Lighting;

/// <summary>
/// The meshes resourcecompiler charts for the lightmap and the order it takes
/// them in (docs/BAKED.md "Lightmap charts in resourcecompiler"):
/// <list type="bullet">
/// <item><see cref="Strip"/> and <see cref="IsBaked"/>:
/// CWorldRendererBuilderNode::BakeLightMaps (0923 18025db60), after the
/// T-junction fix, removes every PerVertexLighting stream from every entry
/// (1812e2ec0 until none is left), then moves out of the node each entry
/// whose attributes have none of 0x2000000909 and bit 29 and whose +0x98
/// (bakelighting in a baking build) is set, keeping their order.</item>
/// <item><see cref="Sort"/>: CStaticLightingProcessor::BakeLighting
/// (180217740) sorts them with std::sort (18021b530): more vertices first,
/// equal counts by memcmp of the vertex floats.</item>
/// </list>
/// </summary>
internal static class ChartMeshes
{
    /// <summary>One node entry as the charting sees it.</summary>
    /// <param name="MeshScale">The CMesh's +0x184 (V_powf(2, lightmapScaleBias) for a map mesh's face set, 1 for a model's).</param>
    /// <param name="EntryScale">The entry's +0x9c (a prop's V_powf(2, lightmapscalebias), 1 for the world).</param>
    /// <param name="Shared">+0x1a0 and +0xc0 bit 4: GenerateCharts charts the CMesh once and places it per instance.</param>
    public sealed record Entry(float[] Vertices, int Stride, IReadOnlyList<Physics.MeshWeld.Stream> Streams, int[] Indices, string Material,
                               ulong Attributes, bool Baked, int TextureWidth, int TextureHeight, float MeshScale = 1f, float EntryScale = 1f,
                               bool Shared = false)
    {
        public int VertexCount => Vertices.Length / Stride;
    }

    /// <summary>18025db60's test for an entry that bakes into the lightmap.</summary>
    public static bool IsBaked(Entry e)
        => (e.Attributes & 0x2000000909UL) == 0 && e.Baked && ((e.Attributes >> 29) & 1) != 0;

    /// <summary>The entry without its PerVertexLighting streams, the later streams moved down.</summary>
    public static Entry Strip(Entry e) => Without(e, "PerVertexLighting");

    /// <summary>The entry without the streams named <paramref name="name"/>, the vertices repacked.</summary>
    public static Entry Without(Entry e, string name)
    {
        if (!e.Streams.Any(s => s.Name == name))
            return e;
        var kept = e.Streams.Where(s => s.Name != name).ToList();
        var stride = kept.Sum(s => s.Count);
        var n = e.VertexCount;
        var v = new float[n * stride];
        var streams = new List<Physics.MeshWeld.Stream>(kept.Count);
        var at = 0;
        foreach (var s in kept)
        {
            for (var i = 0; i < n; i++)
                Array.Copy(e.Vertices, i * e.Stride + s.First, v, i * stride + at, s.Count);
            streams.Add(s with { First = at });
            at += s.Count;
        }
        return e with { Vertices = v, Stride = stride, Streams = streams };
    }

    /// <summary>18021b530's order, element for element (MSVC std::sort).</summary>
    public static Entry[] Sort(IEnumerable<Entry> entries)
    {
        var a = entries.ToArray();
        MsvcSort.Sort(a, Less);
        return a;
    }

    /// <summary>
    /// lightmap_packing_geometry.dat's content from the node's entries (before
    /// BuildNode's closing weld): stripped, chosen, sorted, prepared
    /// (<see cref="ChartMeshPrep"/>), charted (<see cref="LightmapCharts"/>),
    /// assembled (<see cref="PackingAssembly"/>) and packed
    /// (<see cref="LightmapPacker.Pack"/>), each entry through GenerateCharts'
    /// unique-mesh path (180212850): its aspect the representative texture's
    /// width over height (1 when either is 0), its uv set 0, and its scale the
    /// CMesh's +0x184 times the entry's +0x9c (times 182a8cc50's tier for the
    /// CMesh's +0x58 bits, none set on the maps measured).
    /// </summary>
    /// <param name="renderBackfaces">Whether a material's RenderBackfaces is non-zero.</param>
    /// <param name="luxelUvs">Receives the luxel uvs the packer starts from, when given.</param>
    /// <param name="charted">Receives the charted meshes, in order (for <see cref="LightmapSeams"/>), when given.</param>
    public static (PackingGeometry Geometry, bool Packed) Build(IEnumerable<Entry> entries, IReadOnlyList<System.Numerics.Vector3[]> volumes,
                                                                Func<string, bool> renderBackfaces, LightmapSettings settings, float transitionRegion,
                                                                List<float>? luxelUvs = null, List<PackingAssembly.Mesh>? charted = null)
    {
        var identity = new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0 };
        var options = new ChartMeshPrep.Options(settings.Weld, !settings.Sh2Dc, true, transitionRegion);
        var meshes = new List<PackingAssembly.Mesh>();
        foreach (var e in Sort(entries.Select(Strip).Where(IsBaked)))
        {
            // 1802191e0 / 1802104c0 / 18020f690: none of the maps measured has one.
            if (e.Shared)
                throw new NotSupportedException("a shared lightmap chart mesh (an instanced entry, +0xc0 bit 4) is not ported");
            var aspect = e.TextureWidth < 1 || e.TextureHeight < 1 ? 1f : (float)e.TextureWidth / e.TextureHeight;
            var p = ChartMeshPrep.Prepare(e, options, false, renderBackfaces(e.Material), volumes, identity, aspect, e.MeshScale * e.EntryScale);
            meshes.Add(new PackingAssembly.Mesh(LightmapCharts.Run(p.Input), p.Input.Stride, p.Input.PositionOffset, p.NormalOffset, p.TangentOffset,
                                                identity, 0, false, 1f, []));
        }
        var (geometry, uvs) = PackingAssembly.Assemble(meshes);
        luxelUvs?.AddRange(uvs);
        charted?.AddRange(meshes);
        var packed = LightmapPacker.Pack(geometry, uvs, settings.Width, settings.Height, settings.Mip, settings.Passes);
        return (new PackingGeometry
        {
            VertexStride = geometry.VertexStride, Vertices = geometry.Vertices, AtlasUv = uvs, Indices = geometry.Indices, Charts = geometry.Charts,
            TriangleCharts = geometry.TriangleCharts, Offsets = geometry.Offsets,
        }, packed);
    }

    // 18021e610's comparison: a goes first when it has more vertices, or as
    // many and its vertex bytes compare lower (memcmp over count * stride floats).
    private static bool Less(Entry a, Entry b)
    {
        if (a.VertexCount != b.VertexCount)
            return a.VertexCount > b.VertexCount;
        var n = a.VertexCount * a.Stride;
        var x = MemoryMarshal.AsBytes(a.Vertices.AsSpan(0, n));
        var y = MemoryMarshal.AsBytes(b.Vertices.AsSpan(0, Math.Min(n, b.Vertices.Length)));
        return x.SequenceCompareTo(y) < 0;
    }
}
