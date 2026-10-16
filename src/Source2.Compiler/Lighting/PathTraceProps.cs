using System.Numerics;
using Source2.Compiler.Maps;
using ValveKeyValue;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace Source2.Compiler.Lighting;

/// <summary>
/// The static models of the path-trace scene (0923 180284aa0, called by
/// WRB_BuildPathTraceSceneInfo for each model of the builder's prop map at
/// +0x198, in model order): the records EntityLump_StaticModelRecord
/// (180245770) makes from every prop_static and from every brush entity
/// whose generated model has lightmapstatic set (180240a60).
/// <list type="bullet">
/// <item>A model's props are grouped by LOD (lodlevel -1 is LOD 0); each
/// group loads the model's draw calls of that LOD (WRB_LoadPropMeshes,
/// 1801d30d0). Per draw call, then per prop: one shared CMesh, added before
/// its first instance (a prop with per-vertex data, +0x100, would copy it:
/// not ported).</item>
/// <item>An instance: the mesh, the prop's matrix, the material through the
/// skin's material group (1802b6830), a tint not all ones (rendercolor and
/// renderamt over 255; per-draw tint overrides not ported), an
/// emissive_lighting_boost not 1, and the flags (<see cref="Flags"/>).</item>
/// </list>
/// Not ported: materialoverride, deformers, a lodlevel of 0 or more,
/// clutter, draw records flagged 0x800, 180240a60's other conditions on
/// a brush entity (its +0x108 and one-model tests).
/// </summary>
internal static class PathTraceProps
{
    /// <summary>The record fields 180284aa0 reads, by the keys 180245770 fills them from.</summary>
    public sealed record Prop(string Model, float[] Matrix, Vector4 Tint, string? Skin, int DisableShadows, int LightmapStatic, bool Emissive,
                              int EmissiveMode, float EmissiveBoost, bool DoubleSided, int LodLevel);

    /// <summary>
    /// 180245770's record: disableshadows (+0x16f, 0), emissive (+0x173,
    /// default 1), emissive_lighting_boost (+0x174, 1), emissive_mode (+0x178,
    /// 0), bakelightdoublesided (+0x17d), rendercolor and renderamt over 255
    /// (+0x148, 255), lodlevel (+0x1e4, -1) and lightmapstatic (+0x210, 1 for a
    /// prop_static, else 0). Null for a brush entity without lightmapstatic.
    /// </summary>
    public static Prop? Record(string model, LightPrecompute.KeyReader key, float[] matrix, bool propStatic)
    {
        int Int(string name, int fallback) => key(name) is { Length: > 0 } v ? (int)CNumbers.Atoi(v) : fallback;
        bool Bool(string name, bool fallback) => key(name) is { Length: > 0 } v ? v is "true" or "True" || CNumbers.Atoi(v) != 0 : fallback;
        var lightmapStatic = Int("lightmapstatic", propStatic ? 1 : 0);
        if (!propStatic && lightmapStatic == 0)
            return null;
        if (!string.IsNullOrEmpty(key("materialoverride")))
            throw new NotSupportedException($"{model}: a materialoverride (the record's +0x118) is not ported");
        if (Bool("clutterinstance", false))
            throw new NotSupportedException($"{model}: a clutter instance is not ported");
        var color = (key("rendercolor") ?? "255 255 255").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        float Channel(int i) => (i < color.Length ? (int)CNumbers.Atoi(color[i]) & 0xff : 255) * 0.003921569f;
        var amount = Int("renderamt", 255);
        var boost = key("emissive_lighting_boost") is { Length: > 0 } b ? CNumbers.ToFloat32(b) : 1f;
        return new Prop(model, matrix, new Vector4(Channel(0), Channel(1), Channel(2), amount * 0.003921569f), key("skin"), Int("disableshadows", 0),
                        lightmapStatic, Bool("emissive", true), Int("emissive_mode", 0), boost, Bool("bakelightdoublesided", false), Int("lodlevel", -1));
    }

    /// <summary>
    /// 180284aa0's flags: from disableshadows 7 (0, 3, 4), 5 (2) or 1; without
    /// 6 for lightmapstatic 0 and without 2 for 2; 8 when bit 1 is left (the
    /// record's +0x1f0 bit 19 is not set from keys); 0x100 when emissive with
    /// emissive_mode 0; 0x800 for bakelightdoublesided.
    /// </summary>
    public static uint Flags(Prop p)
    {
        var f = p.DisableShadows switch { 0 or 3 or 4 => 7u, 2 => 5u, _ => 1u };
        if (p.LightmapStatic == 0)
            f &= ~6u;
        else if (p.LightmapStatic == 2)
            f &= ~2u;
        if ((f & 2) != 0)
            f |= 8;
        if (p.Emissive && p.EmissiveMode == 0)
            f |= 0x100;
        if (p.DoubleSided)
            f |= 0x800;
        return f;
    }

