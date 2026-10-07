using Source2.Compiler.Io;
using System.Numerics;

namespace Source2.Compiler.Maps;

/// <summary>
/// What <c>WRBMeshEntry_CanMerge</c> (resourcecompiler 1802b63b0) reads of a
/// world renderer builder mesh entry (0x238 bytes) and its <c>CMesh</c>. The
/// fields whose meaning is not read yet keep their offsets as names.
/// </summary>
public sealed record WrbMeshEntry
{
    /// <summary>+0x1b0: the attribute flags from the material (bit 1: never merge).</summary>
    public ulong Attributes { get; init; }
    /// <summary>The mesh's material (CMesh +0x60, a CBufferString).</summary>
    public string Material { get; init; } = "";

    /// <summary>+0x1c0: the material's skyboxslot attribute, which the skybox material list groups by.</summary>
    public string SkyboxSlot { get; init; } = "";
    /// <summary>+0xac: overlay order.</summary>
    public int OverlayOrder { get; init; }
    /// <summary>+0xbc: object flags.</summary>
    public uint ObjectFlags { get; init; }
    /// <summary>CMesh +0x14f and +0x154: the debug colour switch and colour.</summary>
    public bool DebugColor { get; init; }
    public Vector4 DebugColorValue { get; init; }
    /// <summary>+0x28: four floats compared exactly.</summary>
    public Vector4 Field28 { get; init; }
    /// <summary>CMesh +0x1c: floats per vertex.</summary>
    public int Stride { get; init; }
    public IReadOnlyList<Stream> Streams { get; init; } = [];
    /// <summary>CMesh +0x58.</summary>
    public byte Mesh58 { get; init; }
    /// <summary>CMesh +0x40 (count) and +0x48: floats compared exactly.</summary>
    public IReadOnlyList<float> MeshFloats { get; init; } = [];
    public byte Field1a3 { get; init; }
    public float Field_b8 { get; init; }
    public byte Field1a5 { get; init; }
    /// <summary>+0x1a6: GroupRenderObjects runs on it and CreateRenderObject's name takes _kv from it.</summary>
    public byte Field1a6 { get; init; }
    public float Field1a8 { get; init; }
    /// <summary>+0xa0: cubemap; +0xa4: light probe volume.</summary>
    public int Cubemap { get; init; }
    public int LightProbe { get; init; }
    /// <summary>CMesh +0x184.</summary>
    public float Mesh184 { get; init; }
    public float Field9c { get; init; }
    public byte Field1a1 { get; init; }
    public byte Field1a0 { get; init; }
    public float Field_b0 { get; init; }
    /// <summary>+0xb4: fade max.</summary>
    public float FadeMax { get; init; }
    /// <summary>+0x38: a string compared case-sensitively.</summary>
    public string Name { get; init; } = "";
    /// <summary>+0x1c8: a 3x4 matrix, compared within 1e-5.</summary>
    public float[] Matrix { get; init; } = new float[12];
    /// <summary>CMesh +0x152: set, the entry merges with nothing.</summary>
    public bool Mesh152 { get; init; }
    /// <summary>+0x40: the source node id.</summary>
    public int NodeId { get; init; }
    /// <summary>+0xa8: -1 for a map mesh or overlay, 0 for a prop entry (atixref's 1,018 at BuildNode's end).</summary>
    public int FieldA8 { get; init; } = -1;
    /// <summary>
    /// The CMesh's streams as BuildNode left them, when PrepareForMerge has
    /// not run on it yet (null: <see cref="Streams"/>). CompileNode runs that
    /// filter only on the merged lists, and only before the vis merger, so
    /// GroupAggregates groups the other lists' meshes by these; the written
    /// aggregates carry only what the material reads (the filtered
    /// <see cref="Streams"/>).
    /// </summary>
    public IReadOnlyList<Stream>? MeshStreams { get; init; }
    /// <summary>
    /// Whether a texcoord stream of the unfiltered mesh holds a value past
    /// +-16 (FUN_180d4a120), which GroupAggregates' texcoord promotion reads.
    /// </summary>
    public bool TexcoordsPast16 { get; init; }

    /// <summary>FUN_180d4a120 on every texcoord stream of a vertex array: |u| or |v| past 16.</summary>
    internal static bool TexcoordsPastSixteen(float[] vertices, int stride, IEnumerable<Physics.MeshWeld.Stream> streams)
        => streams.Where(s => s.Name.EqualsAscii("texcoord")).Any(s =>
            Enumerable.Range(0, vertices.Length / stride).Any(v => MathF.Abs(vertices[(v * stride) + s.First]) > 16f || MathF.Abs(vertices[(v * stride) + s.First + 1]) > 16f));

