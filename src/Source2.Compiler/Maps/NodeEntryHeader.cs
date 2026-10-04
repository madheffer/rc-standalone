using Source2.Compiler.Io;
using System.Globalization;
using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// The fixed fields of a world node mesh entry (0x238 bytes) as
/// WRB_CollectMeshEntries (18023f6d0) fills them from the mesh's builder
/// buffer (CMapMesh_ConvertMeshForBuilder, then MapMeshBuffer_Unserialize)
/// and WRB_MeshEntryFlags. CMapMesh's keys by field (from the property
/// getters): bakelighting +0x3b58, renderToCubemaps +0x3b59,
/// emissiveLightingEnabled +0x3b5a, emissiveLightingBoost +0x3b64,
/// fademindist +0x3b50, fademaxdist +0x3b54, visexclude +0x3b6a,
/// disablemerging +0x3b6b, disableShadows +0x3b6c, renderwithdynamic
/// +0x3b74, tintColor +0x39dc.
/// </summary>
public sealed record NodeEntryHeader
{
    /// <summary>+0x28: the tint, tintColor / 255 (1 when the mesh has none).</summary>
    public Vector4 Tint { get; init; } = Vector4.One;
    /// <summary>+0x40 and +0x4c: the node id (+0x48 holds the path length, 1).</summary>
    public int NodeId { get; init; }
    /// <summary>+0x80: the node's origin (vf 0x60).</summary>
    public Vector3 Origin { get; init; }
    /// <summary>+0x98: bakelighting when the build bakes lighting (the builder's vf 0x108).</summary>
    public bool Baked { get; init; }
    /// <summary>+0x9c and +0x1a8: 1.</summary>
    public float Field9c { get; init; } = 1f;
    /// <summary>+0xb0, +0xb4: fademindist and fademaxdist.</summary>
    public float FadeMin { get; init; } = -1f;
    public float FadeMax { get; init; }
    /// <summary>+0xb8: emissiveLightingBoost.</summary>
    public float EmissiveBoost { get; init; } = 1f;
    /// <summary>+0xbc: 0x400 renderToCubemaps, 0x200 renderwithdynamic.</summary>
    public uint ObjectFlags { get; init; }
    /// <summary>+0x1a0 to +0x1a3: 0, not visexclude, 1, emissiveLightingEnabled.</summary>
    public byte Byte1a0 { get; init; }
    public byte Byte1a1 { get; init; }
    public byte Byte1a2 { get; init; } = 1;
    public byte Byte1a3 { get; init; }
    /// <summary>+0xc0: a prop's placement flags (PropTransform's, 3); 0 for the world.</summary>
    public int Fieldc0 { get; init; }
    /// <summary>+0x1b0: the attribute flags; +0x1b8: the representative texture size.</summary>
    public ulong Attributes { get; init; }
    public int TextureWidth { get; init; }
    public int TextureHeight { get; init; }

    /// <summary>The header of a world entry made from <paramref name="mesh"/> (a CMapMesh) with <paramref name="material"/>'s attributes.</summary>
    public static NodeEntryHeader World(DmxBinary.Element mesh, int nodeId, Vector3 origin, MeshEntryFlags.Record record,
                                        MeshEntryFlags.IAttributes material, bool buildBakes = false, bool copy = false)
    {
        bool Flag(string key) => mesh.Attributes.GetValueOrDefault(key) is true;
        float Number(string key, float fallback) => mesh.Attributes.GetValueOrDefault(key) is { } v ? Convert.ToSingle(v, CultureInfo.InvariantCulture) : fallback;
        var tint = Vector4.One;
        if (mesh.Attributes.GetValueOrDefault("tintColor") is byte[] { Length: 4 } c && !(c[0] == 255 && c[1] == 255 && c[2] == 255 && c[3] == 255))
            tint = new Vector4(c[0] * 0.003921569f, c[1] * 0.003921569f, c[2] * 0.003921569f, c[3] * 0.003921569f);
        var flags = MeshEntryFlags.Compute(material, record);
        return new NodeEntryHeader
        {
            Tint = tint,
            NodeId = nodeId,
            Origin = origin,
            Baked = buildBakes && Flag("bakelighting"),
            FadeMin = Number("fademindist", -1f),
            FadeMax = Number("fademaxdist", 0f),
            EmissiveBoost = Number("emissiveLightingBoost", 1f),
            // Open (GROUND_TRUTH 41): an instance copy never carries 0x200
            // (atixref's 15 copies of renderwithdynamic meshes), though the
            // property has a setter; measured, not read.
            ObjectFlags = (Flag("renderToCubemaps") ? 0x400u : 0u) | (Flag("renderwithdynamic") && !copy ? 0x200u : 0u),
            Byte1a1 = (byte)(Flag("visexclude") ? 0 : 1),
            Byte1a3 = (byte)(Flag("emissiveLightingEnabled") ? 1 : 0),
            Attributes = flags.Flags,
            TextureWidth = flags.Width,
            TextureHeight = flags.Height,
        };
    }