    /// <summary>
    /// The meshes and instances of the props, models in ordinal order of
    /// their paths and each model's props in record order; the meshes are
    /// appended to <paramref name="meshes"/> and the instances name them by
    /// their index there.
    /// </summary>
    public static void Add(List<PathTraceScene.Mesh> meshes, List<KVObject> instances, IEnumerable<Prop> props, Func<string, Model?> loadModel)
    {
        foreach (var group in props.GroupBy(p => p.Model, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (loadModel(group.Key) is not { } model)
                continue;
            if (group.Any(p => p.LodLevel >= 0))
                throw new NotSupportedException($"{group.Key}: a lodlevel (180284aa0's LOD groups, FUN_18174c9c0) is not ported");
            var calls = Draws(model, 0).ToList();
            foreach (var (mesh, material) in calls)
            {
                var index = -1;
                foreach (var p in group)
                {
                    if (index == -1)
                    {
                        index = meshes.Count;
                        meshes.Add(mesh);
                    }
                    var remap = NodePropEntries.MaterialGroup(model, p.Skin);
                    instances.Add(Instance(index, p, remap.GetValueOrDefault(material, material)));
                }
            }
        }
    }

    private static KVObject Instance(int mesh, Prop p, string material)
    {
        var o = KVObject.Collection();
        o.Add("mesh", new KVObject(mesh));
        var t = KVObject.Array();
        foreach (var x in p.Matrix)
            t.Add(new KVObject((double)x));
        o.Add("transform", t);
        o.Add("material", new KVObject(material) { Flag = KVFlag.ResourceName });
        if (p.Tint != Vector4.One)
        {
            var c = KVObject.Array();
            foreach (var x in new[] { p.Tint.X, p.Tint.Y, p.Tint.Z, p.Tint.W })
                c.Add(new KVObject((double)x));
            o.Add("tint_color", c);
        }
        if (p.EmissiveBoost != 1f)
            o.Add("emissive_lighting_boost", new KVObject((double)p.EmissiveBoost));
        o.Add("flags", new KVObject(Flags(p)));
        return o;
    }

    /// <summary>
    /// WRB_LoadPropMeshes for one LOD: each embedded mesh's draw calls (a
    /// model of several meshes keeps those whose LOD mask has the LOD and
    /// whose mesh group mask meets the default one), as CMeshes, with the
    /// draw call's own material.
    /// </summary>
    public static IEnumerable<(PathTraceScene.Mesh Mesh, string Material)> Draws(Model model, int lod)
    {
        var all = model.GetEmbeddedMeshesAndLoD().ToList();
        var groupMasks = model.Data.GetArray<ulong>("m_refMeshGroupMasks");
        var defaultMask = model.Data.GetUnsignedIntegerProperty("m_nDefaultMeshGroupMask");
        for (var i = 0; i < all.Count; i++)
        {
            var (mesh, _, _, lodMask) = all[i];
            if (all.Count != 1)
            {
                if (((ulong)lodMask & (1UL << lod)) == 0)
                    continue;
                var groupMask = groupMasks != null && i < groupMasks.Length ? groupMasks[i] : ulong.MaxValue;
                if ((groupMask & defaultMask) == 0)
                    continue;
            }
            foreach (var sceneObject in mesh.Data.GetArray("m_sceneObjects"))
                foreach (var call in sceneObject.GetArray("m_drawCalls"))
                {
                    var material = call.GetStringProperty("m_material") ?? "";
                    yield return (DrawMesh(mesh.VBIB, call, material), material);
                }
        }
    }

    /// <summary>
    /// One draw call's CMesh: meshsystem's geometry (18002af80) gives the 18
    /// standard floats and the fields it does not consume, which
    /// WRB_LoadPropMeshes appends as streams of their own after the six
    /// standard ones, named by their shader semantic, in field order.
    /// </summary>
    public static PathTraceScene.Mesh DrawMesh(VBIB vbib, KVObject call, string material)
    {
        var vb = vbib.VertexBuffers[call.GetArray("m_vertexBuffers")[0].GetInt32Property("m_hBuffer")];
        var ib = vbib.IndexBuffers[call.GetSubCollection("m_indexBuffer").GetInt32Property("m_hBuffer")];
        int Index(int i) => ib.ElementSizeInBytes == 2 ? BitConverter.ToUInt16(ib.Data, i * 2) : BitConverter.ToInt32(ib.Data, i * 4);
        var start = call.GetInt32Property("m_nStartIndex");
        var count = call.GetInt32Property("m_nIndexCount");
        var bas = call.GetInt32Property("m_nBaseVertex");
        var lo = Enumerable.Range(start, count).Min(Index) + bas;
        var vertexCount = Enumerable.Range(start, count).Max(Index) + bas - lo + 1;
        var geometry = MeshGeometry.Classify(vb);
        var (standard, hasColor) = NodePropEntries.Vertices(vb, lo, vertexCount, geometry.Color);
        var extraSize = geometry.Extras.Sum(e => e.Components);
        var stride = NodePropEntries.Stride + extraSize;
        var vertices = new float[vertexCount * stride];
        for (var v = 0; v < vertexCount; v++)
        {
            Array.Copy(standard, v * NodePropEntries.Stride, vertices, v * stride, NodePropEntries.Stride);
            var at = v * stride + NodePropEntries.Stride;
            foreach (var e in geometry.Extras)
            {
                MeshGeometry.Unpack(vb, e.Field, lo + v, vertices.AsSpan(at, e.Components));
                at += e.Components;
            }
        }
        var streams = new List<PathTraceScene.Stream>();
        foreach (var s in NodePropEntries.Streams(hasColor))
        {
            var noCompression = (s.Name == "texcoord" && geometry.FloatTexcoords) || (s.Name == "tangent" && geometry.FloatTangents);
            streams.Add(new PathTraceScene.Stream(s.Name, "", streams.Count(x => x.Name == s.Name), s.First, s.Count, false, noCompression));
        }
        var offset = NodePropEntries.Stride;
        foreach (var e in geometry.Extras)
        {
            streams.Add(new PathTraceScene.Stream(e.Name, "", 0, offset, e.Components, false, false));
            offset += e.Components;
        }
        var indices = Enumerable.Range(start, count).Select(i => Index(i) + bas - lo).ToArray();
        return new PathTraceScene.Mesh(vertices, stride, indices, streams, material) { Tint = [0f, 0f, 0f, 0f], CollisionType = uint.MaxValue };
    }
}