    /// <summary>
    /// A CMesh stream (0x28 bytes): name, semantic index (+0x10), float count
    /// (+0x18), high precision (+0x1d), type (+0x20). The rest is implied:
    /// +0x14 is the float offset, the running sum of the counts before it
    /// (DmeMeshToCMesh's stream layout, 180d473d0, and the stream removal of
    /// PrepareForMerge, 1812e2a10, which shifts the later offsets down); +8
    /// (a vertex data element's name) and the byte +0x1c are set only for
    /// the extra vertex data sets DmeMeshToCMesh is handed, which map meshes
    /// and props do not have, so they are empty and 0.
    /// </summary>
    public sealed record Stream(string Name, int Index, int Count, byte Precise, int Type);

    /// <summary>
    /// FUN_1802b6130: boost hash_combine (uint32, h ^= v + 0x9e3779b9 +
    /// (h &lt;&lt; 6) + (h &gt;&gt; 2)) starting from the stream count, over each
    /// stream's MurmurHash2 of its name and of +8 (seed 0x3501a674), then
    /// +0x10, +0x14, +0x18, +0x1c and +0x1d. GroupAggregates' last sort key.
    /// </summary>
    public static uint LayoutHash(IReadOnlyList<Stream> streams)
    {
        static uint Combine(uint h, uint v) => ((h >> 2) + v + (h << 6) + 0x9e3779b9) ^ h;
        var h = (uint)streams.Count;
        var offset = 0;
        foreach (var s in streams)
        {
            h = Combine(h, Murmur2(System.Text.Encoding.UTF8.GetBytes(s.Name), 0x3501a674));
            h = Combine(h, Murmur2([], 0x3501a674));
            h = Combine(h, (uint)s.Index);
            h = Combine(h, (uint)offset);
            h = Combine(h, (uint)s.Count);
            h = Combine(h, 0);
            h = Combine(h, s.Precise);
            offset += s.Count;
        }
        return h;
    }

