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

    /// <summary>
    /// 180285eb0's instance flags: from attribute bits 17 and 16, 1 (both),
    /// 5 (16 only) or 7; 8 more when bit 19 is clear and bit 1 is set; 0x100
    /// for an emissive entry (+0x1a3, object flags without 0x100000); 0x800
    /// for +0x99.
    /// </summary>
    public static uint InstanceFlags(ulong attributes, bool emissive, bool byte99)
    {
        var b16 = (attributes >> 16 & 1) != 0;
        var b17 = (attributes >> 17 & 1) != 0;
        var f = b17 ? (b16 ? 1u : 7u) : (b16 ? 5u : 7u);
        if ((attributes >> 19 & 1) == 0 && (f & 2) != 0)
            f |= 8;
        if (emissive)
            f |= 0x100;
        if (byte99)
            f |= 0x800;
        return f;
    }

    /// <summary>18126a710: a colour channel clamped, sRGB to linear.</summary>
    public static float Linear(float c)
    {
        c = 0f <= c ? (c <= 1f ? c : 1f) : 0f;
        return c <= 0.04045f ? c / 12.92f : Maps.LightPow.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    /// <summary>
    /// 180285eb0's instances for the world entries (the same ones as <see cref="WorldMeshes"/>):
    /// mesh, material, a tint not all ones (rgb made linear), an
    /// emissive_lighting_boost not 1, and the flags.
    /// </summary>
    public static List<KVObject> WorldInstances(IReadOnlyList<Maps.WorldNodeBuild.Node> world)
    {
        var list = new List<KVObject>();
        foreach (var n in world)
        {
            var h = n.Header;
            if ((h.Attributes & 0x2000000001UL) != 0)
                continue;
            var o = KVObject.Collection();
            o.Add("mesh", new KVObject(list.Count));
            o.Add("material", new KVObject(n.Entry.Material) { Flag = KVFlag.ResourceName });
            if (h.Tint != System.Numerics.Vector4.One)
            {
                var t = KVObject.Array();
                foreach (var c in new[] { Linear(h.Tint.X), Linear(h.Tint.Y), Linear(h.Tint.Z), h.Tint.W })
                    t.Add(new KVObject((double)c));
                o.Add("tint_color", t);
            }
            if (h.EmissiveBoost != 1f)
                o.Add("emissive_lighting_boost", new KVObject((double)h.EmissiveBoost));
            o.Add("flags", new KVObject(InstanceFlags(h.Attributes, h.Byte1a3 != 0 && (h.ObjectFlags & 0x100000) == 0, false)));
            list.Add(o);
        }
        return list;
    }

    /// <summary>scene_info.kv3's text: mesh_file, instances, ibl (null without a sky light), lights.</summary>
    public static string InfoFile(IEnumerable<KVObject> instances, KVObject? ibl, IEnumerable<KVObject> lights)
    {
        var root = KVObject.Collection();
        root.Add("mesh_file", new KVObject("scene_meshes.kv3"));
        var i = KVObject.Array();
        foreach (var x in instances)
            i.Add(x);
        root.Add("instances", i);
        root.Add("ibl", ibl ?? KVObject.Null());
        var l = KVObject.Array();
        foreach (var x in lights)
            l.Add(x);
        root.Add("lights", l);
        return Kv3TextWriter.Write(root, GenericFormat);
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
