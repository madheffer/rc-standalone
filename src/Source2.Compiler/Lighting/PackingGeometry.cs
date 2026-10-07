namespace Source2.Compiler.Lighting;

/// <summary>
/// lightmap_packing_geometry.dat, resourcecompiler's NLightBaker output that
/// vrad3 reads (PackingGeometry_t::Unserialize, 180042a90; docs/VRAD3.md).
/// Each array is a u32 count then its data. Read and written back byte for
/// byte on probe01's file.
/// </summary>
public sealed class PackingGeometry
{
    /// <summary>A chart: ARGB debug colour, index, its rect in the atlas and a flags word.
    /// Flags bit 28: small in x, bit 29: small in y (lightmap_image_consolidate_small_charts).</summary>
    public readonly record struct Chart(uint Color, uint Index, int X, int Y, int Width, int Height, uint Flags)
    {
        public bool SmallX => (Flags >> 28 & 1) != 0;
        public bool SmallY => (Flags >> 29 & 1) != 0;
    }

    /// <summary>The leading u32 array (empty on probe01; meaning not read).</summary>
    public uint[] Leading { get; init; } = [];

    /// <summary>Bytes per vertex (40: position, normal, tangent and sign).</summary>
    public int VertexStride { get; init; }

    /// <summary>count x VertexStride bytes.</summary>
    public byte[] Vertices { get; init; } = [];

    /// <summary>Per vertex, its atlas uv (0 to 1).</summary>
    public float[] AtlasUv { get; init; } = [];

    public uint[] Indices { get; init; } = [];
    public Chart[] Charts { get; init; } = [];

    /// <summary>Per triangle, its chart.</summary>
    public uint[] TriangleCharts { get; init; } = [];

    /// <summary>The six trailing u32s: vertex attribute offsets, position (0), unread,
    /// normal (12), tangent (24), two unread.</summary>
    public uint[] Offsets { get; init; } = new uint[6];

    public int VertexCount => VertexStride == 0 ? 0 : Vertices.Length / VertexStride;

    public static PackingGeometry Read(byte[] bytes)
    {
        var r = new BinaryReader(new MemoryStream(bytes));
        uint[] U32s(int n)
        {
            var a = new uint[n];
            for (var i = 0; i < n; i++)
                a[i] = r.ReadUInt32();
            return a;
        }
        var leading = U32s(r.ReadInt32());
        var stride = r.ReadInt32();
        var count = r.ReadInt32();
        var vertices = r.ReadBytes(stride * count);
        var uv = new float[count * 2];
        for (var i = 0; i < uv.Length; i++)
            uv[i] = r.ReadSingle();
        var indices = U32s(r.ReadInt32());
        var charts = new Chart[r.ReadInt32()];
        for (var i = 0; i < charts.Length; i++)
            charts[i] = new Chart(r.ReadUInt32(), r.ReadUInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadUInt32());
        var triangleCharts = U32s(indices.Length / 3);
        var offsets = U32s(6);
        if (r.BaseStream.Position != bytes.Length)
            throw new InvalidDataException($"packing geometry: {bytes.Length - r.BaseStream.Position} bytes left over");
        return new PackingGeometry
        {
            Leading = leading, VertexStride = stride, Vertices = vertices, AtlasUv = uv, Indices = indices,
            Charts = charts, TriangleCharts = triangleCharts, Offsets = offsets,
        };
    }

    public byte[] Write()
    {
        var m = new MemoryStream();
        var w = new BinaryWriter(m);
        w.Write(Leading.Length);
        foreach (var v in Leading)
            w.Write(v);
        w.Write(VertexStride);
        w.Write(VertexCount);
        w.Write(Vertices);
        foreach (var f in AtlasUv)
            w.Write(f);
        w.Write(Indices.Length);
        foreach (var i in Indices)
            w.Write(i);
        w.Write(Charts.Length);
        foreach (var c in Charts)
        {
            w.Write(c.Color);
            w.Write(c.Index);
            w.Write(c.X);
            w.Write(c.Y);
            w.Write(c.Width);
            w.Write(c.Height);
            w.Write(c.Flags);
        }
        foreach (var t in TriangleCharts)
            w.Write(t);
        foreach (var o in Offsets)
            w.Write(o);
        return m.ToArray();
    }
}
