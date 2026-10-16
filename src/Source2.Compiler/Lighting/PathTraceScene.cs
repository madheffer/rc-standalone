using System.Runtime.InteropServices;
using ValveKeyValue;

namespace Source2.Compiler.Lighting;

/// <summary>
/// The path-trace scene resourcecompiler hands vrad3, WRB_BuildPathTraceSceneInfo
/// (0923 180285eb0), which CWorldRendererBuilder::Build (1802472a0) runs before
/// the nodes compile: scene_meshes.kv3, binary KV3 (generic format,
/// uncompressed) of each distinct CMesh the instances use, as CMesh's KV3
/// writer (1812e4550) puts it, and scene_info.kv3.
/// </summary>
internal static class PathTraceScene
{
    public static readonly Guid GenericFormat = new("7412167c-06e9-4698-aff2-e63eb59037e7");

    /// <summary>A CMesh's stream as its KV3 writer reads it: +0, +8, +0x10 .. +0x1d of the 0x28-byte record.</summary>
    public sealed record Stream(string Name, string NameDelta, int SemanticIndex, int Offset, int Size, bool Delta, bool NoCompression);

    /// <summary>The CMesh fields 1812e4550 writes.</summary>
    public sealed record Mesh(float[] Vertices, int Stride, int[] Indices, IReadOnlyList<Stream> Streams, string Material)
    {
        public int VertexCount => Vertices.Length / Stride;
        public int MeshletCount { get; init; }
        public uint LodGroupMask { get; init; }
        public string MeshName { get; init; } = "";
        public int MeshId { get; init; }
        public float[] Tint { get; init; } = [1f, 1f, 1f, 1f];
        /// <summary>+0x184: V_powf(2, the face set's lightmapScaleBias).</summary>
        public float LightmapScale { get; init; } = 1f;
        public bool NoCollapse { get; init; }
        public bool PackSharedVb { get; init; }
        public bool UseBaseVertex { get; init; }
        public bool UseTintColor { get; init; }
        public bool IsLineMesh { get; init; }
        public bool BakedTwoSided { get; init; }
        /// <summary>+0x152, which 1812e4550 writes under no_collapse again, after +0x14c.</summary>
        public bool NoCollapse2 { get; init; }
        public uint CollisionType { get; init; }
    }

    /// <summary>
    /// The meshes of the node's world entries as BuildNode receives them, in
    /// order, those with attributes 0x2000000001 left out (180285eb0's instance
    /// test): each entry's CMesh in its own layout.
    /// </summary>
    public static List<Mesh> WorldMeshes(IReadOnlyList<Maps.WorldNodeBuild.Node> world)
    {
        var meshes = new List<Mesh>();
        foreach (var n in world)
        {
            if ((n.Header.Attributes & 0x2000000001UL) != 0)
                continue;
            var e = n.Entry;
            var streams = new List<Stream>();
            foreach (var s in e.Streams)
                streams.Add(new Stream(s.Name, "", streams.Count(x => x.Name == s.Name), s.First, s.Count, false,
                                       s.Name.StartsWith("texcoord", StringComparison.Ordinal) && e.PreciseTexcoords));
            meshes.Add(new Mesh(e.Vertices, e.Stride, e.Indices, streams, e.Material)
            {
                LightmapScale = Maps.LightPow.Pow(2f, e.LightmapBias), CollisionType = 4,
            });
        }
        return meshes;
    }

    /// <summary>scene_meshes.kv3's bytes.</summary>
    public static byte[] MeshesFile(IReadOnlyList<Mesh> meshes)
    {
        var list = KVObject.Array();
        foreach (var m in meshes)
            list.Add(MeshNode(m));
        var root = Collection(("meshes", list));
        return Kv3V5Writer.Write(root, GenericFormat, ValveResourceFormat.ResourceTypes.KV3BinaryCompressionMethod.Uncompressed);
    }

    // 1812e4550, key for key.
    private static KVObject MeshNode(Mesh m)
    {
        var attributes = KVObject.Array();
        foreach (var s in m.Streams)
            attributes.Add(Collection(("name", new KVObject(s.Name)), ("name_delta", new KVObject(s.NameDelta)), ("semantic_index", Int(s.SemanticIndex)),
                                      ("offset", Int(s.Offset)), ("size", Int(s.Size)), ("delta", new KVObject(s.Delta)), ("no_compression", new KVObject(s.NoCompression))));
        var tint = KVObject.Array();
        foreach (var f in m.Tint)
            tint.Add(new KVObject(f));
        return Collection(
            ("vertex_count", Int(m.VertexCount)), ("vertex_stride", Int(m.Stride)),
            ("vertices", KVObject.Blob(MemoryMarshal.AsBytes(m.Vertices.AsSpan()).ToArray())),
            ("index_count", Int(m.Indices.Length)), ("indices", KVObject.Blob(MemoryMarshal.AsBytes(m.Indices.AsSpan()).ToArray())),
            ("meshlet_count", Int(m.MeshletCount)), ("attribute_count", Int(m.Streams.Count)), ("attributes", attributes),
            ("lod_group_mask", new KVObject(m.LodGroupMask)), ("material_name", new KVObject(m.Material)), ("mesh_name", new KVObject(m.MeshName)),
            ("mesh_id", Int(m.MeshId)), ("tint_color", tint), ("lightmap_scale", new KVObject((double)m.LightmapScale)),
            ("no_collapse", new KVObject(m.NoCollapse2)), ("pack_sharedvb", new KVObject(m.PackSharedVb)), ("use_basevertex", new KVObject(m.UseBaseVertex)),
            ("use_tint_color", new KVObject(m.UseTintColor)), ("is_line_mesh", new KVObject(m.IsLineMesh)), ("baked_twosided", new KVObject(m.BakedTwoSided)),
            ("collision_type", new KVObject(m.CollisionType)));
    }

    // KV3's int setter (181eb8970): the writer gives 0 and 1 their own types.
    private static KVObject Int(int v) => new(v);

    private static KVObject Collection(params (string Key, KVObject Value)[] members)
    {
        var o = KVObject.Collection();
        foreach (var (k, v) in members)
            o.Add(k, v);
        return o;
    }
}