    /// <summary>
    /// The header of a baked prop's entry (WRBNode_PropEntry from the prop
    /// record of 180245770, then AddStaticProps): the tint is rendercolor / 255,
    /// the fades fademindist and fademaxdist, +0xb8 emissive_lighting_boost,
    /// object flags 8 (a .vmdl model), 0x80 disableinlowquality, 0x400
    /// rendertocubemaps, and 0x20000 from the prop's placement flags bit 2;
    /// +0x1a0 is 1, +0x1a3 emissive, +0x80 no lighting origin (FLT_MAX).
    /// </summary>
    public static NodeEntryHeader Prop(DmxBinary.Element keys, int nodeId, string model, MeshEntryFlags.Record record,
                                       MeshEntryFlags.IAttributes material, int placementFlags = PropTransform.Place | PropTransform.Texcoords)
    {
        string? Key(string name) => keys.Get<string>(name);
        bool On(string name) => Key(name) is "1" or "true" or "True";
        float Number(string name, float fallback) => float.TryParse(Key(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        var tint = Vector4.One;
        if (Key("rendercolor") is { } color)
        {
            var parts = color.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => byte.TryParse(x, out var b) ? b : (byte)255).ToArray();
            if (parts.Length >= 3)
                tint = new Vector4(parts[0] * 0.003921569f, parts[1] * 0.003921569f, parts[2] * 0.003921569f, parts.Length > 3 ? parts[3] * 0.003921569f : 1f);
        }
        var flags = MeshEntryFlags.Compute(material, record);
        var objectFlags = (model.ContainsAscii(".vmdl") ? 8u : 0u) | (On("disableinlowquality") ? 0x80u : 0u)
            | (On("rendertocubemaps") ? 0x400u : 0u) | ((placementFlags & 2) != 0 ? 0x20000u : 0u);
        return new NodeEntryHeader
        {
            Tint = tint,
            NodeId = nodeId,
            Origin = new Vector3(float.MaxValue),
            FadeMin = Number("fademindist", 0f),
            FadeMax = Number("fademaxdist", 0f),
            EmissiveBoost = Number("emissive_lighting_boost", 1f),
            ObjectFlags = objectFlags,
            Fieldc0 = placementFlags,
            Byte1a0 = 1,
            Byte1a2 = 0,
            Byte1a3 = (byte)(On("emissive") ? 1 : 0),
            Attributes = flags.Flags,
            TextureWidth = flags.Width,
            TextureHeight = flags.Height,
        };
    }

    /// <summary>The fields as they sit in the entry, for comparison with a captured one: (offset, bytes).</summary>
    public IEnumerable<(int Offset, byte[] Bytes)> Fields()
    {
        static byte[] F(params float[] v) => v.SelectMany(BitConverter.GetBytes).ToArray();
        yield return (0x28, F(Tint.X, Tint.Y, Tint.Z, Tint.W));
        yield return (0x40, BitConverter.GetBytes(NodeId));
        yield return (0x48, BitConverter.GetBytes(1));
        yield return (0x4c, BitConverter.GetBytes(NodeId));
        yield return (0x80, F(Origin.X, Origin.Y, Origin.Z));
        yield return (0x98, [(byte)(Baked ? 1 : 0)]);
        yield return (0x9c, F(Field9c));
        yield return (0xb0, F(FadeMin, FadeMax, EmissiveBoost));
        yield return (0xbc, BitConverter.GetBytes(ObjectFlags));
        yield return (0xc0, BitConverter.GetBytes(Fieldc0));
        yield return (0x1a0, [Byte1a0, Byte1a1, Byte1a2, Byte1a3]);
        yield return (0x1a8, F(1f));
        yield return (0x1b0, BitConverter.GetBytes(Attributes));
        yield return (0x1b8, BitConverter.GetBytes(TextureWidth).Concat(BitConverter.GetBytes(TextureHeight)).ToArray());
    }
}