    /// <summary>MurmurHash2 (m 0x5bd1e995, r 24), seeded with the length xor <paramref name="seed"/>.</summary>
    public static uint Murmur2(ReadOnlySpan<byte> data, uint seed)
    {
        const uint M = 0x5bd1e995;
        var h = (uint)data.Length ^ seed;
        var i = 0;
        for (; data.Length - i >= 4; i += 4)
        {
            var k = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[i..]) * M;
            k ^= k >> 24;
            h = h * M ^ k * M;
        }
        var rest = data.Length - i;
        if (rest == 3)
            h ^= (uint)data[i + 2] << 16;
        if (rest >= 2)
            h ^= (uint)data[i + 1] << 8;
        if (rest >= 1)
            h = (h ^ data[i]) * M;
        h = (h ^ (h >> 13)) * M;
        return h ^ (h >> 15);
    }

    /// <summary>
    /// FUN_180251da0: the lighting mode from attribute bits 16 to 18: bit 17
    /// gives 1 with bit 16 and 3 without; else bit 16 gives 2; else bit 18
    /// gives 4.
    /// </summary>
    public static int LightingMode(ulong attributes)
    {
        var b16 = (attributes >> 16 & 1) != 0;
        if ((attributes >> 17 & 1) != 0)
            return b16 ? 1 : 3;
        if (b16)
            return 2;
        return (int)(attributes >> 16 & 4);
    }

    /// <summary>
    /// WRBMeshEntry_CanMerge: neither never-merge bit set, at most 0x200000
    /// vertices together, then the same attributes, material, overlay order,
    /// object flags, lighting mode, debug colour, +0x28, layout (stride,
    /// streams matched by semantic index and name, case-blind, with the same
    /// precision, count and type), mesh floats, +0xb8 when either carries
    /// object flag 0x100000 with +0x1a3, +0x1a5, +0x1a8, cubemap, probe,
    /// +0x184, +0x9c, +0x1a1, +0x1a0, +0xb0, fade, name, the matrix within
    /// 1e-5, and neither mesh's +0x152.
    /// </summary>
    public static bool CanMerge(WrbMeshEntry a, int aVertices, WrbMeshEntry b, int bVertices)
    {
        if (((a.Attributes | b.Attributes) & 2) != 0)
            return false;
        if (aVertices + bVertices > 0x200000)
            return false;
        if (a.Attributes != b.Attributes || a.Material != b.Material)
            return false;
        if (a.OverlayOrder != b.OverlayOrder || a.ObjectFlags != b.ObjectFlags)
            return false;
        if (LightingMode(a.Attributes) != LightingMode(b.Attributes))
            return false;
        if (a.DebugColor != b.DebugColor || (a.DebugColor && !Same(a.DebugColorValue, b.DebugColorValue)))
            return false;
        if (!Same(a.Field28, b.Field28) || a.Streams.Count != b.Streams.Count || a.Stride != b.Stride)
            return false;
        foreach (var s in a.Streams)
        {
            var t = b.Streams.FirstOrDefault(x => x.Index == s.Index && x.Name.EqualsAscii(s.Name));
            if (t is null || t.Precise != s.Precise || t.Count != s.Count || t.Type != s.Type)
                return false;
        }
        if (a.Mesh58 != b.Mesh58 || a.MeshFloats.Count != b.MeshFloats.Count)
            return false;
        for (var i = 0; i < a.MeshFloats.Count; i++)
            if (a.MeshFloats[i] != b.MeshFloats[i])
                return false;
        var aClear = a.Field1a3 == 0 || (a.ObjectFlags & 0x100000) == 0;
        var bClear = b.Field1a3 == 0 || (b.ObjectFlags & 0x100000) == 0;
        if (!((aClear && bClear) || a.Field_b8 == b.Field_b8) || a.Field1a5 != b.Field1a5)
            return false;
        if (a.Field1a8 != b.Field1a8)
            return false;
        if (a.Cubemap != b.Cubemap || a.LightProbe != b.LightProbe || a.Mesh184 != b.Mesh184 || a.Field9c != b.Field9c
            || a.Field1a1 != b.Field1a1 || a.Field1a0 != b.Field1a0 || a.Field_b0 != b.Field_b0 || a.FadeMax != b.FadeMax)
            return false;
        if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal))
            return false;
        for (var i = 0; i < 12; i++)
            if (1e-5f < MathF.Abs(a.Matrix[i] - b.Matrix[i]))
                return false;
        return !a.Mesh152 && !b.Mesh152;
    }

    static bool Same(Vector4 x, Vector4 y) => x.X == y.X && x.Y == y.Y && x.Z == y.Z && x.W == y.W;

    /// <summary>The fields from an entry's 0x238 bytes and its CMesh's 0x198, with what they point at.</summary>
    public static WrbMeshEntry FromBytes(ReadOnlySpan<byte> entry, ReadOnlySpan<byte> mesh, string material, IReadOnlyList<float> meshFloats,
                                         string name, IReadOnlyList<Stream> streams)
    {
        static float F(ReadOnlySpan<byte> b, int at) => BitConverter.ToSingle(b[at..]);
        static Vector4 V(ReadOnlySpan<byte> b, int at) => new(F(b, at), F(b, at + 4), F(b, at + 8), F(b, at + 12));
        var matrix = new float[12];
        for (var i = 0; i < 12; i++)
            matrix[i] = F(entry, 0x1c8 + i * 4);
        return new WrbMeshEntry
        {
            Attributes = BitConverter.ToUInt64(entry[0x1b0..]),
            Material = material,
            OverlayOrder = BitConverter.ToInt32(entry[0xac..]),
            ObjectFlags = BitConverter.ToUInt32(entry[0xbc..]),
            DebugColor = mesh[0x14f] != 0,
            DebugColorValue = V(mesh, 0x154),
            Field28 = V(entry, 0x28),
            Stride = BitConverter.ToInt32(mesh[0x1c..]),
            Streams = streams,
            Mesh58 = mesh[0x58],
            MeshFloats = meshFloats,
            Field1a3 = entry[0x1a3],
            Field_b8 = F(entry, 0xb8),
            Field1a5 = entry[0x1a5],
            Field1a6 = entry[0x1a6],
            Field1a8 = F(entry, 0x1a8),
            Cubemap = BitConverter.ToInt32(entry[0xa0..]),
            LightProbe = BitConverter.ToInt32(entry[0xa4..]),
            Mesh184 = F(mesh, 0x184),
            Field9c = F(entry, 0x9c),
            Field1a1 = entry[0x1a1],
            Field1a0 = entry[0x1a0],
            Field_b0 = F(entry, 0xb0),
            FadeMax = F(entry, 0xb4),
            Name = name,
            Matrix = matrix,
            Mesh152 = mesh[0x152] != 0,
            NodeId = BitConverter.ToInt32(entry[0x40..]),
            FieldA8 = BitConverter.ToInt32(entry[0xa8..]),
        };
    }
}
